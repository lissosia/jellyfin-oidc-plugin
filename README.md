# Jellyfin OpenID Connect plugin

This repository begins with a Jellyfin 12 and .NET 10 compatibility spike. It does not yet implement OIDC protocol handling. The first milestone checks whether a verified external identity can be adapted to Jellyfin's native authentication and session flow without changing Jellyfin itself.

## Phase 0 implementation

- A minimal .NET 10 plugin targeting the Jellyfin 12.2.0 API packages.
- `IPluginServiceRegistrator` registers a Jellyfin `IAuthenticationProvider`.
- An opt-in mock login issues a random, two-minute credential tied to an existing immutable Jellyfin user ID. The credential is stored as a SHA-256 digest, consumed atomically once, and rejected for other users.
- The Web configuration page can exercise Jellyfin's normal `POST /Users/AuthenticateByName` path and verifies the resulting session against a plugin endpoint. It does not display or persist the returned access token.
- The Web configuration page exercises Jellyfin's own Quick Connect initiation, authenticated authorization, and device-session endpoints; no replacement Quick Connect implementation is added.
- The page is served through the plugin configuration page extension (`IHasWebPages`). It does not inject JavaScript into Jellyfin Web's unauthenticated login page.

The mock flow is for an isolated local test server only. It is disabled by default, requires an administrator to issue a credential, and should be removed before a production release. It is not an OIDC implementation and must not be exposed to untrusted users.

## Build

Install the .NET 10 SDK, then run:

```sh
dotnet restore Jellyfin.Plugin.Oidc.slnx
dotnet build Jellyfin.Plugin.Oidc.slnx --no-restore
```

The build targets Jellyfin 12's `Jellyfin.Controller` and `Jellyfin.Model` 12.2.0 packages. Jellyfin assemblies are excluded from runtime output so the server supplies its own compatible assemblies.

## Phase 0 host check

1. Build the plugin and load it into a disposable Jellyfin 12.2 server.
2. Confirm the plugin and its service registrator load without a startup error.
3. Open **Dashboard → Plugins → OIDC Phase 0** and confirm the configuration page is served.
4. Enable the temporary mock flow, then choose a disposable existing user and run the native session check. It should report the same username after `/Users/AuthenticateByName` and the token-authenticated `who-am-i` request.
5. Confirm an expired credential, replayed credential, wrong-user credential, disabled mock option, and non-administrator ticket request fail.
6. With Quick Connect enabled, run the configuration page's Quick Connect check. Verify Jellyfin accepts the authenticated authorization and the issued device session resolves to the signed-in user.
7. Disable the mock option and confirm the endpoint returns not found. Confirm the normal Jellyfin login still works.

These host checks have not been run in this checkout. Completing them on a real Jellyfin 12.2 host is the exit criterion before starting Phase 1.

## Compatibility notes

The API surface was checked against Jellyfin's `release-12.z` source: `IAuthenticationProvider`, `IRequiresResolvedUser`, `IPluginServiceRegistrator`, `ISessionManager.AuthenticateNewSession`, and `IQuickConnect`. Jellyfin's own `/QuickConnect/Authorize` endpoint requires an authenticated principal; this spike calls that endpoint and leaves code generation, polling, authorization and device-session creation to Jellyfin.

Jellyfin exposes plugin configuration pages through `IHasWebPages`, which is the supported extension point used here. This does not prove that Jellyfin Web offers a supported third-party login-button hook; a login-page integration decision remains open for Phase 4.

All plugin implementation files in this repository are newly written for this project. Jellyfin API names and interfaces are used according to their public contracts; no source code from other OIDC plugins was copied. The repository's GPL-3.0 license is retained.
