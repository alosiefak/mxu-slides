using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "Motion background…" in the Media bin: make a seamless looping animated background. On the
/// left, the gallery of styles, each a small live preview; in the middle, a bigger live preview
/// of what you're making (with sample lyrics over it, to judge whether text will read); on the
/// right, its colours, motion and size. <b>Render</b> draws every frame and files the video into
/// the Media bin (MainViewModel.MotionBackgrounds.cs does the work).
/// <para>
/// Opened with "Edit motion background…" on a tile made here, it starts from that background's
/// recipe, and offers to replace it (same item, so every slide using it changes) or save the
/// change as a new one.
/// </para>
/// Built in code, like the other editors (EditorKit). Windows only: the Mac has no twin.
/// </summary>
internal sealed partial class MotionBackgroundWindow : Window
{
    private const int PreviewWidth = 480;
    private const int PreviewHeight = 270;
    private const int TileWidth = 144;
    private const int TileHeight = 81;

    private readonly MainViewModel model;
    private readonly MediaItem? editing;
    private readonly MotionPreviewer previewer;
    private readonly MotionPreviewer.Target preview;
    private readonly Dictionary<string, (Border Frame, MotionPreviewer.Target Target)> tiles = [];
    private readonly List<(Border Chip, MotionPalette Palette)> chips = [];
    private readonly WrapPanel colourRow = new();
    private readonly List<ColourSlot> slots = [];
    private readonly TextBlock styleName = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
    private readonly TextBlock styleBlurb = EditorKit.Muted("", 12, 0);
    private readonly Slider speed = Slider(MotionRecipe.MinSpeed, MotionRecipe.MaxSpeed, 0.05);
    private readonly Slider intensity = Slider(0, 1, 0.01);
    private readonly Slider length = Slider(MotionRecipe.MinSeconds, MotionRecipe.MaxSeconds, 1);
    private readonly TextBlock speedValue = Value();
    private readonly TextBlock intensityValue = Value();
    private readonly TextBlock lengthValue = Value();
    private readonly ToggleButton hd = Segment("1080p", "1920 × 1080");
    private readonly ToggleButton uhd = Segment("4K", "3840 × 2160: four times the pixels, so about four times as long to make");
    private readonly ToggleButton fps30 = Segment("30 fps", "30 frames a second");
    private readonly ToggleButton fps60 = Segment("60 fps", "60 frames a second: smoother, twice the frames to make");
    private readonly TextBlock variationValue = Value();
    private readonly TextBox name = new() { Tag = "Name in the Media bin", VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock estimate = EditorKit.Muted("", 11, 0);
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button render;
    private readonly Button replace;
    private readonly Button cancel;
    private readonly Button showNow;
    private readonly Grid lyrics = new();
    private readonly CheckBox showLyrics = new() { Content = "Show sample lyrics over it", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private MotionRecipe recipe;
    private bool paletteChosen;
    private bool nameTyped;
    private bool updating;
    private MotionBackgroundJob? job;
    private MediaItem? made;
    private bool closed;

    private MotionBackgroundWindow(Window? owner, MainViewModel model, MediaItem? editing, MotionRecipe? start)
    {
        this.model = model;
        this.editing = editing;
        EditorKit.Dress(this, owner, editing is null ? "Make a motion background" : $"Edit “{editing.Name}”", 1220, 800);
        recipe = start?.Normalized() ?? MotionRecipe.For(MotionStyles.All[0]);
        // Opening a recipe keeps its colours and name; a new one follows each style's own palette.
        paletteChosen = start is not null;
        nameTyped = start is not null;
        previewer = new MotionPreviewer(Dispatcher);
        preview = previewer.Add(PreviewWidth, PreviewHeight, everyRound: true);

        render = EditorKit.Button(editing is null ? "Render" : "Save as New", () => _ = RenderAsync(replaceExisting: false),
            "Draws every frame and adds the video to the Media bin", accent: editing is null);
        render.MinWidth = 110;
        replace = EditorKit.Button(editing is null ? "" : $"Replace “{editing.Name}”", () => _ = RenderAsync(replaceExisting: true),
            "Gives the existing background the new look, everywhere it's used", accent: true);
        replace.Visibility = editing is null ? Visibility.Collapsed : Visibility.Visible;
        cancel = EditorKit.Button("Cancel", () => job?.Cancel(), "Stops making it; nothing is added");
        cancel.Visibility = Visibility.Collapsed;
        showNow = EditorKit.Button("Show it now", ShowMade, "Puts the new background behind whatever is live");
        showNow.Visibility = Visibility.Collapsed;

        var columns = new Grid { Margin = new Thickness(16, 14, 16, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileWidth * 2 + 40) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        var gallery = Gallery();
        var middle = Middle();
        var options = new ScrollViewer { Content = Options(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(options, 2);
        columns.Children.Add(gallery);
        columns.Children.Add(middle);
        columns.Children.Add(options);

        var footer = new DockPanel { Margin = new Thickness(16, 10, 16, 14), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { showNow, cancel, render, replace }) buttons.Children.Add(button);
        replace.Margin = new Thickness(0);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        var progressColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        progressColumn.Children.Add(status);
        progressColumn.Children.Add(progress);
        footer.Children.Add(progressColumn);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(columns);
        Content = root;

        updating = true;
        name.Text = recipe.Name ?? editing?.Name ?? recipe.SuggestedName();
        updating = false;
        ShowRecipe();
        Closed += (_, _) => Leave();
        // A background already being made (this window was closed and opened again): show its progress.
        if (model.MotionJob is { } running) _ = Follow(running);
    }

    /// <summary>Opens the window to make a new background, or (with <paramref name="edit"/>) to change one made here.</summary>
    public static MotionBackgroundWindow Open(Window? owner, MainViewModel model, MediaItem? edit = null)
    {
        if (Application.Current.Windows.OfType<MotionBackgroundWindow>().FirstOrDefault(w => w.editing?.Id == edit?.Id) is { } open)
        {
            open.Activate();
            return open;
        }
        var window = new MotionBackgroundWindow(owner, model, edit, edit is null ? null : model.MotionRecipeOf(edit));
        window.Show();
        return window;
    }

    // MARK: The three columns

    private FrameworkElement Gallery()
    {
        var panel = new DockPanel();
        var title = EditorKit.Section("STYLES", top: 0);
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        var wrap = new WrapPanel();
        foreach (var style in MotionStyles.All)
        {
            var target = previewer.Add(TileWidth, TileHeight, everyRound: false);
            var picture = new Border
            {
                CornerRadius = new CornerRadius(4), ClipToBounds = true, Width = TileWidth, Height = TileHeight,
                Child = new Image { Source = target.Bitmap, Stretch = Stretch.UniformToFill },
            };
            var label = new TextBlock { Text = style.Name, FontSize = 11, Margin = new Thickness(2, 3, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis };
            var frame = new Border
            {
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Padding = new Thickness(2), Margin = new Thickness(0, 0, 6, 6),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = style.Description,
                Child = new StackPanel { Children = { picture, label } },
            };
            var id = style.Id;
            frame.MouseLeftButtonUp += (_, _) => PickStyle(id);
            tiles[style.Id] = (frame, target);
            wrap.Children.Add(frame);
        }
        panel.Children.Add(new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return panel;
    }

    private FrameworkElement Middle()
    {
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("PREVIEW", top: 0));
        // Sample lyrics, drawn on a 1920 × 1080 stage scaled to fit, so they sit as they would on screen.
        var words = new TextBlock
        {
            Text = "Amazing grace, how sweet the sound\nthat saved a wretch like me",
            FontSize = 84, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, LineHeight = 108,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.6 },
        };
        lyrics.Width = 1920;
        lyrics.Height = 1080;
        lyrics.Children.Add(words);
        var stage = new Grid();
        stage.Children.Add(new Image { Source = preview.Bitmap, Stretch = Stretch.Uniform });
        stage.Children.Add(new Viewbox { Child = lyrics, Stretch = Stretch.Uniform });
        panel.Children.Add(new Border
        {
            Child = stage, CornerRadius = new CornerRadius(6), ClipToBounds = true, BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1),
            Background = Brushes.Black,
        });
        showLyrics.Checked += (_, _) => lyrics.Visibility = Visibility.Visible;
        showLyrics.Unchecked += (_, _) => lyrics.Visibility = Visibility.Hidden;
        panel.Children.Add(showLyrics);
        panel.Children.Add(styleName);
        panel.Children.Add(styleBlurb);
        panel.Children.Add(EditorKit.Muted("Every background loops seamlessly: its last frame flows straight into its first. "
            + "The preview is small and runs live; the render draws every frame at full size.", 11, 0));
        return panel;
    }

    private FrameworkElement Options()
    {
        var panel = new StackPanel();

        panel.Children.Add(EditorKit.Section("COLOURS", top: 0));
        var presets = new WrapPanel();
        foreach (var palette in MotionPalette.Presets)
        {
            var stripes = new UniformGrid { Rows = 1, Width = 52, Height = 22 };
            foreach (var hex in palette.Colors) stripes.Children.Add(new Border { Background = Painting.Brush(hex + "FF", SceneColor.Black) });
            var chip = new Border
            {
                Child = new Border { Child = stripes, CornerRadius = new CornerRadius(3), ClipToBounds = true }, BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(5), Padding = new Thickness(1), Margin = new Thickness(0, 0, 4, 4), ToolTip = palette.Name,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var picked = palette;
            chip.MouseLeftButtonUp += (_, _) => PickPalette(picked.Name);
            chips.Add((chip, palette));
            presets.Children.Add(chip);
        }
        panel.Children.Add(presets);
        panel.Children.Add(EditorKit.Muted("Or your own: two to four colours. The darkest is the background.", 11, 4));
        panel.Children.Add(colourRow);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                EditorKit.Ghost("+ Colour", "Add a colour (up to four)", () => ChangeColourCount(+1), 11),
                EditorKit.Ghost("− Colour", "Remove the last colour (two at least)", () => ChangeColourCount(-1), 11),
            },
        });

        panel.Children.Add(EditorKit.Section("MOTION"));
        panel.Children.Add(SliderRow("Speed", speed, speedValue));
        panel.Children.Add(SliderRow("Intensity", intensity, intensityValue));
        var shuffle = EditorKit.Button("Shuffle", () => Change(r => r with { Variation = Random.Shared.Next(1, 10_000) }),
            "Another variation of the same look: everything moves somewhere else");
        panel.Children.Add(EditorKit.Row("Variation", new DockPanel { Children = { shuffle, variationValue } }, 76));

        panel.Children.Add(EditorKit.Section("VIDEO"));
        panel.Children.Add(SliderRow("Length", length, lengthValue));
        panel.Children.Add(EditorKit.Row("Size", Segments(hd, uhd), 76));
        panel.Children.Add(EditorKit.Row("Frame rate", Segments(fps30, fps60), 76));
        panel.Children.Add(EditorKit.Row("Name", name, 76));
        panel.Children.Add(estimate);

        speed.ValueChanged += (_, e) => Change(r => r with { Speed = Math.Round(e.NewValue, 2) });
        intensity.ValueChanged += (_, e) => Change(r => r with { Intensity = Math.Round(e.NewValue, 2) });
        length.ValueChanged += (_, e) => Change(r => r with { Seconds = (int)Math.Round(e.NewValue) });
        hd.Click += (_, _) => Change(r => r with { Height = 1080 });
        uhd.Click += (_, _) => Change(r => r with { Height = 2160 });
        fps30.Click += (_, _) => Change(r => r with { FrameRate = 30 });
        fps60.Click += (_, _) => Change(r => r with { FrameRate = 60 });
        name.TextChanged += (_, _) =>
        {
            if (updating) return;
            nameTyped = name.Text.Trim().Length > 0;
            recipe = recipe with { Name = nameTyped ? name.Text.Trim() : null };
        };
        return panel;
    }

    // MARK: Changing the recipe

    /// <summary>The recipe as it stands (the self-test reads it).</summary>
    internal MotionRecipe Recipe => recipe;

    /// <summary>The live preview (the self-test checks it moves).</summary>
    internal MotionPreviewer.Target Preview => preview;

    /// <summary>The gallery's preview of a style.</summary>
    internal MotionPreviewer.Target Tile(string styleId) => tiles[styleId].Target;

    /// <summary>The line under the progress bar.</summary>
    internal string Status => status.Text;

    /// <summary>A smaller picture than the recipe's, for the self-test's quick render.</summary>
    internal (int Width, int Height)? SizeOverride { get; set; }

    internal void PickStyle(string id)
    {
        if (MotionStyles.Find(id) is not { } style) return;
        Change(r =>
        {
            var next = r with { Style = id };
            // Until you pick colours yourself, each style comes in the palette it was designed for.
            if (!paletteChosen && MotionPalette.Find(style.DefaultPalette) is { } own) next = next with { Palette = own.Name, Colors = own.Colors };
            return next;
        });
    }

    internal void PickPalette(string name)
    {
        if (MotionPalette.Find(name) is not { } palette) return;
        paletteChosen = true;
        Change(r => r with { Palette = palette.Name, Colors = palette.Colors });
    }

    /// <summary>Changes the recipe as the controls would (the self-test's way in).</summary>
    internal void Adjust(Func<MotionRecipe, MotionRecipe> change) => Change(change);

    /// <summary>Your own colours: they replace the preset's.</summary>
    internal void UseColours(IReadOnlyList<string> colours)
    {
        paletteChosen = true;
        Change(r => r with { Palette = null, Colors = [.. colours] });
    }

    private void ChangeColourCount(int by)
    {
        var colours = recipe.Colors.ToList();
        if (by > 0 && colours.Count < MotionPalette.MaxColors) colours.Add(colours.Count > 0 ? colours[^1] : "#FFFFFF");
        else if (by < 0 && colours.Count > MotionPalette.MinColors) colours.RemoveAt(colours.Count - 1);
        else return;
        UseColours(colours);
    }

    private void Change(Func<MotionRecipe, MotionRecipe> change)
    {
        if (updating) return;
        recipe = change(recipe).Normalized();
        if (!nameTyped) recipe = recipe with { Name = null };
        ShowRecipe();
    }

    /// <summary>Puts the recipe into every control and every preview.</summary>
    private void ShowRecipe()
    {
        updating = true;
        try
        {
            var style = MotionStyles.Find(recipe.Style)!;
            foreach (var (id, (frame, target)) in tiles)
            {
                frame.BorderBrush = id == style.Id ? EditorKit.Res<Brush>("Accent") : Brushes.Transparent;
                // The gallery shows each style in your colours once you've picked some, otherwise in its own.
                var tileStyle = MotionStyles.Find(id)!;
                var tileRecipe = paletteChosen
                    ? recipe with { Style = id, Variation = 1 }
                    : MotionRecipe.For(tileStyle) with { Speed = recipe.Speed, Intensity = recipe.Intensity };
                if (target.Recipe is null || !MotionRecipesMatch(target.Recipe, tileRecipe)) target.Recipe = tileRecipe;
            }
            preview.Recipe = recipe;
            styleName.Text = style.Name;
            styleBlurb.Text = style.Description;
            foreach (var (chip, palette) in chips)
                chip.BorderBrush = recipe.Palette == palette.Name ? EditorKit.Res<Brush>("Accent") : Brushes.Transparent;
            ShowColours();
            speed.Value = recipe.Speed;
            speedValue.Text = $"{recipe.Speed:0.##}×";
            intensity.Value = recipe.Intensity;
            intensityValue.Text = $"{recipe.Intensity:P0}";
            length.Value = recipe.Seconds;
            lengthValue.Text = $"{recipe.Seconds} s";
            hd.IsChecked = recipe.Height == 1080;
            uhd.IsChecked = recipe.Height == 2160;
            fps30.IsChecked = recipe.FrameRate == 30;
            fps60.IsChecked = recipe.FrameRate == 60;
            variationValue.Text = $"  #{recipe.Variation}";
            if (!nameTyped) name.Text = editing?.Name ?? recipe.SuggestedName();
            ShowEstimate();
        }
        finally
        {
            updating = false;
        }
    }

    private static bool MotionRecipesMatch(MotionRecipe a, MotionRecipe b) =>
        a.Style == b.Style && a.Palette == b.Palette && a.Colors.SequenceEqual(b.Colors) && a.Speed == b.Speed && a.Intensity == b.Intensity
        && a.Seconds == b.Seconds && a.Variation == b.Variation;

    private void ShowColours()
    {
        while (slots.Count < recipe.Colors.Count)
        {
            var slot = new ColourSlot(OnColourPicked);
            var picker = new ColorPicker { Margin = new Thickness(0, 0, 4, 4), Width = 134 };
            picker.SetBinding(ColorPicker.HexProperty, new Binding(nameof(ColourSlot.Hex)) { Source = slot, Mode = BindingMode.TwoWay });
            slot.Picker = picker;
            slots.Add(slot);
        }
        colourRow.Children.Clear();
        for (var i = 0; i < recipe.Colors.Count; i++)
        {
            slots[i].Show(recipe.Colors[i]);
            colourRow.Children.Add(slots[i].Picker!);
        }
    }

    private void OnColourPicked()
    {
        if (updating) return;
        UseColours([.. slots.Take(recipe.Colors.Count).Select(s => s.Hex.Length >= 7 ? s.Hex[..7] : s.Hex)]);
    }

    private void ShowEstimate()
    {
        var megabytes = recipe.EstimatedBytes() / 1_000_000.0;
        var text = $"{recipe.Width} × {recipe.Height}, {recipe.FrameCount} frames, about {megabytes:0} MB.";
        // How long it takes depends on the style and the PC, but it grows with the pixels drawn per
        // second of video: say how much bigger a job than 1080p at 30 this is.
        var times = recipe.Width * (double)recipe.Height * recipe.FrameRate / (1920 * 1080 * 30);
        if (times > 1.5) text += $" That's {times:0}× the drawing of 1080p at 30 fps, so expect it to take about {times:0} times as long.";
        estimate.Text = text;
    }

    // MARK: Rendering

    /// <summary>
    /// Makes the video: a new item, or (<paramref name="replaceExisting"/>, when editing) a new
    /// file for the item being edited. Returns the item, or null when cancelled or failed.
    /// </summary>
    internal async Task<MediaItem?> RenderAsync(bool replaceExisting)
    {
        if (model.MotionJob is not null)
        {
            status.Text = "Another motion background is being made; wait for it, or cancel it, first.";
            return null;
        }
        var started = model.MakeMotionBackground(recipe, replaceExisting ? editing : null, SizeOverride);
        if (started is null) return null;
        return await Follow(started);
    }

    private async Task<MediaItem?> Follow(MotionBackgroundJob running)
    {
        job = running;
        Busy(true);
        status.Text = $"Making “{running.Replacing?.Name ?? running.Recipe.Name ?? running.Recipe.SuggestedName()}”…";
        void OnProgress(Broadcast.Library.MotionRenderProgress p)
        {
            progress.Value = p.Fraction;
            status.Text = $"Frame {p.Frames} of {p.FrameCount} · {p.FramesPerSecond:0.#} frames a second"
                + (p.Remaining is { } left ? $" · {MainViewModel.Describe(left)} left" : "");
        }
        running.Progressed += OnProgress;
        if (running.Latest is { } latest) OnProgress(latest);
        try
        {
            var item = await running.Done;
            made = item;
            status.Text = item is not null
                ? $"Done: “{item.Name}” is in the Media bin, set to loop."
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
        // While a render runs the gallery stops and the preview slows, leaving the PC to the render.
        previewer.Quiet = busy;
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        render.IsEnabled = replace.IsEnabled = !busy;
        if (busy) showNow.Visibility = Visibility.Collapsed;
    }

    private void ShowMade()
    {
        if (made is not null && model.Library.Get<MediaItem>(made.Id) is { } item) model.FireMedia(item);
    }

    private void Leave()
    {
        // Closing doesn't stop a render: it carries on, and its progress shows in the notice bar.
        closed = true;
        previewer.Dispose();
    }

    // MARK: Small builders

    private static Slider Slider(double min, double max, double step) => new()
    {
        Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, IsSnapToTickEnabled = step >= 1, TickFrequency = step,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Value() => new()
    {
        Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText"), FontSize = 12,
    };

    private static DockPanel SliderRow(string label, Slider slider, TextBlock value)
    {
        var line = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(value);
        line.Children.Add(slider);
        return EditorKit.Row(label, line, 76);
    }

    private static ToggleButton Segment(string text, string tooltip) => new() { Content = text, ToolTip = tooltip, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 4, 0) };

    private static StackPanel Segments(params ToggleButton[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    /// <summary>One custom colour's picker, bound to this so a change comes back to the window.</summary>
    private sealed partial class ColourSlot(Action changed) : ObservableObject
    {
        [ObservableProperty]
        public partial string Hex { get; set; } = "#FFFFFFFF";

        public ColorPicker? Picker { get; set; }

        private bool showing;

        /// <summary>Shows a recipe colour ("#RRGGBB") without calling back.</summary>
        public void Show(string colour)
        {
            showing = true;
            Hex = (colour.Length == 7 ? colour + "FF" : colour).ToUpper(CultureInfo.InvariantCulture);
            showing = false;
        }

        partial void OnHexChanged(string value)
        {
            if (!showing) changed();
        }
    }
}
