using System.Diagnostics;

namespace MxU.Slides.LocalApi.Tests;

/// <summary>
/// Runs the repo's own Mac client, scripts/api-drive.mjs, unchanged against this server:
/// discovery, create/edit, control, view and WebSocket. Skipped without Node.js.
/// </summary>
public sealed class ExampleClientTests
{
    [Fact]
    public async Task ApiDriveScriptPasses()
    {
        var script = FindUp(Path.Combine("scripts", "api-drive.mjs"));
        if (script is null) Assert.Skip("scripts/api-drive.mjs is not above the test folder.");
        var node = FindOnPath("node.exe") ?? FindOnPath("node");
        if (node is null) Assert.Skip("Node.js is not on PATH.");

        var bridge = new ScriptBridge();
        using var host = TestServer.Start(bridge);
        bridge.Changed += host.Server.Publish;

        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(script))!,
        };
        foreach (var argument in new[] { script, "--token", host.Mint(ApiScope.Edit), "--port", host.Port.ToString() })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestServer.Ct);
        var error = process.StandardError.ReadToEndAsync(TestServer.Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestServer.Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var transcript = await output + await error;
        TestContext.Current.SendDiagnosticMessage(transcript);
        Assert.True(process.ExitCode == 0, transcript);
        Assert.Contains("checks passed", transcript);
    }

    private static string? FindUp(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), executable))
            .FirstOrDefault(File.Exists);
}
