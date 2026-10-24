using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Stage panel: each stage screen (confidence monitor) with the layout it shows, so the
/// band's monitor can show chords while the pastor's shows the clock; a preview of one of them
/// (click a screen's name to preview it); and the stage message. Port of
/// Sources/Modules/ConfidenceModule.swift (screens and Stage Message). Windows has no stage
/// layout editor yet, so the Mac's "Edit Layout in Library" isn't offered.
/// </summary>
public sealed class StagePanel : UserControl
{
    private sealed record LayoutChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly StackPanel screens = new();
    private readonly SceneView preview = new() { PlaysVideo = false, PlaybackScope = "stage-preview" };
    private readonly TextBlock status = new() { FontSize = 11, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private MainViewModel? model;

    public StagePanel()
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "STAGE", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(badge);
        header.Children.Add(new TextBlock { Text = "Confidence monitors", Style = EditorKit.Res<Style>("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

        var frame = new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Brushes.Black, Margin = new Thickness(0, 4, 0, 0), Child = preview };
        preview.SetBinding(HeightProperty, new Binding(nameof(ActualWidth)) { Source = preview, Converter = new SixteenByNine() });

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(screens);
        panel.Children.Add(frame);
        panel.Children.Add(StageMessage());
        Content = panel;
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null)
        {
            model.StageLayoutsChanged -= Rebuild;
            model.PropertyChanged -= OnModelChanged;
        }
        model = next;
        if (model is null) return;
        preview.Media = model.Media;
        // The view model draws the stage into this preview with every stage refresh.
        model.StagePreview = preview;
        model.StageLayoutsChanged += Rebuild;
        model.PropertyChanged += OnModelChanged;
        Rebuild();
        ShowStatus();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.StageMessageStatus)) ShowStatus();
        if (e.PropertyName is nameof(MainViewModel.StagePreviewScreenId)) Rebuild();
    }

    /// <summary>The stage screens' names, in order (the self-test reads these).</summary>
    internal List<string> ScreenNames { get; } = [];

    internal void Rebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Rebuild);
            return;
        }
        screens.Children.Clear();
        ScreenNames.Clear();
        if (model is null) return;
        var rows = model.StageScreenRows();
        if (rows.Count == 0)
        {
            screens.Children.Add(EditorKit.Muted("No confidence monitors. Give a screen the Stage job in Screens & Settings › More screens.", 11, 4));
            return;
        }
        List<LayoutChoice> choices = [new(null, "Built-in Layout"), .. model.StageLayouts.Select(l => new LayoutChoice(l.Id, l.Name))];
        var previewing = model.StagePreviewScreenId is { } chosen && rows.Any(r => r.Id == chosen) ? chosen : rows[0].Id;
        foreach (var row in rows)
        {
            ScreenNames.Add(row.Name);
            var picker = new ComboBox { ItemsSource = choices, MinWidth = 150, MaxWidth = 190, ToolTip = $"The layout {row.Name} shows" };
            picker.SelectedItem = choices.FirstOrDefault(c => c.Id == row.LayoutId) ?? choices[0];
            var id = row.Id;
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is LayoutChoice choice) model.SetStageLayout(id, choice.Id);
            };
            var name = new TextBlock
            {
                Text = row.Name, FontSize = 12, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand, ToolTip = "Preview this screen below",
                Foreground = id == previewing && rows.Count > 1 ? EditorKit.Res<Brush>("Accent") : EditorKit.Res<Brush>("Text"),
            };
            name.MouseLeftButtonUp += (_, _) => model.StagePreviewScreenId = id;
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(picker, Dock.Right);
            line.Children.Add(picker);
            line.Children.Add(name);
            screens.Children.Add(line);
        }
    }

    // MARK: Stage message

    private StackPanel StageMessage()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "STAGE MESSAGE", Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(0, 0, 0, 4) });
        var box = new TextBox { Tag = "Message to stage…", ToolTip = "e.g. Wrap up in 5. {Sermon} shows a timer" };
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.AlertText)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            model?.ShowAlertCommand.Execute(null);
            e.Handled = true;
        };
        panel.Children.Add(box);
        var audience = new CheckBox { Content = "Audience too", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Also show it to the room" };
        audience.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(MainViewModel.AlertOnAudience)));
        var send = new Button { Content = "Send", Style = EditorKit.Res<Style>("AccentButton"), Margin = new Thickness(0, 0, 6, 0) };
        send.SetBinding(Button.CommandProperty, new Binding(nameof(MainViewModel.ShowAlertCommand)));
        var clear = new Button { Content = "Clear", ToolTip = "Take the message down (F6)" };
        clear.Click += (_, _) => model?.DismissAlert();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { audience, send, clear } };
        panel.Children.Add(buttons);
        status.Foreground = EditorKit.Res<Brush>("MutedText");
        panel.Children.Add(status);
        return panel;
    }

    private void ShowStatus()
    {
        status.Text = model?.StageMessageStatus ?? "";
        status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
