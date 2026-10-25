namespace MxU.Slides.Core.Automation;

/// <summary>
/// Steps the shared documents can't hold, run beside an owner's own steps: on Windows, the
/// production steps (switch the ATEM to camera 2, OBS to the Worship scene, the lights to the
/// Sermon look) kept in <see cref="Library.WindowsExtras"/> under the owner's id. The
/// <see cref="ActionRouter"/> says each time an owner has run (a slide or media item fired, a
/// combo ran, a schedule trigger or a timeline actions cue went off) and the extras run their
/// own steps, with their own delays. Windows only: the Mac has no twin.
/// </summary>
public interface IExtraSteps
{
    /// <summary>The owner (a slide, media item, combo, schedule trigger or timeline cue id) has just run its steps.</summary>
    void OwnerRan(string ownerId);

    /// <summary>Clear All: drop every extra step still waiting on its delay, as the router drops its own.</summary>
    void CancelPending();
}
