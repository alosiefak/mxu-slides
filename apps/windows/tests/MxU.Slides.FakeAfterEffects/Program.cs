using System.Text.Json;
using System.Text.Json.Nodes;

// A stand-in After Effects. Run as "aerender.exe" it renders (prints aerender's progress lines and
// copies a prepared video to -output); run as "AfterFX.exe -r script.jsx" it does what the bridge's
// script asks (lists compositions, or writes the changes into the project copy). What it finds and
// does comes from the project, a text file of "key=value" lines the tests write:
//   log=<file>       every run appends its arguments here (one JSON array per line)
//   source=<file>    the video aerender "renders" (copied to -output)
//   frames=<n>, delay=<ms>  how many frames aerender reports, and how slowly
//   comp=<name>|<w>|<h>|<seconds>|<fps>, text=<comp>|<layer>|<name>|<words>,
//   colour=<comp>|<layer>|<layer name>|<property name>|<path 1,2,3>|<#hex>
//   fail=<comp>      aerender can't find this composition
//   dirty=true       After Effects has unsaved changes (the script refuses)
var exe = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").ToLowerInvariant();
return exe.Contains("aerender", StringComparison.Ordinal) ? Aerender(args) : AfterFx(args);

static Dictionary<string, List<string>> Read(string path)
{
    var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadAllLines(path))
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) continue;
        var key = line[..eq].Trim();
        if (!values.TryGetValue(key, out var list)) values[key] = list = [];
        list.Add(line[(eq + 1)..]);
    }
    return values;
}

static string? One(Dictionary<string, List<string>> values, string key) => values.TryGetValue(key, out var list) ? list[0] : null;

static void Log(Dictionary<string, List<string>> project, string who, string[] args)
{
    if (One(project, "log") is { } log) File.AppendAllText(log, JsonSerializer.Serialize(new[] { who }.Concat(args)) + Environment.NewLine);
}

static int Aerender(string[] args)
{
    string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    var projectPath = Arg("-project");
    if (projectPath is null || !File.Exists(projectPath))
    {
        Console.WriteLine("aerender ERROR: The project file could not be found.");
        return 1;
    }
    var project = Read(projectPath);
    Log(project, "aerender", args);
    var comp = Arg("-comp") ?? "";
    Console.WriteLine("aerender version 26.0x12");
    Console.WriteLine("PROGRESS:  Launching After Effects...");
    Console.WriteLine($"PROGRESS:  Opening project: {projectPath}");
    if (project.TryGetValue("fail", out var failing) && failing.Contains(comp))
    {
        Console.WriteLine("aerender ERROR: No comp was found with the given name.");
        return 1;
    }
    var frames = int.Parse(One(project, "frames") ?? "30");
    var delay = int.Parse(One(project, "delay") ?? "0");
    Console.WriteLine($"PROGRESS:  25/10/2026 15:02:11: Starting composition “{comp}”.");
    Console.WriteLine("PROGRESS:  Render Settings: Best Settings");
    Console.WriteLine($"PROGRESS:  Output Module: {Arg("-OMtemplate")}");
    Console.WriteLine("PROGRESS:  Start: 0:00:00:00");
    Console.WriteLine($"PROGRESS:  End: 0:00:{(frames - 1) / 30:00}:{(frames - 1) % 30:00}");
    Console.WriteLine($"PROGRESS:  Duration: 0:00:{frames / 30:00}:{frames % 30:00}");
    Console.WriteLine("PROGRESS:  Frame Rate: 30.00 (comp)");
    for (var f = 0; f < frames; f++)
    {
        Console.WriteLine($"PROGRESS:  0:00:{f / 30:00}:{f % 30:00} ({f + 1}): 0 Seconds");
        Console.Out.Flush();
        if (delay > 0) Thread.Sleep(delay);
    }
    if (Arg("-output") is { } output)
    {
        if (One(project, "source") is { } source) File.Copy(source, output, overwrite: true);
        else File.WriteAllBytes(output, [0, 0, 0, 24, 102, 116, 121, 112]);
    }
    Console.WriteLine($"PROGRESS:  25/10/2026 15:02:30: Finished composition “{comp}”.");
    Console.WriteLine("PROGRESS:  Total Time Elapsed: 2 Seconds");
    return 0;
}

static int AfterFx(string[] args)
{
    var r = Array.IndexOf(args, "-r");
    if (r < 0 || r + 1 >= args.Length) return 2;
    var script = File.ReadAllText(args[r + 1]);
    var jobLine = script.Split('\n').First(l => l.StartsWith("var MXU_JOB = ", StringComparison.Ordinal));
    var job = JsonNode.Parse(jobLine["var MXU_JOB = ".Length..].TrimEnd().TrimEnd(';'))!.AsObject();
    var projectPath = job["project"]!.GetValue<string>();
    var resultPath = job["result"]!.GetValue<string>();
    var project = Read(projectPath);
    Log(project, "AfterFX", args);
    JsonObject result;
    if (One(project, "dirty") == "true")
    {
        result = new JsonObject { ["ok"] = false, ["error"] = "After Effects has unsaved changes. Save or close your project in After Effects, then try again.", ["applied"] = 0 };
    }
    else if (job["kind"]!.GetValue<string>() == "apply")
    {
        var changes = job["changes"]!.AsArray();
        // As After Effects would save the copy with its changes in it.
        File.AppendAllLines(projectPath, changes.Select(c => "applied=" + c!.ToJsonString()));
        result = new JsonObject { ["ok"] = true, ["applied"] = changes.Count, ["missing"] = new JsonArray() };
    }
    else
    {
        var comps = new JsonArray();
        foreach (var line in project.GetValueOrDefault("comp") ?? [])
        {
            var p = line.Split('|');
            var texts = new JsonArray();
            foreach (var t in (project.GetValueOrDefault("text") ?? []).Select(t => t.Split('|')).Where(t => t[0] == p[0]))
                texts.Add(new JsonObject { ["layer"] = int.Parse(t[1]), ["name"] = t[2], ["text"] = t[3] });
            var colours = new JsonArray();
            foreach (var c in (project.GetValueOrDefault("colour") ?? []).Select(c => c.Split('|')).Where(c => c[0] == p[0]))
                colours.Add(new JsonObject
                {
                    ["layer"] = int.Parse(c[1]), ["layerName"] = c[2], ["name"] = c[3],
                    ["path"] = new JsonArray([.. c[4].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => (JsonNode)int.Parse(n))]),
                    ["hex"] = c[5],
                });
            comps.Add(new JsonObject
            {
                ["name"] = p[0], ["width"] = double.Parse(p[1]), ["height"] = double.Parse(p[2]), ["duration"] = double.Parse(p[3]),
                ["frameRate"] = double.Parse(p[4]), ["texts"] = texts, ["colours"] = colours,
            });
        }
        result = new JsonObject { ["ok"] = true, ["compositions"] = comps, ["applied"] = 0 };
    }
    // Written in one go, as the real script does.
    File.WriteAllText(resultPath + ".tmp", result.ToJsonString());
    File.Move(resultPath + ".tmp", resultPath, overwrite: true);
    return 0;
}
