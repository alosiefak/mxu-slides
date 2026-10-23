using System.Security.Cryptography;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Runs the Local API (REST + WebSocket remote control) when Settings turn it on,
/// and pushes show, timer and library changes to connected remotes.
/// </summary>
public sealed partial class MainViewModel
{
    private LocalApiServer? api;
    private ApiTokenStore? apiTokens;
    private bool apiEventsHooked;

    [ObservableProperty]
    public partial string ApiStatus { get; set; } = "Off";

    /// <summary>The keys remotes sign in with (created on first use).</summary>
    public ApiTokenStore ApiTokens => apiTokens ??= new ApiTokenStore(
        Path.Combine(AppPaths.DataRoot, "local-api-tokens.json"), DpapiKeyVault(Path.Combine(AppPaths.DataRoot, "local-api-default-key.bin")));

    /// <summary>The address a phone or tablet on the same network opens, e.g. http://192.168.1.20:6980.</summary>
    public string ApiAddress => $"http://{LocalApiServer.LanIPv4() ?? "localhost"}:{(api?.IsRunning == true ? api.Port : Settings.LocalApiPort)}";

    /// <summary>Starts, restarts or stops the server to match Settings. Call on the UI thread: requests run on it.</summary>
    public void ApplyApiSettings()
    {
        if (!apiEventsHooked)
        {
            apiEventsHooked = true;
            Show.Changed += () => PublishApi(ApiTopic.Show);
            Timers.Changed += () => PublishApi(ApiTopic.Timers);
            Library.Changed += (_, _) => PublishApi(ApiTopic.Library);
        }
        if (!Settings.LocalApiEnabled)
        {
            api?.Stop();
            ApiStatus = "Off";
            return;
        }
        api ??= new LocalApiServer();
        if (api.IsRunning && api.Port == Settings.LocalApiPort) return;
        api.Stop();
        ApiTokens.EnsureDefaultKey();
        if (api.Start(Settings.LocalApiPort, this, ApiTokens))
        {
            ApiStatus = $"On — {ApiAddress}";
            Log.Info($"Local API listening on port {api.Port}");
        }
        else
        {
            ApiStatus = $"Couldn't start: {api.LastError}";
            Log.Warn($"Local API didn't start on port {Settings.LocalApiPort}: {api.LastError}");
        }
    }

    public void StopApi() => api?.Stop();

    private void PublishApi(ApiTopic topic)
    {
        if (api?.IsRunning != true) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Publish();
        else dispatcher.BeginInvoke(Publish);

        void Publish()
        {
            try
            {
                api.Publish(topic);
                if (topic == ApiTopic.Show) api.Publish(ApiTopic.Transport);
            }
            catch (Exception ex)
            {
                Log.Error($"Publishing {topic} to API clients failed", ex);
            }
        }
    }

    /// <summary>Keeps the default key's secret encrypted for this Windows user (DPAPI), as the Mac keeps it in the Keychain.</summary>
    private static ApiDefaultKeyVault DpapiKeyVault(string path) => new(
        Load: () =>
        {
            try
            {
                return File.Exists(path)
                    ? System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser))
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return null;
            }
        },
        Save: secret => Core.Library.LibraryStore.AtomicWrite(path,
            ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser)),
        Clear: () =>
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
}
