using System.IO.Compression;
using System.Text;
using System.Windows;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Windows.Views.Notes;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Notes → slides (MxU PR #13), through the real windows: a Word file imported as a note, a line
/// marked by hand, Make Slides picking the rest and making a deck in the theme, the slides it
/// made, an edit to the note proposing an Update, applying it from Changes from the Notes (the
/// old words kept in the slide's history), and the note beside the slides with the live slide lit.
/// The grid's Update and Add badges are checked too. Pictures 120–129.
/// </summary>
public sealed partial class SelfTest
{
    private const string SermonText =
        "Unshakable\n"
        + "Hebrews 12 · Kingdom Come, week 4\n"
        + "Intro: the earthquake in 1989\n"
        + "Psalm 46:1 – God is our refuge and strength, a very present help in trouble.\n"
        + "1. Everything that can be shaken will be shaken\n"
        + "2. What God builds can’t be shaken\n"
        + "3. So we worship with reverence and awe\n"
        + "“Faith is taking the first step even when you don’t see the whole staircase.” — Martin Luther King Jr.\n"
        + "Next step: Write down one thing you’re holding onto.";

    private async Task NotesCheck()
    {
        NotesWindow? notes = null;
        NoteDocument? note = null;
        Presentation? deck = null;
        try
        {
            // A Word file, as a pastor would send it: read off the UI thread into a note.
            var docx = Path.Combine(Path.GetTempPath(), $"MxU self-test {Guid.NewGuid():N}", "Unshakable.docx");
            Directory.CreateDirectory(Path.GetDirectoryName(docx)!);
            WriteDocx(docx, SermonText.Split('\n'));
            note = await model.ImportNoteAsync(docx);
            Check("a Word file becomes a note in the library", note is not null && model.Library.Get<NoteDocument>(note.Id) is not null);
            if (note is null) return;
            // Nine lines: the three numbered points are one list, as on the Mac.
            Check("the note keeps the file's lines", note.Body?.Blocks.Count == 7 && note.Body.PlainText.Contains("Psalm 46:1", StringComparison.Ordinal));

            notes = NotesWindow.Open(window, model);
            notes.Select(note.Id);
            await Delay(500);
            // The quote is marked by hand; Make Slides picks the rest.
            var quote = note.Body!.Blocks.FindIndex(b => NoteReading.Text(b).StartsWith('“'));
            notes.SelectBlocks([quote]);
            notes.MarkSelected("quote");
            note = model.Library.Get<NoteDocument>(note.Id)!;
            var marked = NoteSlideMarks.Groups(note.Body!);
            Check("a line marked by hand is a Quote passage", marked.Count == 1 && marked[0].Type == "quote" && marked[0].State is null);
            await Delay(300);
            Save(notes, "120-notes-window");

            var make = MakeSlidesWindow.Open(notes, model, note);
            await Delay(1200);
            Save(make, "121-make-slides");
            make.MakeNow();
            note = model.Library.Get<NoteDocument>(note.Id)!;
            deck = model.DeckOf(note);
            Check("Make Slides makes a deck in the notes theme", deck is { Slides.Count: >= 5 } && deck.ThemeId == model.NotesThemeId);
            if (deck is null) return;
            var kinds = (note.MadePassages ?? []).Select(p => NoteSlideMarks.Groups(note.Body!).FirstOrDefault(g => g.Id == p.GroupId)?.Type).ToList();
            Check("the note remembers each passage and the slides it made",
                note.MadePassages is { Count: >= 5 } memory && memory.SelectMany(m => m.Slides).All(s => deck.Slides.Any(d => d.Id == s.SlideId)));
            Check("the title, scripture, points and quote each found their kind",
                kinds.Contains("title") && kinds.Contains("scripture") && kinds.Contains("points") && kinds.Contains("quote"));
            var scripture = deck.Slides.FirstOrDefault(s => s.Objects.Any(o => o.Text.Contains("Psalm 46:1", StringComparison.Ordinal)));
            Check("the scripture slide puts its reference in the design's own box",
                scripture is not null && scripture.ThemeSlideName is { Length: > 0 } && scripture.Objects.Any(o => o.Text == "Psalm 46:1"));
            Check("the run-sheet line (Intro: …) is no slide", !deck.Slides.Any(s => s.Objects.Any(o => o.Text.Contains("earthquake", StringComparison.Ordinal))));
            // Words that don't fit their box are cut off on the output: name the boxes if any do.
            // Only the boxes Make Slides wrote in count. A box still holding the design's own text
            // (the starter Quote design's big “ mark measures as too big for its box) is the theme's.
            var designs = model.ThemeById(deck.ThemeId)?.Slides ?? [];
            bool Written(Slide slide, SlideObject box) =>
                !(designs.FirstOrDefault(d => d.Name == slide.ThemeSlideName)?.Objects ?? []).Any(o => o.Name == box.Name && o.Text == box.Text);
            var cut = deck.Slides.SelectMany((slide, i) =>
                Core.Scene.SlideSceneBuilder.ClippedTextObjectIds(slide, model.SceneOf(deck, slide), Rendering.TextLayout.OverflowsFrame)
                    .Select(id => slide.Objects.First(o => o.Id == id)).Where(box => Written(slide, box))
                    .Select(box => $"slide {i + 1} “{box.Name}”")).ToList();
            Check($"no made slide cuts off the note's words{(cut.Count == 0 ? "" : $" ({string.Join(", ", cut)})")}", cut.Count == 0);
            await Snapshot("122-made-title", model.SceneOf(deck, deck.Slides[0]));
            if (scripture is not null) await Snapshot("123-made-scripture", model.SceneOf(deck, scripture));
            await Delay(400);
            Save(window, "124-slides-made-grid");

            // An edit to the note proposes an Update for the slide it made.
            notes.Select(note.Id);
            notes.EditText(text => text.Replace("strength, a very present help", "strength, an ever-present help", StringComparison.Ordinal));
            note = model.Library.Get<NoteDocument>(note.Id)!;
            var update = note.PendingUpdates?.FirstOrDefault(u => u.Kind == NotePendingUpdates.Words);
            Check("an edited passage proposes an Update to its slide (its mark found again by its words)", update is not null);
            notes.Select(note.Id);
            await Delay(400);
            Save(notes, "125-notes-update-badge");
            if (update is not null)
            {
                // The same change shows on the slide grid, as on the Mac: an orange Update on the slide it touches.
                Check("the slide the edit touches wears an Update badge in the grid",
                    model.Slides.Where(t => t.NoteUpdate == "Update").Select(t => t.Slide.Id).SequenceEqual([update.SlideId]));
                await Delay(300);
                Save(window, "128-grid-update-badge");
                var changes = NoteChangesWindow.Open(notes, model, note);
                await Delay(600);
                Save(changes, "126-changes-from-the-notes");
                model.ApplyNoteUpdate(note, update);
                changes.Close();
                deck = model.Library.Get<Presentation>(deck.Id)!;
                var slide = deck.Slides.First(s => s.Id == update.SlideId);
                Check("Apply puts the new words on the slide and keeps the old ones in its history",
                    slide.Objects.Any(o => o.Text.Contains("ever-present help", StringComparison.Ordinal))
                    && slide.History?.FirstOrDefault()?.Reason == NoteSlides.UpdatedReason);
                Check("nothing waits once it's applied", model.Library.Get<NoteDocument>(note.Id)!.PendingUpdates is null);
                Check("the grid's badge goes once the change is applied", model.Slides.Count > 0 && model.Slides.All(t => t.NoteUpdate is null && t.NoteAdds is null));

                // A new verse in the note is a new passage: an Add, shown on the slide it would follow.
                notes.Select(note.Id);
                notes.EditText(text => text + "\nHebrews 12:28 – Therefore let us be grateful for receiving a kingdom that cannot be shaken.");
                note = model.Library.Get<NoteDocument>(note.Id)!;
                var waiting = note.PendingUpdates ?? [];
                Check("a verse added to the note proposes an Add, shown on the slide it would follow",
                    waiting.Any(u => u.Kind == NotePendingUpdates.New) && model.Slides.Any(t => t.NoteAdds is not null));
                await Delay(300);
                Save(window, "129-grid-add-badge");
            }

            // Present beside the note: the live slide's line lights up.
            note = model.Library.Get<NoteDocument>(note.Id)!;
            var beside = NoteFollowAlongWindow.Open(notes, model, note);
            await Delay(500);
            var target = scripture ?? deck.Slides[^1];
            model.FireNoteSlide(deck.Id, target.Id);
            var lit = await Until(() => beside.LitLine, line => line is not null, 5000);
            Check("the note beside the slides lights the live slide's passage", lit is not null && model.LiveSlideIds?.SlideId == target.Id);
            await Delay(400);
            Save(beside, "127-present-beside-the-note");
            beside.Close();
        }
        finally
        {
            notes?.Close();
            model.ClearAllCommand.Execute(null);
            if (deck is not null) model.Library.Delete<Presentation>(deck.Id);
            if (note is not null) model.DeleteNote(note);
        }
    }

    /// <summary>A plain .docx of the lines (a paragraph each, the first a big bold title).</summary>
    private static void WriteDocx(string path, IReadOnlyList<string> lines)
    {
        var body = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var size = i == 0 ? 48 : 24;
            var bold = i == 0 ? "<w:b/>" : "";
            body.Append($"<w:p><w:r><w:rPr>{bold}<w:sz w:val=\"{size}\"/></w:rPr><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(lines[i])}</w:t></w:r></w:p>");
        }
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
        Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");
        Add("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>");
    }
}
