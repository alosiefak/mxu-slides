using System.Text;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Saves a song from the editor, keeps services and the live output in step, and opens it.</summary>
    public void SaveSong(Presentation song, bool isNew)
    {
        Library.Save(song);
        Show.PresentationEdited(song);
        foreach (var service in Library.All<Service>())
        {
            var changed = false;
            foreach (var item in service.Items.Where(i => i.RefId == song.Id && i.ItemKind == ServiceItemKind.Presentation && i.Name != song.Name))
            {
                item.Name = song.Name;
                changed = true;
            }
            if (changed) Library.Save(service);
        }
        RefreshLibrary();
        Open(song, isNew ? null : CurrentContextItemId, song.DefaultArrangementId);
        if (Show.Live?.Presentation?.Id == song.Id) RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
        Notice = isNew ? $"Added \"{song.Name}\" to the library." : $"Saved \"{song.Name}\".";
    }

    /// <summary>Imports SongSelect, ChordPro or plain-text lyric files as songs.</summary>
    public void ImportLyricsFiles(IEnumerable<string> paths)
    {
        Presentation? last = null;
        var count = 0;
        var failed = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var text = ReadText(path);
                var song = LyricTextImporter.MakePresentation(text, Path.GetFileNameWithoutExtension(path),
                    themeId: Settings.DefaultThemeId, linesPerSlide: Settings.LinesPerSlide);
                song.PresentationKind = PresentationKind.Song;
                song.Origin = new PresentationOrigin
                {
                    Source = LyricTextImporter.DetectFormat(text) == LyricTextFormat.SongSelect
                        ? PresentationOriginSource.SongSelect
                        : PresentationOriginSource.LyricsFile,
                    Detail = Path.GetFileName(path),
                    CreatedAt = DateTimeOffset.Now.ToString("O"),
                };
                Library.Save(song);
                last = song;
                count++;
            }
            catch (Exception ex)
            {
                Log.Error($"Importing lyrics from {path} failed", ex);
                failed.Add(Path.GetFileName(path));
            }
        }
        RefreshLibrary();
        if (last is not null) Open(last, null, last.DefaultArrangementId);
        if (failed.Count > 0) Problem = $"Couldn't import: {string.Join(", ", failed)}";
        if (count > 0) Notice = count == 1 ? $"Imported \"{last!.Name}\"." : $"Imported {count} songs.";
    }

    /// <summary>Reads UTF-8 (with or without BOM) or UTF-16; falls back to Windows-1252 for old files.</summary>
    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
