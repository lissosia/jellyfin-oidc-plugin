using Jellyfin.Plugin.Oidc.Authentication;

static void Check(bool passed, string label)
{
    if (!passed) throw new InvalidOperationException(label);
    Console.WriteLine("PASS: " + label);
}

var store = new MockLoginTicketStore();
var userId = Guid.NewGuid();
var ticket = store.Issue(userId);
Check(!store.TryConsume(ticket, Guid.NewGuid()), "wrong user rejected");
Check(store.TryConsume(ticket, userId), "intended user can consume after mismatch");
Check(!store.TryConsume(ticket, userId), "replay rejected");
Check(!store.TryConsume(string.Empty, userId), "empty credential rejected");
Check(!store.TryConsume("unknown", userId), "unknown credential rejected");
var concurrentTicket = store.Issue(userId);
var accepted = 0;
Parallel.For(0, 64, _ =>
{
    if (store.TryConsume(concurrentTicket, userId)) Interlocked.Increment(ref accepted);
});
Check(accepted == 1, "exactly one of 64 concurrent consumers succeeds");

if (args.Contains("--expiry"))
{
    var expiringTicket = store.Issue(userId);
    Console.WriteLine("Waiting for the real two-minute ticket lifetime…");
    await Task.Delay(TimeSpan.FromSeconds(121));
    Check(!store.TryConsume(expiringTicket, userId), "expired credential rejected");
}
