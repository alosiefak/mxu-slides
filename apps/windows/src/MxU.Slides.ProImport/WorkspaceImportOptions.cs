using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.ProImport;

/// <summary>What to do when an imported document is already in the library.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImportConflictPolicy>))]
public enum ImportConflictPolicy
{
    /// <summary>Ask <see cref="ImportConflictHandler"/> about each one; without a handler, keep the library's copy.</summary>
    [JsonStringEnumMemberName("ask")] Ask,
    /// <summary>Keep mine — only add new documents.</summary>
    [JsonStringEnumMemberName("keepMine")] KeepMine,
    /// <summary>Update unedited — refresh earlier imports that haven't been changed here since.</summary>
    [JsonStringEnumMemberName("updateUnedited")] UpdateUnedited,
    /// <summary>Replace with imported.</summary>
    [JsonStringEnumMemberName("replace")] Replace,
}

/// <summary>A document the import would overwrite. <see cref="EditedSinceImport"/> is true when it was changed here after an earlier import (or wasn't imported at all).</summary>
public sealed record ImportConflict(Type DocumentType, string Id, string Label, bool EditedSinceImport);

/// <summary>Return true to overwrite the library's copy. Called on the import's worker thread.</summary>
public delegate ValueTask<bool> ImportConflictHandler(ImportConflict conflict, CancellationToken cancellationToken);

/// <summary>Options for importing .pro documents, playlist bundles and themes.</summary>
public sealed record ProImportOptions
{
    public ImportConflictPolicy Policy { get; init; } = ImportConflictPolicy.Ask;
    public ImportConflictHandler? ConfirmOverwrite { get; init; }
    /// <summary>The library folder imported presentations are filed under.</summary>
    public string? Folder { get; init; } = ProPresenterImporter.DefaultFolder;
}

/// <summary>Which parts of a ProPresenter workspace to import. Serialises with the Mac's keys; missing keys keep their defaults.</summary>
public sealed record WorkspaceImportOptions
{
    [JsonPropertyName("policy")] public ImportConflictPolicy Policy { get; set; } = ImportConflictPolicy.Ask;
    [JsonPropertyName("presentations")] public bool Presentations { get; set; } = true;
    [JsonPropertyName("presentationMedia")] public bool PresentationMedia { get; set; } = true;
    [JsonPropertyName("media")] public bool Media { get; set; } = true;
    [JsonPropertyName("themes")] public bool Themes { get; set; } = true;
    [JsonPropertyName("overlays")] public bool Overlays { get; set; } = true;
    [JsonPropertyName("alerts")] public bool Alerts { get; set; } = true;
    [JsonPropertyName("confidenceLayouts")] public bool ConfidenceLayouts { get; set; } = true;
    [JsonPropertyName("combos")] public bool Combos { get; set; } = true;
    [JsonPropertyName("playlists")] public bool Playlists { get; set; } = true;
    [JsonPropertyName("schedules")] public bool Schedules { get; set; } = true;
    [JsonPropertyName("groupHotKeys")] public bool GroupHotKeys { get; set; } = true;
    [JsonPropertyName("screens")] public bool Screens { get; set; } = true;
    [JsonPropertyName("screenCorrections")] public bool ScreenCorrections { get; set; } = true;
    [JsonPropertyName("screenSlices")] public bool ScreenSlices { get; set; } = true;
    [JsonPropertyName("screenMasks")] public bool ScreenMasks { get; set; } = true;
    [JsonPropertyName("looks")] public bool Looks { get; set; } = true;
    [JsonPropertyName("stageAssignments")] public bool StageAssignments { get; set; } = true;
    [JsonPropertyName("timers")] public bool Timers { get; set; } = true;
    [JsonPropertyName("videoInputs")] public bool VideoInputs { get; set; } = true;
    [JsonPropertyName("midiDevices")] public bool MidiDevices { get; set; } = true;
}

/// <summary>How many of each thing a workspace holds, for the options screen.</summary>
public sealed record WorkspaceScan
{
    public int Presentations { get; set; }
    public int Media { get; set; }
    public int Themes { get; set; }
    public int Props { get; set; }
    public int Messages { get; set; }
    public int StageLayouts { get; set; }
    public int Macros { get; set; }
    public int Playlists { get; set; }
    public int CalendarEvents { get; set; }
    public int GroupHotKeys { get; set; }
    public int Timers { get; set; }
    public int VideoInputs { get; set; }
    public int MidiDevices { get; set; }
    public int Screens { get; set; }
    public int Looks { get; set; }
}

public enum ImportSkipReason
{
    /// <summary>Already in the library; kept the library's copy.</summary>
    Existing,
    /// <summary>Edited here since it was imported; kept the edits.</summary>
    Edited,
}

/// <summary>
/// Writes imported documents through the conflict policy, and keeps the import ledger: a hash of each
/// document as imported, so a later import can tell an untouched import from one edited here.
/// </summary>
internal sealed class ImportWriter
{
    public const string LedgerFileName = "ImportLedger.json";
    private const string LedgerId = "import-ledger";

    private readonly LibraryStore library;
    private readonly ImportConflictHandler? confirm;
    private readonly ImportLedger ledger;
    private bool ledgerDirty;

    public ImportWriter(LibraryStore library, ImportConflictPolicy policy, ImportConflictHandler? confirm = null)
    {
        this.library = library;
        Policy = policy;
        this.confirm = confirm;
        ledger = LoadLedger(library);
    }

    public ImportConflictPolicy Policy { get; }
    public int SkippedExisting { get; private set; }
    public List<string> SkippedEdited { get; } = [];

    public static string LedgerPath(LibraryStore library) => Path.Combine(library.Root, LedgerFileName);

    public static ImportLedger LoadLedger(LibraryStore library)
    {
        try
        {
            var path = LedgerPath(library);
            if (File.Exists(path)) return MxuJson.Deserialize<ImportLedger>(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            // A damaged ledger only means earlier imports count as edited; start a fresh one.
        }
        return new ImportLedger { Id = LedgerId, Entries = [] };
    }

    public static string? Fingerprint<T>(T value)
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(MxuJson.SerializeToUtf8(value, indented: false)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool IsUnedited(string id, string? hash) =>
        hash is not null && ledger.Entries.FirstOrDefault(e => e.DocId == id)?.Hash == hash;

    private void Stamp(string id, string hash)
    {
        var entry = ledger.Entries.FirstOrDefault(e => e.DocId == id);
        if (entry is null) ledger.Entries.Add(new ImportLedgerEntry { DocId = id, Hash = hash });
        else entry.Hash = hash;
        ledgerDirty = true;
    }

    /// <summary>Null when the document may be written; otherwise why it was skipped.</summary>
    public async ValueTask<ImportSkipReason?> CheckAsync<T>(string id, string label, CancellationToken cancellationToken)
        where T : class, ILibraryDocument
    {
        if (library.Get<T>(id) is not { } existing) return null;
        var unedited = IsUnedited(id, Fingerprint(existing));
        switch (Policy)
        {
            case ImportConflictPolicy.KeepMine:
                SkippedExisting++;
                return ImportSkipReason.Existing;
            case ImportConflictPolicy.UpdateUnedited:
                if (unedited) return null;
                SkippedEdited.Add(label);
                return ImportSkipReason.Edited;
            case ImportConflictPolicy.Replace:
                return null;
            default:
                if (confirm is not null
                    && await confirm(new ImportConflict(typeof(T), id, label, !unedited), cancellationToken).ConfigureAwait(false))
                    return null;
                if (unedited)
                {
                    SkippedExisting++;
                    return ImportSkipReason.Existing;
                }
                SkippedEdited.Add(label);
                return ImportSkipReason.Edited;
        }
    }

    public bool Write<T>(T value, string label, List<string> warnings) where T : class, ILibraryDocument
    {
        try
        {
            library.Save(value);
            if (Fingerprint(value) is { } hash) Stamp(value.Id, hash);
            return true;
        }
        catch (Exception error)
        {
            warnings.Add($"{label} could not be saved: {error.Message}");
            return false;
        }
    }

    public void SaveLedger()
    {
        if (!ledgerDirty) return;
        Directory.CreateDirectory(library.Root);
        LibraryStore.AtomicWrite(LedgerPath(library), MxuJson.SerializeToUtf8(ledger));
        ledgerDirty = false;
    }

    public void AppendSkipSummary(List<string> warnings)
    {
        if (SkippedExisting > 0)
            warnings.Add($"{SkippedExisting} already here — kept yours (policy: only add new)");
        if (SkippedEdited.Count > 0)
            warnings.Add($"{SkippedEdited.Count} skipped — edited here since import: {string.Join(", ", SkippedEdited)}");
    }
}
