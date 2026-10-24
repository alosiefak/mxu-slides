using System.Security.Cryptography;
using System.Text;

namespace MxU.Slides.Broadcast.Library;

/// <summary>
/// Keeps stream keys out of the library. Each key is encrypted with Windows' data protection
/// (DPAPI) for the current Windows user and stored as its own small file, named by the
/// destination's id, under the app's local data folder. Only this Windows user on this PC can
/// decrypt them; the library JSON (which may be synced or shared) never holds them. The Mac
/// keeps the same secrets in the Keychain. Thread-safe.
/// </summary>
public sealed class StreamKeyVault
{
    // Extra input to the encryption, so another app using DPAPI can't be tricked into decrypting ours.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MxU Slides stream key v1");

    private readonly Lock gate = new();

    /// <param name="folder">Where the encrypted keys live, e.g. %LOCALAPPDATA%\MxU Slides\StreamKeys.</param>
    public StreamKeyVault(string folder) => Folder = folder;

    /// <summary>The default folder: StreamKeys under MXU_SLIDES_LOCAL (as the self-test sets it) or %LOCALAPPDATA%\MxU Slides.</summary>
    public static string DefaultFolder =>
        Path.Combine(
            Environment.GetEnvironmentVariable("MXU_SLIDES_LOCAL")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MxU Slides"),
            "StreamKeys");

    public string Folder { get; }

    private string PathFor(string destinationId)
    {
        // Ids are GUIDs in practice; anything else is made file-name safe.
        var safe = new string(destinationId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return Path.Combine(Folder, safe + ".key");
    }

    /// <summary>Stores (or, with null or empty, removes) the key for a destination.</summary>
    public void Set(string destinationId, string? streamKey)
    {
        if (string.IsNullOrEmpty(streamKey))
        {
            Remove(destinationId);
            return;
        }
        var sealedKey = ProtectedData.Protect(Encoding.UTF8.GetBytes(streamKey), Entropy, DataProtectionScope.CurrentUser);
        lock (gate)
        {
            Directory.CreateDirectory(Folder);
            var path = PathFor(destinationId);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, sealedKey);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>The key for a destination, or null if none is stored (or it can't be decrypted, e.g. another Windows user's).</summary>
    public string? Get(string destinationId)
    {
        byte[] sealedKey;
        lock (gate)
        {
            var path = PathFor(destinationId);
            if (!File.Exists(path)) return null;
            sealedKey = File.ReadAllBytes(path);
        }
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedKey, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public bool Contains(string destinationId)
    {
        lock (gate) return File.Exists(PathFor(destinationId));
    }

    public void Remove(string destinationId)
    {
        lock (gate)
        {
            var path = PathFor(destinationId);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
