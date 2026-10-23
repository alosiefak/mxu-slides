using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

public sealed partial class MainViewModel
{
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
    }

    public bool HasService => SelectedService is not null;

    partial void OnSelectedServiceItemChanged(ServiceItemViewModel? value)
    {
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
        if (SelectedService is { } service)
        {
            var number = 0;
            foreach (var item in service.Items)
            {
                string? detail = null;
                var missing = false;
                switch (item.ItemKind)
                {
                    case ServiceItemKind.Presentation:
                        var p = Library.Get<Presentation>(item.RefId);
                        missing = p is null;
                        if (p is not null)
                        {
                            var arrangement = p.Arrangements?.FirstOrDefault(a => a.Id == (item.ArrangementId ?? p.DefaultArrangementId));
                            detail = arrangement is null ? $"{p.Slides.Count} slides" : arrangement.Name;
                        }
                        break;
                    case ServiceItemKind.Media:
                        var m = Library.Get<MediaItem>(item.RefId);
                        missing = m is null;
                        detail = m is null ? null : m.MediaKind == MediaKind.Video ? "Video" : "Image";
                        break;
                }
                if (item.ItemKind != ServiceItemKind.Header) number++;
                ServiceItems.Add(new ServiceItemViewModel(item, number, missing ? "Missing from library" : detail, missing));
            }
        }
        SelectedServiceItem = ServiceItems.FirstOrDefault(r => r.Item.Id == selectedId);
        suppressServiceItemOpen = false;
        MarkLiveTiles();
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
        service.Name = name.Trim();
        Library.Save(service);
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
        var index = SelectedServiceItem is { } selected ? service.Items.FindIndex(i => i.Id == selected.Item.Id) + 1 : service.Items.Count;
        service.Items.Insert(Math.Clamp(index, 0, service.Items.Count), item);
        Library.Save(service);
        RefreshServiceItems();
        suppressServiceItemOpen = true;
        SelectedServiceItem = ServiceItems.FirstOrDefault(r => r.Item.Id == item.Id);
        suppressServiceItemOpen = false;
    }

    [RelayCommand]
    private void RemoveServiceItem(ServiceItemViewModel? row)
    {
        row ??= SelectedServiceItem;
        if (row is null || SelectedService is not { } service) return;
        service.Items.RemoveAll(i => i.Id == row.Item.Id);
        Library.Save(service);
        RefreshServiceItems();
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
        if (SelectedService is not { } service) return;
        var index = service.Items.FindIndex(i => i.Id == itemId);
        if (index < 0) return;
        var item = service.Items[index];
        service.Items.RemoveAt(index);
        service.Items.Insert(Math.Clamp(target, 0, service.Items.Count), item);
        Library.Save(service);
        RefreshServiceItems();
        suppressServiceItemOpen = true;
        SelectedServiceItem = ServiceItems.FirstOrDefault(r => r.Item.Id == itemId);
        suppressServiceItemOpen = false;
    }

    public void RenameServiceItem(ServiceItemViewModel row, string name)
    {
        if (SelectedService is not { } service || string.IsNullOrWhiteSpace(name)) return;
        var item = service.Items.FirstOrDefault(i => i.Id == row.Item.Id);
        if (item is null) return;
        item.Name = name.Trim();
        Library.Save(service);
        RefreshServiceItems();
    }

    /// <summary>Takes a service row live: a presentation's first slide, or the media item.</summary>
    [RelayCommand]
    private void FireServiceItem(ServiceItemViewModel? row)
    {
        if (row is null) return;
        switch (row.Item.ItemKind)
        {
            case ServiceItemKind.Presentation when Library.Get<Presentation>(row.Item.RefId) is { } p:
                SelectedServiceItem = row;
                Fire(p, row.Item.ArrangementId ?? p.DefaultArrangementId, 0, row.Item.Id);
                break;
            case ServiceItemKind.Media when Library.Get<MediaItem>(row.Item.RefId) is { } media:
                FireMedia(media);
                Live = new LiveContext(row.Item.Id, Live?.PresentationId ?? "", null, 0);
                MarkLiveTiles();
                break;
        }
    }
}
