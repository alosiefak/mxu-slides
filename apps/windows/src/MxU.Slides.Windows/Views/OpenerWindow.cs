using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;
using MxU.Slides.Core.Openers;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "Make an opener…" in the Media bin (Phase 13): a volunteer makes a short video for the service
/// in a few clicks. Along the top, the four kinds: a sermon-series <b>bumper</b>, a
/// <b>countdown</b>, a <b>montage</b> of photos and clips cut to music, and an <b>announcement
/// loop</b>. On the left, the looks (a still of this opener in each) and the motion background
/// behind it; in the middle, a live preview that plays what will be made; on the right, a short
/// form. <b>Make</b> draws every frame and files the video into the Media bin
/// (MainViewModel.Openers.cs does the work, off the UI thread).
/// <para>
/// Opened with "Edit opener…" on a tile made here, it starts from that opener's recipe, and offers
/// to replace it (same item, so everything using it changes) or save the change as a new one:
/// next week's series title is one box to retype.
/// </para>
/// Built in code, like the other editors (EditorKit). Windows only: the Mac has no twin.
/// </summary>
internal sealed class OpenerWindow : Window
{
    private const string PictureFilter = "Pictures|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif";
    private const string ShotFilter = "Pictures and videos|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.mp4;*.mov;*.m4v;*.wmv;*.avi|All files|*.*";
    private const string MusicFilter = "Music|*.mp3;*.m4a;*.aac;*.wav;*.aif;*.aiff;*.flac;*.wma|All files|*.*";

    private static readonly (OpenerKind Kind, string Name, string Tip)[] Kinds =
    [
        (OpenerKind.Bumper, "Series bumper", "A sermon-series title that animates in and out, 5 to 15 seconds"),
        (OpenerKind.Countdown, "Countdown", "A clock running down to the start of the service"),
        (OpenerKind.Montage, "Montage", "Your photos and clips, cut to the beat of a music track"),
        (OpenerKind.Announcements, "Announcement loop", "Announcement cards that go round and round before the service"),
    ];

    private readonly MainViewModel model;
    private readonly MediaItem? editing;
    private readonly OpenerPreviewer previewer;
    private readonly Dictionary<OpenerKind, OpenerRecipe> drafts = [];
    private readonly Dictionary<OpenerKind, ToggleButton> kindButtons = [];
    private readonly Dictionary<string, Border> lookTiles = [];
    private readonly ComboBox background = new() { MinWidth = 150 };
    private readonly ComboBox palette = new() { MinWidth = 150 };
    private readonly StackPanel form = new();
    private readonly TextBlock summary = EditorKit.Muted("", 12, 0);
    private readonly Slider position = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock positionText = new() { Width = 86, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock liveWarning = new()
    {
        Text = "Something is on the screens. An opener is made at low priority so the show comes first, but a long one is best made after the service.",
        TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42)), Margin = new Thickness(0, 0, 0, 4),
        Visibility = Visibility.Collapsed,
    };
    private readonly Button make;
    private readonly Button replace;
    private readonly Button cancel;
    private readonly Button showNow;
    private OpenerKind kind;
    private OpenerPlan plan;
    private (string Path, BeatAnalysis? Found)? music;
    private string? analysing;
    private int selectedCard;
    private bool updating;
    private bool closed;
    private OpenerJob? job;
    private MediaItem? made;

    private OpenerWindow(Window? owner, MainViewModel model, MediaItem? editing, OpenerRecipe? start)
    {
        this.model = model;
        this.editing = editing;
        EditorKit.Dress(this, owner, editing is null ? "Make an opener" : $"Edit “{editing.Name}”", 1280, 800);
        foreach (var (k, _, _) in Kinds) drafts[k] = OpenerRecipe.For(k);
        if (start is not null) drafts[start.Kind] = start;
        kind = start?.Kind ?? OpenerKind.Bumper;
        plan = OpenerPlan.For(drafts[kind]);
        previewer = new OpenerPreviewer(Dispatcher, model.MotionFonts);
        previewer.Played += OnPlayed;

        make = EditorKit.Button(editing is null ? "Make" : "Save as New", () => _ = MakeAsync(replaceExisting: false),
            "Draws every frame and adds the video to the Media bin", accent: editing is null);
        make.MinWidth = 110;
        replace = EditorKit.Button(editing is null ? "" : $"Replace “{editing.Name}”", () => _ = MakeAsync(replaceExisting: true),
            "Makes the existing opener again with these changes, everywhere it's used", accent: true);
        replace.Visibility = editing is null ? Visibility.Collapsed : Visibility.Visible;
        replace.Margin = new Thickness(0);
        cancel = EditorKit.Button("Cancel", () => job?.Cancel(), "Stops making it; nothing is added");
        cancel.Visibility = Visibility.Collapsed;
        showNow = EditorKit.Button("Show it now", ShowMade, "Plays the new opener on the screens");
        showNow.Visibility = Visibility.Collapsed;

        var columns = new Grid { Margin = new Thickness(16, 10, 16, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(OpenerPreviewer.StillWidth + 34) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        var looks = Looks();
        var middle = Middle();
        var right = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        columns.Children.Add(looks);
        columns.Children.Add(middle);
        columns.Children.Add(right);

        var footer = new DockPanel { Margin = new Thickness(16, 10, 16, 14), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var button in new[] { showNow, cancel, make, replace }) buttons.Children.Add(button);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { liveWarning, status, progress } });

        var kinds = KindBar();
        var root = new DockPanel();
        DockPanel.SetDock(kinds, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(kinds);
        root.Children.Add(footer);
        root.Children.Add(columns);
        Content = root;

        BuildForm();
        Refresh();
        Closed += (_, _) => Leave();
        // An opener already being made (this window was closed and opened again): show its progress.
        if (model.OpenerJob is { } running) _ = Follow(running);
    }

    /// <summary>Opens the window to make a new opener, or (with <paramref name="edit"/>) to change one made here.</summary>
    public static OpenerWindow Open(Window? owner, MainViewModel model, MediaItem? edit = null)
    {
        if (Application.Current.Windows.OfType<OpenerWindow>().FirstOrDefault(w => w.editing?.Id == edit?.Id) is { } open)
        {
            open.Activate();
            return open;
        }
        var window = new OpenerWindow(owner, model, edit, edit is null ? null : model.OpenerRecipeOf(edit));
        window.Show();
        return window;
    }

    // MARK: The layout

    private FrameworkElement KindBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 14, 16, 0) };
        foreach (var (k, name, tip) in Kinds)
        {
            var button = new ToggleButton { Content = name, ToolTip = tip, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 6, 0), FontSize = 13 };
            var picked = k;
            button.Click += (_, _) => Pick(picked);
            kindButtons[k] = button;
            bar.Children.Add(button);
        }
        return bar;
    }

    private FrameworkElement Looks()
    {
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("LOOK", top: 0));
        foreach (var look in OpenerLooks.All)
        {
            var picture = new Border
            {
                CornerRadius = new CornerRadius(4), ClipToBounds = true, Width = OpenerPreviewer.StillWidth, Height = OpenerPreviewer.StillHeight, Background = Brushes.Black,
                Child = new Image { Source = previewer.Stills[look.Id], Stretch = Stretch.UniformToFill },
            };
            var label = new TextBlock { Text = look.Name, FontSize = 11, Margin = new Thickness(2, 3, 0, 1) };
            var tile = new Border
            {
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Padding = new Thickness(2), Margin = new Thickness(0, 0, 0, 4),
                Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = look.Description, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new StackPanel { Children = { picture, label } },
            };
            var id = look.Id;
            tile.MouseLeftButtonUp += (_, _) => Change(r => r with { Look = id });
            lookTiles[look.Id] = tile;
            panel.Children.Add(tile);
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private FrameworkElement Middle()
    {
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("PREVIEW", top: 0));
        panel.Children.Add(new Border
        {
            Child = new Image { Source = previewer.Bitmap, Stretch = Stretch.Uniform },
            CornerRadius = new CornerRadius(6), ClipToBounds = true, BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1), Background = Brushes.Black,
        });
        var scrub = new DockPanel { Margin = new Thickness(0, 8, 0, 6) };
        DockPanel.SetDock(positionText, Dock.Right);
        scrub.Children.Add(positionText);
        scrub.Children.Add(position);
        position.ToolTip = "Drag to look at any moment; the preview plays on from there";
        position.ValueChanged += (_, e) =>
        {
            if (!updating) previewer.Seek(e.NewValue);
        };
        panel.Children.Add(scrub);
        panel.Children.Add(summary);

        panel.Children.Add(EditorKit.Section("BACKGROUND"));
        background.Items.Add(new ComboBoxItem { Content = "The look's own", Tag = null });
        foreach (var style in MotionStyles.All) background.Items.Add(new ComboBoxItem { Content = style.Name, Tag = style.Id });
        palette.Items.Add(new ComboBoxItem { Content = "Its own colours", Tag = null });
        foreach (var preset in MotionPalette.Presets) palette.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset.Name });
        background.SelectionChanged += (_, _) => Change(r => r with { Background = (background.SelectedItem as ComboBoxItem)?.Tag as string });
        palette.SelectionChanged += (_, _) => Change(r => r with { Palette = (palette.SelectedItem as ComboBoxItem)?.Tag as string });
        var pickers = new StackPanel { Orientation = Orientation.Horizontal };
        pickers.Children.Add(background);
        pickers.Children.Add(new TextBlock { Text = "in", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
        pickers.Children.Add(palette);
        panel.Children.Add(pickers);
        panel.Children.Add(EditorKit.Muted("Any of the motion backgrounds, in any palette. The preview is small, silent and runs live; "
            + "Make draws every frame at 1920 × 1080.", 11, 0));
        ((TextBlock)panel.Children[^1]).Margin = new Thickness(0, 8, 0, 0);
        return panel;
    }

    // MARK: The form, one per kind

    private void BuildForm()
    {
        updating = true;
        try
        {
            form.Children.Clear();
            var recipe = Recipe;
            switch (kind)
            {
                case OpenerKind.Bumper:
                    form.Children.Add(EditorKit.Section("WORDS", top: 0));
                    form.Children.Add(TextRow("Title", recipe.Title, "The series title", (r, text) => r with { Title = text }));
                    form.Children.Add(TextRow("Subtitle", recipe.Subtitle, "A subtitle or the speaker", (r, text) => r with { Subtitle = text }));
                    form.Children.Add(EditorKit.Section("PICTURE OR LOGO"));
                    form.Children.Add(FileRow(recipe.Picture, "No picture", PictureFilter, (r, path) => r with { Picture = path }));
                    form.Children.Add(EditorKit.Section("LENGTH"));
                    form.Children.Add(SliderRow("Seconds", 5, 15, 1, recipe.Seconds, v => $"{v:0} s", (r, v) => r with { Seconds = v }));
                    break;

                case OpenerKind.Countdown:
                    form.Children.Add(EditorKit.Section("COUNT DOWN FROM", top: 0));
                    form.Children.Add(SliderRow("Minutes", 1, 10, 1, Math.Max(1, recipe.Seconds / 60), v => $"{v:0}:00", (r, v) => r with { Seconds = v * 60 }));
                    form.Children.Add(EditorKit.Section("WORDS"));
                    form.Children.Add(TextRow("Above", recipe.Title, "Service starts in", (r, text) => r with { Title = text }));
                    form.Children.Add(TextRow("Below", recipe.Subtitle, "The church's name (or leave blank)", (r, text) => r with { Subtitle = text }));
                    form.Children.Add(EditorKit.Section("WHEN IT REACHES ZERO"));
                    form.Children.Add(TextRow("End on", recipe.EndTitle, "Welcome", (r, text) => r with { EndTitle = text }));
                    form.Children.Add(EditorKit.Muted("A card that follows the clock for five seconds. Leave it blank to end on zero.", 11));
                    break;

                case OpenerKind.Montage:
                    form.Children.Add(EditorKit.Section("PICTURES AND CLIPS", top: 0));
                    form.Children.Add(ListEditor(recipe.Shots.Select(Path.GetFileName).ToList()!, ref selectedShot, i => selectedShot = i,
                        "+ Add…", AddShotsFromDialog, RemoveShot, MoveShot, 150));
                    form.Children.Add(EditorKit.Muted("Shown in this order, each with a slow move and a dissolve to the next. With more time than pictures, they come round again.", 11));
                    form.Children.Add(EditorKit.Section("MUSIC"));
                    form.Children.Add(FileRow(recipe.Music, "No music: cut evenly, silent", MusicFilter, (r, path) => r with { Music = path }));
                    form.Children.Add(MontageLength(recipe));
                    form.Children.Add(EditorKit.Section("TITLE CARDS"));
                    form.Children.Add(TextRow("First", recipe.Title, "An opening title (or leave blank)", (r, text) => r with { Title = text }));
                    form.Children.Add(TextRow("", recipe.Subtitle, "and a line under it", (r, text) => r with { Subtitle = text }));
                    form.Children.Add(TextRow("Last", recipe.EndTitle, "A closing line (or leave blank)", (r, text) => r with { EndTitle = text }));
                    break;

                default:
                    form.Children.Add(EditorKit.Section("CARDS", top: 0));
                    selectedCard = Math.Clamp(selectedCard, 0, Math.Max(0, recipe.Cards.Count - 1));
                    form.Children.Add(ListEditor(recipe.Cards.Select((c, i) => string.IsNullOrWhiteSpace(c.Title) ? $"Card {i + 1}" : c.Title).ToList(), ref selectedCard,
                        i =>
                        {
                            selectedCard = i;
                            BuildForm();
                            previewer.Seek(i * Recipe.CardSeconds + 0.1);
                        },
                        "+ Card", () => Change(r => r with { Cards = [.. r.Cards, new AnnouncementCard("New announcement")] }, rebuild: true, select: Recipe.Cards.Count),
                        RemoveCard, MoveCard, 110));
                    if (recipe.Cards.Count > 0)
                    {
                        var index = selectedCard;
                        var card = recipe.Cards[index];
                        form.Children.Add(EditorKit.Section($"CARD {index + 1}"));
                        form.Children.Add(TextRow("Title", card.Title, "The headline", (r, text) => WithCard(r, index, c => c with { Title = text })));
                        form.Children.Add(TextRow("Details", card.Body, "When, where, who to ask", (r, text) => WithCard(r, index, c => c with { Body = text }), lines: 4));
                        form.Children.Add(FileRow(card.Picture, "No picture", PictureFilter, (r, path) => WithCard(r, index, c => c with { Picture = path })));
                    }
                    form.Children.Add(EditorKit.Section("TIMING"));
                    form.Children.Add(SliderRow("Each card", 3, 30, 1, recipe.CardSeconds, v => $"{v:0} s", (r, v) => r with { CardSeconds = (int)v }));
                    form.Children.Add(EditorKit.Muted("The loop has no seam: the last card flows into the first, and the background with it.", 11));
                    break;
            }
            form.Children.Add(EditorKit.Section("NAME"));
            form.Children.Add(TextRow("In the bin", recipe.Name ?? "", "Leave blank to name it for you", (r, text) => r with { Name = text.Trim().Length == 0 ? null : text }));
        }
        finally
        {
            updating = false;
        }
    }

    private int selectedShot;

    private static OpenerRecipe WithCard(OpenerRecipe recipe, int index, Func<AnnouncementCard, AnnouncementCard> change)
    {
        if (index < 0 || index >= recipe.Cards.Count) return recipe;
        var cards = recipe.Cards.ToList();
        cards[index] = change(cards[index]);
        return recipe with { Cards = cards };
    }

    private void RemoveCard()
    {
        if (Recipe.Cards.Count <= 1) return;
        var index = selectedCard;
        Change(r => r with { Cards = [.. r.Cards.Where((_, i) => i != index)] }, rebuild: true, select: Math.Max(0, index - 1));
    }

    private void MoveCard(int by)
    {
        var (from, to) = (selectedCard, selectedCard + by);
        if (to < 0 || to >= Recipe.Cards.Count) return;
        Change(r =>
        {
            var cards = r.Cards.ToList();
            (cards[from], cards[to]) = (cards[to], cards[from]);
            return r with { Cards = cards };
        }, rebuild: true, select: to);
    }

    private void AddShotsFromDialog()
    {
        var dialog = new OpenFileDialog { Title = "Choose pictures and video clips", Filter = ShotFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddShots(dialog.FileNames);
    }

    /// <summary>Adds pictures and clips to the montage (the self-test's way in; the Add… button's dialog ends here too).</summary>
    internal void AddShots(IEnumerable<string> paths)
    {
        if (kind != OpenerKind.Montage) Pick(OpenerKind.Montage);
        Change(r => r with { Shots = [.. r.Shots, .. paths] }, rebuild: true);
    }

    private void RemoveShot()
    {
        var index = selectedShot;
        if (index < 0 || index >= Recipe.Shots.Count) return;
        selectedShot = Math.Max(0, index - 1);
        Change(r => r with { Shots = [.. r.Shots.Where((_, i) => i != index)] }, rebuild: true);
    }

    private void MoveShot(int by)
    {
        var (from, to) = (selectedShot, selectedShot + by);
        if (from < 0 || to < 0 || to >= Recipe.Shots.Count) return;
        selectedShot = to;
        Change(r =>
        {
            var shots = r.Shots.ToList();
            (shots[from], shots[to]) = (shots[to], shots[from]);
            return r with { Shots = shots };
        }, rebuild: true);
    }

    private FrameworkElement MontageLength(OpenerRecipe recipe)
    {
        var box = new ComboBox();
        (string Name, double Seconds)[] choices =
        [
            (recipe.Music is null ? "Four seconds a picture" : "As long as the music", 0), ("30 seconds", 30), ("1 minute", 60), ("1½ minutes", 90), ("2 minutes", 120),
            ("3 minutes", 180), ("5 minutes", 300),
        ];
        foreach (var (name, seconds) in choices) box.Items.Add(new ComboBoxItem { Content = name, Tag = seconds });
        box.SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => c.Seconds == recipe.Seconds));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: double seconds }) Change(r => r with { Seconds = seconds });
        };
        return EditorKit.Row("Length", box, 76);
    }

    // MARK: Small builders

    private DockPanel TextRow(string label, string text, string hint, Func<OpenerRecipe, string, OpenerRecipe> set, int lines = 1)
    {
        var box = new TextBox { Text = text, Tag = hint, ToolTip = hint, VerticalContentAlignment = lines > 1 ? VerticalAlignment.Top : VerticalAlignment.Center };
        if (lines > 1)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = lines * 19 + 8;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        box.TextChanged += (_, _) => Change(r => set(r, box.Text));
        return EditorKit.Row(label, box, 76);
    }

    private DockPanel SliderRow(string label, double min, double max, double step, double value, Func<double, string> show, Func<OpenerRecipe, double, OpenerRecipe> set)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, IsSnapToTickEnabled = true, TickFrequency = step,
            Value = Math.Clamp(value, min, max), VerticalAlignment = VerticalAlignment.Center,
        };
        var shown = new TextBlock
        {
            Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText"), FontSize = 12,
            Text = show(slider.Value),
        };
        slider.ValueChanged += (_, e) =>
        {
            shown.Text = show(e.NewValue);
            Change(r => set(r, e.NewValue));
        };
        var line = new DockPanel();
        DockPanel.SetDock(shown, Dock.Right);
        line.Children.Add(shown);
        line.Children.Add(slider);
        return EditorKit.Row(label, line, 76);
    }

    /// <summary>A file's name with Choose… and a way to clear it.</summary>
    private FrameworkElement FileRow(string? path, string none, string filter, Func<OpenerRecipe, string?, OpenerRecipe> set)
    {
        var missing = path is not null && !File.Exists(path);
        var name = new TextBlock
        {
            Text = path is null ? none : Path.GetFileName(path) + (missing ? " (can't be found)" : ""), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path, FontSize = 12,
            Foreground = path is null ? EditorKit.Res<Brush>("MutedText") : missing ? new SolidColorBrush(Color.FromRgb(0xF5, 0x8B, 0x7A)) : EditorKit.Res<Brush>("Text"),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (path is not null) buttons.Children.Add(EditorKit.Ghost("✕", "Take it away", () => Change(r => set(r, null), rebuild: true)));
        buttons.Children.Add(EditorKit.Button("Choose…", () =>
        {
            var dialog = new OpenFileDialog { Filter = filter };
            if (dialog.ShowDialog(this) == true) Change(r => set(r, dialog.FileName), rebuild: true);
        }));
        ((Button)buttons.Children[^1]).Margin = new Thickness(0);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(name);
        return row;
    }

    /// <summary>A list with add, remove and move buttons under it.</summary>
    private static FrameworkElement ListEditor(List<string> names, ref int selected, Action<int> select, string addText, Action add, Action remove, Action<int> move, double height)
    {
        selected = Math.Clamp(selected, 0, Math.Max(0, names.Count - 1));
        var list = new ListBox { Height = height, Margin = new Thickness(0, 0, 0, 4) };
        for (var i = 0; i < names.Count; i++) list.Items.Add(new ListBoxItem { Content = $"{i + 1}.  {names[i]}" });
        if (names.Count > 0) list.SelectedIndex = selected;
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0) select(list.SelectedIndex);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        buttons.Children.Add(EditorKit.Button(addText, add));
        buttons.Children.Add(EditorKit.Button("Remove", remove));
        buttons.Children.Add(EditorKit.Ghost("↑", "Move it earlier", () => move(-1)));
        buttons.Children.Add(EditorKit.Ghost("↓", "Move it later", () => move(+1)));
        return new StackPanel { Children = { list, buttons } };
    }

    // MARK: Changing the recipe

    /// <summary>The recipe being made, as typed (the self-test reads it).</summary>
    internal OpenerRecipe Recipe => drafts[kind];

    /// <summary>The recipe laid out in time, as the preview shows it.</summary>
    internal OpenerPlan Plan => plan;

    /// <summary>The live preview and the looks' stills (the self-test checks they move).</summary>
    internal OpenerPreviewer Preview => previewer;

    /// <summary>The line under the progress bar.</summary>
    internal string Status => status.Text;

    /// <summary>The line under the preview: how long, how many shots, whether it's cut on the beat.</summary>
    internal string Summary => summary.Text;

    /// <summary>A smaller picture than the recipe's, for the self-test's quick renders.</summary>
    internal (int Width, int Height)? SizeOverride { get; set; }

    /// <summary>True while a montage's music is being read for its beats.</summary>
    internal bool FindingBeats => analysing is not null;

    /// <summary>Switches to another of the four kinds; each keeps what you typed into it.</summary>
    internal void Pick(OpenerKind next)
    {
        kind = next;
        // What was said about the last opener made doesn't describe this one.
        if (job is null)
        {
            status.Text = "";
            showNow.Visibility = Visibility.Collapsed;
        }
        BuildForm();
        Refresh();
    }

    /// <summary>Changes the recipe as the controls would (the self-test's way in).</summary>
    internal void Adjust(Func<OpenerRecipe, OpenerRecipe> change) => Change(change, rebuild: true);

    private void Change(Func<OpenerRecipe, OpenerRecipe> change, bool rebuild = false, int? select = null)
    {
        if (updating) return;
        drafts[kind] = change(drafts[kind]);
        if (select is { } index) selectedCard = index;
        if (rebuild) BuildForm();
        Refresh();
    }

    /// <summary>Lays the recipe out again and shows it everywhere: the preview, the stills, the pickers, the summary.</summary>
    private void Refresh()
    {
        var recipe = Recipe;
        // A montage's music has to be read for its length and beats before it can be laid out.
        BeatAnalysis? found = null;
        if (recipe is { Kind: OpenerKind.Montage, Music: { } track })
        {
            if (music is { } known && known.Path == track) found = known.Found;
            else if (analysing != track) _ = Analyse(track);
        }
        plan = OpenerPlan.For(recipe, found?.Music);
        previewer.Show(plan, found?.Music, recipe);

        updating = true;
        try
        {
            foreach (var (k, button) in kindButtons) button.IsChecked = k == kind;
            foreach (var (id, tile) in lookTiles) tile.BorderBrush = id == plan.Look.Id ? EditorKit.Res<Brush>("Accent") : Brushes.Transparent;
            background.SelectedItem = background.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == recipe.Background);
            palette.SelectedItem = palette.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == recipe.Palette);
            position.Maximum = plan.Seconds;
        }
        finally
        {
            updating = false;
        }

        var text = $"{Clock(plan.Seconds)} · {plan.FrameCount:N0} frames at {plan.Recipe.Width} × {plan.Recipe.Height}";
        if (kind == OpenerKind.Montage)
        {
            var missing = recipe.Shots.Count(s => !File.Exists(s));
            text += plan.Shots.Count == 0 ? " · add at least one picture"
                : $" · {plan.Shots.Count} shots"
                  + (analysing is not null ? " · finding the beat…"
                      : recipe.Music is null ? ", cut evenly"
                      : found is null ? " · the music can't be read"
                      : plan.OnBeats ? $", cut on the beat ({found.BeatsPerMinute:0} a minute)"
                      : found.Beats.Count == 0 ? ", cut evenly (the music has no clear beat)" : ", cut near the beat");
            if (missing > 0) text += $" · {missing} file{(missing == 1 ? "" : "s")} can't be found";
        }
        else if (plan.Seconds > 45)
        {
            text += ". Making it takes about as long as it plays.";
        }
        summary.Text = text;
        if (job is null) make.IsEnabled = replace.IsEnabled = kind != OpenerKind.Montage || plan.Shots.Count > 0;
    }

    private async Task Analyse(string path)
    {
        analysing = path;
        var found = await model.AnalyseMusicAsync(path);
        if (closed) return;
        music = (path, found);
        if (analysing == path) analysing = null;
        Refresh();
    }

    private void OnPlayed(double seconds)
    {
        if (closed) return;
        updating = true;
        // Not while the mouse has hold of it: dragging decides where the preview is.
        if (!position.IsMouseCaptureWithin) position.Value = seconds;
        updating = false;
        positionText.Text = $"{Clock(seconds)} / {Clock(plan.Seconds)}";
        // The show can go live (or clear) while this window is open: keep the warning true.
        ShowLiveWarning();
    }

    private static string Clock(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

    private void ShowLiveWarning() => liveWarning.Visibility = model.ShowIsLive && job is null ? Visibility.Visible : Visibility.Collapsed;

    // MARK: Making it

    /// <summary>
    /// Makes the video: a new item, or (<paramref name="replaceExisting"/>, when editing) a new
    /// file for the item being edited. Returns the item, or null when cancelled or failed.
    /// </summary>
    internal async Task<MediaItem?> MakeAsync(bool replaceExisting)
    {
        if (model.OpenerJob is not null || model.MotionJob is not null)
        {
            status.Text = "Something else is being made; wait for it, or cancel it, first.";
            return null;
        }
        // The beats have to be in before the cuts can be planned.
        for (var i = 0; i < 600 && analysing is not null; i++) await Task.Delay(50);
        var started = model.MakeOpener(Recipe, replaceExisting ? editing : null, SizeOverride);
        if (started is null) return null;
        return await Follow(started);
    }

    private async Task<MediaItem?> Follow(OpenerJob running)
    {
        job = running;
        Busy(true);
        status.Text = $"Making “{running.Replacing?.Name ?? running.Recipe.Name ?? running.Recipe.SuggestedName()}”…";
        void OnProgress(MotionRenderProgress p)
        {
            progress.Value = p.Fraction;
            status.Text = $"Frame {p.Frames:N0} of {p.FrameCount:N0} · {p.FramesPerSecond:0.#} frames a second"
                + (p.Remaining is { } left ? $" · {MainViewModel.Describe(left)} left" : "");
        }
        running.Progressed += OnProgress;
        if (running.Latest is { } latest) OnProgress(latest);
        try
        {
            var item = await running.Done;
            made = item;
            status.Text = item is not null
                ? $"Done: “{item.Name}” is in the Media bin" + (running.Recipe.Loops ? ", set to loop." : ".")
                : running.Failure is { } why ? $"Couldn't make it: {why}" : "Stopped. Nothing was added.";
            showNow.Visibility = item is not null ? Visibility.Visible : Visibility.Collapsed;
            return item;
        }
        finally
        {
            running.Progressed -= OnProgress;
            job = null;
            if (!closed) Busy(false);
        }
    }

    private void Busy(bool busy)
    {
        // While it's being made the preview slows, leaving the PC to the work.
        previewer.Quiet = busy;
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        make.IsEnabled = replace.IsEnabled = !busy && (kind != OpenerKind.Montage || plan.Shots.Count > 0);
        if (busy) showNow.Visibility = Visibility.Collapsed;
        ShowLiveWarning();
    }

    private void ShowMade()
    {
        if (made is not null && model.Library.Get<MediaItem>(made.Id) is { } item) model.FireMedia(item);
    }

    private void Leave()
    {
        // Closing doesn't stop a render: it carries on, and its progress shows in the notice bar.
        closed = true;
        previewer.Played -= OnPlayed;
        previewer.Dispose();
    }
}
