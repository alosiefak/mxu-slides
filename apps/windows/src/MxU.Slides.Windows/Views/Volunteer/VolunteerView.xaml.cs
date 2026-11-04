using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MxU.Slides.Core.Control;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Volunteer;

/// <summary>
/// Volunteer mode's layout. It shows only the panels Settings › Present says volunteers keep, and
/// every control in it runs the show: there's nothing here that edits a song, the service or a
/// screen. The way out is the Leave button, which asks for this PC's PIN when one is set.
/// </summary>
public partial class VolunteerView : UserControl
{
    private readonly MainViewModel model;
    private readonly Dictionary<VolunteerPanel, (ToggleButton Tab, FrameworkElement Bin)> bins = [];

    public VolunteerView(MainViewModel model)
    {
        this.model = model;
        DataContext = model;
        InitializeComponent();
        Preview.Media = model.Media;
        foreach (var (panel, bin) in new (VolunteerPanel, FrameworkElement)[]
                 {
                     (VolunteerPanel.Presentations, PresentationBin), (VolunteerPanel.Overlays, OverlayBin), (VolunteerPanel.Media, MediaBin),
                     (VolunteerPanel.Music, MusicBin), (VolunteerPanel.Combos, ComboBin),
                 })
        {
            var tab = new ToggleButton { Content = panel.Title(), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 4, 0) };
            tab.Click += (_, _) => ShowBin(panel);
            BinTabs.Children.Add(tab);
            bins[panel] = (tab, bin);
        }
        ApplyPanels();
        ShowDecks();
        model.VolunteerChanged += ApplyPanels;
        model.Library.Changed += OnLibraryChanged;
        Unloaded += (_, _) =>
        {
            model.VolunteerChanged -= ApplyPanels;
            model.Library.Changed -= OnLibraryChanged;
        };
        Loaded += (_, _) => ShowOutputStatus();
    }

    // MARK: The library's decks

    /// <summary>The decks in the Presentations bin, A to Z, narrowed by the search box (the self-test reads these).</summary>
    internal IReadOnlyList<LibraryEntryViewModel> ShownDecks => (Decks.ItemsSource as IReadOnlyList<LibraryEntryViewModel>) ?? [];

    private void OnLibraryChanged(Type type, string id)
    {
        if (type == typeof(Core.Model.Presentation)) Dispatcher.BeginInvoke(ShowDecks);
    }

    private void ShowDecks()
    {
        var query = DeckSearch.Text.Trim();
        Decks.ItemsSource = model.Library.All<Core.Model.Presentation>()
            .Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new LibraryEntryViewModel(p))
            .ToList();
    }

    private void DeckSearch_TextChanged(object sender, TextChangedEventArgs e) => ShowDecks();

    private void Deck_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryEntryViewModel deck) model.PresentFromLibrary(deck.Presentation.Id);
    }

    /// <summary>Shows the panels and bins volunteers keep, and only those.</summary>
    private void ApplyPanels()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ApplyPanels);
            return;
        }
        TimersBox.Visibility = Kept(VolunteerPanel.Timers);
        AlertsBox.Visibility = Kept(VolunteerPanel.Alerts);
        TrackingPanel.Visibility = Kept(VolunteerPanel.ServiceTracking);
        StageMessageBox.Visibility = Kept(VolunteerPanel.StageMessage);
        var kept = bins.Keys.Where(model.VolunteerKeeps).ToList();
        foreach (var (panel, (tab, _)) in bins) tab.Visibility = kept.Contains(panel) ? Visibility.Visible : Visibility.Collapsed;
        Bins.Visibility = kept.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowBin(kept.FirstOrDefault(p => bins[p].Tab.IsChecked == true, kept.FirstOrDefault()));
        ShowOutputStatus();
    }

    private Visibility Kept(VolunteerPanel panel) => model.VolunteerKeeps(panel) ? Visibility.Visible : Visibility.Collapsed;

    private void ShowBin(VolunteerPanel panel)
    {
        foreach (var (each, (tab, bin)) in bins)
        {
            tab.IsChecked = each == panel;
            bin.Visibility = each == panel ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The outputs' state, read-only: a volunteer can't turn screens on or off.</summary>
    private void ShowOutputStatus()
    {
        if (model.Outputs is not { } outputs) return;
        OutputStatus.Text = $"Audience: {outputs.StatusText(OutputRole.Audience)}  ·  Stage: {outputs.StatusText(OutputRole.Stage)}";
    }

    /// <summary>The panels volunteers keep, as shown (the self-test reads these).</summary>
    internal IReadOnlyList<string> ShownPanels =>
    [
        .. new (string, FrameworkElement)[] { ("Timers", TimersBox), ("Alerts", AlertsBox), ("Service Tracking", TrackingPanel), ("Stage Message", StageMessageBox) }
            .Where(p => p.Item2.Visibility == Visibility.Visible).Select(p => p.Item1),
        .. bins.Where(b => b.Value.Tab.Visibility == Visibility.Visible).Select(b => b.Key.Title()),
    ];

    internal ContinuousSlidesView SlidesView => Slides;

    // MARK: Running the show

    private void Rows_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ServiceItemViewModel row) model.FireServiceItemCommand.Execute(row);
    }

    private void OverlayTile_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OverlayTileViewModel tile) model.ToggleOverlayCommand.Execute(tile);
    }

    private void MediaTile_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MediaTileViewModel tile) model.MediaClickedCommand.Execute(tile);
    }

    private void StageMessage_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        model.ShowAlertCommand.Execute(null);
        e.Handled = true;
    }

    private void StageMessageClear_Click(object sender, RoutedEventArgs e) => model.DismissAlert();

    // MARK: Leaving

    private void Leave_Click(object sender, RoutedEventArgs e) => Leave();

    /// <summary>Leaves volunteer mode: straight away with no PIN, else once the PIN is typed right.</summary>
    internal void Leave(string? typed = null)
    {
        if (!model.HasVolunteerPin)
        {
            model.TryLeaveVolunteer(null);
            return;
        }
        var pin = typed ?? PinDialog.Ask(Window.GetWindow(this), "Leave Volunteer Mode", "Type this computer's PIN to leave Volunteer Mode.");
        if (pin is null) return;
        if (!model.TryLeaveVolunteer(pin)) model.Notice = "That PIN isn't right. Volunteer Mode is still on.";
    }
}
