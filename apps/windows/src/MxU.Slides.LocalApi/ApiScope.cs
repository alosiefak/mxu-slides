using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MxU.Slides.LocalApi;

/// <summary>What a token may do. Scopes nest: edit ⊃ control ⊃ view. Port of APIScope.</summary>
public enum ApiScope { View, Control, Edit }

public static class ApiScopes
{
    public static IReadOnlyList<ApiScope> All { get; } = Enum.GetValues<ApiScope>();

    public static string Raw(this ApiScope scope) => scope.ToString().ToLowerInvariant();

    public static bool Allows(this ApiScope scope, ApiScope required) => scope >= required;
}

/// <summary>A minted key. Only a salted hash of its secret is kept; <see cref="Prefix"/> is the first 9 characters, for display.</summary>
public sealed record ApiToken(
    string Id,
    string Name,
    ApiScope Scope,
    string SecretRecord,
    string Prefix,
    [property: JsonConverter(typeof(ApiReferenceDateConverter))] DateTimeOffset CreatedAt);

/// <summary>Salted SHA-256: <c>hex(salt) + "$" + hex(sha256(salt + secret))</c>, as on the Mac.</summary>
internal static class ApiSecretHash
{
    public static string Record(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexStringLower(salt) + "$" + Digest(secret, salt);
    }

    public static bool Matches(string secret, string record)
    {
        var parts = record.Split('$', 2);
        if (parts.Length != 2) return false;
        byte[] salt;
        try { salt = Convert.FromHexString(parts[0]); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Digest(secret, salt)), Encoding.ASCII.GetBytes(parts[1]));
    }

    private static string Digest(string secret, byte[] salt) =>
        Convert.ToHexStringLower(SHA256.HashData([.. salt, .. Encoding.UTF8.GetBytes(secret)]));
}

/// <summary>
/// Where the default key's secret lives so Settings can show it again (the Mac uses the
/// Keychain; the Windows app supplies its own, e.g. DPAPI). Port of APIDefaultKeyVault.
/// </summary>
public sealed record ApiDefaultKeyVault(Func<string?> Load, Action<string> Save, Action Clear)
{
    public static ApiDefaultKeyVault InMemory()
    {
        string? value = null;
        return new(() => value, secret => value = secret, () => value = null);
    }
}

/// <summary>
/// The app's access keys, saved as JSON (hashes only) in the same shape as the Mac's
/// local-api-tokens.json. Thread-safe. Port of APITokenStore.
/// </summary>
public sealed class ApiTokenStore
{
    public const string DefaultKeyName = "Default key";

    private static readonly JsonSerializerOptions FileOptions = new(ApiJson.Options) { WriteIndented = true };

    private readonly string filePath;
    private readonly ApiDefaultKeyVault vault;
    private readonly Lock gate = new();
    private readonly List<ApiToken> tokens;

    public ApiTokenStore(string filePath, ApiDefaultKeyVault? vault = null)
    {
        this.filePath = filePath;
        this.vault = vault ?? ApiDefaultKeyVault.InMemory();
        try
        {
            tokens = JsonSerializer.Deserialize<List<ApiToken>>(File.ReadAllBytes(filePath), FileOptions) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            tokens = [];
        }
    }

    public IReadOnlyList<ApiToken> All
    {
        get { lock (gate) return [.. tokens]; }
    }

    /// <summary>The default key's secret while that key still exists.</summary>
    public string? DefaultKeySecret
    {
        get
        {
            if (vault.Load() is not { } secret) return null;
            if (Verify(secret) is not null) return secret;
            vault.Clear();
            return null;
        }
    }

    /// <summary>
    /// Mints the Operate-level default key the first time, so a fresh install can connect
    /// straight away. Does nothing when hand-made keys exist, unless <paramref name="evenIfPopulated"/>.
    /// </summary>
    public string? EnsureDefaultKey(bool evenIfPopulated = false)
    {
        if (DefaultKeySecret is { } existing) return existing;
        if (All.Count > 0 && !evenIfPopulated) return null;
        var (_, secret) = Create(DefaultKeyName, ApiScope.Control);
        vault.Save(secret);
        return secret;
    }

    /// <summary>A new key. The secret (<c>mxu_</c> + 48 hex digits) is returned once and never stored.</summary>
    public (ApiToken Token, string Secret) Create(string name, ApiScope scope)
    {
        var secret = "mxu_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var token = new ApiToken(
            Guid.NewGuid().ToString().ToUpperInvariant(), name, scope,
            ApiSecretHash.Record(secret), secret[..9], DateTimeOffset.UtcNow);
        lock (gate)
        {
            tokens.Add(token);
            PersistLocked();
        }
        return (token, secret);
    }

    public void Rename(string id, string name)
    {
        lock (gate)
        {
            var index = tokens.FindIndex(t => t.Id == id);
            if (index < 0) return;
            tokens[index] = tokens[index] with { Name = name };
            PersistLocked();
        }
    }

    /// <summary>Removes the key; connections using it lose access immediately.</summary>
    public void Revoke(string id)
    {
        if (vault.Load() is { } secret && Verify(secret)?.Id == id) vault.Clear();
        lock (gate)
        {
            tokens.RemoveAll(t => t.Id == id);
            PersistLocked();
        }
    }

    public ApiToken? Verify(string secret)
    {
        lock (gate) return tokens.FirstOrDefault(t => ApiSecretHash.Matches(secret, t.SecretRecord));
    }

    /// <summary>Rechecks an authenticated session without keeping its secret.</summary>
    internal ApiToken? ActiveToken(string id)
    {
        lock (gate) return tokens.FirstOrDefault(t => t.Id == id);
    }

    private void PersistLocked()
    {
        try
        {
            if (Path.GetDirectoryName(filePath) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            var temp = filePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(tokens, FileOptions));
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Like the Mac: the keys stay usable in memory; the next change retries the write.
        }
    }
}

/// <summary>Swift's default Date encoding: seconds since 2001-01-01 UTC, as a number.</summary>
internal sealed class ApiReferenceDateConverter : JsonConverter<DateTimeOffset>
{
    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReferenceDate.AddSeconds(reader.GetDouble());

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value - ReferenceDate).TotalSeconds);
}
