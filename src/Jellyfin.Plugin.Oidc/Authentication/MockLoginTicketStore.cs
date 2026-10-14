using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Oidc.Authentication;

/// <summary>
/// Stores short-lived, single-use credentials for the Phase 0 mock identity flow.
/// Only a digest of each credential is kept in memory.
/// </summary>
public sealed class MockLoginTicketStore
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a credential bound to one existing Jellyfin user.
    /// </summary>
    public string Issue(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user id is required.", nameof(userId));
        }

        PruneExpired();

        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var digest = Digest(credential);

        if (!_tickets.TryAdd(digest, new Ticket(userId, DateTimeOffset.UtcNow + TicketLifetime)))
        {
            throw new CryptographicException("Unable to allocate a unique mock login ticket.");
        }

        return credential;
    }

    /// <summary>
    /// Consumes a credential only when it is valid for the resolved Jellyfin user.
    /// </summary>
    public bool TryConsume(string credential, Guid userId)
    {
        if (string.IsNullOrEmpty(credential) || userId == Guid.Empty)
        {
            return false;
        }

        var digest = Digest(credential);
        if (!_tickets.TryGetValue(digest, out var ticket)
            || ticket.UserId != userId
            || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (ticket is not null && ticket.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                ((ICollection<KeyValuePair<string, Ticket>>)_tickets).Remove(new(digest, ticket));
            }

            return false;
        }

        return ((ICollection<KeyValuePair<string, Ticket>>)_tickets).Remove(new(digest, ticket));
    }

    private static string Digest(string credential)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (digest, ticket) in _tickets)
        {
            if (ticket.ExpiresAt <= now)
            {
                ((ICollection<KeyValuePair<string, Ticket>>)_tickets).Remove(new(digest, ticket));
            }
        }
    }

    private sealed record Ticket(Guid UserId, DateTimeOffset ExpiresAt);
}
