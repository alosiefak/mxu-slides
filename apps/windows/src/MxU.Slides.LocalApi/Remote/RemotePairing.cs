using System.Security.Cryptography;
using System.Text;

namespace MxU.Slides.LocalApi.Remote;

/// <summary>A pairing code on offer: shown in Settings until it's used, cancelled or runs out.</summary>
public sealed record RemotePairingCode(string Code, ApiScope Scope, DateTimeOffset Expires);

/// <summary>
/// Pairing a phone or tablet with a short code, so nobody has to type a 52-character key on a phone.
/// <list type="number">
/// <item>The operator presses "Pair a phone" in Settings and picks what it may do (View or Control).
/// That offers a 6-digit code for five minutes (<see cref="Begin"/>).</item>
/// <item>The phone opens /remote and types the code. If it matches, a new key with that scope is
/// made in the app's key list (so it can be revoked there like any other) and handed to the phone,
/// which keeps it in the browser's own storage. The code is then used up.</item>
/// </list>
/// The key never appears in a URL or a QR code, and this computer keeps only its salted hash
/// (<see cref="ApiTokenStore"/>), so it can't leak from here. Five wrong guesses burn the code
/// (a million codes, five tries: guessing is hopeless), and only one code is on offer at a time.
/// Thread-safe. Windows only.
/// </summary>
public sealed class RemotePairing
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaxTries = 5;

    private readonly Lock gate = new();
    private Pending? pending;

    private sealed record Pending(RemotePairingCode Offer, int TriesLeft);

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>A phone used a code. Raised on a server thread, with the key's record (never its secret).</summary>
    public event Action<ApiToken>? Paired;

    /// <summary>Offers a fresh code (replacing any other). The remote only views or controls, so Edit is offered as Control.</summary>
    public RemotePairingCode Begin(ApiScope scope)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var offer = new RemotePairingCode(code, scope == ApiScope.View ? ApiScope.View : ApiScope.Control, Clock.GetUtcNow() + Lifetime);
        lock (gate) pending = new Pending(offer, MaxTries);
        return offer;
    }

    public void Cancel()
    {
        lock (gate) pending = null;
    }

    /// <summary>The code on offer now, or null.</summary>
    public RemotePairingCode? Current
    {
        get
        {
            lock (gate)
            {
                if (pending is { } p && p.Offer.Expires <= Clock.GetUtcNow()) pending = null;
                return pending?.Offer;
            }
        }
    }

    /// <summary>What a phone's attempt came to.</summary>
    public enum Outcome { Paired, NoCode, Wrong, TooManyTries }

    public sealed record Result(Outcome Outcome, ApiToken? Token = null, string? Secret = null, int TriesLeft = 0);

    /// <summary>Checks a typed code; on a match makes the key, named after the device ("Browser remote: Ann's iPad").</summary>
    public Result Redeem(string? code, string? deviceName, ApiTokenStore tokens)
    {
        RemotePairingCode offer;
        lock (gate)
        {
            if (pending is not { } p || p.Offer.Expires <= Clock.GetUtcNow())
            {
                pending = null;
                return new Result(Outcome.NoCode);
            }
            var typed = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
            // Compared in constant time, so how long a wrong guess takes says nothing about the code.
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(typed.PadRight(6)), Encoding.ASCII.GetBytes(p.Offer.Code)))
            {
                var left = p.TriesLeft - 1;
                pending = left > 0 ? p with { TriesLeft = left } : null;
                return new Result(left > 0 ? Outcome.Wrong : Outcome.TooManyTries, TriesLeft: left);
            }
            pending = null;
            offer = p.Offer;
        }
        var (token, secret) = tokens.Create(KeyName(deviceName), offer.Scope);
        Paired?.Invoke(token);
        return new Result(Outcome.Paired, token, secret);
    }

    /// <summary>"Browser remote: " and the device's name, kept short and printable.</summary>
    public static string KeyName(string? deviceName)
    {
        var cleaned = new string((deviceName ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 40) cleaned = cleaned[..40].Trim();
        return "Browser remote: " + (cleaned.Length == 0 ? "phone" : cleaned);
    }
}
