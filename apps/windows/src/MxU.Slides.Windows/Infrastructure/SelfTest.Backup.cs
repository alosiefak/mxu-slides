using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Follow;
using MxU.Slides.Core.Model;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Follow;
using MxU.Slides.Windows.Views;
using MxU.Slides.Windows.Views.Follow;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Backing up another computer (MxU PR #12), Windows to Windows on this one PC: this app is the
/// booth, and a second copy of it (its own throwaway library and settings, its own API port, named
/// "Backup PC (self-test)") is the backup. The second copy pairs with the booth's code, follows it,
/// copies its items, replays a slide and a clear, holds a remote's commands, takes over (the booth
/// steps down) and hands back. Pictures 130–134.
/// </summary>
public sealed partial class SelfTest
{
    private const string BackupName = "Backup PC (self-test)";

    private async Task BackupCheck()
    {
        var s = model.Settings;
        var (wasOn, wasPort) = (s.LocalApiEnabled, s.LocalApiPort);
        var boothPort = FreePort();
        s.LocalApiEnabled = true;
        s.LocalApiPort = boothPort;
        model.ApplyApiSettings();
        Process? backup = null;
        Presentation? deck = null;
        var root = Path.Combine(Path.GetTempPath(), $"MxU self-test backup {Guid.NewGuid():N}");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            // Something on the booth the backup doesn't have yet, to copy and to fire.
            deck = new Presentation
            {
                Id = Core.Library.LibraryStore.NewId(), Name = "Backup check", PresentationKind = PresentationKind.Deck,
                ThemeId = model.Library.All<Theme>().FirstOrDefault()?.Id ?? "",
                Slides = [.. new[] { "Welcome", "Announcements", "Thanks for coming" }.Select(text => new Slide
                {
                    Id = Core.Library.LibraryStore.NewId(), Name = text,
                    Objects = [new SlideObject { Id = Core.Library.LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text, Name = "Text", Text = text, X = 160, Y = 400, Width = 1600, Height = 280 }],
                })],
            };
            model.Library.Save(deck);

            // Settings › Backup on the booth: the code a backup types.
            var settings = new SettingsWindow(model, model.Outputs!) { Owner = window };
            settings.Show();
            await Delay(800);
            var section = Descendant<BackupSettingsSection>(settings);
            section?.BringIntoView();
            await Delay(300);
            var code = section?.ShownCode;
            Check("Settings › Backup shows the code a backup types", code is { Length: 4 });
            if (section is not null) SavePart(section, "130-settings-backup-booth");
            var pair = PairBackupWindow.Open(settings, model);
            await Delay(1500);
            Save(pair, "131-make-this-the-backup");
            pair.Close();
            settings.Close();
            if (code is null) return;

            // The backup asks for a key with the code, as Make This the Backup does.
            var pairUrl = new Uri($"http://127.0.0.1:{boothPort}/v1/follow/pair");
            var wrong = await http.PostAsJsonAsync(pairUrl, new ApiFollowPairRequest(code == "0000" ? "1111" : "0000", "x", BackupName), ApiJson.Options);
            Check("a wrong code is refused", wrong.StatusCode == HttpStatusCode.Forbidden);
            var paired = await http.PostAsJsonAsync(pairUrl, new ApiFollowPairRequest(code, "backup-self-test", BackupName), ApiJson.Options);
            var key = paired.IsSuccessStatusCode ? (await paired.Content.ReadFromJsonAsync<ApiFollowPairResponse>(ApiJson.Options))?.Token : null;
            Check("the right code gets the backup an Operate key named for it",
                key is not null && model.ApiTokens.All.Any(t => t.Name == FollowPresence.Prefix + BackupName && t.Scope == ApiScope.Control));
            if (key is null) return;

            // The second copy: its own folders and port, paired before it opens.
            var (data, local) = (Path.Combine(root, "data"), Path.Combine(root, "local"));
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(local);
            var backupPort = FreePort();
            var settingsJson = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
            await File.WriteAllBytesAsync(Path.Combine(data, "settings.json"),
                JsonSerializer.SerializeToUtf8Bytes(new AppSettings { LocalApiEnabled = true, LocalApiPort = backupPort, OutputsOnAtLaunch = false }, settingsJson));
            var backupKey = new ApiTokenStore(Path.Combine(data, "local-api-tokens.json")).Create("Self-test", ApiScope.Control).Secret;
            FollowController.Prepare(local, new FollowController.Pairing
            {
                StationHexId = model.Computer.Id, Name = model.Computer.Name, Host = "127.0.0.1", Port = boothPort,
                Automatic = true, Role = FollowRole.Start(model.Computer.Id), Mirrors = true,
            }, key);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.Environment["MXU_SLIDES_DATA"] = data;
            start.Environment["MXU_SLIDES_LOCAL"] = local;
            start.Environment["MXU_SLIDES_COMPUTER_NAME"] = BackupName;
            backup = Process.Start(start);

            var other = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{backupPort}"), Timeout = TimeSpan.FromSeconds(5) };
            other.DefaultRequestHeaders.Authorization = new("Bearer", backupKey);
            using var _ = other;
            async Task<JsonNode?> Read(string path)
            {
                try { return JsonNode.Parse(await other.GetStringAsync(path)); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
            }
            async Task<JsonNode?> Wait(string path, Func<JsonNode, bool> done, int seconds)
            {
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                JsonNode? last = null;
                while (DateTime.UtcNow < deadline)
                {
                    if ((last = await Read(path)) is { } node && done(node)) return node;
                    await Delay(250);
                }
                return null;
            }

            var following = await Wait("/v1/follow", f => (string?)f["role"] == "backingUp" && (string?)f["health"] == "live", 60);
            Check("the second copy opens, connects and follows the booth", following is not null);
            var heard = await Until(() => model.Follow.ConnectedBackups.Contains(BackupName), on => on, 8000);
            Check("the booth sees its backup is connected", heard);
            var copied = await Wait("/v1/library/presentations", list => list.AsArray().Any(e => (string?)e?["id"] == deck.Id), 30);
            Check("the booth's items are copied to the backup", copied is not null);

            var slide = deck.Slides[1];
            model.FireNoteSlide(deck.Id, slide.Id);
            var live = await Wait("/v1/status", st => (string?)st["liveSlide"]?["slideId"] == slide.Id, 10);
            Check("a slide fired on the booth goes live on the backup", live is not null);
            await Delay(800);
            SaveOtherWindow(backup, "132-backup-follows");

            model.ClearAllCommand.Execute(null);
            Check("a clear on the booth clears the backup", await Wait("/v1/status", st => st["liveSlide"] is null, 10) is not null);
            var held = await other.PostAsync("/v1/show/advance", JsonContent.Create(new { steps = 1 }));
            Check("the backup holds a remote's commands while it follows",
                held.StatusCode == HttpStatusCode.Conflict && (await held.Content.ReadAsStringAsync()).Contains("backing_up", StringComparison.Ordinal));
            Check("a backup is held, a booth is not", !FollowController.IsBackingUp);

            // Take over on the backup: the booth hears it and steps down.
            var takeOver = await other.PostAsync("/v1/follow/takeover", null);
            Check("Take Over on the backup answers", takeOver.IsSuccessStatusCode);
            var down = await Until(() => model.Follow.SteppedDownTo?.BackupName, name => name == BackupName, 8000);
            Check("the booth steps down when its backup takes over", down == BackupName && FollowController.IsBackingUp);
            Check("the backup is in charge", await Wait("/v1/follow", f => (string?)f["role"] == "inCharge", 10) is not null);
            await Delay(500);
            Save(window, "133-booth-stepped-down");
            SaveOtherWindow(backup, "134-backup-in-charge");
            var answered = await other.PostAsync("/v1/show/advance", JsonContent.Create(new { steps = 1 }));
            Check("once in charge, the backup answers a remote", answered.StatusCode != HttpStatusCode.Conflict);

            var handBack = await other.PostAsync("/v1/follow/handback", null);
            Check("Hand Back gives the booth its say again", handBack.IsSuccessStatusCode
                && await Until(() => model.Follow.SteppedDownTo, d => d is null, 8000) is null && !FollowController.IsBackingUp
                && await Wait("/v1/follow", f => (string?)f["role"] == "backingUp", 10) is not null);
            Check("copying never changed the booth's library", model.Library.Get<Presentation>(deck.Id)?.Slides.Count == 3);
        }
        finally
        {
            if (backup is { HasExited: false })
            {
                backup.CloseMainWindow();
                if (!backup.WaitForExit(15000)) backup.Kill(entireProcessTree: true);
            }
            backup?.Dispose();
            if (model.Follow.Backups.Contains(BackupName)) model.Follow.Forget(BackupName);
            if (model.Follow.SteppedDownTo is not null) model.Follow.TakeBack();
            model.ClearAllCommand.Execute(null);
            if (deck is not null) model.Library.Delete<Presentation>(deck.Id);
            s.LocalApiEnabled = wasOn;
            s.LocalApiPort = wasPort;
            model.ApplyApiSettings();
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>A picture of another process's main window (the second copy), as Windows draws it.</summary>
    private void SaveOtherWindow(Process? process, string name)
    {
        try
        {
            if (process is null) return;
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;
            var (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (width < 10 || height < 10) return;
            var screen = GetDC(IntPtr.Zero);
            var dc = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, width, height);
            var old = SelectObject(dc, bitmap);
            try
            {
                PrintWindow(handle, dc, 2); // PW_RENDERFULLCONTENT: what WPF drew, even behind other windows
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                Write(source, name);
            }
            finally
            {
                SelectObject(dc, old);
                DeleteObject(bitmap);
                DeleteDC(dc);
                _ = ReleaseDC(IntPtr.Zero, screen);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException)
        {
            report.AppendLine($"NOTE  couldn't capture {name}: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr item);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
}
