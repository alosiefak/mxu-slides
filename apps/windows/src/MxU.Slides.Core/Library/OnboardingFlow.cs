namespace MxU.Slides.Core.Library;

/// <summary>
/// The first-run guide's steps and choices. Port of the Mac's OnboardingFlow.swift (the step
/// order, Back and Continue stopping at the ends, "start from scratch" bringing the Getting
/// Started service), with the steps Windows needs: welcome, the audience and stage screens, a
/// look for new songs, bringing songs in, and where Help is. The Mac has two steps (content and
/// screens) and offers its looks elsewhere.
/// </summary>
public sealed class OnboardingFlow
{
    public enum Step
    {
        Welcome,
        Screens,
        Look,
        Songs,
        Help,
    }

    public enum ContentChoice
    {
        ImportSongs,
        StartFromScratch,
        RestoreBackup,
    }

    public OnboardingFlow(bool firstRun) => IncludeGettingStarted = firstRun;

    public Step Current { get; private set; } = Step.Welcome;

    public ContentChoice? Choice { get; private set; }

    /// <summary>Whether the Getting Started service and welcome presentation stay (on a first run they're already made).</summary>
    public bool IncludeGettingStarted { get; set; }

    public static IReadOnlyList<Step> Steps { get; } = Enum.GetValues<Step>();

    public bool CanGoBack => Current != Step.Welcome;

    public bool IsLastStep => Current == Step.Help;

    public int Number => (int)Current + 1;

    public static string Title(Step step) => step switch
    {
        Step.Screens => "Screens",
        Step.Look => "Look",
        Step.Songs => "Songs",
        Step.Help => "Help",
        _ => "Welcome",
    };

    public void Advance()
    {
        if (!IsLastStep) Current++;
    }

    public void Back()
    {
        if (CanGoBack) Current--;
    }

    /// <summary>
    /// What to bring in. Starting from scratch keeps the Getting Started service and moves on, as
    /// on the Mac; importing or restoring stays on the step while it runs.
    /// </summary>
    public void Choose(ContentChoice choice)
    {
        Choice = choice;
        if (choice == ContentChoice.StartFromScratch)
        {
            IncludeGettingStarted = true;
            Advance();
        }
    }

    /// <summary>The library's "I've been welcomed" marker (the Mac's name for it).</summary>
    public const string MarkerFileName = ".onboarded-v1";

    /// <summary>
    /// Whether to show the guide: on a library that has just been made, until it's finished once.
    /// A library already in use is marked as welcomed (so updating the app never shows it to an
    /// operator mid-season), the Mac's <c>needsOnboarding</c> rule.
    /// </summary>
    public static bool Needed(string libraryRoot, bool libraryIsNew)
    {
        if (File.Exists(Path.Combine(libraryRoot, MarkerFileName))) return false;
        // Windows makes a new library's starter songs before the window opens, so "it has no songs"
        // (the Mac's test) can't tell a new library on the second launch. A pending marker can: the
        // guide comes back until it's finished, even if the app was closed half way.
        var pending = Path.Combine(libraryRoot, PendingFileName);
        if (libraryIsNew) Write(pending);
        if (File.Exists(pending)) return true;
        MarkDone(libraryRoot);
        return false;
    }

    private const string PendingFileName = ".onboarding-pending";

    public static void MarkDone(string libraryRoot)
    {
        Write(Path.Combine(libraryRoot, MarkerFileName));
        try { File.Delete(Path.Combine(libraryRoot, PendingFileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Write(string path)
    {
        try { File.WriteAllText(path, ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
