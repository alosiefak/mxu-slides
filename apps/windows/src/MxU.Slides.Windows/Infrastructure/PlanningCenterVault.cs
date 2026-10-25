using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.PlanningCenter;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Keeps the church's Planning Center token (application id and secret) on this PC, encrypted for
/// the current Windows user with DPAPI, as <c>StreamKeyVault</c> keeps stream keys. Only this
/// Windows user on this PC can read it back; it is never written to the library (which may be
/// synced or shared), to settings.json, or to a log. The file lives in the local data folder
/// (<c>MXU_SLIDES_LOCAL</c>, so the self-test's scratch folder keeps it away from the real one).
/// Thread-safe. Windows only.
/// </summary>
public sealed class PlanningCenterVault(string path)
{
    // Extra input to the encryption, so another app using DPAPI can't be tricked into decrypting ours.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MxU Slides Planning Center token v1");

    private readonly Lock gate = new();

    public static string DefaultPath => Path.Combine(AppPaths.LocalRoot, "Connect", "planning-center.bin");

    private sealed record Stored(string ApplicationId, string Secret);

    public bool HasCredentials
    {
        get { lock (gate) return File.Exists(path); }
    }

    public void Save(PcoCredentials credentials)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Stored(credentials.ApplicationId.Trim(), credentials.Secret.Trim()));
        var sealedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(json);
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            LibraryStore.AtomicWrite(path, sealedBytes);
        }
    }

    /// <summary>The stored token, or null when there's none (or it can't be decrypted: another Windows user's).</summary>
    public PcoCredentials? Load()
    {
        byte[] sealedBytes;
        lock (gate)
        {
            if (!File.Exists(path)) return null;
            try { sealedBytes = File.ReadAllBytes(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        try
        {
            var json = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<Stored>(json);
            CryptographicOperations.ZeroMemory(json);
            return stored is null ? null : new PcoCredentials(stored.ApplicationId, stored.Secret);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try { File.Delete(path + ".bak"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
