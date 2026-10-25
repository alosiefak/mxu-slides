using System.Windows;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

// The timeline editor's checks: they drive the real editor window through its view model
// (the same calls the mouse makes) on the sample song, then put the song back as it was.
public sealed partial class SelfTest
{
    /// <summary>
    /// The timeline editor: opens it on the sample song, adds a music clip (a generated tone)
    /// and two slide cues at the playhead, drags the second cue later, trims the clip's start,
    /// undoes and redoes, then plays and checks the slides fire at their new times. Then it
    /// imports two stems, which make their tracks. Pictures of the editor are saved.
    /// </summary>
    private async Task TimelineEditorCheck()
    {
        var songRow = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
        if (songRow is null)
        {
            Skip("the timeline editor", "the sample song isn't in the service");
            return;
        }
        model.SelectedServiceItem = songRow;
        await Delay(300);
        if (model.CurrentPresentation is not { } song || model.Slides.Count < 3) return;
        var timelinesBefore = song.Timelines is null ? null : MxuJson.Clone(song.Timelines);
        var mapBefore = song.SongMap is null ? null : MxuJson.Clone(song.SongMap);
        var folder = Path.Combine(Path.GetTempPath(), $"MxU self-test timeline {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        TimelineEditorWindow? editor = null;
        var channelsBefore = model.Library.Get<TrackChannelList>(TrackChannelList.WellKnownId) is { } list ? MxuJson.Clone(list) : null;
        try
        {
            var tone = Path.Combine(folder, "MxU self-test timeline tone.wav");
            WriteWave(tone, seconds: 8, hertz: 330);
            model.ImportAudioFiles([tone]);
            var item = model.Library.All<AudioItem>().FirstOrDefault(a => a.Name == "MxU self-test timeline tone");
            Check("the timeline editor's test music imports", item is { DurationSeconds: > 7.5 });
            if (item is null) return;

            model.TimelineRewindCommand.Execute(null);
            editor = TimelineEditorWindow.Open(window, model);
            await Delay(800);
            var vm = editor.ViewModel;
            Check("the timeline editor opens on the open song", vm.Song?.Id == song.Id && vm.Layout.Rows.Any(r => r.Lane == TimelineLane.Slide));

            // Add the music at the start, then two slide cues: slide 2 at 1 s and slide 3 at 2 s.
            vm.Seek(0);
            vm.AddMusic(item);
            vm.Seek(1);
            vm.AddSlide(1);
            vm.Seek(2);
            vm.AddSlide(2);
            vm.Seek(0);
            var music = vm.Cues.FirstOrDefault(c => c.Kind() == TimelineLane.Audio && c.MediaId == item.Id);
            var slides = vm.Cues.Where(c => c.Kind() == TimelineLane.Slide).OrderBy(c => c.At).ToList();
            Check("+ on the Music lane adds the clip at the playhead", music is { At: 0, Duration: > 7.5 });
            Check("+ on the Slides lane adds slide cues at the playhead", slides.Count == 2 && slides[0].At == 1 && slides[1].At == 2);
            if (music is null || slides.Count != 2) return;
            vm.Waveforms.Settle(TimeSpan.FromSeconds(30));
            await Delay(500);
            Check("the clip's waveform is worked out off the UI thread", vm.Waveforms.Levels(vm.WaveSources([music]), music.At, vm.Layout.CardFor(music.Id)!.Width / vm.Zoom, music.Id) is not null);
            Save(editor, "20-timeline-editor");

            // Drag the second slide cue 1.5 seconds later, as the mouse would (press, move, let go).
            var second = slides[1];
            var pin = vm.Layout.CardFor(second.Id)!;
            var y = pin.Top + pin.Height / 2;
            var distance = 1.5 * vm.Zoom;
            vm.PointerPressed(pin.X, y);
            vm.PointerMoved(pin.X + 10, y, free: true);
            vm.PointerMoved(pin.X + distance, y, free: true);
            await Delay(200);
            Save(editor, "21-timeline-dragging");
            vm.PointerReleased(pin.X + distance, y, free: true);
            var moved = vm.Cues.First(c => c.Id == second.Id);
            Check("dragging a slide cue moves it, on the frame grid", Math.Abs(moved.At - 3.5) < 0.02 && moved.At == PresentationTimelines.SnappedToFrame(moved.At));
            Check("the dragged cue is selected", vm.Selection.SetEquals([second.Id]));

            // Trim the clip's start by half a second: its audio stays where it was.
            var clip = vm.Layout.CardFor(music.Id)!;
            var clipY = clip.Top + clip.Height / 2;
            Check("the clip's left edge trims", vm.Layout.CardAt(clip.Left + 3, clipY)?.Mode == TimelineBlockEdit.Mode.Trim);
            vm.PointerPressed(clip.Left + 3, clipY);
            vm.PointerMoved(clip.Left + 13, clipY, free: true);
            vm.PointerReleased(clip.Left + 3 + 0.5 * vm.Zoom, clipY, free: true);
            var trimmed = vm.Cues.First(c => c.Id == music.Id);
            Check("trimming the clip's start keeps its audio in place",
                Math.Abs(trimmed.At - 0.5) < 0.02 && Math.Abs((trimmed.InPoint ?? 0) - 0.5) < 0.02 && Math.Abs(trimmed.At + (trimmed.Duration ?? 0) - (music.At + music.Duration!.Value)) < 0.02);

            vm.Undo();
            Check("undo puts the clip back", vm.Cues.First(c => c.Id == music.Id).At == 0);
            vm.Redo();
            Check("redo trims it again", Math.Abs(vm.Cues.First(c => c.Id == music.Id).At - 0.5) < 0.02);
            vm.Select([music.Id]);
            vm.SetFade(vm.Cues.First(c => c.Id == music.Id), @out: false, TimelineClips.Shape.SCurve);
            Check("a clip's menu adds a fade in", vm.Cues.First(c => c.Id == music.Id).FadeIn is { Shape: "sCurve", Seconds: > 0 });
            await Delay(300);
            Save(editor, "22-timeline-edited");

            // Play from the start: the slides change by themselves, at their new times.
            model.ClearAllCommand.Execute(null);
            vm.GoToStart();
            vm.PlayOrPause();
            Check("the editor's play button plays the timeline", vm.IsPlaying);
            var fired = new List<(string SlideId, double At)>();
            var heardMusic = false;
            var pictured = false;
            var third = model.Slides[2].Slide.Id;
            var deadline = DateTime.Now.AddSeconds(15); // the cues are at 1 s and 3.5 s; the picture taken on the way can be slow
            while (DateTime.Now < deadline && !fired.Any(f => f.SlideId == third))
            {
                if (model.Show.Live?.Slide.Id is { } live && (fired.Count == 0 || fired[^1].SlideId != live)) fired.Add((live, vm.Playhead));
                heardMusic |= model.Audio.Status().Any(b => b.AudioItemId == item.Id && b.Playing);
                if (!pictured && vm.Playhead > 2.2)
                {
                    pictured = true;
                    Save(editor, "23-timeline-playing");
                }
                await Delay(20);
            }
            var slide2 = model.Slides[1].Slide.Id;
            var firstFire = fired.FirstOrDefault(f => f.SlideId == slide2);
            var secondFire = fired.FirstOrDefault(f => f.SlideId == third);
            Check($"playing fires slide 2 at 1.0 s (at {firstFire.At:0.00})", firstFire.SlideId is not null && Math.Abs(firstFire.At - 1.0) < 0.2);
            Check($"…and the dragged slide 3 at its new time, 3.5 s (at {secondFire.At:0.00})", secondFire.SlideId is not null && Math.Abs(secondFire.At - 3.5) < 0.2);
            Check("the music clip plays with the timeline", heardMusic);
            vm.PlayOrPause();
            model.TimelineRewindCommand.Execute(null);
            model.ClearAllCommand.Execute(null);

            // Stems: two files land on tracks made from their names, and show on their rows.
            var click = Path.Combine(folder, "Click.wav");
            var keys = Path.Combine(folder, "Keys.wav");
            WriteWave(click, seconds: 4, hertz: 1000);
            WriteWave(keys, seconds: 4, hertz: 220);
            var imported = new TaskCompletionSource();
            void Finished() => imported.TrySetResult();
            vm.ImportFinished += Finished;
            vm.ImportStems([click, keys], 0);
            await Task.WhenAny(imported.Task, Task.Delay(20000));
            vm.ImportFinished -= Finished;
            var stems = TimelineTracks.Stems(vm.Cues);
            var channels = model.TrackChannels;
            Check("importing stems makes their tracks (Click is a click track)",
                channels.Any(c => c.Name == "Click" && c.TrackRole() == TimelineTracks.Role.Click) && channels.Any(c => c.Name == "Keys"));
            Check("the stems land on their tracks at the playhead", stems.Count == 2 && stems.All(s => s.At == 0));
            Check("the stems are filed as the song's track set", model.Library.All<TrackSet>().Any(s => s.Stems.Count == 2 && s.Name.StartsWith(song.Name, StringComparison.Ordinal)));
            var routing = TrackRouting.Build(channels, model.TrackOutputs, 2);
            Check("the keys play on the house pair and the click never does",
                routing.Channels.Any(c => channels.First(k => k.Name == "Keys").Id == c.Id && c.Sends.ContainsKey(TrackRouting.MainMix))
                && routing.Channels.All(c => c.Id != channels.First(k => k.Name == "Click").Id || !c.Sends.ContainsKey(TrackRouting.MainMix)));
            vm.TracksExpanded = true;
            vm.Waveforms.Settle(TimeSpan.FromSeconds(30));
            await Delay(500);
            Save(editor, "24-timeline-stems");

            await TimelineTransitionCheck(editor);
            await TimelineDialogsCheck(editor);
        }
        finally
        {
            model.TimelineRewindCommand.Execute(null);
            model.ClearAllCommand.Execute(null);
            editor?.Close();
            if (model.Library.Get<Presentation>(song.Id) is { } restored)
            {
                restored.Timelines = timelinesBefore;
                restored.SongMap = mapBefore;
                model.Library.Save(restored);
            }
            if (channelsBefore is not null) model.Library.Save(channelsBefore);
            else model.Library.Delete<TrackChannelList>(TrackChannelList.WellKnownId);
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// The song's transition to the next item: a crossfade draws its curves and handover line,
    /// and dragging its handle sets how long it takes. The service item is put back after.
    /// </summary>
    private async Task TimelineTransitionCheck(TimelineEditorWindow editor)
    {
        var vm = editor.ViewModel;
        if (model.SelectedService is not { } service || vm.ServiceItem is not { } item
            || service.Items.FirstOrDefault(i => i.Id == item.Id) is not { } stored)
        {
            Skip("the song's transition in the editor", "the song isn't open from a service");
            return;
        }
        var before = stored.Transition;
        try
        {
            vm.SetTransition(new SongTransition { Kind = "crossfade", Seconds = 2 });
            Check("a crossfade draws its handover on the lanes", vm.ShownTransition is { Plan.Kind: SongTransitions.Kind.Crossfade } shown
                && Math.Abs(shown.Plan.End - shown.Plan.NextAt - 2) < 0.001);
            if (vm.TransitionHandle is { } handle)
            {
                vm.PointerPressed(handle.X, handle.Y);
                vm.PointerMoved(handle.X - 10, handle.Y);
                vm.PointerMoved(handle.X - vm.Zoom, handle.Y);
                vm.PointerReleased(handle.X - vm.Zoom, handle.Y);
                Check("dragging the transition's handle a second earlier makes it a second longer", vm.Transition?.Seconds == 3);
            }
            else
            {
                Check("the transition has a handle to drag", false);
            }
            await Delay(300);
            Save(editor, "25-timeline-transition");
        }
        finally
        {
            stored.Transition = before;
            model.Library.Save(service);
            model.Timeline.RefreshTransition();
        }
    }

    /// <summary>The editor's dialogs, pictured (each is shown without waiting, then closed): section map, timecode, outputs, stems, actions.</summary>
    private async Task TimelineDialogsCheck(TimelineEditorWindow editor)
    {
        var vm = editor.ViewModel;
        vm.Seek(0);
        vm.MakeSongMap();
        vm.Seek(1);
        vm.AddMarker();
        Check("a section map is made from the timeline's audio, and a marker added at the playhead",
            vm.SongMap is { Sections.Count: 1 } map && Math.Abs(map.Sections[0].Start - 1) < 0.001);
        vm.Seek(0);
        vm.AddActions([new SlideAction { Id = TimelineEditing.NewId(), Kind = SlideActionKind.ClearAll }]);
        var actions = vm.Cues.FirstOrDefault(c => c.Kind() == TimelineLane.Actions);
        Check("+ on the Actions lane adds an actions cue at the playhead", actions is { At: 0, Actions: [{ Kind: SlideActionKind.ClearAll }] });
        var plan = TimelineTracks.Plan([new TimelineStemImport("Shaker Loop.wav", "shaker", 4)], model.TrackChannels);
        var windows = new List<(Window Window, string Name)>
        {
            (SongMapWindow.Make(editor, vm), "26-timeline-section-map"),
            (TimecodeStartWindow.Make(editor, vm), "27-timeline-timecode-start"),
            (TrackOutputsWindow.Make(editor, model), "28-timeline-track-outputs"),
            (UnmatchedStemsWindow.Make(editor, plan, model.TrackChannels, _ => { }), "29-timeline-unmatched-stems"),
        };
        if (actions is not null) windows.Add((TimelineActionsWindow.Make(editor, vm, actions), "30-timeline-actions"));
        foreach (var (shown, name) in windows)
        {
            shown.Show();
            await Delay(500);
            Save(shown, name);
            shown.Close();
        }
        Save(editor, "31-timeline-map-and-actions");
    }

    /// <summary>A short sine-wave WAV file (16-bit stereo, 48 kHz).</summary>
    private static void WriteWave(string path, double seconds, double hertz)
    {
        const int rate = 48000;
        var frames = (int)(rate * seconds);
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + frames * 4);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)2);
        file.Write(rate);
        file.Write(rate * 4);
        file.Write((short)4);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(frames * 4);
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * hertz * i / rate) * 6000);
            file.Write(sample);
            file.Write(sample);
        }
    }
}
