using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A timer's name and readout, kept up to date four times a second (the self-test and older views read these).</summary>
public sealed partial class TimerRowViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Display { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial Brush DisplayBrush { get; set; } = Brushes.White;
}

/// <summary>
/// The show's timers: kept in <see cref="TimerController"/> (Core), saved between launches in
/// timers.json and their folders and order in timer-board.json, both beside the settings (they
/// belong to this PC, as the Mac keeps them in its own preferences). The Timers panel
/// (Views/Operator/TimersPanel.cs) draws them and calls the controller directly.
/// </summary>
public sealed partial class MainViewModel
{
    public TimerController Timers { get; } = new();

    public ObservableCollection<TimerRowViewModel> TimerRows { get; } = [];

    /// <summary>Raised each time the readouts refresh (four times a second): the Timers panel redraws its digits then.</summary>
    public event Action? TimersTicked;

    private static string TimersFile => Path.Combine(AppPaths.DataRoot, "timers.json");

    private static string TimerBoardFile => Path.Combine(AppPaths.DataRoot, "timer-board.json");

    private void InitializeTimers()
    {
        try
        {
            if (File.Exists(TimersFile))
                Timers.Restore(JsonSerializer.Deserialize<List<PersistedTimer>>(File.ReadAllText(TimersFile)) ?? []);
            if (File.Exists(TimerBoardFile))
                Timers.RestoreBoard(TimerBoard.FromJson(File.ReadAllText(TimerBoardFile)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Saved timers unreadable: {ex.Message}");
        }
        if (Timers.Timers.Count == 0)
        {
            Timers.AddCountdown("Sermon", 30 * 60);
            Timers.AddCountdown("Countdown", 5 * 60);
        }
        Timers.Changed += () =>
        {
            SaveTimers();
            SyncTimerRows();
            RenderStage(SceneTransition.Cut, refreshOnly: true);
        };
        SyncTimerRows();
    }

    private byte[]? savedTimers, savedBoard;

    private void SaveTimers()
    {
        try
        {
            // The service-tracking timers change several times a second and are never saved: a
            // refresh that leaves the user's timers and the board as they were writes nothing.
            var timers = JsonSerializer.SerializeToUtf8Bytes(Timers.Export());
            var board = System.Text.Encoding.UTF8.GetBytes(Timers.Board.ToJson());
            Directory.CreateDirectory(AppPaths.DataRoot);
            if (savedTimers is null || !timers.AsSpan().SequenceEqual(savedTimers))
            {
                LibraryStore.AtomicWrite(TimersFile, timers);
                savedTimers = timers;
            }
            if (savedBoard is null || !board.AsSpan().SequenceEqual(savedBoard))
            {
                LibraryStore.AtomicWrite(TimerBoardFile, board);
                savedBoard = board;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save timers: {ex.Message}");
        }
    }

    private void SyncTimerRows()
    {
        var ids = Timers.Timers.Select(t => t.Id).ToList();
        for (var i = TimerRows.Count - 1; i >= 0; i--)
            if (!ids.Contains(TimerRows[i].Id)) TimerRows.RemoveAt(i);
        for (var i = 0; i < ids.Count; i++)
        {
            var at = TimerRows.ToList().FindIndex(r => r.Id == ids[i]);
            if (at < 0) TimerRows.Insert(Math.Min(i, TimerRows.Count), new TimerRowViewModel(ids[i]));
            else if (at != i) TimerRows.Move(at, i);
        }
        TickTimers();
    }

    /// <summary>Refreshes the timer readouts (called four times a second).</summary>
    private void TickTimers()
    {
        var now = DateTimeOffset.Now;
        foreach (var row in TimerRows)
        {
            if (Timers.Snapshot(row.Id) is not { } timer) continue;
            row.Name = timer.Name;
            row.Display = TimerPanel.RowText(timer, now);
            row.IsRunning = timer.IsLive;
            row.DisplayBrush = TimerBrush(TimerPanel.RowTint(timer, now));
        }
        TimersTicked?.Invoke();
    }

    /// <summary>A readout's colour: the active warning's, else green while it runs, else the panel's quiet grey.</summary>
    public static Brush TimerBrush(TimerTint tint) =>
        tint.WarningHex is { } hex ? Painting.Brush(hex, SceneColor.White)
        : tint.IsLive ? Painting.Brush("#7ED957FF", SceneColor.White)
        : Painting.Brush("#A3A3A3FF", SceneColor.White);
}
