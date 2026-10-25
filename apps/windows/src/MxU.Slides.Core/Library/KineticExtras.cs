using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Kinetic builds for theme text placeholders, kept as Windows extras (WindowsExtras.cs): the
/// worship looks' lyrics rise in line by line on Windows, while the shared theme carries a
/// whole-text rise the Mac plays as it is.
/// <para>
/// Each build is saved against a fingerprint of its placeholder's own steps. If the theme is later
/// changed on a Mac (someone picks another In for the lyrics), the fingerprint no longer matches
/// and the build is ignored, so the Mac's change shows on Windows too instead of being overridden.
/// </para>
/// Looking a build up happens every time a slide is drawn, so answers are remembered (by
/// placeholder id and fingerprint) and forgotten whenever an extra changes. Windows-only.
/// </summary>
public sealed class KineticExtras
{
    /// <summary>The extras folder these live in ("Windows extras/kinetic-text").</summary>
    public const string Kind = "kinetic-text";

    private readonly WindowsExtras extras;
    private readonly Dictionary<(string Id, string Basis), KineticBuild?> remembered = [];
    private readonly Lock gate = new();

    public KineticExtras(WindowsExtras extras)
    {
        this.extras = extras;
        extras.Changed += (kind, _) =>
        {
            if (kind != Kind) return;
            lock (gate) remembered.Clear();
        };
    }

    /// <summary>What a placeholder's shared steps look like, as the basis its build is saved against.</summary>
    public static string BasisOf(SlideObject placeholder) => WindowsExtras.Fingerprint(placeholder.AnimationSteps);

    /// <summary>The kinetic build for a theme placeholder, or null when it has none or its steps have changed since.</summary>
    public KineticBuild? For(SlideObject placeholder)
    {
        if (placeholder.ObjectKind != SlideObjectKind.Text || placeholder.AnimationSteps is not { Count: > 0 }) return null;
        var key = (placeholder.Id, BasisOf(placeholder));
        lock (gate)
        {
            if (remembered.TryGetValue(key, out var known)) return known;
        }
        var build = extras.Get<KineticBuild>(Kind, key.Id, key.Item2);
        lock (gate) remembered[key] = build;
        return build;
    }

    /// <summary>Saves a placeholder's kinetic build, against its steps as they are now.</summary>
    public void Save(SlideObject placeholder, KineticBuild build) => extras.Save(Kind, placeholder.Id, build, BasisOf(placeholder));

    /// <summary>
    /// Saves each build the starter looks declare, unless the same one is already saved for the
    /// same steps. A build whose theme has been changed elsewhere stays as it was (ignored).
    /// </summary>
    public int Seed(IEnumerable<(SlideObject Placeholder, KineticBuild Build)> builds)
    {
        var saved = 0;
        foreach (var (placeholder, build) in builds)
        {
            if (extras.Get<KineticBuild>(Kind, placeholder.Id, BasisOf(placeholder)) is { } existing
                && WindowsExtras.Fingerprint(existing) == WindowsExtras.Fingerprint(build)) continue;
            Save(placeholder, build);
            saved++;
        }
        return saved;
    }
}
