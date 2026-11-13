using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MxU.Slides.Assistant.Spotting;
using MxU.Slides.Audio;
using MxU.Slides.Core.Automation;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Assistant;

/// <summary>
/// The scripture spotter's strip at the top of the Bible window: a Listen switch, the sound input
/// it listens to, what it last heard, what recognition is costing the processor, and the
/// passages it has spotted. The newest passage is put in the Bible window's box and the strip
/// flashes; <b>the operator presses Enter to show it</b>. Nothing here puts anything on a screen.
/// (A flash, not a chime: a sound from this PC could come out of the hall's speakers.)
/// </summary>
public sealed class SpotterStrip : UserControl
{
    private readonly MainViewModel model;
    private readonly ToggleButton listen = new() { Content = "Listen for scripture", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock cpu = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock heard = new() { FontSize = 11, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WrapPanel spots = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly Border card;
    private readonly SolidColorBrush flash = new(Colors.Transparent);
    private Spot? newest;

    public SpotterStrip(MainViewModel model)
    {
        this.model = model;
        listen.ToolTip = "Listens to the preacher with Windows' own speech recognizer, on this computer only, and offers each Bible passage named. You press Enter to show one.";
        listen.Click += async (_, _) =>
        {
            if (listen.IsChecked == true) await model.StartSpotterAsync();
            else model.StopSpotter();
        };
        var top = new DockPanel();
        DockPanel.SetDock(listen, Dock.Left);
        top.Children.Add(listen);
        var inputs = InputPicker();
        DockPanel.SetDock(inputs, Dock.Left);
        top.Children.Add(inputs);
        DockPanel.SetDock(cpu, Dock.Right);
        top.Children.Add(cpu);
        top.Children.Add(status);
        var panel = new StackPanel();
        panel.Children.Add(top);
        panel.Children.Add(heard);
        panel.Children.Add(spots);
        card = new Border
        {
            Child = panel, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(6),
            BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1), Background = flash,
        };
        Content = card;
        Loaded += (_, _) =>
        {
            model.SpotterChanged += Draw;
            model.Scripture.Changed += Draw; // which passage is in the box
            Draw();
        };
        Unloaded += (_, _) =>
        {
            model.SpotterChanged -= Draw;
            model.Scripture.Changed -= Draw;
        };
    }

    private ComboBox InputPicker()
    {
        var devices = AudioDeviceList.InputDevices();
        var choices = devices.Count == 0
            ? [new EditorChoice("", "No sound input found")]
            : devices.Select(d => new EditorChoice(d.Uid, d.Name)).ToList();
        var chosen = devices.FirstOrDefault(d => d.Uid == model.Settings.SpotterInputUid)?.Uid ?? choices[0].Id;
        var picker = EditorKit.Picker(choices, chosen, async uid =>
        {
            if (uid.Length == 0 || uid == model.Settings.SpotterInputUid) return;
            model.Settings.SpotterInputUid = uid;
            model.Settings.Save();
            if (model.SpotterListening) await model.StartSpotterAsync();
        }, minWidth: 200);
        picker.MaxWidth = 260;
        picker.Margin = new Thickness(0, 0, 10, 0);
        picker.ToolTip = "The sound input the preacher's microphone comes in on";
        return picker;
    }

    private void Draw()
    {
        listen.IsChecked = model.SpotterListening;
        status.Text = model.SpotterStatus;
        status.Foreground = EditorKit.Res<Brush>("MutedText");
        // The last sentence as the recognizer wrote it, so the operator can see how well it is hearing.
        heard.Text = model.SpotterListening && model.SpotterHeard.Length > 0 ? $"Last heard: “{model.SpotterHeard}”" : "";
        heard.Visibility = heard.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        heard.Foreground = EditorKit.Res<Brush>("FaintText");
        // Of one core, and of the whole PC, so "9%" isn't read as a tenth of the laptop.
        cpu.Text = model.SpotterListening
            ? string.Create(CultureInfo.InvariantCulture, $"Speech recognition: {model.SpotterCpu:0}% of one core ({model.SpotterCpu / Environment.ProcessorCount:0.0}% of this PC){(model.SpotterAsked > 0 ? $" · {model.SpotterAsked} sent to the assistant" : "")}")
            : "";
        cpu.Foreground = EditorKit.Res<Brush>(model.SpotterCpu > 60 ? "Warn" : "FaintText");

        spots.Children.Clear();
        var list = model.Spots;
        for (var i = 0; i < list.Count; i++)
        {
            var spot = list[i];
            var guess = spot.Source == SpotSource.Assistant;
            // The one in the Bible window's box is the one Enter would show.
            var inBox = spot.Display == model.Scripture.Query;
            var button = EditorKit.Button(
                (guess ? $"{spot.Display} ?" : spot.Display) + (inBox ? "   ⏎ Enter shows it" : ""),
                () => model.UseSpot(spot),
                $"{(guess ? "The assistant's guess from" : "Heard")}: “{spot.Heard}” at {spot.When.LocalDateTime:HH:mm:ss}. Click to put it in the box; Enter shows it.",
                accent: inBox);
            button.Margin = new Thickness(0, 0, 6, 4);
            spots.Children.Add(button);
        }
        if (list.Count > 0 && !Equals(list[0], newest))
        {
            newest = list[0];
            // A new passage is waiting: the strip glows amber and fades.
            flash.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(0xB0, 0xE0, 0xA0, 0x20), Colors.Transparent, TimeSpan.FromSeconds(1.6)));
        }
    }
}
