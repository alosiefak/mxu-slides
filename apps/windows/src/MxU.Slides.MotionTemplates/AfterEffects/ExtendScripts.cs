using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MxU.Slides.MotionTemplates.AfterEffects;

/// <summary>A text layer in a composition: its index (After Effects counts from 1), name and words.</summary>
public sealed record AeText(int Layer, string Name, string Text);

/// <summary>
/// A colour in a composition that a template can change: a shape's fill or stroke, an effect's
/// colour, or a text layer's fill (<see cref="Path"/> empty). <see cref="Path"/> is the chain of
/// property indices from the layer down to it, which is how the script finds it again.
/// </summary>
public sealed record AeColour(int Layer, string LayerName, string Name, int[] Path, string Hex);

public sealed record AeComposition(string Name, double Width, double Height, double Duration, double FrameRate, List<AeText> Texts, List<AeColour> Colours);

/// <summary>What the listing script found in a project.</summary>
public sealed record AeProject(List<AeComposition> Compositions);

/// <summary>One change to make in a copy before it's rendered: new words for a text layer, or a new colour.</summary>
public sealed record AeChange(string Composition, int Layer, string Kind, string? Text = null, int[]? Path = null, string? Hex = null)
{
    public const string TextKind = "text", ColourKind = "colour", TextColourKind = "textColour";

    public static AeChange Words(string composition, int layer, string text) => new(composition, layer, TextKind, Text: text);

    public static AeChange Colour(string composition, AeColour colour, string hex) =>
        new(composition, colour.Layer, colour.Path.Length == 0 ? TextColourKind : ColourKind, Path: colour.Path, Hex: hex);
}

/// <summary>
/// The ExtendScript (After Effects' JavaScript) the bridge runs with <c>AfterFX.exe -r</c>. There are
/// two jobs: <b>list</b> reads a project's compositions, their text layers and the colours that
/// can be changed; <b>apply</b> opens a copy of a project, puts new words and colours in, and saves
/// the copy. Both write what happened to a JSON file, which the bridge waits for.
/// <para>
/// The job is written into the script as its first line, <c>var MXU_JOB = {…};</c>: JSON, with every
/// character outside plain ASCII written as <c>\uXXXX</c>, so it's also a valid JavaScript literal
/// whatever encoding After Effects reads the file in. (ExtendScript is an old JavaScript: no JSON
/// object, so the script brings a small writer of its own.) The script never touches a project
/// with unsaved changes, and it never saves anything but the copy it was given.
/// </para>
/// </summary>
public static class ExtendScripts
{
    public const string ListKind = "list";
    public const string ApplyKind = "apply";

    /// <summary>JSON as the bridge reads and writes it (camelCase, like JavaScript).</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Non-ASCII as \uXXXX: valid JSON and valid ExtendScript, in any encoding.
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>The job the script does, written into it as <c>MXU_JOB</c>.</summary>
    public sealed record Job(string Kind, string Project, string Result, bool Quit, List<AeChange>? Changes = null);

    /// <summary>What the script reports: <c>ok</c>, why not, and (for a listing) the compositions.</summary>
    public sealed record Outcome(bool Ok, string? Error, List<AeComposition>? Compositions, int Applied, List<string>? Missing);

    /// <summary>The first line of every script: the job as a JavaScript literal.</summary>
    public static string JobLine(Job job) => "var MXU_JOB = " + JsonSerializer.Serialize(job, Json) + ";";

    /// <summary>The whole script for <paramref name="job"/>.</summary>
    public static string Script(Job job) => JobLine(job) + "\n" + Body;

    /// <summary>Reads the job back out of a script (the stand-in After Effects in the tests does this).</summary>
    public static Job? ReadJob(string script)
    {
        var line = script.Split('\n').FirstOrDefault(l => l.StartsWith("var MXU_JOB = ", StringComparison.Ordinal));
        if (line is null) return null;
        var json = line["var MXU_JOB = ".Length..].TrimEnd().TrimEnd(';');
        return JsonSerializer.Deserialize<Job>(json, Json);
    }

    public static Outcome? ReadOutcome(string json) => JsonSerializer.Deserialize<Outcome>(json, Json);

    // The script itself. Written for ExtendScript (ES3): var, no JSON, no Array.prototype.map.
    private const string Body = """
// MxU Slides: the After Effects bridge's script. It reads compositions, or changes a COPY of a
// project and saves the copy. It never saves the project you are working on.
(function () {
    function quote(s) {
        s = String(s);
        var out = '"';
        for (var i = 0; i < s.length; i++) {
            var ch = s.charAt(i), code = s.charCodeAt(i);
            if (ch == '"' || ch == '\\') out += '\\' + ch;
            else if (code < 32 || code > 126) { var h = code.toString(16); while (h.length < 4) h = '0' + h; out += '\\u' + h; }
            else out += ch;
        }
        return out + '"';
    }
    function json(v) {
        if (v === null || v === undefined) return 'null';
        if (typeof v == 'number') return isFinite(v) ? String(v) : 'null';
        if (typeof v == 'boolean') return v ? 'true' : 'false';
        if (typeof v == 'string') return quote(v);
        if (v instanceof Array) { var a = []; for (var i = 0; i < v.length; i++) a.push(json(v[i])); return '[' + a.join(',') + ']'; }
        var parts = [];
        for (var k in v) if (v.hasOwnProperty(k)) parts.push(quote(k) + ':' + json(v[k]));
        return '{' + parts.join(',') + '}';
    }
    function write(result) {
        var f = new File(MXU_JOB.result);
        f.encoding = 'UTF-8';
        f.open('w');
        f.write(json(result));
        f.close();
    }
    function hex(c) {
        function b(x) { var h = Math.round(Math.max(0, Math.min(1, x)) * 255).toString(16).toUpperCase(); return h.length < 2 ? '0' + h : h; }
        return '#' + b(c[0]) + b(c[1]) + b(c[2]);
    }
    function rgb(h, like) {
        var v = like ? like.slice(0) : [0, 0, 0, 1];
        v[0] = parseInt(h.substr(1, 2), 16) / 255; v[1] = parseInt(h.substr(3, 2), 16) / 255; v[2] = parseInt(h.substr(5, 2), 16) / 255;
        return v;
    }
    function colours(group, path, layerIndex, layerName, out) {
        for (var i = 1; i <= group.numProperties && out.length < 200; i++) {
            var p = group.property(i);
            if (!p) continue;
            var here = path.concat([i]);
            if (p.propertyType == PropertyType.PROPERTY) {
                if (p.propertyValueType == PropertyValueType.COLOR && p.numKeys == 0 && p.enabled !== false)
                    out.push({ layer: layerIndex, layerName: layerName, name: (p.parentProperty ? p.parentProperty.name + ' ' : '') + p.name, path: here, hex: hex(p.value) });
            } else {
                colours(p, here, layerIndex, layerName, out);
            }
        }
    }
    function comp(name) {
        for (var i = 1; i <= app.project.numItems; i++) {
            var item = app.project.item(i);
            if (item instanceof CompItem && item.name == name) return item;
        }
        return null;
    }
    function list() {
        var comps = [];
        for (var i = 1; i <= app.project.numItems; i++) {
            var item = app.project.item(i);
            if (!(item instanceof CompItem)) continue;
            var texts = [], found = [];
            for (var j = 1; j <= item.numLayers; j++) {
                var layer = item.layer(j);
                if (layer instanceof TextLayer) {
                    var doc = layer.property('Source Text').value;
                    texts.push({ layer: j, name: layer.name, text: doc.text });
                    if (doc.applyFill) found.push({ layer: j, layerName: layer.name, name: 'Text fill', path: [], hex: hex(doc.fillColor) });
                }
                colours(layer, [], j, layer.name, found);
            }
            comps.push({ name: item.name, width: item.width, height: item.height, duration: item.duration, frameRate: item.frameRate, texts: texts, colours: found });
        }
        return { ok: true, compositions: comps, applied: 0 };
    }
    function apply() {
        var applied = 0, missing = [];
        var changes = MXU_JOB.changes || [];
        for (var i = 0; i < changes.length; i++) {
            var c = changes[i], item = comp(c.composition);
            var layer = item && c.layer <= item.numLayers ? item.layer(c.layer) : null;
            if (!layer) { missing.push(c.composition + ' #' + c.layer); continue; }
            if (c.kind == 'text' || c.kind == 'textColour') {
                var source = layer.property('Source Text');
                if (!source) { missing.push(c.composition + ' #' + c.layer); continue; }
                if (source.numKeys > 0) {
                    for (var k = 1; k <= source.numKeys; k++) {
                        var kd = source.keyValue(k);
                        if (c.kind == 'text') kd.text = c.text; else kd.fillColor = rgb(c.hex);
                        source.setValueAtKey(k, kd);
                    }
                } else {
                    var d = source.value;
                    if (c.kind == 'text') d.text = c.text; else d.fillColor = rgb(c.hex);
                    source.setValue(d);
                }
                applied++;
            } else if (c.kind == 'colour') {
                var p = layer;
                for (var n = 0; p && n < c.path.length; n++) p = p.property(c.path[n]);
                if (!p || p.propertyValueType != PropertyValueType.COLOR) { missing.push(c.composition + ' #' + c.layer); continue; }
                p.setValue(rgb(c.hex, p.value));
                applied++;
            }
        }
        app.project.save();
        return { ok: true, applied: applied, missing: missing };
    }
    var dialogs = false;
    try {
        app.beginSuppressDialogs();
        dialogs = true;
        if (app.project && app.project.dirty) {
            write({ ok: false, error: 'After Effects has unsaved changes. Save or close your project in After Effects, then try again.', applied: 0 });
            return;
        }
        var opened = app.open(new File(MXU_JOB.project));
        if (!opened) throw new Error('After Effects could not open ' + MXU_JOB.project);
        var result = MXU_JOB.kind == 'apply' ? apply() : list();
        app.project.close(CloseOptions.DO_NOT_SAVE_CHANGES);
        write(result);
    } catch (e) {
        try { write({ ok: false, error: String(e), applied: 0 }); } catch (ignored) { }
    } finally {
        if (dialogs) { try { app.endSuppressDialogs(false); } catch (ignored) { } }
        if (MXU_JOB.quit) { try { app.quit(); } catch (ignored) { } }
    }
})();
""";
}
