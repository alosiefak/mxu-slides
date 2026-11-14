using System.Collections.Immutable;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

// Key feeds: a screen that shows only the words and overlays over black or green, so a video
// switcher (a Blackmagic ATEM, say) can lay them over the camera. Windows only. It is set per
// screen in the Screens window and saved with the screen, beside its look (ScreenSetup), in this
// PC's settings.json: never in a shared library document. The rules themselves are in KeyFeeds.
public sealed partial class OutputConfiguration
{
    private ImmutableDictionary<Guid, KeyFeedMode> keyFeeds = ImmutableDictionary<Guid, KeyFeedMode>.Empty;

    /// <summary>A screen's key feed (a slice answers for its screen). Off unless it was switched on.</summary>
    public KeyFeedMode KeyFeed(Guid screenId) => keyFeeds.GetValueOrDefault(SliceInfo(screenId)?.ScreenId ?? screenId);

    /// <summary>Makes a screen a key feed over black or green, or an ordinary screen again (Off).</summary>
    public void SetKeyFeed(Guid screenId, KeyFeedMode mode)
    {
        if (Screen(screenId) is null || KeyFeed(screenId) == mode) return;
        keyFeeds = mode == KeyFeedMode.Off ? keyFeeds.Remove(screenId) : keyFeeds.SetItem(screenId, mode);
        Persist();
    }

    /// <summary>
    /// The key feed an output is showing right now: its screen's, while it shows the program.
    /// A stage screen, the alignment grid and the sync test are never keyed.
    /// </summary>
    public KeyFeedMode KeyFeed(OutputFeed feed) =>
        feed is { Kind: OutputFeedKind.Program, ScreenId: { } id } ? KeyFeed(id) : KeyFeedMode.Off;

    /// <summary>
    /// <see cref="Program(OutputFeed, AnimatedScene, Func{string, AnimatedScene?})"/>, then as a key feed when the
    /// output's screen is one. The backgrounds come out before the scene is reshaped for the screen (a
    /// background is recognised by covering the slide's own canvas) and once more after, in case the
    /// Screen Map brought a layer in from another look.
    /// </summary>
    public AnimatedScene KeyedProgram(OutputFeed feed, AnimatedScene scene, Func<string, AnimatedScene?>? looks = null)
    {
        var mode = KeyFeed(feed);
        return mode == KeyFeedMode.Off
            ? Program(feed, scene, looks)
            : KeyFeeds.Apply(Program(feed, KeyFeeds.Apply(scene, mode), looks), mode);
    }

    /// <inheritdoc cref="KeyedProgram(OutputFeed, AnimatedScene, Func{string, AnimatedScene?})"/>
    public RenderScene KeyedProgram(OutputFeed feed, RenderScene scene)
    {
        var mode = KeyFeed(feed);
        return mode == KeyFeedMode.Off
            ? Program(feed, scene)
            : KeyFeeds.Apply(Program(feed, KeyFeeds.Apply(scene, mode)), mode);
    }
}
