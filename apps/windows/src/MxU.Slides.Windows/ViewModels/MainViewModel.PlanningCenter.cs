using System.Net.Http;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.PlanningCenter;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Planning Center Services: connecting with the church's Personal Access Token, and importing a
/// plan as a service (or bringing one up to date). The network work is async and never on the UI
/// thread's critical path: requests are awaited, and building the service runs on a background
/// thread (<see cref="PlanImporter"/> in Core). The token is kept by <see cref="PlanningCenterVault"/>.
/// Windows only.
/// </summary>
public sealed partial class MainViewModel
{
    private PlanningCenterVault? planningCenterVault;

    public PlanningCenterVault PlanningCenterVault => planningCenterVault ??= new PlanningCenterVault(PlanningCenterVault.DefaultPath);

    /// <summary>The self-test points the client at a fake Planning Center: its HTTP handler and address.</summary>
    internal Func<HttpMessageHandler>? PlanningCenterHandler { get; set; }

    internal Uri? PlanningCenterBase { get; set; }

    public bool PlanningCenterConnected => PlanningCenterVault.HasCredentials;

    /// <summary>Raised (on the UI thread) when the token is saved or removed, so Settings and the window redraw.</summary>
    public event Action? PlanningCenterChanged;

    /// <summary>A client with the stored token, or null when Planning Center isn't connected.</summary>
    public PlanningCenterClient? PlanningCenterApi(IProgress<string>? progress = null) =>
        PlanningCenterVault.Load() is { } credentials ? NewPlanningCenterClient(credentials, progress) : null;

    private PlanningCenterClient NewPlanningCenterClient(PcoCredentials credentials, IProgress<string>? progress) =>
        // The log line names the request and its status only ("GET service_types → 200"); the client never gives it more.
        new(credentials, PlanningCenterHandler?.Invoke(), PlanningCenterBase, Log.Info) { Progress = progress };

    /// <summary>
    /// Checks a token against Planning Center and, if it works, keeps it. Returns the church's name
    /// ("Grace Community Church") for Settings to show. Throws <see cref="PlanningCenterException"/>
    /// with a message to show when it doesn't work; nothing is saved then.
    /// </summary>
    public async Task<string> ConnectPlanningCenterAsync(string applicationId, string secret, CancellationToken cancellation)
    {
        var credentials = new PcoCredentials(applicationId.Trim(), secret.Trim());
        if (!credentials.IsComplete)
            throw new PlanningCenterException(PlanningCenterProblem.Unauthorized, "Paste both the Application ID and the Secret.");
        using var client = NewPlanningCenterClient(credentials, null);
        var name = await client.OrganizationNameAsync(cancellation);
        PlanningCenterVault.Save(credentials);
        Log.Info("Planning Center connected");
        PlanningCenterChanged?.Invoke();
        return name;
    }

    public void DisconnectPlanningCenter()
    {
        PlanningCenterVault.Clear();
        Log.Info("Planning Center disconnected");
        PlanningCenterChanged?.Invoke();
    }

    /// <summary>The plans already imported here: plan id → service id (the import window marks them).</summary>
    public Dictionary<string, string> ImportedPlans() => PlanLink.ImportedPlans(Library);

    /// <summary>The plan a service came from, if it came from one.</summary>
    public PlanLink? PlanLinkFor(Service? service) => service is null ? null : PlanLink.For(Library, service.Id);

    /// <summary>
    /// Imports a plan as a service, or updates the service it made before, then opens that service
    /// and says what happened in the notice bar. Call on the UI thread; it returns there.
    /// </summary>
    public async Task<PlanImportSummary> ImportPlanAsync(
        string serviceTypeId, string planId, string serviceTypeName, IProgress<string>? progress, CancellationToken cancellation)
    {
        using var client = PlanningCenterApi(progress)
            ?? throw new PlanningCenterException(PlanningCenterProblem.Unauthorized,
                "Planning Center isn't connected. Add the church's Personal Access Token in Screens & Settings › Planning Center.");
        progress?.Report("Reading the plan from Planning Center…");
        var contents = await client.PlanAsync(serviceTypeId, planId, serviceTypeName, cancellation);
        progress?.Report("Building the service…");
        var options = new PlanImportOptions(Settings.DefaultThemeId, Settings.LinesPerSlide, TimeZoneInfo.Local);
        // Matching songs and turning lyrics into slides is real work: off the UI thread, so the show never waits.
        var summary = await Task.Run(() => PlanImporter.Import(Library, contents, options), cancellation);
        RefreshServices(keepSelection: true);
        if (Services.FirstOrDefault(s => s.Id == summary.ServiceId) is { } service) SelectedService = service;
        Notice = $"Planning Center: {summary.Text}";
        Log.Info($"Planning Center {(summary.Updated ? "updated" : "imported")} plan {planId}: {summary.Rows} rows");
        return summary;
    }

    /// <summary>Service › Update from Planning Center: imports the service's plan again, keeping the operator's changes.</summary>
    public async Task UpdateFromPlanningCenterAsync(Service service)
    {
        if (PlanLinkFor(service) is not { } link) return;
        Notice = "Planning Center: updating…";
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await ImportPlanAsync(link.ServiceTypeId, link.PlanId, link.ServiceTypeName, null, limit.Token);
        }
        catch (PlanningCenterException e)
        {
            Notice = null;
            Problem = e.Message;
        }
        catch (OperationCanceledException)
        {
            Notice = null;
            Problem = "Planning Center took too long. Try again in a minute.";
        }
    }

    /// <summary>What a plan row with nothing to play says under its name in the service list.</summary>
    private static string? PlanRowDetail(ServiceItem item)
    {
        if (item.MxuPcoItemId is null) return null;
        var length = item.Duration is { } seconds and > 0 ? $" · {Clock(seconds)}" : "";
        return item.MxuKind switch
        {
            "media" => "Media to add (Planning Center)" + length,
            "song" => "Song to link: not in the library" + length,
            _ => "Planning Center" + length,
        };
    }
}
