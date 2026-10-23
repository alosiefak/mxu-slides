using System.Windows;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

public partial class SettingsWindow : Window
{
    private sealed record DisplayChoice(string? DeviceName, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record TransitionChoice(OutputTransition Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly MainViewModel model;
    private readonly OutputManager outputs;

    public SettingsWindow(MainViewModel model, OutputManager outputs)
    {
        this.model = model;
        this.outputs = outputs;
        InitializeComponent();
        var s = model.Settings;
        LoadDisplays();
        OutputsAtLaunch.IsChecked = s.OutputsOnAtLaunch;
        PreventSleep.IsChecked = s.PreventSleep;

        TransitionCombo.ItemsSource = new[]
        {
            new TransitionChoice(OutputTransition.Dissolve, "Dissolve"),
            new TransitionChoice(OutputTransition.FadeThroughBlack, "Fade through black"),
            new TransitionChoice(OutputTransition.Cut, "Cut (instant)"),
        };
        TransitionCombo.SelectedItem = ((TransitionChoice[])TransitionCombo.ItemsSource).First(c => c.Value == s.Transition);
        TransitionSeconds.Value = s.TransitionSeconds;

        var themes = model.Library.All<Core.Model.Theme>();
        ThemeCombo.ItemsSource = themes;
        ThemeCombo.SelectedItem = themes.FirstOrDefault(t => t.Id == s.DefaultThemeId);
        LinesCombo.ItemsSource = new[] { 1, 2, 3, 4, 5, 6, 8 };
        LinesCombo.SelectedItem = s.LinesPerSlide;
        Volume.Value = s.VideoVolume;
        LibraryPath.Text = $"Your songs, services and media live in {s.LibraryPath}. Every day the first launch copies the documents into Backups (the last 14 days are kept).";
    }

    private void LoadDisplays()
    {
        var displays = Displays.All();
        var choices = new List<DisplayChoice> { new(null, displays.Count > 1 ? "Off / preview window" : "Preview window (one screen connected)") };
        choices.AddRange(displays.Select(d => new DisplayChoice(d.DeviceName, d.Label)));
        AudienceCombo.ItemsSource = choices;
        StageCombo.ItemsSource = choices;
        AudienceCombo.SelectedItem = choices.FirstOrDefault(c => c.DeviceName == model.Settings.AudienceDisplay) ?? choices[0];
        StageCombo.SelectedItem = choices.FirstOrDefault(c => c.DeviceName == model.Settings.StageDisplay) ?? choices[0];
    }

    private void Identify_Click(object sender, RoutedEventArgs e) => OutputManager.Identify();

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadDisplays();

    private void TransitionSeconds_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        TransitionLabel.Text = $"{e.NewValue:0.0} seconds";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = model.Settings;
        var audience = AudienceCombo.SelectedItem as DisplayChoice;
        var stage = StageCombo.SelectedItem as DisplayChoice;
        if (audience?.DeviceName is not null && audience.DeviceName == stage?.DeviceName)
        {
            MessageBox.Show(this, "Audience and Stage can't use the same display.", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var main = Displays.ForWindow(new System.Windows.Interop.WindowInteropHelper(Owner).Handle);
        if (audience?.DeviceName is { } chosen && chosen == main?.DeviceName
            && MessageBox.Show(this, "The audience output would cover this window's screen, and you'd lose sight of the controls. Use it anyway?",
                "MxU Slides", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        s.AudienceDisplay = audience?.DeviceName;
        s.AudienceDisplayName = audience?.DeviceName is null ? null : audience.Label;
        s.StageDisplay = stage?.DeviceName;
        s.StageDisplayName = stage?.DeviceName is null ? null : stage.Label;
        s.OutputsOnAtLaunch = OutputsAtLaunch.IsChecked == true;
        s.PreventSleep = PreventSleep.IsChecked == true;
        s.Transition = (TransitionCombo.SelectedItem as TransitionChoice)?.Value ?? OutputTransition.Dissolve;
        s.TransitionSeconds = Math.Round(TransitionSeconds.Value, 1);
        if (ThemeCombo.SelectedItem is Core.Model.Theme theme) s.DefaultThemeId = theme.Id;
        if (LinesCombo.SelectedItem is int lines) s.LinesPerSlide = lines;
        s.VideoVolume = Volume.Value;
        s.Save();

        // Outputs that now have a display move there; ones set to preview stay where they are.
        foreach (var role in Enum.GetValues<OutputRole>())
        {
            var device = role == OutputRole.Audience ? s.AudienceDisplay : s.StageDisplay;
            if (device is null && outputs.IsOnDisplay(role)) outputs.Hide(role);
        }
        outputs.Apply();
        DialogResult = true;
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(model.Settings.LibraryPath);

    private void OpenBackups_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(model.Library.BackupDirectory);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(AppPaths.LogDirectory);
}
