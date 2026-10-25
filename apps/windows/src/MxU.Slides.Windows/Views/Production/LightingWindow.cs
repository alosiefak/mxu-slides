using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Core.Automation;
using MxU.Slides.Production.Actions;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Dmx;
using MxU.Slides.Production.Lighting;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Production;

/// <summary>
/// Lighting looks and cues: build looks with a slider per channel (32 to a page) or per group
/// of channels, put them in a cue list and step through it with GO, and the grand master,
/// blackout and All off. With "Show on the rig while I edit" on, the look being edited goes
/// straight to the lights as you move the sliders (the lighting desk's "programmer": it
/// overrides everything until you turn it off or close the window).
/// <para>
/// The looks belong to the library (its Windows extras), so every Windows PC with the same
/// library has them; which network node each universe goes to is this PC's (Settings ›
/// Production). Edits save themselves a moment after the last change. The levels themselves
/// are worked out by the Production project's <see cref="DmxMixer"/>, which has the tests.
/// </para>
/// </summary>
internal sealed class LightingWindow : Window
{
    private const int PerPage = 32;

    private static LightingWindow? current;

    private readonly MainViewModel model;
    private readonly ListBox looks = new() { MinHeight = 150, MaxHeight = 260 };
    private readonly ListBox cues = new() { MinHeight = 120, MaxHeight = 220 };
    private readonly StackPanel editor = new() { Margin = new Thickness(16, 0, 0, 0) };
    private readonly Slider master = new() { Minimum = 0, Maximum = 100, Width = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock masterText = new() { Width = 46, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
    private readonly ToggleButton blackout = new() { Content = "Blackout", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "All brightness to zero at once (colours and positions stay)" };
    private readonly StackPanel dmxLine = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox cueFade;
    private readonly DispatcherTimer saveSoon;
    private readonly DispatcherTimer refresh;
    private LightingShow show;
    private string? lookId;
    private int universe = 1;
    private int page;
    private bool live = true;
    private bool building;

    private LightingWindow(Window? owner, MainViewModel model)
    {
        this.model = model;
        show = model.Production.Lighting.Show;
        lookId = show.Looks.FirstOrDefault()?.Id;
        EditorKit.Dress(this, owner, "Lighting looks and cues", 1000, 700);
        saveSoon = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher);
        saveSoon.Stop();
        refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);

        // Left: the looks and the cue list.
        var left = new StackPanel { Width = 290 };
        left.Children.Add(EditorKit.Section("LOOKS", 0));
        looks.SelectionChanged += (_, _) =>
        {
            if (building || looks.SelectedItem is not ListBoxItem { Tag: string id }) return;
            lookId = id;
            page = 0;
            universe = show.Look(id)?.Levels.FirstOrDefault()?.Universe ?? universe;
            BuildEditor();
            Program();
        };
        left.Children.Add(looks);
        var lookTools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        lookTools.Children.Add(EditorKit.Button("+ New", () => NewLook(), "A new look, all channels out"));
        lookTools.Children.Add(EditorKit.Button("Duplicate", DuplicateLook));
        lookTools.Children.Add(EditorKit.Button("Delete", DeleteLook));
        left.Children.Add(lookTools);

        left.Children.Add(EditorKit.Section("CUE LIST", 16));
        cues.MouseDoubleClick += (_, _) =>
        {
            if (cues.SelectedItem is ListBoxItem { Tag: string id }) Go(id);
        };
        cues.SelectionChanged += (_, _) => ShowCueFade();
        left.Children.Add(cues);
        var cueTools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        cueTools.Children.Add(EditorKit.Button("+ Add the look", () => AddCue(), "Adds the look being edited at the end of the cue list"));
        cueTools.Children.Add(EditorKit.Ghost("", "Move up", () => MoveCue(-1)));
        cueTools.Children.Add(EditorKit.Ghost("", "Move down", () => MoveCue(1)));
        cueTools.Children.Add(EditorKit.Ghost("", "Remove the cue", RemoveCue));
        foreach (var tool in cueTools.Children.OfType<Button>().Skip(1)) tool.FontFamily = EditorKit.IconFont;
        left.Children.Add(cueTools);
        cueFade = EditorKit.CommitBox("", typed =>
        {
            if (SelectedCue() is not { } cue) return "";
            cue.FadeSeconds = typed.Trim().Length == 0 ? null : ProductionEditing.ParseSeconds(typed, 0, 600);
            Changed();
            Rebuild();
            return cue.FadeSeconds is { } f ? f.ToString("0.##", CultureInfo.CurrentCulture) : "";
        }, "the look's", 80);
        left.Children.Add(EditorKit.Row("Cue's fade (s)", cueFade, 110));
        var goRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var go = EditorKit.Button("GO", () => Go(), "The next cue (double-click a cue to go straight to it)", accent: true);
        go.MinWidth = 120;
        go.MinHeight = 36;
        go.FontSize = 16;
        goRow.Children.Add(go);
        goRow.Children.Add(EditorKit.Button("Back", Back, "The cue before"));
        left.Children.Add(goRow);

        // Bottom: the grand master, blackout, all off, and where the DMX goes.
        var bottom = new DockPanel { Margin = new Thickness(16, 8, 16, 12), LastChildFill = true };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(new TextBlock { Text = "GRAND MASTER", Style = EditorKit.Res<Style>("SectionLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        master.ValueChanged += (_, e) =>
        {
            masterText.Text = $"{Math.Round(e.NewValue).ToString(CultureInfo.CurrentCulture)} %";
            if (!building) model.Production.Lighting.SetGrandMaster(Math.Round(e.NewValue));
        };
        controls.Children.Add(master);
        controls.Children.Add(masterText);
        blackout.Click += (_, _) => model.Production.Lighting.SetBlackout(blackout.IsChecked == true);
        controls.Children.Add(blackout);
        controls.Children.Add(EditorKit.Button("All off", () => model.Production.Lighting.AllOff(3), "Fades every look out over 3 s"));
        DockPanel.SetDock(controls, Dock.Left);
        bottom.Children.Add(controls);
        dmxLine.Margin = new Thickness(16, 0, 0, 0);
        bottom.Children.Add(dmxLine);

        var liveBox = new CheckBox { Content = "Show on the rig while I edit", IsChecked = true, Margin = new Thickness(0, 0, 0, 10), ToolTip = "The look being edited goes to the lights as you move the sliders, over everything else" };
        liveBox.Click += (_, _) =>
        {
            live = liveBox.IsChecked == true;
            Program();
        };

        var right = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var columns = new DockPanel { Margin = new Thickness(16, 14, 16, 0) };
        DockPanel.SetDock(left, Dock.Left);
        columns.Children.Add(left);
        var rightSide = new DockPanel();
        var liveHolder = new Border { Child = liveBox, Margin = new Thickness(16, 0, 0, 0) };
        DockPanel.SetDock(liveHolder, Dock.Top);
        rightSide.Children.Add(liveHolder);
        rightSide.Children.Add(right);
        columns.Children.Add(rightSide);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Height = 1, Background = EditorKit.Res<Brush>("Border"), Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Bottom });
        root.Children.Add(columns);
        Content = root;

        Rebuild();
        Refresh();
        refresh.Start();
        Closed += (_, _) =>
        {
            refresh.Stop();
            SaveNow();
            model.Production.Lighting.SetProgrammer(null);
            if (current == this) current = null;
        };
        Loaded += (_, _) => Program();
    }

    /// <summary>Opens the window (or brings the open one forward).</summary>
    public static LightingWindow Open(Window? owner, MainViewModel model)
    {
        if (current is { IsVisible: true })
        {
            current.Activate();
            return current;
        }
        current = new LightingWindow(owner, model);
        current.Show();
        return current;
    }

    // MARK: Looks

    private LightingLook? Look => show.Look(lookId);

    internal LightingLook NewLook(string? name = null)
    {
        var look = new LightingLook { Name = name ?? $"Look {show.Looks.Count + 1}" };
        show.Looks.Add(look);
        lookId = look.Id;
        page = 0;
        Changed();
        Rebuild();
        return look;
    }

    private void DuplicateLook()
    {
        if (Look is not { } look) return;
        var copy = MxU.Slides.Core.Json.MxuJson.Clone(look);
        copy.Id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        copy.Name = look.Name + " Copy";
        show.Looks.Insert(show.Looks.IndexOf(look) + 1, copy);
        lookId = copy.Id;
        Changed();
        Rebuild();
    }

    private void DeleteLook()
    {
        if (Look is not { } look) return;
        var used = show.Cues.Count(c => c.LookId == look.Id);
        var question = used > 0 ? $"Delete \"{look.Name}\" and the {used} cue{(used == 1 ? "" : "s")} that use{(used == 1 ? "s" : "")} it?" : $"Delete \"{look.Name}\"?";
        if (MessageBox.Show(this, question + " Production steps that fire it will be skipped.", "Delete look", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        show.Cues.RemoveAll(c => c.LookId == look.Id);
        show.Looks.Remove(look);
        lookId = show.Looks.FirstOrDefault()?.Id;
        Changed();
        Rebuild();
    }

    /// <summary>Sets one channel of the look being edited (the self-test and the sliders).</summary>
    internal void SetChannel(int channel, int? value)
    {
        if (Look is not { } look) return;
        look.SetLevel(universe, channel, value);
        Changed();
        Program();
    }

    internal void SelectLook(string id)
    {
        lookId = id;
        Rebuild();
        Program();
    }

    // MARK: The cue list

    private LightingCue? SelectedCue() => cues.SelectedItem is ListBoxItem { Tag: string id } ? show.Cues.FirstOrDefault(c => c.Id == id) : null;

    internal void AddCue()
    {
        if (Look is not { } look) return;
        show.Cues.Add(new LightingCue { LookId = look.Id });
        Changed();
        Rebuild();
    }

    private void MoveCue(int by)
    {
        if (SelectedCue() is not { } cue) return;
        var index = show.Cues.IndexOf(cue);
        var target = index + by;
        if (target < 0 || target >= show.Cues.Count) return;
        (show.Cues[index], show.Cues[target]) = (show.Cues[target], show.Cues[index]);
        Changed();
        Rebuild();
        cues.SelectedIndex = target;
    }

    private void RemoveCue()
    {
        if (SelectedCue() is not { } cue) return;
        show.Cues.Remove(cue);
        Changed();
        Rebuild();
    }

    private void ShowCueFade() =>
        cueFade.Text = SelectedCue()?.FadeSeconds is { } fade ? fade.ToString("0.##", CultureInfo.CurrentCulture) : "";

    internal bool Go(string? cueId = null)
    {
        SaveNow();
        var done = model.Production.Lighting.Go(cueId);
        if (!done) model.Notice = "The cue list is at its end";
        Refresh();
        return done;
    }

    private void Back()
    {
        SaveNow();
        model.Production.Lighting.Back();
        Refresh();
    }

    // MARK: Drawing

    private void Rebuild()
    {
        building = true;
        looks.Items.Clear();
        foreach (var look in show.Looks)
        {
            looks.Items.Add(new ListBoxItem
            {
                Tag = look.Id, IsSelected = look.Id == lookId,
                Content = $"{(look.Name.Length > 0 ? look.Name : "Untitled look")}   ·   {look.Levels.Count} channel{(look.Levels.Count == 1 ? "" : "s")}",
            });
        }
        var selected = cues.SelectedItem is ListBoxItem { Tag: string sel } ? sel : null;
        cues.Items.Clear();
        foreach (var cue in show.Cues)
            cues.Items.Add(new ListBoxItem { Tag = cue.Id, IsSelected = cue.Id == selected, Content = CueText(cue) });
        building = false;
        BuildEditor();
        MarkCurrentCue();
    }

    private string CueText(LightingCue cue)
    {
        var look = show.Look(cue.LookId);
        var fade = cue.FadeSeconds ?? look?.FadeSeconds ?? 0;
        return $"{ProductionEditing.CueName(show, cue.Id)}   ·   {fade.ToString("0.##", CultureInfo.CurrentCulture)} s";
    }

    private void MarkCurrentCue()
    {
        var now = model.Production.Lighting.CurrentCue?.Id;
        foreach (ListBoxItem item in cues.Items)
        {
            var isNow = item.Tag as string == now;
            item.FontWeight = isNow ? FontWeights.SemiBold : FontWeights.Normal;
            item.Foreground = isNow ? EditorKit.Res<Brush>("Good") : EditorKit.Res<Brush>("Text");
        }
    }

    private void BuildEditor()
    {
        building = true;
        editor.Children.Clear();
        if (Look is not { } look)
        {
            editor.Children.Add(EditorKit.Muted("No look chosen. Press + New to make one: set its channels with the sliders, then fire it from a slide, a combo or the cue list.", 12, 8));
            building = false;
            return;
        }
        var id = look.Id;
        var name = EditorKit.CommitBox(look.Name, typed =>
        {
            look.Name = typed.Trim();
            Changed();
            Rebuild();
            return look.Name;
        }, "Look name");
        name.FontSize = 16;
        name.Margin = new Thickness(0, 0, 0, 8);
        editor.Children.Add(name);
        var fade = EditorKit.CommitBox(look.FadeSeconds.ToString("0.##", CultureInfo.CurrentCulture), typed =>
        {
            if (ProductionEditing.ParseSeconds(typed, 0, 600) is { } seconds) look.FadeSeconds = seconds;
            Changed();
            return look.FadeSeconds.ToString("0.##", CultureInfo.CurrentCulture);
        }, width: 60);
        var fadeRow = new StackPanel { Orientation = Orientation.Horizontal };
        fadeRow.Children.Add(fade);
        fadeRow.Children.Add(new TextBlock { Text = "seconds, when a step or cue doesn't give its own", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
        editor.Children.Add(EditorKit.Row("Fade", fadeRow, 80));

        var fire = new WrapPanel { Margin = new Thickness(80, 2, 0, 6) };
        fire.Children.Add(EditorKit.Button("Fire", () => FireLook(id, null, false), "Crossfade to this look over its fade"));
        fire.Children.Add(EditorKit.Button("Fire now", () => FireLook(id, 0, false), "Straight to this look, no fade"));
        fire.Children.Add(EditorKit.Button("Add on top", () => FireLook(id, null, true), "Fade it in over what's showing (brightest wins)"));
        fire.Children.Add(EditorKit.Button("Release", () =>
        {
            SaveNow();
            model.Production.Lighting.ReleaseLook(id);
        }, "Fade it out again"));
        editor.Children.Add(fire);

        var universes = Enumerable.Range(1, 8).Union(show.Looks.SelectMany(l => l.Levels.Select(v => v.Universe))).Union(show.Groups.Select(g => g.Universe)).Order()
            .Select(u => new EditorChoice(u.ToString(CultureInfo.InvariantCulture), $"Universe {u}")).ToList();
        editor.Children.Add(EditorKit.Row("Universe", EditorKit.Picker(universes, universe.ToString(CultureInfo.InvariantCulture), picked =>
        {
            universe = int.Parse(picked, CultureInfo.InvariantCulture);
            page = 0;
            BuildEditor();
        }, minWidth: 140), 80));

        editor.Children.Add(EditorKit.Section("GROUPS", 12));
        foreach (var group in show.Groups.Where(g => g.Universe == universe)) editor.Children.Add(GroupRow(look, group));
        editor.Children.Add(NewGroupRow());

        var header = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        var first = page * PerPage + 1;
        var last = Math.Min(512, first + PerPage - 1);
        nav.Children.Add(EditorKit.Ghost("‹", "The channels before", () => Page(-1), 14));
        nav.Children.Add(new TextBlock { Text = $"Channels {first}–{last}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        nav.Children.Add(EditorKit.Ghost("›", "The channels after", () => Page(1), 14));
        nav.Children.Add(EditorKit.Button("Leave these out", () =>
        {
            for (var c = first; c <= last; c++) look.SetLevel(universe, c, null);
            Changed();
            Program();
            BuildEditor();
        }, "Takes these channels out of the look (brightness then goes out; attributes stay as they were)"));
        DockPanel.SetDock(nav, Dock.Right);
        header.Children.Add(nav);
        header.Children.Add(EditorKit.Section("CHANNELS", 0));
        editor.Children.Add(header);
        var grid = new UniformGrid { Columns = 16 };
        for (var channel = first; channel <= last; channel++) grid.Children.Add(ChannelCell(look, channel));
        editor.Children.Add(grid);
        editor.Children.Add(EditorKit.Muted("A channel the look doesn't use shows \"–\": move its slider to add it. Right-click a slider to leave its channel out.", 10.5, 0));
        building = false;
    }

    private void Page(int by)
    {
        page = Math.Clamp(page + by, 0, (512 / PerPage) - 1);
        BuildEditor();
    }

    private StackPanel ChannelCell(LightingLook look, int channel)
    {
        var level = look.Level(universe, channel);
        var cell = new StackPanel { Margin = new Thickness(2, 0, 2, 8) };
        var value = new TextBlock { Text = level?.ToString(CultureInfo.InvariantCulture) ?? "–", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10.5 };
        var slider = new Slider
        {
            Orientation = Orientation.Vertical, Minimum = 0, Maximum = 255, Height = 110, Value = level ?? 0, HorizontalAlignment = HorizontalAlignment.Center,
            SmallChange = 1, LargeChange = 16, IsMoveToPointEnabled = true, Opacity = level is null ? 0.45 : 1,
            ToolTip = $"Channel {channel} of universe {universe}",
        };
        var attribute = show.AttributeChannels().Contains((universe, channel));
        var number = new TextBlock
        {
            Text = channel.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10.5,
            Foreground = attribute ? EditorKit.Res<Brush>("Accent") : EditorKit.Res<Brush>("MutedText"),
            ToolTip = attribute ? "An attribute (colour, position…): latest takes precedence, not dimmed by the grand master" : null,
        };
        slider.ValueChanged += (_, e) =>
        {
            if (building) return;
            var v = (int)Math.Round(e.NewValue);
            value.Text = v.ToString(CultureInfo.InvariantCulture);
            slider.Opacity = 1;
            SetChannel(channel, v);
        };
        slider.ContextMenu = new ContextMenu();
        slider.ContextMenu.Items.Add(EditorKit.Command("Leave this channel out", () =>
        {
            SetChannel(channel, null);
            BuildEditor();
        }));
        slider.ContextMenu.Items.Add(EditorKit.Command("Full (255)", () =>
        {
            SetChannel(channel, 255);
            BuildEditor();
        }));
        cell.Children.Add(value);
        cell.Children.Add(slider);
        cell.Children.Add(number);
        return cell;
    }

    private DockPanel GroupRow(LightingLook look, ChannelGroup group)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var levels = group.Channels.Select(c => look.Level(group.Universe, c)).ToList();
        var shown = levels.Where(l => l is not null).Select(l => l!.Value).DefaultIfEmpty(0).Max();
        var value = new TextBlock { Text = levels.Any(l => l is not null) ? shown.ToString(CultureInfo.InvariantCulture) : "–", Width = 34, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        var remove = EditorKit.Ghost("", "Remove the group (its channels stay in the looks)", () =>
        {
            show.Groups.Remove(group);
            Changed();
            BuildEditor();
        });
        remove.FontFamily = EditorKit.IconFont;
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        var name = EditorKit.CommitBox(group.Name, typed =>
        {
            group.Name = typed.Trim();
            Changed();
            return group.Name;
        }, "Group name", 130);
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        var channels = EditorKit.CommitBox(ChannelList.Format(group.Channels), typed =>
        {
            group.Channels = ChannelList.Parse(typed);
            Changed();
            BuildEditor();
            return ChannelList.Format(group.Channels);
        }, "1-6", 80);
        channels.Margin = new Thickness(6, 0, 0, 0);
        channels.ToolTip = "Its channels, e.g. 1-6, 9";
        DockPanel.SetDock(channels, Dock.Left);
        row.Children.Add(channels);
        var attribute = new CheckBox { Content = "Attribute", IsChecked = group.Attribute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), ToolTip = "Colour, position, gobo…: latest takes precedence and the grand master leaves it alone" };
        attribute.Click += (_, _) =>
        {
            group.Attribute = attribute.IsChecked == true;
            Changed();
            BuildEditor();
        };
        DockPanel.SetDock(attribute, Dock.Left);
        row.Children.Add(attribute);
        var slider = new Slider { Minimum = 0, Maximum = 255, Value = shown, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, SmallChange = 1, LargeChange = 16 };
        slider.ValueChanged += (_, e) =>
        {
            if (building) return;
            var v = (int)Math.Round(e.NewValue);
            value.Text = v.ToString(CultureInfo.InvariantCulture);
            foreach (var c in group.Channels) look.SetLevel(group.Universe, c, v);
            Changed();
            Program();
        };
        // The channel sliders below show the new levels once the drag ends.
        slider.PreviewMouseUp += (_, _) => BuildEditor();
        row.Children.Add(slider);
        return row;
    }

    private StackPanel NewGroupRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var name = new TextBox { Width = 130, Tag = "New group's name" };
        var channels = new TextBox { Width = 80, Tag = "1-6", Margin = new Thickness(6, 0, 6, 0) };
        row.Children.Add(name);
        row.Children.Add(channels);
        row.Children.Add(EditorKit.Button("+ Add group", () =>
        {
            var list = ChannelList.Parse(channels.Text);
            if (list.Count == 0)
            {
                MessageBox.Show(this, "Type the group's channels, e.g. 1-6 or 1, 3, 5.", "New group");
                return;
            }
            show.Groups.Add(new ChannelGroup { Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : $"Channels {ChannelList.Format(list)}", Universe = universe, Channels = list });
            Changed();
            BuildEditor();
        }, $"A group in universe {universe}: one slider sets all its channels"));
        return row;
    }

    // MARK: Doing

    private void FireLook(string id, double? fade, bool onTop)
    {
        SaveNow();
        model.Production.Lighting.FireLook(id, fade, onTop);
    }

    /// <summary>Sends the look being edited to the rig while "Show on the rig" is on; lets it go when it's off.</summary>
    private void Program() =>
        model.Production.Lighting.SetProgrammer(live && Look is { } look ? look.Levels : null);

    private void Changed()
    {
        saveSoon.Stop();
        saveSoon.Start();
    }

    /// <summary>Saves the looks, cues and groups to the library now (the lights pick them up at once).</summary>
    internal void SaveNow()
    {
        saveSoon.Stop();
        try { model.Production.SaveLighting(show.Copy()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            model.Problem = $"Couldn't save the lighting: {ex.Message}";
        }
    }

    /// <summary>Five times a second: the cue marker, the master, blackout and the DMX nodes' lights.</summary>
    private void Refresh()
    {
        var lights = model.Production.Lighting;
        building = true;
        if (!master.IsMouseCaptureWithin) master.Value = Math.Round(lights.GrandMaster * 100);
        masterText.Text = $"{Math.Round(master.Value).ToString(CultureInfo.CurrentCulture)} %";
        blackout.IsChecked = lights.Blackout;
        blackout.Background = lights.Blackout ? EditorKit.Res<Brush>("Live") : null;
        building = false;
        MarkCurrentCue();
        dmxLine.Children.Clear();
        var nodes = model.Production.Devices.OfType<DmxOutput>().ToList();
        if (nodes.Count == 0)
        {
            dmxLine.Children.Add(ProductionUi.Line("No DMX node is set up on this PC: add one in Settings › Production. The looks still work here, and on PCs that have one."));
            return;
        }
        foreach (var node in nodes)
        {
            dmxLine.Children.Add(ProductionUi.Dot(node.Status));
            dmxLine.Children.Add(new TextBlock { Text = node.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), ToolTip = node.Status.Detail });
        }
    }
}
