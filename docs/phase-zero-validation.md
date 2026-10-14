# Phase 0 validation

Validated on 2026-10-14 using .NET SDK 10.0.402 and a Jellyfin 12.2.0 Linux x64 UAT server. Installed plugin version: **0.1.0.1**.

## Packaging and loading

- Release build: zero warnings, zero errors.
- Release ZIP downloaded independently and matched the manifest's MD5 checksum.
- Jellyfin catalog discovered the repository, downloaded the package and installed it.
- After restart, `/Plugins` reported `OpenID Connect` version `0.1.0.1` as `Active`.
- Enabling the mock configuration exposed the registered provider through `/Auth/Providers`.
- Jellyfin served the embedded configuration page, and its controls executed in Jellyfin Web over private HTTP.

## Native session and authorization checks

| Check | Result |
| --- | --- |
| Mock issuance disabled by default | HTTP 404 |
| Anonymous credential issuance | HTTP 401 |
| Mock credential exchanged through `/Users/AuthenticateByName` | Native session issued |
| New token and matching device ID used against `/Users/Me` | Intended immutable user ID returned |
| Credential replay | HTTP 401 |
| Credential submitted for another mock-provider user | HTTP 401 |
| Intended user consumes credential after wrong-user attempt | Succeeded |
| Empty credential | HTTP 401 |
| Non-administrator tries issuing a mock credential | HTTP 403 |
| Anonymous Quick Connect authorization | HTTP 401 |
| Mock-authenticated user authorizes pending Quick Connect code | Succeeded |
| Device retrieves and uses native Quick Connect session | Intended user ID returned |
| Browser mock login button | Passed; test session logged out |
| Browser Quick Connect button | Passed; test session logged out |
| Local administrator recovery session | Worked; Default provider retained |

The browser checks use separate randomly generated device IDs. Explicit native session tokens are sent together with their device IDs; they do not replace the administrator's browser session. Device IDs use `crypto.getRandomValues()` so the check works over private HTTP as well as HTTPS.

## Ticket-store checks

`dotnet run --project tests/TicketStoreChecks -c Release -- --expiry` passed:

- Wrong user rejected; the intended user can still consume the ticket.
- Replay, unknown credentials and empty credentials rejected.
- Exactly one of 64 concurrent consumers succeeded.
- A ticket was rejected after its real two-minute lifetime elapsed.

## Final UAT state

Plugin `0.1.0.1` remains installed and Active. Mock login was restored to disabled. Test accounts were removed and test sessions logged out. The local administrator retains Jellyfin's Default provider. Existing Quick Connect configuration was preserved.

## Phase 1 boundary

This proves the native Jellyfin provider/session adapter and Quick Connect integration without a core modification. Mock identity issuance is an administrator-only development substitute for an OIDC callback. Real provider discovery, PKCE, state/nonce validation, token verification, identity linking and client/transaction binding still belong to Phase 1. This spike installs no additional cookie authentication scheme; protocol library selection can preserve the proven native session boundary.
