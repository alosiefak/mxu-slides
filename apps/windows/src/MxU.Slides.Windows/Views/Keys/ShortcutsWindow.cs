using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Control;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Keys;

/// <summary>
/// Keyboard Shortcuts (Phase 8): every command with its keys, grouped as on the Mac (Show Control,
/// Clear Layers, Video, Create &amp; Library, Application), then the ones that get a key only once
/// they're given one (looks, timers, overlays, previews, action combos), then the group hot keys
/// (C = Chorus…). Click a chip and press the new key; Esc cancels, Backspace removes it. A key used
/// twice where both could fire is marked orange with "Also used by…", and the Conflicts filter lists
/// them. Saved on this PC (settings.json); the group hot keys are the library's (they follow the
/// songs). Port of the Mac's KeyboardMapPane (KeyboardSettingsPane.swift).
/// </summary>
public sealed class ShortcutsWindow : Window
{
    private enum Filter { Assigned, All, Customized, Conflicts, Unused }

    private static ShortcutsWindow? open;

    private readonly MainViewModel model;
    private readonly StackPanel rows = new() { Margin = new Thickness(0, 0, 12, 0) };
    private readonly StackPanel filters = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock message = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00)), FontSize = 12, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private Filter filter = Filter.Assigned;
    private (KeyChip Chip, Action<KeyChord?> Apply, bool LettersOnly)? recording;
    private Button? addCombo;

    /// <summary>Opens the window (or brings it to the front); null in volunteer mode, where shortcuts can't be changed.</summary>
    public static ShortcutsWindow? Open(Window? owner, MainViewModel model)
    {
        if (!model.Permit(OperatorAction.ChangeShortcuts)) return null;
        if (open is null)
        {
            open = new ShortcutsWindow(owner, model);
            open.Closed += (_, _) => open = null;
            open.Show();
        }
        open.Activate();
        return open;
    }

    private ShortcutsWindow(Window? owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Keyboard Shortcuts", 700, 760);
        foreach (var each in Enum.GetValues<Filter>())
        {
            var button = new ToggleButton { Content = each.ToString(), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 4, 0), Tag = each };
            button.Click += (_, _) =>
            {
                filter = each;
                Rebuild();
            };
            filters.Children.Add(button);
        }
        var restore = EditorKit.Button("Restore Defaults…", RestoreDefaults, "Every command back to its own keys (combos, looks, timers and overlays keep theirs)");
        var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(restore, Dock.Right);
        foot.Children.Add(restore);
        foot.Children.Add(EditorKit.Muted("Letters and numbers work only in the main window, and never while typing. On a laptop the F-keys may need Fn.", 11, 0));
        var body = new DockPanel();
        DockPanel.SetDock(filters, Dock.Top);
        DockPanel.SetDock(message, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(filters);
        body.Children.Add(message);
        body.Children.Add(foot);
        body.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        // Padding on a border, not a margin on the panel, so a picture of the window (the self-test's) isn't shifted.
        Content = new Border { Padding = new Thickness(18, 14, 6, 14), Child = body };
        PreviewKeyDown += OnPreviewKeyDown;
        model.KeyMapChanged += OnKeyMapChanged;
        Closed += (_, _) => model.KeyMapChanged -= OnKeyMapChanged;
        Rebuild();
    }

    private void OnKeyMapChanged()
    {
        if (recording is null) Dispatcher.BeginInvoke(Rebuild);
    }

    private KeyCommandMap Map => model.KeyMap;

    // MARK: The rows

    /// <summary>The rows' names as shown, with their keys (the self-test reads these).</summary>
    internal List<string> RowTexts { get; } = [];

    internal void Rebuild()
    {
        rows.Children.Clear();
        RowTexts.Clear();
        foreach (var button in filters.Children.OfType<ToggleButton>()) button.IsChecked = (Filter)button.Tag == filter;
        var conflicted = Map.ConflictedKeys();
        foreach (var group in Enum.GetValues<KeyCommandGroup>())
        {
            var commands = Enum.GetValues<KeyCommand>().Where(c => c.Group() == group && Shows(c, conflicted)).ToList();
            if (commands.Count == 0) continue;
            rows.Children.Add(EditorKit.Section(group.DisplayName().ToUpperInvariant(), rows.Children.Count == 0 ? 0 : 14));
            foreach (var command in commands) rows.Children.Add(CommandRow(command, conflicted));
        }
        foreach (var (title, footer, specs) in model.ShortcutSections())
        {
            var shown = specs.Where(s => Shows(s.Key, conflicted)).ToList();
            if (shown.Count == 0) continue;
            rows.Children.Add(EditorKit.Section(title.ToUpperInvariant()));
            foreach (var spec in shown) rows.Children.Add(GeneratedRow(spec.Key, spec.Label, conflicted));
            if (footer is not null) rows.Children.Add(EditorKit.Muted(footer, 11, 2));
        }
        CombosSection(conflicted);
        HotKeysSection();
    }

    private bool Shows(KeyCommand command, HashSet<string> conflicted) => filter switch
    {
        Filter.All => true,
        Filter.Assigned => Map.ChordsFor(command).Count > 0 || command.FixedKeyDescription() is not null,
        Filter.Customized => Map.IsCustomized(command),
        Filter.Conflicts => conflicted.Contains(command.Raw()),
        _ => Map.ChordsFor(command).Count == 0 && command.FixedKeyDescription() is null,
    };

    private bool Shows(GeneratedKey key, HashSet<string> conflicted)
    {
        var bound = Map.ChordFor(key) is not null;
        return filter switch
        {
            Filter.All => true,
            Filter.Assigned or Filter.Customized => bound,
            Filter.Conflicts => conflicted.Contains(key.MapKey),
            _ => !bound,
        };
    }

    private DockPanel Row(string label, string? caption, KeyCommandScope scope, KeyChip chip, bool customized = false, Action? reset = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4), MinHeight = 26 };
        DockPanel.SetDock(chip, Dock.Right);
        row.Children.Add(chip);
        var scopeText = new TextBlock { Text = scope.DisplayName(), Width = 64, FontSize = 10, Foreground = EditorKit.Res<Brush>("FaintText"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(scopeText, Dock.Right);
        row.Children.Add(scopeText);
        if (reset is not null)
        {
            var undo = EditorKit.Ghost(new TextBlock { Text = "", FontFamily = EditorKit.IconFont, FontSize = 11 }, "Reset to the default", reset);
            DockPanel.SetDock(undo, Dock.Right);
            row.Children.Add(undo);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        if (customized) title.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = EditorKit.Res<Brush>("Accent"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(title);
        if (caption is not null) text.Children.Add(new TextBlock { Text = caption, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00)), TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        return row;
    }

    private DockPanel CommandRow(KeyCommand command, HashSet<string> conflicted)
    {
        var chords = Map.ChordsFor(command);
        var isConflicted = conflicted.Contains(command.Raw());
        var caption = isConflicted ? Caption(command.Raw(), chords, command.Scope()) : null;
        RowTexts.Add($"{command.DisplayName()}: {(command.FixedKeyDescription() ?? string.Join(" or ", chords.Select(c => c.Display)))}");
        KeyChip? chip = null;
        chip = new KeyChip(chords, command.FixedKeyDescription(), isConflicted, () => StartRecording(chip!, chord => model.UpdateKeyMap(map => map.SetChord(chord, command))));
        return Row(command.DisplayName(), caption, command.Scope(), chip, Map.IsCustomized(command),
            Map.IsCustomized(command) ? () => model.UpdateKeyMap(map => map.ResetToDefault(command)) : null);
    }

    private DockPanel GeneratedRow(GeneratedKey key, string label, HashSet<string> conflicted)
    {
        var chord = Map.ChordFor(key);
        var chords = chord is null ? (IReadOnlyList<KeyChord>)[] : [chord];
        var isConflicted = conflicted.Contains(key.MapKey);
        RowTexts.Add($"{label}: {chord?.Display ?? "—"}");
        KeyChip? chip = null;
        chip = new KeyChip(chords, null, isConflicted, () => StartRecording(chip!, chosen => model.UpdateKeyMap(map => map.SetChord(chosen, key))));
        return Row(label, isConflicted ? Caption(key.MapKey, chords, key.Kind.Scope()) : null, key.Kind.Scope(), chip);
    }

    /// <summary>"Also used by Clear All, Start “Sermon”".</summary>
    private string? Caption(string mapKey, IReadOnlyList<KeyChord> chords, KeyCommandScope scope)
    {
        var others = Map.OthersUsing(mapKey, chords, scope).Select(model.ShortcutLabel).ToList();
        return others.Count == 0 ? null : $"Also used by {string.Join(", ", others)}";
    }

    private void CombosSection(HashSet<string> conflicted)
    {
        var bound = Map.BoundIds(GeneratedKeyKind.Combo)
            .Where(id => filter switch
            {
                Filter.Unused => false,
                Filter.Conflicts => conflicted.Contains(new GeneratedKey(GeneratedKeyKind.Combo, id).MapKey),
                _ => true,
            }).ToList();
        var offersAdd = filter is Filter.Assigned or Filter.All;
        if (bound.Count == 0 && !offersAdd) return;
        rows.Children.Add(EditorKit.Section("ACTION COMBOS"));
        foreach (var id in bound) rows.Children.Add(GeneratedRow(new GeneratedKey(GeneratedKeyKind.Combo, id), model.ShortcutLabel(new GeneratedKey(GeneratedKeyKind.Combo, id).MapKey), conflicted));
        if (offersAdd)
        {
            var add = addCombo = EditorKit.Button("Add Combo Shortcut…", () => { });
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Click += (_, _) =>
            {
                var unbound = model.ShortcutCombos().Where(c => Map.ChordFor(new GeneratedKey(GeneratedKeyKind.Combo, c.Id)) is null).ToList();
                List<object> items = unbound.Count == 0
                    ? [EditorKit.Command("Every combo has a shortcut", () => { }, enabled: false)]
                    : [.. unbound.Select(c => (object)EditorKit.Command(c.Name, () => RecordNewCombo(c)))];
                OperatorKit.Open(OperatorKit.Menu(items), add);
            };
            rows.Children.Add(add);
        }
        rows.Children.Add(EditorKit.Muted("Any combo can have a key: a feature with no command of its own is one combo away from a shortcut.", 11, 2));
    }

    /// <summary>Adds a row for a combo with no key yet and starts recording it.</summary>
    private void RecordNewCombo(ActionCombo combo)
    {
        var key = new GeneratedKey(GeneratedKeyKind.Combo, combo.Id);
        KeyChip? chip = null;
        chip = new KeyChip([], null, false, () => StartRecording(chip!, chord => model.UpdateKeyMap(map => map.SetChord(chord, key))));
        var row = Row(combo.Name, null, KeyCommandScope.Present, chip);
        var index = addCombo is null ? -1 : rows.Children.IndexOf(addCombo);
        rows.Children.Insert(index >= 0 ? index : rows.Children.Count, row);
        StartRecording(chip, chord =>
        {
            if (chord is not null) model.UpdateKeyMap(map => map.SetChord(chord, key));
            else Rebuild();
        });
    }

    private void HotKeysSection()
    {
        var groups = model.GroupHotKeyGroups.Where(g => filter switch
        {
            Filter.All => true,
            Filter.Assigned => HotKeyJump.EffectiveHotKey(g) is not null,
            Filter.Customized => g.HotKey is not null,
            Filter.Conflicts => false,
            _ => HotKeyJump.EffectiveHotKey(g) is null,
        }).ToList();
        if (groups.Count == 0) return;
        rows.Children.Add(EditorKit.Section("GROUP HOT KEYS"));
        foreach (var group in groups)
        {
            var letter = HotKeyJump.EffectiveHotKey(group);
            RowTexts.Add($"{group.Name}: {letter?.ToUpperInvariant() ?? "—"}");
            KeyChip? chip = null;
            var id = group.Id;
            chip = new KeyChip(letter is null ? [] : [new KeyChord(letter)], null, false,
                () => StartRecording(chip!, chord => model.SetGroupHotKey(id, chord is null ? "" : chord.Key), lettersOnly: true));
            var row = Row(group.Name, null, KeyCommandScope.Present, chip, group.HotKey is not null,
                group.HotKey is not null ? () => model.SetGroupHotKey(id, null) : null);
            var dot = new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Painting.Brush(group.ColorHex, new SceneColor(0.5, 0.5, 0.5)),
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Insert(0, dot);
            rows.Children.Add(row);
        }
        rows.Children.Add(EditorKit.Muted("A letter jumps the live song to that section; press it again for the next time it's sung. They're the library's, so they follow the songs to every computer and to the Mac.", 11, 2));
    }

    // MARK: Recording a key

    private void StartRecording(KeyChip chip, Action<KeyChord?> apply, bool lettersOnly = false)
    {
        recording = (chip, apply, lettersOnly);
        message.Text = "";
        chip.ShowRecording();
        Keyboard.Focus(this);
    }

    /// <summary>Records the next key as a shortcut: Esc cancels, Backspace or Delete removes it.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (recording is not { } waiting) return;
        var key = KeyTokens.RealKey(e);
        if (KeyTokens.IsModifierOnly(key)) return;
        e.Handled = true;
        if (KeyTokens.Chord(key, Keyboard.Modifiers) is not { } chord) return;
        if (chord.IsBare && chord.Key == "escape")
        {
            recording = null;
            Rebuild();
            return;
        }
        if (chord.IsBare && chord.Key == "delete")
        {
            recording = null;
            waiting.Apply(null);
            Rebuild();
            return;
        }
        if (waiting.LettersOnly && !(chord.IsBare && chord.Key is [var letter] && char.IsAsciiLetter(letter)))
        {
            message.Text = "A group's hot key is a single letter, with no Ctrl, Alt or Shift.";
            return;
        }
        if (KeyCommandMap.ReservedReason(chord) is { } reserved)
        {
            message.Text = $"{reserved}, so it can't be a shortcut. Try another.";
            return;
        }
        recording = null;
        waiting.Apply(chord);
        Rebuild();
    }

    /// <summary>Records a key as if pressed (the self-test drives the window this way).</summary>
    internal void Record(KeyCommand command, KeyChord? chord) => model.UpdateKeyMap(map => map.SetChord(chord, command));

    private void RestoreDefaults()
    {
        if (!Map.HasCustomizedCommands) return;
        if (MessageBox.Show(this, "Put every command back to its own keys? Shortcuts for combos, looks, timers and overlays stay.", "Restore Defaults",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        model.UpdateKeyMap(map => map.RestoreCommandDefaults());
    }

    /// <summary>Shows one filter (the self-test pictures the Conflicts one).</summary>
    internal void ShowFilter(string name)
    {
        filter = Enum.Parse<Filter>(name);
        Rebuild();
    }
}
