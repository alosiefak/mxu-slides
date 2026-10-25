using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The services, each as this computer runs it (its version applied: see MainViewModel.ServiceVersions.cs).</summary>
    public ObservableCollection<Service> Services { get; } = [];

    public ObservableCollection<ServiceItemViewModel> ServiceItems { get; } = [];

    [ObservableProperty]
    public partial Service? SelectedService { get; set; }

    [ObservableProperty]
    public partial ServiceItemViewModel? SelectedServiceItem { get; set; }

    partial void OnSelectedServiceChanged(Service? value)
    {
        RefreshServiceItems();
        Session.ServiceId = value?.Id;
        Session.Save();
        OnPropertyChanged(nameof(HasService));
        OnPropertyChanged(nameof(ServiceVersionName));
        OnPropertyChanged(nameof(ServiceHasVersions));
        // Another service: its clock starts from nothing. The same one refreshed after an edit keeps
        // it, and so does the moment the picker is empty while the service list is rebuilt.
        if (value is not null) ServiceTiming.ServiceChanged(value.Id);
        RefreshServiceTracking(force: true);
        // After this change has settled (claiming saves the service, which refreshes the list again).
        if (value is not null) System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => ClaimRememberedVersion(SelectedService));
    }

    public bool HasService => SelectedService is not null;

    partial void OnSelectedServiceItemChanged(ServiceItemViewModel? value)
    {
        RefreshMusicCard();
        if (value is null || suppressServiceItemOpen) return;
        var item = value.Item;
        switch (item.ItemKind)
        {
            case ServiceItemKind.Presentation when Library.Get<Presentation>(item.RefId) is { } p:
                suppressLibrarySelection = true;
                SelectedLibraryEntry = null;
                suppressLibrarySelection = false;
                Open(p, item.Id, item.ArrangementId);
                break;
            case ServiceItemKind.Media when Library.Get<MediaItem>(item.RefId) is { } media:
                SelectedMedia = MediaItems.FirstOrDefault(m => m.Item.Id == media.Id);
                break;
        }
    }

    private bool suppressServiceItemOpen;

    public void RefreshServices(bool keepSelection = false)
    {
        var selectedId = SelectedService?.Id;
        var services = Library.All<Service>()
            .OrderByDescending(s => s.ServiceDate, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(RunningView)
            .ToList();
        Services.Clear();
        foreach (var s in services) Services.Add(s);
        if (keepSelection && selectedId is not null)
        {
            var match = Services.FirstOrDefault(s => s.Id == selectedId);
            if (!ReferenceEquals(match, SelectedService)) SelectedService = match;
            else RefreshServiceItems();
        }
    }

    public void RefreshServiceItems()
    {
        var selectedId = SelectedServiceItem?.Item.Id;
        suppressServiceItemOpen = true;
        ServiceItems.Clear();
        HiddenServiceItems.Clear();
        if (SelectedService is { } service)
        {
            var changed = VersionChangedItemIds(service);
            var number = 0;
            foreach (var item in service.Items)
            {
                var hidden = item.IsHidden();
                // Hidden rows aren't numbered: the numbers are the run of show's.
                if (!hidden && item.ItemKind != ServiceItemKind.Header) number++;
                var (detail, missing) = RowDetail(item);
                // "File missing" / "2 media files missing" when its media has no file on this PC (MainViewModel.MissingMedia.cs).
                var note = missing ? null : MissingMediaNote(item);
                var row = new ServiceItemViewModel(item, hidden ? 0 : number, missing ? "Missing from library" : note ?? detail, missing || note is not null)
                {
                    IsChanged = changed.Contains(item.Id),
                };
                (hidden ? HiddenServiceItems : ServiceItems).Add(row);
            }
        }
        SelectedServiceItem = ServiceItems.FirstOrDefault(r => r.Item.Id == selectedId);
        suppressServiceItemOpen = false;
        OnPropertyChanged(nameof(HiddenGroupTitle));
        OnPropertyChanged(nameof(HasHiddenServiceItems));
        OnPropertyChanged(nameof(ServiceVersionName));
        OnPropertyChanged(nameof(ServiceHasVersions));
        MarkLiveTiles();
        RefreshServiceTracking(force: true);
    }

    /// <summary>What a row says under its name ("12 slides", "Video", "Playlist · 6 songs"), and whether its document is gone.</summary>
    private (string? Detail, bool Missing) RowDetail(ServiceItem item)
    {
        switch (item.ItemKind)
        {
            case ServiceItemKind.Presentation:
                if (Library.Get<Presentation>(item.RefId) is not { } p) return (null, true);
                var arrangement = p.Arrangements?.FirstOrDefault(a => a.Id == (item.ArrangementId ?? p.DefaultArrangementId));
                return (arrangement is null ? $"{p.Slides.Count} slides" : arrangement.Name, false);
            case ServiceItemKind.Media:
                if (Library.Get<MediaItem>(item.RefId) is not { } m) return (null, true);
                return (m.MediaKind == MediaKind.Video ? "Video" : "Image", false);
            case ServiceItemKind.Audio:
                if (Library.Get<AudioItem>(item.RefId) is not { } song) return (null, true);
                return (song.DurationSeconds is { } seconds and > 0 ? $"Music · {Clock(seconds)}" : "Music", false);
            case ServiceItemKind.Playlist:
                if (Library.Get<Playlist>(item.RefId) is not { } playlist) return (null, true);
                return ($"Playlist · {playlist.Entries.Count} song{(playlist.Entries.Count == 1 ? "" : "s")}", false);
            case ServiceItemKind.Info:
                return (PlanRowDetail(item), false); // a plan row with nothing to play yet (MainViewModel.PlanningCenter.cs)
            default:
                return (null, false);
        }
    }

    [RelayCommand]
    private void NewService()
    {
        var date = NextSunday(DateOnly.FromDateTime(DateTime.Today));
        var service = new Service
        {
            Id = LibraryStore.NewId(),
            Name = $"Sunday Service {date:d MMM}",
            ServiceDate = date.ToString("yyyy-MM-dd"),
        };
        Library.Save(service);
        RefreshServices();
        SelectedService = Services.FirstOrDefault(s => s.Id == service.Id);
    }

    private static DateOnly NextSunday(DateOnly from)
    {
        var days = ((int)DayOfWeek.Sunday - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(days);
    }

    public void RenameService(Service service, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        UpdateService(service.Id, s => s.Name = name.Trim());
    }

    [RelayCommand]
    private void DeleteService()
    {
        if (SelectedService is not { } service) return;
        Library.Delete<Service>(service.Id);
        RefreshServices();
        SelectedService = Services.FirstOrDefault();
        Notice = $"Moved \"{service.Name}\" to the library's Trash folder.";
    }

    [RelayCommand]
    private void AddCurrentToService()
    {
        if (CurrentPresentation is not { } p) return;
        if (SelectedService is null) NewService();
        if (SelectedService is not { } service) return;
        var item = new ServiceItem
        {
            Id = LibraryStore.NewId(),
            ItemKind = ServiceItemKind.Presentation,
            Name = p.Name,
            RefId = p.Id,
            ArrangementId = SelectedArrangement?.Id,
        };
        InsertServiceItem(service, item);
        CurrentContextItemId = item.Id;
    }

    public void AddMediaToService(MediaItem media)
    {
        if (SelectedService is null) NewService();
        if (SelectedService is not { } service) return;
        InsertServiceItem(service, new ServiceItem { Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Media, Name = media.Name, RefId = media.Id });
    }

    [RelayCommand]
    private void AddHeader()
    {
        if (SelectedService is not { } service) return;
        InsertServiceItem(service, new ServiceItem
        {
            Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Header, Name = "New Section", RefId = "", ColorHex = "#3B82F6FF",
        });
    }

    /// <summary>Adds after the selected row, or at the end.</summary>
    private void InsertServiceItem(Service service, ServiceItem item)
    {
        var afterId = SelectedServiceItem?.Item.Id;
        UpdateService(service.Id, s =>
        {
            var index = afterId is null ? s.Items.Count : s.Items.FindIndex(i => i.Id == afterId) + 1;
            s.Items.Insert(Math.Clamp(index, 0, s.Items.Count), item);
        });
        suppressServiceItemOpen = true;
        SelectedServiceItem = ServiceItems.FirstOrDefault(r => r.Item.Id == item.Id);
        suppressServiceItemOpen = false;
    }

    [RelayCommand]
    private void RemoveServiceItem(ServiceItemViewModel? row)
    {
        row ??= SelectedServiceItem;
        if (row is null || SelectedService is not { } service) return;
        UpdateService(service.Id, s => s.Items.RemoveAll(i => i.Id == row.Item.Id));
    }

    [RelayCommand]
    private void MoveServiceItemUp(ServiceItemViewModel? row) => MoveServiceItem(row ?? SelectedServiceItem, -1);

    [RelayCommand]
    private void MoveServiceItemDown(ServiceItemViewModel? row) => MoveServiceItem(row ?? SelectedServiceItem, +1);

    public void MoveServiceItem(ServiceItemViewModel? row, int offset)
    {
        if (row is null || SelectedService is not { } service) return;
        var index = service.Items.FindIndex(i => i.Id == row.Item.Id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= service.Items.Count) return;
        MoveServiceItemTo(row.Item.Id, target);
    }

    /// <summary>Moves a row to an index (drag and drop).</summary>
    public void MoveServiceItemTo(string itemId, int target)
    {
        if (SelectedService is not { } service || !service.Items.Any(i => i.Id == itemId)) return;
        UpdateService(service.Id, s =>
        {
            var index = s.Items.FindIndex(i => i.Id == itemId);
            if (index < 0) return;
            var item = s.Items[index];
            s.Items.RemoveAt(index);
            s.Items.Insert(Math.Clamp(target, 0, s.Items.Count), item);
        });
        suppressServiceItemOpen = true;
        SelectedServiceItem = ServiceItems.FirstOrDefault(r => r.Item.Id == itemId);
        suppressServiceItemOpen = false;
    }

    public void RenameServiceItem(ServiceItemViewModel row, string name)
    {
        if (SelectedService is not { } service || string.IsNullOrWhiteSpace(name)) return;
        UpdateService(service.Id, s =>
        {
            if (s.Items.FirstOrDefault(i => i.Id == row.Item.Id) is { } item) item.Name = name.Trim();
        });
    }

    /// <summary>Takes a service row live: a presentation's first slide, the media item, or its music (double-click, Go live).</summary>
    [RelayCommand]
    private void FireServiceItem(ServiceItemViewModel? row)
    {
        if (row is null) return;
        if (SelectedServiceItem != row) SelectedServiceItem = row;
        FireRow(row.Item);
    }
}
