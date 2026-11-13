using System.Security.Cryptography;
using System.Text;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Assistant;

/// <summary>
/// Keeps the church's Anthropic API key on this PC, encrypted for the current Windows user with
/// DPAPI (Windows' own "protect this for me" service), as stream keys and the Planning Center
/// token are kept. Only this Windows user on this PC can read it back. It is never written to
/// the library (which may be synced or shared), to settings.json, to a log or into a web address:
/// it travels only in the request's <c>Authorization</c> header, which the SDK adds.
/// Thread-safe. Windows only.
/// </summary>
public sealed class AssistantKeyVault(string path)
{
    // Extra input to the encryption, so another app using DPAPI can't be tricked into decrypting ours.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MxU Slides assistant API key v1");

    private readonly Lock gate = new();
    private string? cached;
    private bool read;

    public bool HasKey => Load() is not null;

    public void Save(string key)
    {
        var plain = Encoding.UTF8.GetBytes(key.Trim());
        var sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            LibraryStore.AtomicWrite(path, sealedBytes);
            (cached, read) = (key.Trim(), true);
        }
    }

    /// <summary>The key, or null when there's none (or it can't be decrypted: another Windows user's file).</summary>
    public string? Load()
    {
        lock (gate)
        {
            if (read) return cached;
            read = true;
            try
            {
                if (!File.Exists(path)) return cached = null;
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                var key = Encoding.UTF8.GetString(plain).Trim();
                CryptographicOperations.ZeroMemory(plain);
                return cached = key.Length == 0 ? null : key;
            }
            catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
            {
                return cached = null;
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            (cached, read) = (null, true);
            foreach (var file in new[] { path, path + ".bak" })
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>"sk-ant-…h3Qk": enough to recognise the key in Settings, never enough to use it.</summary>
    public static string Masked(string key) => key.Length <= 12 ? "…" : $"{key[..7]}…{key[^4..]}";
}
