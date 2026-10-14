using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Oidc.Authentication;

/// <summary>Stores short-lived OIDC state and PKCE material for browser login transactions.</summary>
public sealed class OidcTransactionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, OidcTransaction> _transactions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Creates and stores a high-entropy transaction.</summary>
    public OidcTransaction Create(string redirectUri)
    {
        lock (_gate)
        {
            PruneExpired();
            if (_transactions.Count >= 2048) throw new InvalidOperationException("Too many OIDC sign-in attempts are already in progress.");
            var state = RandomToken(32);
            var transaction = new OidcTransaction(state, RandomToken(32), RandomToken(48), redirectUri, DateTimeOffset.UtcNow.Add(Lifetime));
            _transactions[state] = transaction;
            return transaction;
        }
    }

    /// <summary>Atomically consumes a transaction if it is still live.</summary>
    public bool TryConsume(string state, out OidcTransaction transaction)
    {
        transaction = null!;
        if (!_transactions.TryRemove(state, out var stored) || stored.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        transaction = stored;
        return true;
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _transactions)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _transactions.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string RandomToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A single-use browser authorization transaction.</summary>
public sealed record OidcTransaction(string State, string Nonce, string CodeVerifier, string RedirectUri, DateTimeOffset ExpiresAt);
