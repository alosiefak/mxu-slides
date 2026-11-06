using System.Globalization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Core.Scene;
using MxU.Slides.PptxImport;
using MxU.Slides.ProImport.Rtf;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Notes in the library, and making slides from them (the Mac's MxU PR #13). The logic is in
/// Core/Notes (ported from the Mac); this partial keeps the library side: new, pasted and
/// imported notes, the shared SlideBuildingSettings document, Make Slides, the note's memory of
/// what it made, and the changes the note's later edits propose. The windows in Views/Notes draw it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Raised when a note is added, saved or deleted, and when its proposed changes change.</summary>
    public event Action? NotesChanged;

    public IReadOnlyList<NoteDocument> Notes =>
        Library.All<NoteDocument>().Where(n => n.RemovedFromMxU != true).OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Where pictures read out of note files are kept on this PC (a note refers to them by file URL, as on the Mac).</summary>
    public string NoteMediaFolder => Path.Combine(AppPaths.LocalRoot, "Note media");

    public NoteDocument NewNote(string name = "New Note")
    {
        var note = LocalNotes.Note(name, null);
        SaveNote(note);
        return note;
    }

    /// <summary>A note from pasted text, named after its first line.</summary>
    public NoteDocument PasteNote(string text)
    {
        var note = LocalNotes.Note(LocalNotes.PastedName(text), NoteFileReader.Body(text));
        SaveNote(note);
        return note;
    }

    /// <summary>Import Notes… or a dropped file: read off the UI thread (a long PDF takes a moment), then saved.</summary>
    public async Task<NoteDocument?> ImportNoteAsync(string path)
    {
        var save = NoteMedia.Saver(NoteMediaFolder);
        var body = await Task.Run(() => ReadNoteFile(path, (data, name) => save(data, name)));
        if (body is null)
        {
            Problem = $"“{Path.GetFileName(path)}” has no words to make a note from. Notes can be made from Word, ODT, RTF, PDF, PowerPoint and text files.";
            return null;
        }
        var note = LocalNotes.Note(Path.GetFileNameWithoutExtension(path), body);
        SaveNote(note);
        return note;
    }

    /// <summary>Reads any note file: RTF and PowerPoint through their importers, the rest through Core's readers.</summary>
    internal static NoteBody? ReadNoteFile(string path, NoteFileReader.SaveMedia? save)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".rtf" => RtfNoteLines.Body(File.ReadAllBytes(path)) is { Blocks.Count: > 0 } rtf ? rtf : null,
                ".pptx" => PptxNoteText.Body(path) is { Blocks.Count: > 0 } deck ? deck : null,
                _ => NoteFiles.Read(path, save),
            };
        }
        // A damaged file (a PowerPoint the importer refuses, say) is no note; it never stops the app.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warn($"Couldn't read the note file {path}: {e.Message}");
            return null;
        }
    }

    public static bool IsNoteFile(string path) =>
        NoteFileReader.FileExtensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    /// <summary>Saves a note. A note that made slides then looks for the changes its edit proposes for them.</summary>
    public void SaveNote(NoteDocument note)
    {
        note.Body ??= new NoteBody { Blocks = [], PlainText = "" };
        note.Body.PlainText = NoteReading.PlainText(note.Body.Blocks);
        note.MxuUpdatedAt = Now();
        FindNoteChanges(note);
        Library.Save(note);
        NotesChanged?.Invoke();
    }

    public void DeleteNote(NoteDocument note)
    {
        Library.Delete<NoteDocument>(note.Id);
        NotesChanged?.Invoke();
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // MARK: The church's slide-building settings (one shared document, as on the Mac)

    public SlideBuildingSettings SlideBuilding =>
        Library.Get<SlideBuildingSettings>(SlideBuildingSettings.WellKnownId) ?? SlideBuildingSettingsLogic.New();

    public void SaveSlideBuilding(SlideBuildingSettings settings)
    {
        settings.Id = SlideBuildingSettings.WellKnownId;
        Library.Save(settings);
    }

    public SlideDesignMap DesignMap => SlideBuilding.SlideDesignMap();

    public void SaveDesignMap(SlideDesignMap map)
    {
        var settings = SlideBuilding;
        settings.SetSlideDesignMap(map);
        SaveSlideBuilding(settings);
    }

    /// <summary>The theme a note's slides are made in: the lyrics theme, else this PC's default theme.</summary>
    public string NotesThemeId
    {
        get
        {
            var id = SlideBuilding.NotesThemeId(Library.Get<Theme>(Settings.DefaultThemeId) is null ? null : Settings.DefaultThemeId);
            return id.Length > 0 && Library.Get<Theme>(id) is null ? "" : id;
        }
    }

    public Theme? ThemeById(string? id) => string.IsNullOrEmpty(id) ? null : Library.Get<Theme>(id);

    /// <summary>The planner Make Slides uses: the design map's options, measured with the app's own text layout.</summary>
    public DeterministicSlidePlanner NotePlanner(SlideOptions? options = null, IReadOnlyDictionary<string, double>? aspects = null, bool scaleChosen = false) =>
        new(options ?? DesignMap.Options, aspects, scaleChosen, TextLayout.OverflowsFrame);

    // MARK: Make Slides

    /// <summary>What the Make Slides window asks for.</summary>
    /// <param name="Body">The note with its passages marked (suggestions still "suggested" are accepted here).</param>
    /// <param name="AddToPresentationId">An existing deck to add to; null makes a new presentation.</param>
    /// <param name="AutoMark">How the passages were picked, so later Adds are picked the same way.</param>
    public sealed record MakeSlidesRequest(
        NoteDocument Note, NoteBody Body, string ThemeId,
        IReadOnlyDictionary<string, SlideRowOverride> Overrides,
        IReadOnlyDictionary<string, NoteSlideBuilder.SwitchOverride> Switches,
        string? AddToPresentationId, NoteSlideMarks.AutoMark AutoMark);

    /// <summary>Makes the slides, saves the deck and the note's memory of them, and opens the deck.</summary>
    public Presentation MakeSlides(MakeSlidesRequest request)
    {
        var note = request.Note;
        var original = note.Body ?? new NoteBody { Blocks = [], PlainText = "" };
        var body = NoteSlideMarks.AcceptingSuggestions(request.Body);
        var map = DesignMap;
        var mediaIds = ImportNoteMedia(note, body);
        var groups = NoteSlideBuilder.Groups(body);
        var target = request.AddToPresentationId is { } existingId ? Library.Get<Presentation>(existingId) : null;
        var themeId = target?.ThemeId ?? request.ThemeId;
        var built = NoteLiveUpdates.NewSlides(groups, themeId, map, ThemeById, NotePlanner(map.Options, NotePictureAspects(body)), mediaIds, request.Overrides, request.Switches);

        Presentation deck;
        if (target is not null)
        {
            deck = MxuJson.Clone(target);
            deck.Slides.AddRange(built.Slides);
            if (built.Sections.Count > 0) deck.Sections = [.. deck.Sections ?? [], .. built.Sections];
        }
        else
        {
            deck = new Presentation
            {
                Id = LibraryStore.NewId(), Name = note.Name, PresentationKind = PresentationKind.Deck, ThemeId = themeId,
                Slides = built.Slides, Sections = built.Sections.Count > 0 ? built.Sections : null,
                Origin = new PresentationOrigin { Source = PresentationOriginSource.MadeHere, Detail = "Notes", CreatedAt = Now() },
            };
            if (built.Arrangement is { } arrangement)
            {
                deck.Arrangements = [arrangement];
                deck.DefaultArrangementId = arrangement.Id;
            }
        }
        SavePresentation(deck);

        var memory = NoteLiveUpdates.Memory(body, deck, built.GroupBySlideId);
        if (target is not null && note.PresentationId == target.Id && note.MadePassages is { } earlier)
            memory = [.. earlier.Where(p => memory.All(m => m.GroupId != p.GroupId)), .. memory];
        note.Body = NoteLiveUpdates.SavedBody(body, deck.Id);
        note.PresentationId = deck.Id;
        note.MadePassages = memory;
        note.PendingUpdates = null;
        note.SkippedUpdates = null;
        note.SlidesMadeFromSubmittedAt = Now();
        note.BuildMode = request.AutoMark == NoteSlideMarks.AutoMark.Nothing
            ? NoteLiveUpdates.ByHand
            : NoteLiveUpdates.BuildMode(original, request.AutoMark switch
            {
                NoteSlideMarks.AutoMark.BoldLines => NoteSlideMarks.SuggestMode.BoldLines,
                NoteSlideMarks.AutoMark.Structure => NoteSlideMarks.SuggestMode.Structure,
                _ => NoteSlideMarks.SuggestMode.PickForMe,
            });
        SaveNote(note);
        SelectedServiceItem = null;
        Open(deck, null, null);
        return deck;
    }

    /// <summary>
    /// Brings the note's pictures and videos kept on this PC into the library (Media › Needs
    /// Sorted, as on the Mac), and remembers which item each became. Pictures on the web aren't
    /// fetched: their slides wait with the link in their notes.
    /// </summary>
    private Dictionary<string, string> ImportNoteMedia(NoteDocument note, NoteBody body)
    {
        var remembered = note.MediaImports ?? [];
        var landed = new List<NoteMediaImport>();
        foreach (var block in NoteMedia.Importable(body, remembered, id => Library.Get<MediaItem>(id) is not null))
        {
            if (NoteMedia.Key(block) is not { } key || block.Url is not { } url || NoteMedia.Resolve(url) is not { IsFile: true } uri) continue;
            try
            {
                if (Library.ImportMedia(uri.LocalPath) is not { } item) continue;
                if (item.Folder is null)
                {
                    item.Folder = NoteMedia.NeedsSorted;
                    Library.Save(item);
                }
                landed.Add(new NoteMediaImport { Key = key, MediaId = item.Id });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Couldn't bring a note's picture into the library: {e.Message}");
            }
        }
        note.MediaImports = NoteMedia.Remembering(remembered, landed, body);
        return note.MediaImports.Where(i => Library.Get<MediaItem>(i.MediaId) is not null).ToDictionary(i => i.Key, i => i.MediaId);
    }

    /// <summary>The picture's width over its height, for pictures kept on this PC (a tall picture takes Scale and Blur in a spot).</summary>
    public static Dictionary<string, double> NotePictureAspects(NoteBody body)
    {
        var aspects = new Dictionary<string, double>();
        foreach (var block in NoteMedia.LocalFiles(body))
        {
            if (NoteMedia.Key(block) is not { } key || block.Url is not { } url || NoteMedia.Resolve(url) is not { IsFile: true } uri) continue;
            try
            {
                using var file = File.OpenRead(uri.LocalPath);
                var head = new byte[64 * 1024];
                var read = file.Read(head, 0, head.Length);
                if (NoteFileReader.PictureSize(head.AsSpan(0, read)) is var (width, height) && height > 0) aspects[key] = width / (double)height;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return aspects;
    }

    // MARK: Changes from the notes

    /// <summary>The deck a note made, while it exists.</summary>
    public Presentation? DeckOf(NoteDocument note) => note.PresentationId is { } id ? Library.Get<Presentation>(id) : null;

    /// <summary>Compares the note with the slides it made, and keeps the changes it proposes (the Update and Add badges).</summary>
    private void FindNoteChanges(NoteDocument note)
    {
        if (DeckOf(note) is not { } deck || note.MadePassages is not { Count: > 0 } memory || note.Body is not { } body)
        {
            note.PendingUpdates = null;
            return;
        }
        var found = NoteChanges(note, deck, memory, body);
        if (found.Remarked is { } remarked) note.Body = NoteLiveUpdates.SavedBody(remarked, deck.Id);
        note.PendingUpdates = found.Updates.Count == 0 ? null : found.Updates;
    }

    private NoteLiveUpdates.Found NoteChanges(NoteDocument note, Presentation deck, IReadOnlyList<NoteMadePassage> memory, NoteBody body) =>
        NoteLiveUpdates.Changes(body, deck, memory, note.BuildMode, note.SkippedUpdates ?? [], DesignMap, ThemeById,
            (note.MediaImports ?? []).ToDictionary(i => i.Key, i => i.MediaId));

    /// <summary>Applies one proposed change to the deck (its old words kept in the slide's history), and remembers it.</summary>
    public void ApplyNoteUpdate(NoteDocument note, NotePendingUpdate update)
    {
        if (DeckOf(note) is not { } deck || note.Body is not { } body) return;
        var applied = NoteLiveUpdates.Applying(update, deck, note.MadePassages ?? [], body, note.BuildMode, Now());
        SavePresentation(applied.Deck);
        if (CurrentPresentation?.Id == applied.Deck.Id) Open(applied.Deck, CurrentContextItemId, SelectedArrangement?.Id);
        var waiting = (note.PendingUpdates ?? []).Where(u => u.Id != update.Id).ToList();
        note.Body = applied.Body is { } marked ? NoteLiveUpdates.SavedBody(marked, deck.Id) : body;
        note.MadePassages = NoteLiveUpdates.SettlingTexts(applied.Memory, note.Body, waiting);
        SaveNote(note);
    }

    /// <summary>Turns a proposed change down: it stays away until the passage's words change again.</summary>
    public void SkipNoteUpdate(NoteDocument note, NotePendingUpdate update)
    {
        if (DeckOf(note) is not { } deck || note.Body is not { } body) return;
        var found = NoteChanges(note, deck, note.MadePassages ?? [], body);
        note.SkippedUpdates = NoteLiveUpdates.Skipping(update.Id, found.Texts.GetValueOrDefault(update.Id) ?? "", note.SkippedUpdates ?? []);
        SaveNote(note);
    }

    // MARK: Present beside the note

    /// <summary>The note beside its slides: prose, passages with their slides, and the deck's other slides.</summary>
    public List<NoteFollowAlong.Item> FollowAlong(NoteDocument note)
    {
        var deck = DeckOf(note);
        var body = note.Body ?? new NoteBody { Blocks = [], PlainText = "" };
        Slide? Lookup(NoteFollowAlong.Slot slot) =>
            (slot.PresentationId == deck?.Id ? deck : Library.Get<Presentation>(slot.PresentationId))?.Slides.FirstOrDefault(s => s.Id == slot.SlideId);
        return NoteFollowAlong.Items(body, deck, note.MadePassages ?? [], Lookup,
            (group, slides) => NoteSlideUpdate.Replan(group, slides, ThemeById(slides.FirstOrDefault()?.ThemeId ?? deck?.ThemeId), DesignMap.Options, TextLayout.OverflowsFrame));
    }

    /// <summary>A slide's picture for a note window.</summary>
    public RenderScene? SlideScene(string presentationId, string slideId)
    {
        if (Library.Get<Presentation>(presentationId) is not { } deck || deck.Slides.FirstOrDefault(s => s.Id == slideId) is not { } slide) return null;
        return SceneOf(deck, slide);
    }

    public RenderScene SceneOf(Presentation deck, Slide slide) => SlideSceneBuilder.Scene(slide, ThemeFor(slide, deck), deck, null, pager: TextLayout.Pager);

    /// <summary>The live slide's presentation and id, for the note beside it.</summary>
    public (string PresentationId, string SlideId)? LiveSlideIds =>
        Live is { } live && Show.Live?.Slide is { } slide ? (live.PresentationId, slide.Id) : null;

    /// <summary>Puts a note's slide on screen (and opens its deck), as clicking it in the grid would.</summary>
    public void FireNoteSlide(string presentationId, string slideId)
    {
        if (Library.Get<Presentation>(presentationId) is not { } deck) return;
        var arranged = SlideSceneBuilder.ArrangedSlides(deck, null);
        var index = arranged.FindIndex(s => s.Id == slideId);
        if (index < 0) return;
        if (CurrentPresentation?.Id != deck.Id)
        {
            SelectedServiceItem = null;
            Open(deck, null, null);
        }
        Fire(deck, null, index, null);
    }
}
