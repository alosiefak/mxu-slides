using System.Diagnostics;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.MotionTemplates.AfterEffects;

/// <summary>
/// The app's bridge to After Effects, for churches that design their graphics there.
/// <list type="number">
/// <item><b>Read a project</b> (<see cref="ReadProject"/>): After Effects opens a copy and the
/// listing script reports its compositions, text layers and colours.</item>
/// <item><b>Render</b> (<see cref="Render"/>): the .aep is copied into a work folder; if words or
/// colours are to change, After Effects opens the copy, the apply script changes it and saves it;
/// then aerender renders the composition from the copy to a video file, reporting each frame. The
/// caller imports the file as media.</item>
/// </list>
/// <b>The user's .aep is never changed</b>: After Effects only ever opens the copy. A project
/// remembers where its footage is by full path, so the copy, in another folder, still finds it.
/// Every step can be cancelled; the work folder is removed afterwards whatever happened.
/// <para>
/// After Effects renders to H.264 MP4 (2023 and later), which plays live but without
/// transparency. The route that plays live with transparency is Lottie (see <see cref="LottieGuide"/>).
/// </para>
/// </summary>
public sealed class AfterEffectsBridge(AfterEffectsInstall install, string workRoot)
{
    public AfterEffectsInstall Install { get; } = install;

    /// <summary>How long to wait for After Effects to run a script (it may have to start first, which takes a while).</summary>
    public TimeSpan ScriptTimeLimit { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The compositions in <paramref name="aepPath"/>, read by After Effects from a copy.</summary>
    /// <exception cref="IOException">After Effects couldn't do it; the message says why.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public AeProject ReadProject(string aepPath, CancellationToken cancellationToken = default)
    {
        var job = NewJobFolder();
        try
        {
            var copy = CopyProject(aepPath, job);
            var outcome = RunScript(ExtendScripts.ListKind, copy, job, null, cancellationToken);
            return new AeProject(outcome.Compositions ?? []);
        }
        finally
        {
            TryDeleteFolder(job);
        }
    }

    /// <summary>
    /// Renders <paramref name="composition"/> of <paramref name="aepPath"/> to <paramref name="output"/>,
    /// after making <paramref name="changes"/> in a copy. Returns how aerender finished (it has
    /// <see cref="AerenderResult.Succeeded"/> only when the file was written).
    /// </summary>
    /// <exception cref="IOException">After Effects couldn't change the copy; the message says why.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    public AerenderResult Render(string aepPath, string composition, IReadOnlyList<AeChange> changes, string output,
        IProgress<AerenderStatus>? progress = null, CancellationToken cancellationToken = default, string outputModule = Aerender.H264)
    {
        var job = NewJobFolder();
        var succeeded = false;
        try
        {
            var copy = CopyProject(aepPath, job);
            if (changes.Count > 0)
            {
                var applied = RunScript(ExtendScripts.ApplyKind, copy, job, [.. changes], cancellationToken);
                if (applied.Missing is { Count: > 0 } missing)
                    progress?.Report(new AerenderStatus(0, null, composition, [$"Not found in the project: {string.Join(", ", missing)}"], null));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var result = Aerender.Run(Install.Aerender, Aerender.Arguments(new AerenderRequest(copy, composition, output, outputModule)), progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Succeeded && !(File.Exists(output) && new FileInfo(output).Length > 0))
                result = result with { ExitCode = result.ExitCode == 0 ? -1 : result.ExitCode, Log = [.. result.Log, "aerender finished, but wrote no file"] };
            succeeded = result.Succeeded;
            return result;
        }
        finally
        {
            if (!succeeded) TryDelete(output);
            TryDeleteFolder(job);
        }
    }

    /// <summary>The media library folder After Effects renders are filed in.</summary>
    public const string MediaFolder = "After Effects";

    /// <summary>
    /// Files a finished render into the media library as an ordinary video (in the "After Effects"
    /// folder): a looping background, or a foreground video that plays once. The file is copied in and
    /// then removed. A file Windows can't play is still kept, but flagged (Phase 5's import probe), so
    /// it can be turned into H.264.
    /// </summary>
    /// <exception cref="IOException">The file isn't a video.</exception>
    public static MediaItem FileIntoLibrary(LibraryStore library, string path, string name, bool background)
    {
        try
        {
            var probe = library.Prober?.Probe(path) ?? MediaImportProbe.ProbeVideo(path);
            var item = MediaImporting.AddMedia(library, path, name, background ? MediaClassification.Background : MediaClassification.Foreground, probe: probe)
                       ?? throw new IOException("The render isn't a video the library can take");
            item.Folder = MediaFolder;
            item.Loops = background;
            library.Save(item);
            return item;
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>Copies the project into the job folder under its own name: After Effects only ever opens this copy.</summary>
    internal static string CopyProject(string aepPath, string job)
    {
        if (!File.Exists(aepPath)) throw new IOException($"The project “{Path.GetFileName(aepPath)}” isn't there any more.");
        var copy = Path.Combine(job, Path.GetFileName(aepPath));
        File.Copy(aepPath, copy, overwrite: true);
        // A read-only original makes a read-only copy, which the apply script couldn't save.
        File.SetAttributes(copy, File.GetAttributes(copy) & ~FileAttributes.ReadOnly);
        return copy;
    }

    private ExtendScripts.Outcome RunScript(string kind, string project, string job, List<AeChange>? changes, CancellationToken cancellationToken)
    {
        if (Install.AfterFx is not { } afterFx) throw new IOException($"{ExtendScriptsHelp.NoAfterFx} ({Install.Folder})");
        var result = Path.Combine(job, kind + "-result.json");
        // If After Effects isn't open now, the script closes it again when it's done.
        var quit = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(afterFx)).Length == 0;
        var script = Path.Combine(job, kind + ".jsx");
        File.WriteAllText(script, ExtendScripts.Script(new ExtendScripts.Job(kind, project, result, quit, changes)), new System.Text.UTF8Encoding(false));

        var start = new ProcessStartInfo(afterFx) { UseShellExecute = false, CreateNoWindow = false };
        start.ArgumentList.Add("-r");
        start.ArgumentList.Add(script);
        using (Process.Start(start)) { }

        // AfterFX.exe hands the script to After Effects (starting it if need be) and may return at
        // once or stay open, so the bridge waits for the result file rather than for the program.
        var clock = Stopwatch.StartNew();
        while (!File.Exists(result) || !IsComplete(result))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > ScriptTimeLimit)
                throw new IOException($"After Effects didn't answer within {ScriptTimeLimit.TotalMinutes:0} minutes. Is it waiting for something on its screen?");
            cancellationToken.WaitHandle.WaitOne(250);
        }
        var outcome = ExtendScripts.ReadOutcome(File.ReadAllText(result))
                      ?? throw new IOException("After Effects' answer couldn't be read.");
        if (!outcome.Ok) throw new IOException(outcome.Error ?? "After Effects couldn't run the script.");
        return outcome;
    }

    /// <summary>The script writes its answer in one go; a file still being written isn't valid JSON yet.</summary>
    private static bool IsComplete(string path)
    {
        try { return ExtendScripts.ReadOutcome(File.ReadAllText(path)) is not null; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return false; }
    }

    private string NewJobFolder()
    {
        var job = Path.Combine(workRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(job);
        return job;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Words the bridge and its window share.</summary>
public static class ExtendScriptsHelp
{
    public const string NoAfterFx = "AfterFX.exe isn't in this After Effects' Support Files folder, so its text and colours can't be changed (rendering still works)";
}

/// <summary>
/// The short guide the app shows for the route that plays live with transparency: exporting a
/// composition from After Effects as Lottie, with Bodymovin or the LottieFiles plug-in.
/// </summary>
public static class LottieGuide
{
    public const string Title = "Exporting Lottie from After Effects";

    public static readonly IReadOnlyList<(string Heading, string Text)> Steps =
    [
        ("Why Lottie", "A Lottie file plays live in MxU Slides with its transparency, stays sharp at any screen size, and its words and colours can be changed here, in the show. A rendered video can't do any of that."),
        ("1. Get the exporter", "In After Effects, open Window › Extensions and install Bodymovin (free, from aescripts.com/bodymovin) or the LottieFiles plug-in (lottiefiles.com/plugins/after-effects). Both are made for this."),
        ("2. Keep it Lottie-friendly", "Use shape layers, text layers, solids, images, masks and track mattes. Avoid effects that only After Effects draws (particles, 3-D renderers, most third-party plug-ins), and keep expressions simple: Lottie bakes or drops them."),
        ("3. Name your layers", "Name each text layer for what it holds (\"Speaker Name\", \"Title\"): that's the label its field gets here. Colours are listed by the layers that use them."),
        ("4. Add In, Hold and Out markers", "Add composition markers named In, Hold and Out at the start of each part. MxU Slides plays the In when it goes up, holds (looping the Hold if it moves), and plays the Out on Take Down."),
        ("5. Export", "In Bodymovin, tick the composition, choose a folder, and under Settings tick Glyphs off (so text stays editable) and Assets › Include in json (so pictures travel with it). Render. LottieFiles' plug-in exports a .lottie or .json the same way."),
        ("6. Import", "Here, open Motion Templates and press Import Lottie…, or drop the .json or .lottie on the window. Fonts the PC hasn't got are taken from the library's fonts folder when they match."),
    ];
}
