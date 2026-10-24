using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.ProImport;

/// <summary>
/// ProPresenter's group hot keys ("Verse 1 is A") brought into the library's group palette,
/// under the import's conflict policy. The import ledger remembers each letter an import set,
/// so "Update unedited" can tell an untouched letter from one the operator changed. Port of
/// GroupPalette.applyImportedHotKeys (ImportLedgerLogic.swift) and AppModel.applyImportedGroupHotKeys.
/// </summary>
public static class ProGroupHotKeys
{
    /// <summary>The ledger key prefix for hot keys ("hotkey.chorus"), as on the Mac.</summary>
    public const string LedgerPrefix = "hotkey.";

    /// <summary>
    /// Applies <paramref name="imported"/> (normalized group name → letter) to the palette and
    /// returns the ledger stamps to record (ledger key, letter).
    /// </summary>
    public static List<(string DocId, string Hash)> Apply(
        GroupPalette palette, IReadOnlyDictionary<string, string> imported, ImportConflictPolicy policy, ImportLedger ledger)
    {
        var stamps = new List<(string, string)>();
        foreach (var group in palette.Groups)
        {
            var name = HotKeyJump.NormalizedName(group.Name);
            if (GroupPalettes.ImportedLetter(imported, name) is not { } letter) continue;
            var stampId = LedgerPrefix + name;
            var mayWrite = policy switch
            {
                ImportConflictPolicy.Replace => true,
                ImportConflictPolicy.UpdateUnedited => group.HotKey is null || group.HotKey == ledger.Entries.FirstOrDefault(e => e.DocId == stampId)?.Hash,
                _ => group.HotKey is null,
            };
            if (!mayWrite) continue;
            if (HotKeyJump.DefaultHotKeys.GetValueOrDefault(name) == letter)
            {
                // The import agrees with the default: clear any override so the default applies.
                group.HotKey = null;
            }
            else if (group.HotKey != letter)
            {
                group.HotKey = letter;
                stamps.Add((stampId, letter));
            }
        }
        return stamps;
    }

    /// <summary>
    /// Applies imported hot keys to the library's palette and saves it and the ledger. Returns
    /// how many groups got a new letter. Ask means "Update unedited" here (one question per
    /// letter would be too many).
    /// </summary>
    public static int Apply(LibraryStore library, IReadOnlyDictionary<string, string> imported, ImportConflictPolicy policy)
    {
        if (imported.Count == 0) return 0;
        var palette = GroupPalettes.Current(library);
        var before = MxuJson.SerializeToUtf8(palette, indented: false);
        var ledger = ImportWriter.LoadLedger(library);
        var stamps = Apply(palette, imported, policy == ImportConflictPolicy.Ask ? ImportConflictPolicy.UpdateUnedited : policy, ledger);
        if (!MxuJson.SerializeToUtf8(palette, indented: false).AsSpan().SequenceEqual(before) || library.Get<GroupPalette>(GroupPalettes.WellKnownId) is null)
            library.Save(palette);
        if (stamps.Count > 0)
        {
            foreach (var (docId, hash) in stamps)
            {
                if (ledger.Entries.FirstOrDefault(e => e.DocId == docId) is { } entry) entry.Hash = hash;
                else ledger.Entries.Add(new ImportLedgerEntry { DocId = docId, Hash = hash });
            }
            LibraryStore.AtomicWrite(ImportWriter.LedgerPath(library), MxuJson.SerializeToUtf8(ledger));
        }
        return stamps.Count;
    }

    /// <summary>The hot keys of a whole import: the workspace's, then each presentation's (first one wins).</summary>
    public static Dictionary<string, string> Merged(IReadOnlyDictionary<string, string> workspace, IEnumerable<ProDocumentSummary> documents)
    {
        var merged = new Dictionary<string, string>(workspace, StringComparer.Ordinal);
        foreach (var document in documents)
            foreach (var (name, letter) in document.GroupHotKeys) merged.TryAdd(name, letter);
        return merged;
    }
}
