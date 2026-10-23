namespace MxU.Slides.LocalApi;

/// <summary>The human-readable reference served at /docs, with a curl example per endpoint. Port of DocsPage.</summary>
public static class DocsPage
{
    private static readonly string[] TagOrder =
        ["Show", "Media", "Overlays", "Alerts", "Timers", "Audio", "Transport", "Outputs", "Services", "Documents", "Library"];

    public static string Html(IEnumerable<ApiRoute> routes, string baseUrl, int schemaVersion)
    {
        var grouped = routes.GroupBy(r => r.Tag).ToDictionary(g => g.Key, g => g.ToList());
        var orderedTags = TagOrder.Where(grouped.ContainsKey)
            .Concat(grouped.Keys.Where(t => !TagOrder.Contains(t)).OrderBy(t => t, StringComparer.Ordinal));

        var sections = "";
        var nav = "";
        foreach (var tag in orderedTags)
        {
            nav += $"<a href=\"#{Slug(tag)}\">{Escape(tag)}</a>";
            sections += $"<section id=\"{Slug(tag)}\"><h2>{Escape(tag)}</h2>";
            foreach (var route in grouped[tag].OrderBy(r => r.Path, StringComparer.Ordinal))
                sections += EndpointBlock(route, baseUrl);
            sections += "</section>";
        }

        return $$"""
            <!doctype html>
            <html lang="en"><head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MxU Slides — Local API</title>
            <style>{{Css}}</style>
            </head><body>
            <header>
              <h1>MxU Slides — Local API</h1>
              <p class="lede">Local show-control and content API served by this PC.
              API version {{Escape(OpenApiDocument.ApiVersion)}} · document schema v{{schemaVersion}}.</p>
              <p class="base">Base URL <code>http://{{Escape(baseUrl)}}</code></p>
            </header>
            <div class="layout">
              <nav>{{nav}}<a href="/openapi.json">openapi.json ↗</a><a href="/asyncapi.json">asyncapi.json ↗</a></nav>
              <main>
              <section id="auth"><h2>Authenticating</h2>
                {{AuthBlock(baseUrl)}}
              </section>
              {{sections}}
              </main>
            </div>
            </body></html>
            """;
    }

    private static string AuthBlock(string baseUrl)
    {
        var curl = CurlBox($"curl {ShellUrl(baseUrl, "/v1/status")} \\\n  -H \"Authorization: Bearer YOUR_KEY\"");
        return $$"""
        <p>Every request carries an access key as a bearer token — the address
        alone won't get you in. The default Operate key sits under
        <strong>Settings › Network &amp; API › Connect at</strong>; create more
        (Watch / Operate / Manage access) with <strong>Manage Keys</strong>.
        Send it in the <code>Authorization</code> header:</p>
        {{curl}}
        <p>The WebSocket takes the key in the query string
        (<code>ws://{{Escape(baseUrl)}}/v1/ws?token=YOUR_KEY</code>) — send
        <code>{"type":"subscribe","topics":["show"]}</code> to receive
        live state, or a <code>command</code> frame to invoke any endpoint
        below over the socket. Each topic's <code>data</code> is the same shape
        as its REST snapshot — <code>show</code> is <code>GET /v1/status</code>,
        whose <code>liveSlide</code> carries <code>stepIndex</code> /
        <code>stepCount</code> while a slide with animation steps is live
        (<code>POST /v1/show/advance</code> reveals one step per call). The
        full channel list is at <code>/asyncapi.json</code>.</p>
        """;
    }

    private static string EndpointBlock(ApiRoute route, string baseUrl)
    {
        var method = route.Method.ToUpperInvariant();
        var block = $"""
            <article class="endpoint">
              <div class="ep-head">
                <span class="method m-{method.ToLowerInvariant()}">{method}</span>
                <code class="path">{Escape(route.Path)}</code>
                <span class="scope scope-{route.Scope.Raw()}">{ScopeLabel(route.Scope)}</span>
              </div>
              <p class="summary">{Escape(route.Summary)}</p>
            """;
        block += CurlBox(CurlExample(route, baseUrl));
        if (route.RequestSchema is not null)
            block += "<p class=\"note\">Body: JSON — see the schema in <a href=\"/openapi.json\">openapi.json</a>.</p>";
        return block + "</article>";
    }

    private static string CurlExample(ApiRoute route, string baseUrl)
    {
        var method = route.Method.ToUpperInvariant();
        var path = route.Path.Replace("{kind}", "presentations").Replace("{id}", "ID");
        List<string> lines = [method == "GET" ? $"curl {ShellUrl(baseUrl, path)}" : $"curl -X {method} {ShellUrl(baseUrl, path)}"];
        lines.Add("  -H \"Authorization: Bearer YOUR_KEY\"");
        if (SampleBody(route) is { } body)
        {
            lines.Add("  -H \"Content-Type: application/json\"");
            lines.Add($"  -d '{body}'");
        }
        return string.Join(" \\\n", lines);
    }

    private static string? SampleBody(ApiRoute route) => route.OperationId switch
    {
        "fireSlide" => """{"serviceItemId": "ITEM", "occurrence": 0}""",
        "advance" => """{"steps": 1}""",
        "clear" => """{"function": "slides"}""",
        "fireAlert" => """{"presetId": "PRESET", "tokens": {"number": "142"}}""",
        "audioTransport" => """{"action": "playPause"}""",
        "mediaTransport" => """{"action": "seek", "position": 12.5}""",
        "addServiceItem" => """{"refId": "PRESENTATION"}""",
        "createDocument" or "updateDocument" => """{"name": "New Song", "presentationKind": "deck", "themeId": "", "slides": []}""",
        "fireAudioPlaylist" => """{"startAtEntryId": null}""",
        _ => null,
    };

    private static string ScopeLabel(ApiScope scope) => scope switch
    {
        ApiScope.View => "Watch",
        ApiScope.Control => "Operate",
        _ => "Manage",
    };

    private static string CurlBox(string text) => $"<pre class=\"curl\">{Escape(text)}</pre>";

    private static string ShellUrl(string baseUrl, string path) => $"http://{baseUrl}{path}";

    private static string Slug(string text) => text.ToLowerInvariant().Replace(' ', '-');

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private const string Css = """
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body { margin: 0; font: 14px/1.5 -apple-system, system-ui, sans-serif;
          background: #1f1f1e; color: #e8e8e6; }
        header { padding: 28px 32px 20px; border-bottom: 1px solid #333; }
        h1 { margin: 0 0 4px; font-size: 20px; }
        .lede { margin: 0 0 6px; color: #9a9a97; }
        .base { margin: 0; color: #9a9a97; }
        code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
        header code { color: #cfcfcc; }
        .layout { display: flex; align-items: flex-start; }
        nav { position: sticky; top: 0; width: 180px; flex: none; padding: 24px 16px;
          display: flex; flex-direction: column; gap: 2px; height: 100vh; overflow: auto; }
        nav a { color: #b7b7b4; text-decoration: none; padding: 5px 10px;
          border-radius: 8px; font-size: 13px; }
        nav a:hover { background: #2a2a2a; color: #fff; }
        main { flex: 1; padding: 24px 32px 80px; max-width: 820px; min-width: 0; }
        section { margin-bottom: 36px; }
        h2 { font-size: 16px; border-bottom: 1px solid #333; padding-bottom: 6px; }
        .endpoint { background: #262626; border: 1px solid #333; border-radius: 8px;
          padding: 14px 16px; margin: 12px 0; }
        .ep-head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
        .method { font-weight: 700; font-size: 12px; padding: 2px 8px; border-radius: 6px;
          font-family: ui-monospace, Consolas, monospace; }
        .m-get { background: #16351f; color: #6bd08a; }
        .m-post { background: #1a2c3d; color: #6bb6e0; }
        .m-put { background: #3a2f14; color: #d8b34a; }
        .m-delete { background: #3a1a1a; color: #e08585; }
        .path { font-size: 13px; color: #e8e8e6; }
        .scope { margin-left: auto; font-size: 11px; font-weight: 600; padding: 2px 8px;
          border-radius: 999px; background: #333; color: #b7b7b4; }
        .scope-view { color: #8fb7d8; }
        .scope-control { color: #6bd08a; }
        .scope-edit { color: #d8b34a; }
        .summary { color: #b7b7b4; margin: 8px 0; }
        pre.curl { background: #161616; border: 1px solid #333; border-radius: 8px;
          padding: 12px 14px; overflow-x: auto; font-size: 12.5px; color: #cfcfcc;
          font-family: ui-monospace, Consolas, monospace; }
        .note { font-size: 12px; color: #8a8a87; margin: 6px 0 0; }
        @media (max-width: 720px) { nav { display: none; } main { padding: 20px; } }
        """;
}
