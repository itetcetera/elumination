using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Drone.Server.Identity;

/// <summary>Who a player is. Rooms and seats only keep <see cref="Id"/>, so accounts can replace
/// guests later without touching them.</summary>
public interface IPlayerIdentity
{
    string Id { get; }
    string Name { get; }
}

public sealed record GuestIdentity(string Id, string Name) : IPlayerIdentity;

/// <summary>Hands out identities and finds them again from the token a browser keeps.</summary>
public interface IIdentityService
{
    /// <summary>A new guest with this name, and the secret token that proves it later.</summary>
    (IPlayerIdentity Identity, string Token) CreateGuest(string name);

    /// <summary>The identity a token belongs to, or null.</summary>
    IPlayerIdentity? Resolve(string? token);
}

/// <summary>Guests only: a random id and token, kept in memory.</summary>
public sealed class GuestIdentityService : IIdentityService
{
    readonly ConcurrentDictionary<string, IPlayerIdentity> _byToken = new();

    public (IPlayerIdentity Identity, string Token) CreateGuest(string name)
    {
        var identity = new GuestIdentity("g_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), CleanName(name));
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _byToken[token] = identity;
        return (identity, token);
    }

    public IPlayerIdentity? Resolve(string? token) =>
        token != null && _byToken.TryGetValue(token, out var id) ? id : null;

    /// <summary>Names are shown to the other players: letters, digits and spaces, up to 12 characters.</summary>
    public static string CleanName(string? name)
    {
        var s = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
        if (s.Length > 12) s = s[..12].TrimEnd();
        return s.Length == 0 ? "PLAYER" : s.ToUpperInvariant();
    }
}
