using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Service versions (the 9:00 and the 11:00), hidden rows, a row's output preset, and the
/// service's menu (Duplicate, Change Date). The rules are Core's <see cref="ServiceVersions"/>;
/// this is the app's half of the Mac's AppModel service functions.
/// <para>
/// How versions reach the rest of the app: <see cref="SelectedService"/> is the service as this
/// computer runs it. Without a version that's the library's own document; with one it's a copy
/// with the version applied (<see cref="RunningView"/>), so the slide grid, Next, the stage and
/// the timeline all follow the version without knowing about it. Edits must never save that copy
/// over the library's document: they go through <see cref="UpdateService"/> or
/// <see cref="SaveService"/>, which record the change in the running version and leave Main alone.
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    private ThisComputer? thisComputer;

    /// <summary>This computer's id and name, and the version it picked last.</summary>
    public ThisComputer Computer => thisComputer ??= ThisComputer.Load();

    /// <summary>The version this computer runs of a service (by its own choice, else the name it picked last), or null for Main.</summary>
    public ServiceVersion? RunningVersion(Service main) => ServiceVersions.Running(main, Computer.Id, Computer.RememberedVersionName);

    /// <summary>The service as this computer runs it: the library's document, or a copy with its version applied.</summary>
    private Service RunningView(Service main) => RunningVersion(main) is { } version ? ServiceVersions.Apply(version, main) : main;

    /// <summary>
    /// Changes a service the way the Mac's updateService does: while a version runs, the change is
    /// recorded in the version (Main stays as it was, apart from the parts all versions share);
    /// otherwise Main changes. Always on a copy, so nothing holding the old document sees half an edit.
    /// </summary>
    public void UpdateService(string serviceId, Action<Service> mutate)
    {
        if (Library.Get<Service>(serviceId) is not { } main) return;
        if (RunningVersion(main) is { } version)
        {
            Library.Save(ServiceVersions.Edit(main, version.Id, mutate));
            return;
        }
        var copy = MxuJson.Clone(main);
        mutate(copy);
        Library.Save(copy);
    }

    /// <summary>
    /// Saves a service that was changed in place (<see cref="SelectedService"/> after an edit). With a
    /// version running, the copy's differences go into the version instead of over Main.
    /// </summary>
    public void SaveService(Service edited)
    {
        if (Library.Get<Service>(edited.Id) is { } main && !ReferenceEquals(main, edited) && RunningVersion(main) is not null)
        {
            var changed = MxuJson.Clone(edited);
            UpdateService(edited.Id, run =>
            {
                foreach (var property in typeof(Service).GetProperties().Where(p => p.CanWrite && p.Name != nameof(Service.Versions)))
                    property.SetValue(run, property.GetValue(changed));
            });
            return;
        }
        Library.Save(edited);
    }

    /// <summary>Changes Main itself (choosing, naming or deleting a version is about the versions, not one of them).</summary>
    private void ModifyMainService(string serviceId, Action<Service> mutate)
    {
        if (Library.Get<Service>(serviceId) is not { } main) return;
        var copy = MxuJson.Clone(main);
        mutate(copy);
        Library.Save(copy);
    }

    // MARK: Versions

    /// <summary>The selected service's versions (from Main), for the version picker.</summary>
    public IReadOnlyList<ServiceVersion> SelectedServiceVersions =>
        SelectedService is { } service ? Library.Get<Service>(service.Id)?.Versions ?? [] : [];

    /// <summary>The version this computer runs of the selected service, or null for Main.</summary>
    public ServiceVersion? SelectedServiceRunningVersion =>
        SelectedService is { } service && Library.Get<Service>(service.Id) is { } main ? RunningVersion(main) : null;

    /// <summary>The version chip's text ("Main", "11am"); the chip shows once a service has versions.</summary>
    public string ServiceVersionName => SelectedServiceRunningVersion?.Name ?? "Main";

    public bool ServiceHasVersions => SelectedServiceVersions.Count > 0;

    /// <summary>This computer runs <paramref name="versionId"/> (null: Main), and remembers its name for next week's service.</summary>
    public void ChooseServiceVersion(string serviceId, string? versionId)
    {
        if (Library.Get<Service>(serviceId) is not { } main) return;
        Computer.RememberedVersionName = versionId is null ? null : main.Versions?.FirstOrDefault(v => v.Id == versionId)?.Name;
        Computer.Save();
        var station = new ServiceVersionStation { Id = Computer.Id, Name = Computer.Name };
        ModifyMainService(serviceId, service => ServiceVersions.Choose(versionId, service, station));
        Notice = versionId is null ? "This computer runs the service's Main version." : $"This computer runs the \"{Computer.RememberedVersionName}\" version.";
    }

    /// <summary>A new version starts as a copy of Main; this computer runs it.</summary>
    public void CreateServiceVersion(string serviceId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        ModifyMainService(serviceId, service => ServiceVersions.Create(name.Trim(), service, id));
        ChooseServiceVersion(serviceId, id);
    }

    public void RenameServiceVersion(string serviceId, string versionId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Library.Get<Service>(serviceId) is not { } main) return;
        // Renaming the version this computer chose keeps it chosen by its new name too.
        if (Computer.RememberedVersionName is not null
            && main.Versions?.Any(v => v.Id == versionId && v.Stations?.Any(s => s.Id == Computer.Id) == true) == true)
        {
            Computer.RememberedVersionName = name.Trim();
            Computer.Save();
        }
        ModifyMainService(serviceId, service =>
        {
            if (service.Versions?.FirstOrDefault(v => v.Id == versionId) is { } version) version.Name = name.Trim();
        });
    }

    /// <summary>Its changes are deleted, and any computer running it goes back to Main.</summary>
    public void DeleteServiceVersion(string serviceId, string versionId) =>
        ModifyMainService(serviceId, service =>
        {
            service.Versions?.RemoveAll(v => v.Id == versionId);
            if (service.Versions is { Count: 0 }) service.Versions = null;
        });

    /// <summary>"Use Main's": the running version forgets what it changed about this row.</summary>
    public void UseMainForServiceItem(string itemId)
    {
        if (SelectedService is not { } service || Library.Get<Service>(service.Id) is not { } main || RunningVersion(main) is not { } version) return;
        ModifyMainService(service.Id, copy => ServiceVersions.UseMain(itemId, version.Id, copy));
    }

    /// <summary>The rows the running version changed (they wear a "Changed" badge).</summary>
    private HashSet<string> VersionChangedItemIds(Service view) =>
        Library.Get<Service>(view.Id) is { } main && RunningVersion(main) is { } version ? ServiceVersions.ChangedItemIds(version) : [];

    /// <summary>
    /// The service runs a version by the name this computer picked last week but doesn't name this
    /// computer yet: claim it, so every computer can see who runs what (the Mac's claimRememberedVersion).
    /// </summary>
    private void ClaimRememberedVersion(Service? view)
    {
        if (view is null || Library.Get<Service>(view.Id) is not { } main || RunningVersion(main) is not { } version) return;
        if (version.Stations?.Any(s => s.Id == Computer.Id) == true) return;
        ChooseServiceVersion(main.Id, version.Id);
    }

    // MARK: Hidden rows

    /// <summary>The rows hidden from the run of show, listed under "HIDDEN (n)" with Unhide.</summary>
    public ObservableCollection<ServiceItemViewModel> HiddenServiceItems { get; } = [];

    [ObservableProperty]
    public partial bool HiddenGroupOpen { get; set; }

    public string HiddenGroupTitle => $"HIDDEN ({HiddenServiceItems.Count})";

    public bool HasHiddenServiceItems => HiddenServiceItems.Count > 0;

    [RelayCommand]
    private void ToggleHiddenGroup() => HiddenGroupOpen = !HiddenGroupOpen;

    /// <summary>Hide: the row leaves the run of show (Next, the stage, timelines skip it) but stays in the service.</summary>
    public void SetServiceItemHidden(string itemId, bool hidden)
    {
        if (SelectedService is not { } service) return;
        UpdateService(service.Id, s =>
        {
            if (s.Items.FirstOrDefault(i => i.Id == itemId) is { } item) item.HiddenInPresenter = hidden ? true : null;
        });
    }

    [RelayCommand]
    private void UnhideServiceItem(ServiceItemViewModel? row)
    {
        if (row is not null) SetServiceItemHidden(row.Item.Id, false);
    }

    // MARK: A row's output preset

    /// <summary>The output preset a row switches to when it fires, or null to keep the current look.</summary>
    public void SetServiceItemOutputPreset(string itemId, string? presetId)
    {
        if (SelectedService is not { } service) return;
        UpdateService(service.Id, s =>
        {
            if (s.Items.FirstOrDefault(i => i.Id == itemId) is { } item) item.OutputPresetId = string.IsNullOrEmpty(presetId) ? null : presetId;
        });
    }

    // MARK: The service menu

    /// <summary>A copy of the whole service ("Sunday Copy"), versions and all, opened.</summary>
    [RelayCommand]
    private void DuplicateService()
    {
        if (SelectedService is not { } service || Library.Get<Service>(service.Id) is not { } main) return;
        var copy = MxuJson.Clone(main);
        copy.Id = LibraryStore.NewId();
        copy.Name += " Copy";
        Library.Save(copy);
        RefreshServices();
        SelectedService = Services.FirstOrDefault(s => s.Id == copy.Id);
        Notice = $"Made \"{copy.Name}\".";
    }

    /// <summary>The service's date (every version shares it).</summary>
    public void ChangeServiceDate(DateOnly date)
    {
        if (SelectedService is not { } service) return;
        UpdateService(service.Id, s => s.ServiceDate = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The selected service's date, if it has a readable one.</summary>
    public DateOnly? SelectedServiceDate =>
        DateOnly.TryParseExact(SelectedService?.ServiceDate, "yyyy-MM-dd", out var date) ? date : null;
}
