using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows;

public partial class App : Application
{
    private Mutex? singleInstance;

    /// <summary>True when the previous run ended without a clean shutdown (crash, power cut, killed).</summary>
    public static bool RecoveredFromCrash { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One copy per data folder: two copies writing one library would fight, but a self-test
        // (on its own throwaway folders) can run beside the real app, and the other way round.
        singleInstance = new Mutex(true, InstanceName(), out var isFirst);
        if (!isFirst)
        {
            // A self-test never waits on a dialog nobody will answer.
            if (SelfTest.FolderFromArgs(e.Args) is null)
                MessageBox.Show("MxU Slides is already running.", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Fatal error", args.ExceptionObject as Exception);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Background task failed", args.Exception);
            args.SetObserved();
        };

        Directory.CreateDirectory(AppPaths.LocalRoot);
        RecoveredFromCrash = File.Exists(AppPaths.RunningMarker);
        File.WriteAllText(AppPaths.RunningMarker, DateTime.Now.ToString("O"));
        Log.Info($"MxU Slides {typeof(App).Assembly.GetName().Version} starting{(RecoveredFromCrash ? " after an unexpected exit" : "")}");

        var settings = AppSettings.Load();
        var library = OpenLibrary(settings);
        if (library is null)
        {
            Shutdown();
            return;
        }

        try
        {
            var model = new MainViewModel(library, settings, AppSession.Load());
            var window = new MainWindow(model);
            MainWindow = window;
            if (SelfTest.FolderFromArgs(e.Args) is { } testFolder)
            {
                // Hooked before Show: a window raises Loaded during Show itself.
                window.ContentRendered += async (_, _) =>
                {
                    await new SelfTest(window, model, testFolder).RunAsync();
                    window.ForceClose();
                };
            }
            window.Show();
        }
        catch (Exception ex)
        {
            // Without a main window the app would linger invisibly; say what happened and quit.
            Log.Error("Could not open the main window", ex);
            Log.Flush();
            MessageBox.Show($"MxU Slides couldn't start: {ex.Message}\n\nDetails are in the log:\n{Log.CurrentFile}",
                "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>The single-copy lock's name, from this copy's data folder (its settings and logs).</summary>
    private static string InstanceName()
    {
        var folder = Path.GetFullPath(AppPaths.LocalRoot).TrimEnd('\\').ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(folder)))[..16];
        return $@"Local\MxU.Slides.Windows.SingleInstance.{hash}";
    }

    private static LibraryStore? OpenLibrary(AppSettings settings)
    {
        try
        {
            var library = new LibraryStore(settings.LibraryPath) { Prober = new Broadcast.Media.MediaImportProbe() };
            library.Load();
            if (library.IsNew || library.Count<Core.Model.Theme>() == 0) LibrarySeeder.Seed(library);
            else LibrarySeeder.RefreshStarterPacks(library);
            if (library.IsNew) StarterBackgrounds.AddTo(library);
            foreach (var problem in library.LoadProblems) Log.Warn($"Library: {problem}");
            try
            {
                if (library.BackupIfDue() is { } backup) Log.Info($"Backed up the library to {backup}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Daily backup skipped: {ex.Message}");
            }
            return library;
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the library", ex);
            MessageBox.Show($"MxU Slides could not open its library at\n{settings.LibraryPath}\n\n{ex.Message}",
                "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the show running: log it, tell the operator, carry on.
        Log.Error("Unhandled UI error", e.Exception);
        e.Handled = true;
        (MainWindow as MainWindow)?.ShowProblem($"Something went wrong: {e.Exception.Message}. The show is still running; details are in the log.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("MxU Slides exiting cleanly");
        try
        {
            File.Delete(AppPaths.RunningMarker);
        }
        catch
        {
        }
        Log.Flush();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
