using System.Security.Cryptography;
using System.Text;

namespace MxU.Slides.Production.Devices;

/// <summary>
/// Keeps device passwords (OBS's server password, vMix's web controller password) out of
/// production.json and out of the library. Each is encrypted with Windows' data protection
/// (DPAPI) for the current Windows user and kept as its own small file named by the device's
/// id, the same way Broadcast/Library/StreamKeyVault.cs keeps stream keys. Only this Windows
/// user on this PC can read them back. Passwords are never written to the log. Thread-safe.
/// </summary>
public sealed class DeviceSecrets(string folder)
{
    // Extra input to the encryption, so another app using DPAPI can't be tricked into decrypting ours.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MxU Slides production device password v1");

    private readonly Lock gate = new();

    /// <summary>Where the encrypted passwords live, e.g. %LOCALAPPDATA%\MxU Slides\ProductionSecrets.</summary>
    public string Folder { get; } = folder;

    private string PathFor(string deviceId)
    {
        // Ids are GUIDs in practice; anything else is made file-name safe.
        var safe = new string([.. deviceId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return Path.Combine(Folder, safe + ".key");
    }

    /// <summary>Stores (or, with null or empty, removes) a device's password.</summary>
    public void Set(string deviceId, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            Remove(deviceId);
            return;
        }
        var sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        lock (gate)
        {
            Directory.CreateDirectory(Folder);
            var path = PathFor(deviceId);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, sealedBytes);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>The device's password, or null when none is stored (or it can't be decrypted, e.g. another Windows user's).</summary>
    public string? Get(string deviceId)
    {
        byte[] sealedBytes;
        lock (gate)
        {
            var path = PathFor(deviceId);
            if (!File.Exists(path)) return null;
            try { sealedBytes = File.ReadAllBytes(path); }
            catch (IOException) { return null; }
        }
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public bool Contains(string deviceId)
    {
        lock (gate) return File.Exists(PathFor(deviceId));
    }

    public void Remove(string deviceId)
    {
        lock (gate)
        {
            var path = PathFor(deviceId);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
