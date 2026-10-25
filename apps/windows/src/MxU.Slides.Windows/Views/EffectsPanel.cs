using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Effects section: a card per effect (bypass switch, name, Move up / down, remove, and its
/// sliders with a box to type the number), Add Effect and Presets menus, and a warning when this
/// PC can't draw some of them. Drag a card by its grip onto another to reorder (effects run top to
/// bottom, so order changes the picture). Give it an <see cref="EffectsEditorViewModel"/> as its
/// DataContext. Built in code, like the automation editors (Views/EditorKit.cs).
/// </summary>
public sealed class EffectsPanel : UserControl
{
    private const string DragFormat = "mxu/effect-index";

    private readonly StackPanel cards = new();
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox applyBelow = new()
    {
        Content = "Apply to content below", Margin = new Thickness(0, 4, 0, 0),
        ToolTip = "The shape's effects change everything under it, like an adjustment layer (a Mac draws this; Windows draws them on the shape itself for now)",
    };

    private EffectsEditorViewModel? vm;
    private Point dragStart;
    private int? dragFrom;

    public EffectsPanel()
    {
        var add = new Button { Content = "Add effect ▾", Margin = new Thickness(0, 0, 6, 0), ToolTip = "Blur, colour adjust, vignette, warp, stained glass…" };
        add.Click += (_, _) => ShowAddMenu(add);
        var presets = new Button { Content = "Presets ▾", ToolTip = "The Mac's recommended looks (Frost, Heat Haze…) and the ones you saved" };
        presets.Click += (_, _) => ShowPresetsMenu(presets);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { add, presets } };

        note.Foreground = EditorKit.Res<Brush>("Warn");
        applyBelow.Checked += (_, _) => { if (vm is not null && !refreshing) vm.ApplyBelow = true; };
        applyBelow.Unchecked += (_, _) => { if (vm is not null && !refreshing) vm.ApplyBelow = false; };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "EFFECTS", Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(0, 10, 0, 4) });
        panel.Children.Add(cards);
        panel.Children.Add(buttons);
        panel.Children.Add(note);
        panel.Children.Add(applyBelow);
        Content = panel;
        DataContextChanged += (_, e) => Attach(e.NewValue as EffectsEditorViewModel);
    }

    private void Attach(EffectsEditorViewModel? next)
    {
        if (vm is not null) vm.ChainChanged -= Rebuild;
        vm = next;
        if (vm is not null) vm.ChainChanged += Rebuild;
        Rebuild();
    }

    private bool refreshing;

    /// <summary>Draws the cards again from the chain (after a change from here, or the owner's <see cref="Rebuild"/>).</summary>
    public void Rebuild()
    {
        cards.Children.Clear();
        if (vm is null) return;
        var chain = vm.Chain;
        if (chain.Count == 0)
            cards.Children.Add(EditorKit.Muted("No effects. Add one to blur, tint, warp or texture the picture.", 11, 4));
        for (var i = 0; i < chain.Count; i++) cards.Children.Add(Card(chain[i], i, chain.Count));
        note.Text = vm.SoftwareNote ?? "";
        note.Visibility = string.IsNullOrEmpty(note.Text) ? Visibility.Collapsed : Visibility.Visible;
        refreshing = true;
        applyBelow.IsChecked = vm.ApplyBelow;
        refreshing = false;
        applyBelow.Visibility = vm.ShowsApplyBelow ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The number of effect cards showing (the self-test reads it).</summary>
    internal int CardCount => vm?.Chain.Count ?? 0;

    private Border Card(Effect effect, int index, int count)
    {
        var on = effect.Enabled != false;
        var grip = new TextBlock
        {
            Text = "", FontFamily = EditorKit.IconFont, FontSize = 11, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText"), ToolTip = "Drag onto another effect to reorder",
        };
        grip.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(this);
            dragFrom = index;
        };
        grip.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || dragFrom != index) return;
            var delta = e.GetPosition(this) - dragStart;
            if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            dragFrom = null;
            DragDrop.DoDragDrop(grip, new DataObject(DragFormat, index), DragDropEffects.Move);
        };
        var toggle = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Off bypasses the effect and keeps its settings" };
        toggle.Click += (_, _) => vm?.SetEnabled(index, toggle.IsChecked == true);
        var name = new TextBlock
        {
            Text = effect.EffectKind.DisplayName(), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = EditorKit.Res<Brush>(on ? "Text" : "FaintText"),
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var up = EditorKit.Ghost("↑", "Move up (runs sooner)", () => vm?.Step(index, -1));
        up.IsEnabled = index > 0;
        var down = EditorKit.Ghost("↓", "Move down (runs later)", () => vm?.Step(index, +1));
        down.IsEnabled = index < count - 1;
        actions.Children.Add(up);
        actions.Children.Add(down);
        actions.Children.Add(EditorKit.Ghost("✕", "Remove this effect", () => vm?.Remove(index)));
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(grip);
        header.Children.Add(toggle);
        header.Children.Add(name);

        var body = new StackPanel();
        body.Children.Add(header);
        foreach (var dial in EffectEditing.Dials(effect.EffectKind)) body.Children.Add(DialRow(effect, index, dial, on));
        var card = EditorKit.Card(body, 4);
        card.Padding = new Thickness(8, 6, 8, 4);
        card.AllowDrop = true;
        card.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            card.BorderBrush = e.Effects == DragDropEffects.Move ? EditorKit.Res<Brush>("Accent") : EditorKit.Res<Brush>("Border");
            e.Handled = true;
        };
        card.DragLeave += (_, _) => card.BorderBrush = EditorKit.Res<Brush>("Border");
        card.Drop += (_, e) =>
        {
            card.BorderBrush = EditorKit.Res<Brush>("Border");
            if (e.Data.GetData(DragFormat) is not int from) return;
            // Dropped on its lower half: after this card; on its upper half: before it.
            var after = e.GetPosition(card).Y > card.ActualHeight / 2;
            vm?.Move(from, after ? index + 1 : index);
            e.Handled = true;
        };
        return card;
    }

    private UIElement DialRow(Effect effect, int index, EffectDial dial, bool on)
    {
        var value = EffectEditing.Read(effect, dial.Field, dial.Default);
        var slider = new Slider
        {
            Minimum = dial.Min, Maximum = dial.Max, Value = value, SmallChange = dial.Step, LargeChange = dial.Step * 10,
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = on, Margin = new Thickness(0, 0, 6, 0),
        };
        var box = new TextBox { Width = 52, Text = Format(value), VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = on, TextAlignment = TextAlignment.Right };
        var setting = false;
        slider.ValueChanged += (_, e) =>
        {
            if (setting || vm is null) return;
            setting = true;
            var stored = vm.SetValue(index, dial, e.NewValue);
            box.Text = Format(stored);
            setting = false;
        };
        void Typed()
        {
            if (vm is null) return;
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var typed)
                && !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out typed))
            {
                box.Text = Format(slider.Value);
                return;
            }
            setting = true;
            var stored = vm.SetValue(index, dial, typed);
            slider.Value = stored;
            box.Text = Format(stored);
            setting = false;
        }
        box.LostKeyboardFocus += (_, _) => Typed();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Typed();
            e.Handled = true;
        };
        DockPanel.SetDock(box, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3), Children = { box, slider } };
        return EditorKit.Row(dial.Label, row, labelWidth: 74, bottom: 2);
    }

    private static string Format(double value) => Math.Round(value, 2).ToString(CultureInfo.CurrentCulture);

    // MARK: Menus

    private void ShowAddMenu(FrameworkElement under)
    {
        if (vm is null) return;
        var menu = new ContextMenu();
        foreach (var kind in EffectEditing.Addable)
        {
            var label = kind.DisplayName() + (!EffectsEditorViewModel.CanDrawShaders && EffectEditing.NeedsPixelShader(kind) ? "  (not drawn on this PC)" : "");
            menu.Items.Add(EditorKit.Command(label, () => vm.Add(kind)));
        }
        EditorKit.Open(menu, under);
    }

    private void ShowPresetsMenu(FrameworkElement under)
    {
        if (vm is null) return;
        var menu = new ContextMenu();
        var recommended = new MenuItem { Header = "Recommended", Style = (Style)EditorKit.Styles["CascadingMenuItem"] };
        foreach (var group in vm.Presets.Where(p => !p.IsMine).GroupBy(p => p.Group))
        {
            if (group.Count() == 1) recommended.Items.Add(PresetItem(group.First()));
            else
            {
                var sub = new MenuItem { Header = group.Key, Style = (Style)EditorKit.Styles["CascadingMenuItem"] };
                foreach (var choice in group) sub.Items.Add(PresetItem(choice));
                recommended.Items.Add(sub);
            }
        }
        menu.Items.Add(recommended);
        var mine = vm.Presets.Where(p => p.IsMine).ToList();
        if (mine.Count > 0) menu.Items.Add(EditorKit.Separator());
        foreach (var choice in mine) menu.Items.Add(PresetItem(choice));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(EditorKit.Command("Save as preset…", SaveAsPreset, vm.Chain.Count > 0 && vm.Library is not null));
        EditorKit.Open(menu, under);
    }

    private MenuItem PresetItem(EffectPresetChoice choice)
    {
        var item = new MenuItem { Header = choice.Name, Style = (Style)EditorKit.Styles["CascadingMenuItem"] };
        item.Items.Add(EditorKit.Command("Apply", () => vm?.ApplyPreset(choice.Preset, add: false)));
        item.Items.Add(EditorKit.Command("Add", () => vm?.ApplyPreset(choice.Preset, add: true)));
        if (choice.IsMine)
        {
            item.Items.Add(EditorKit.Command("Save over", () => vm?.SaveOver(choice.Preset), vm?.Chain.Count > 0));
            item.Items.Add(EditorKit.Separator());
            item.Items.Add(EditorKit.Command("Delete", () => vm?.DeletePreset(choice.Preset)));
        }
        return item;
    }

    private void SaveAsPreset()
    {
        if (vm is null || Window.GetWindow(this) is not { } owner) return;
        if (InputDialog.Ask(owner, "New effect preset", "A name for these effects:", "My look") is { } name) vm.SavePreset(name);
    }
}
