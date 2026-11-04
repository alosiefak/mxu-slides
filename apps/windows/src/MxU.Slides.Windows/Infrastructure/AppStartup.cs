using System.Windows;
using System.Windows.Threading;
using MxU.Slides.Core.Diagnostics;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Phase 8's part of starting the app, called from three places in App.xaml.cs: the self-test's
/// throwaway-library rule (before anything opens), handing files to a copy that's already running
/// (a second copy started by a double-click in Explorer), and what starts once the window is up:
/// the hang watch, listening for files from later copies, importing files the app was started
/// with, automatic backups, and the welcome guide on a new library.
/// </summary>
internal static class AppStartup
{
    private static HangWatch? hangWatch;
    private static FileHandoff? handoff;

    /// <summary>
    /// True when the app was started as a self-test (--self-test) without a throwaway library: it
    /// then refuses, writing why into the report (so a script sees it) and saying so on screen
    /// when it was started by hand. Nothing of the library is opened.
    /// </summary>
    public static bool RefusesUnsafeSelfTest(string[] args)
    {
        if (SelfTest.FolderFromArgs(args) is not { } folder) return false;
        var problem = SelfTestSandbox.Problem(
            Environment.GetEnvironmentVariable("MXU_SLIDES_DATA"), Environment.GetEnvironmentVariable("MXU_SLIDES_LOCAL"),
            Environment.GetEnvironmentVariable(SelfTestSandbox.MarkerVariable),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppFolderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.AppFolderName),
            Path.GetTempPath());
        if (problem is null) return false;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "report.txt"), $"MxU Slides self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nFAIL  refused to run: {problem}\nRESULT: FAIL (refused: not a throwaway library)\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (Environment.GetEnvironmentVariable(SelfTestSandbox.MarkerVariable) is null && Environment.UserInteractive)
            MessageBox.Show(problem, "MxU Slides self-test", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    /// <summary>
    /// A second copy: hands the files it was started with to the running one (which imports them and
    /// comes to the front). True when that worked and this copy should quietly go.
    /// </summary>
    public static bool HandedToRunningCopy(string instanceName, string[] args) =>
        SelfTest.FolderFromArgs(args) is null && FileHandoff.SendToRunningCopy(instanceName, OpenRequests.Files(args, Environment.CurrentDirectory));

    /// <summary>What starts once the main window is showing.</summary>
    public static void AfterWindow(MainWindow window, MainViewModel model, string[] args, string instanceName)
    {
        var selfTest = SelfTest.FolderFromArgs(args) is not null;
        hangWatch = HangWatch.Start(window.Dispatcher, captureStacks: !selfTest);
        handoff = FileHandoff.Listen(instanceName, files => window.Dispatcher.BeginInvoke(() => window.OpenFromOutside(files)));
        var files = OpenRequests.Files(args, Environment.CurrentDirectory);
        if (files.Count > 0) window.Dispatcher.BeginInvoke(() => window.OpenFromOutside(files), DispatcherPriority.ApplicationIdle);
        if (selfTest) return; // the self-test makes its own backups and opens the guide itself
        model.StartAutomaticBackups();
        if (OnboardingFlow.Needed(model.Library.Root, model.Library.IsNew))
            window.Dispatcher.BeginInvoke(() => OnboardingWindow.Open(window, model), DispatcherPriority.ApplicationIdle);
    }
}
