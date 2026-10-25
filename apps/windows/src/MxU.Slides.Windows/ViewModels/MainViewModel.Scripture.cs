using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

// The Bible tool's half of the main view model: opening its window, putting its passages on
// screen, and keeping them (ScriptureViewModel has the rest).
public sealed partial class MainViewModel
{
    private ScriptureViewModel? scripture;

    /// <summary>The Bible tool (made the first time it's needed; it reads the Bible in the background then).</summary>
    public ScriptureViewModel Scripture => scripture ??= new ScriptureViewModel(this);

    /// <summary>Ctrl+B and the toolbar's Bible button.</summary>
    [RelayCommand]
    private void OpenBible()
    {
        if (System.Windows.Application.Current?.MainWindow is not { } owner) return;
        Views.BibleWindow.Open(owner, this);
    }

    /// <summary>
    /// Puts a slide of a Bible passage on screen. The passage's presentation isn't in the library,
    /// so it has no service row; the live label names it and Next asks the Bible tool what follows.
    /// </summary>
    internal void FireScripture(Presentation passage, int slide) => Fire(passage, null, slide, null);

    /// <summary>Next and Previous while a Bible passage is live (called from <c>Advance</c>): its next slide, then the next verse.</summary>
    private bool StepScripture(int direction) => scripture is { IsLive: true } bible && bible.Step(direction);

    /// <summary>Saves a passage's presentation in the library (once: saving the same passage again reuses it). Returns its name.</summary>
    internal string SaveScripture(Presentation passage)
    {
        var kept = Keep(passage);
        Notice = $"Saved “{kept.Name}” in the library.";
        return kept.Name;
    }

    /// <summary>Saves a passage's presentation and adds it to the open service, after the selected row.</summary>
    internal string AddScriptureToService(Presentation passage)
    {
        var kept = Keep(passage);
        if (SelectedService is null) NewService();
        if (SelectedService is not { } service) return kept.Name;
        InsertServiceItem(service, new ServiceItem
        {
            Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Presentation, Name = kept.Name, RefId = kept.Id,
        });
        Notice = $"Added “{kept.Name}” to {service.Name}.";
        return kept.Name;
    }

    // The same passage, in the same theme with the same words on each slide, is kept once.
    private Presentation Keep(Presentation passage)
    {
        static IEnumerable<string> Words(Presentation p) => p.Slides.SelectMany(s => s.Objects.Select(o => o.Text));
        var same = Library.All<Presentation>().FirstOrDefault(p => p.Name == passage.Name && p.ThemeId == passage.ThemeId
            && p.Origin?.Detail == passage.Origin?.Detail && Words(p).SequenceEqual(Words(passage)));
        if (same is not null) return same;
        Library.Save(passage);
        RefreshLibrary();
        return passage;
    }
}
