using MxU.Slides.Core.Control;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The service list's extras (Phase 8):
/// <list type="bullet">
/// <item>Collapsing a header folds its rows away in the list (Next still walks them). Which ones are
/// collapsed is this PC's choice, kept in its settings, as the Mac keeps it per Mac.</item>
/// <item>Several rows chosen at once (Ctrl- and Shift-click) and the bulk menu: Transition, Hide,
/// Remove (Core's <see cref="ListMultiSelect"/> and <see cref="ServiceBulkEdit"/>).</item>
/// <item>The plan time this PC runs, for a Planning Center plan with several (Core's
/// <see cref="ServicePlanTimes"/> over the ported <see cref="ServiceRunOrder"/> rules).</item>
/// <item>The service picker's Upcoming and Earlier groups (Views/ServicePicker.cs).</item>
/// </list>
/// Dragging rows to reorder them is Phase 7's (MainWindow.xaml.cs); this is selection, collapsing and grouping.
/// </summary>
public sealed partial class MainViewModel
{
    // MARK: Collapsing headers

    private HashSet<string> CollapsedHeaders(string serviceId) =>
        Settings.CollapsedHeaders.TryGetValue(serviceId, out var ids) ? [.. ids] : [];

    public bool IsHeaderCollapsed(string headerId) => SelectedService is { } service && CollapsedHeaders(service.Id).Contains(headerId);

    /// <summary>Folds a header's rows away, or back out.</summary>
    public void SetHeaderCollapsed(string headerId, bool collapsed)
    {
        if (SelectedService is not { } service) return;
        var ids = CollapsedHeaders(service.Id);
        if (!(collapsed ? ids.Add(headerId) : ids.Remove(headerId))) return;
        SaveCollapsed(service.Id, ids);
    }

    public void ToggleHeaderCollapsed(string headerId) => SetHeaderCollapsed(headerId, !IsHeaderCollapsed(headerId));

    /// <summary>Collapse All / Expand All in a header's menu.</summary>
    public void SetAllHeadersCollapsed(bool collapsed)
    {
        if (SelectedService is not { } service) return;
        var ids = collapsed ? ServiceItems.Where(r => r.IsHeader).Select(r => r.Item.Id).ToHashSet() : [];
        SaveCollapsed(service.Id, ids);
    }

    private void SaveCollapsed(string serviceId, HashSet<string> ids)
    {
        if (ids.Count == 0) Settings.CollapsedHeaders.Remove(serviceId);
        else Settings.CollapsedHeaders[serviceId] = [.. ids.Order(StringComparer.Ordinal)];
        Settings.Save();
        ApplyCollapse();
    }

    /// <summary>
    /// Marks each header collapsed or not, and each row under a collapsed header as folded away
    /// (the list hides those). The rows stay in the list's collection, so MIDI's "select service
    /// item n" and Next count them as before.
    /// </summary>
    private void ApplyCollapse()
    {
        var collapsed = SelectedService is { } service ? CollapsedHeaders(service.Id) : [];
        var shown = ServiceRunOrder.SidebarRows([.. ServiceItems.Select(r => r.Item)], collapsed).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in ServiceItems)
        {
            row.IsCollapsed = row.IsHeader && collapsed.Contains(row.Item.Id);
            row.IsCollapsedAway = !shown.Contains(row.Item.Id);
        }
    }

    /// <summary>A row inside a collapsed group goes live (Next walked into it): its group opens, so the operator sees where the service is.</summary>
    private void RevealServiceRow(string itemId)
    {
        if (ServiceItems.FirstOrDefault(r => r.Item.Id == itemId) is not { IsCollapsedAway: true }) return;
        var index = ServiceItems.ToList().FindIndex(r => r.Item.Id == itemId);
        var header = ServiceItems.Take(index).LastOrDefault(r => r.IsHeader);
        if (header is not null) SetHeaderCollapsed(header.Item.Id, false);
        else ApplyCollapse();
    }

    // MARK: Several rows at once

    /// <summary>The rows chosen in the list now (Ctrl- and Shift-click choose several; set by the list as it changes).</summary>
    public IReadOnlyList<ServiceItemViewModel> ChosenServiceRows { get; set; } = [];

    /// <summary>What a right-click on <paramref name="clicked"/> works on: the whole choice when it's in it, else just that row (the Finder's rule).</summary>
    public List<string> ServiceBatch(string clicked) =>
        ListMultiSelect.Batch(clicked, ChosenServiceRows.Select(r => r.Item.Id).ToHashSet(StringComparer.Ordinal), [.. ServiceItems.Select(r => r.Item.Id)]);

    public void HideServiceRows(IReadOnlyCollection<string> ids)
    {
        if (!Permit(OperatorAction.EditService) || SelectedService is not { } service) return;
        UpdateService(service.Id, s => ServiceBulkEdit.Hide(s.Items, ids));
    }

    /// <summary>Removes rows from the service (the library keeps their songs and media).</summary>
    public void RemoveServiceRows(IReadOnlyCollection<string> ids)
    {
        if (!Permit(OperatorAction.EditService) || SelectedService is not { } service) return;
        UpdateService(service.Id, s => ServiceBulkEdit.Remove(s.Items, ids));
    }

    /// <summary>
    /// How the songs among <paramref name="ids"/> (null: every song in the service: Set All
    /// Transitions) hand over to the next item in a timeline: Stop, Fade, Overlap or Crossfade.
    /// </summary>
    public void SetServiceTransition(IReadOnlyCollection<string>? ids, SongTransition? transition)
    {
        if (!Permit(OperatorAction.EditService) || SelectedService is not { } service) return;
        UpdateService(service.Id, s => ServiceBulkEdit.SetTransition(s.Items, ids, transition));
        timeline?.RefreshTransition();
    }

    /// <summary>A row's Arrangement › menu: which arrangement the song plays in this service (null: its default).</summary>
    public void SetServiceItemArrangement(string itemId, string? arrangementId)
    {
        if (!Permit(OperatorAction.ChangeArrangement) || SelectedService is not { } service) return;
        UpdateService(service.Id, s =>
        {
            if (s.Items.FirstOrDefault(i => i.Id == itemId) is { } item) item.ArrangementId = arrangementId;
        });
        // The open song from that row follows at once.
        if (CurrentContextItemId == itemId && CurrentPresentation is { } p)
        {
            ReloadArrangementChoices(p, arrangementId ?? p.DefaultArrangementId);
            ReanchorLive(p);
            ArrangementsChanged?.Invoke();
        }
    }

    // MARK: Plan times

    /// <summary>The plan time this PC runs for a service (null: every row, in the service's own order).</summary>
    public string? PlanTimeId(Service service) =>
        ServicePlanTimes.Chosen(service, Settings.PlanTimes.GetValueOrDefault(service.Id))?.HexId;

    /// <summary>The selected service's times, for the picker chip (none when it has fewer than two).</summary>
    public IReadOnlyList<MxUPlanTime> SelectedServicePlanTimes => SelectedService is { } service ? ServicePlanTimes.Times(service) : [];

    public MxUPlanTime? SelectedServicePlanTime => SelectedService is { } service ? ServicePlanTimes.Chosen(service, Settings.PlanTimes.GetValueOrDefault(service.Id)) : null;

    /// <summary>This PC runs one plan time of the selected service (null: all of it). Running the show, so a volunteer may.</summary>
    public void ChoosePlanTime(string? timeHexId)
    {
        if (!Permit(OperatorAction.ChoosePlanTime) || SelectedService is not { } service) return;
        if (timeHexId is null) Settings.PlanTimes.Remove(service.Id);
        else Settings.PlanTimes[service.Id] = timeHexId;
        Settings.Save();
        RefreshServiceItems();
        RefreshContinuous();
        OnPropertyChanged(nameof(SelectedServicePlanTime));
        Notice = timeHexId is null ? "This computer runs every row of the service." :
            $"This computer runs the {ServicePlanTimes.Label(SelectedServicePlanTime!)} time: {ServicePlanTimes.LeftOut(service, timeHexId)} row(s) aren't in it.";
    }

    /// <summary>The run of show as this PC runs it: the plan time's rows in its order, hidden rows left out.</summary>
    private List<ServiceItem> RunOfShow(Service service) =>
        ServiceRunOrder.Visible(service.Items, PlanTimeId(service), ServicePlanTimes.Order(service, PlanTimeId(service)));

    /// <summary>Every row as this PC lists it (hidden ones too, for the Hidden group), in its plan time's order.</summary>
    private List<ServiceItem> ListedRows(Service service) => ServicePlanTimes.Rows(service, PlanTimeId(service));

    // MARK: The service picker

    /// <summary>The services in the picker's two groups: today's and later ones (then undated), and Earlier.</summary>
    public ServicePicking.Groups ServicePickerGroups() => ServicePicking.Group(Services, ServicePicking.Today(DateTime.Now));
}
