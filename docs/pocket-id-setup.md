# Pocket ID setup for Jellyfin UAT

This guide configures a single Pocket ID OIDC client for this Jellyfin plugin.

## 1. Create the Pocket ID OIDC client

In Pocket ID, create an OIDC client for Jellyfin. Use a confidential client if Pocket ID offers a client secret; the plugin also supports PKCE public clients without a secret.

In **Allowed User Groups**, grant access to `jellyfin_users` and `jellyfin_admins`. Pocket ID restricts new clients by default, so a user can be denied before Jellyfin sees the callback if the client has no allowed group. Assign `yeshwanth` to both groups.

No custom claims are required. Jellyfin requests `openid profile email groups`. Pocket ID returns `preferred_username`, `name`, and `picture` in the `profile` scope, and the names of assigned groups in the `groups` scope.

## 2. Configure Jellyfin

Open **Dashboard → Plugins → OpenID Connect**. Fill in:

| Setting | Value |
| --- | --- |
| Pocket ID issuer | The Pocket ID base URL, such as `https://id.example.com` |
| Public Jellyfin URL | The Jellyfin URL the browser uses, without a trailing path |
| Client ID | The value shown by the Pocket ID client |
| Client secret | The secret shown by Pocket ID, if the client has one |
| Required group | `jellyfin_users` |
| Create accounts for allowed group | Enabled |
| Administrator group | `jellyfin_admins` |
| Allow administrator mapping | Enabled |
| Avatar ownership | Import once |
| Pocket ID sign-in | Enabled |

Save. Copy the callback URI displayed on the page, then add that exact URI to the Pocket ID client's redirect URIs. The plugin protects the client secret with Jellyfin's data-protection keys and clears the input after saving.

The Pocket ID issuer must be reachable from the Jellyfin server. This UAT build expects the Pocket ID discovery, authorization, token, and signing-key endpoints to use the issuer's HTTPS host. Profile picture URLs must also be HTTPS on that host and return JPEG or PNG data. The importer accepts images up to 2 MiB and 4096 × 4096 pixels.

## 3. Test the login

From the plugin settings page, click **Sign in with Pocket ID**. Complete Pocket ID login and consent. Jellyfin validates the callback and creates `yeshwanth` if no Jellyfin account already has that username. The user is linked to Pocket ID using `(issuer, sub)`, receives administrator access from `jellyfin_admins`, and gets the Pocket ID profile picture when one is available.

The browser then switches to the new Jellyfin session. Confirm that the Web client shows `yeshwanth`, that the account has administrator access, and that the avatar appears. Keep another local Jellyfin administrator for recovery.

If an unlinked Jellyfin account already uses `yeshwanth`, the plugin rejects sign-in rather than claiming that account by username. Account linking is not part of this UAT milestone.

## UAT limitations

- The OIDC callback and user links are held in memory/configuration; starting a login and restarting Jellyfin before callback invalidates that transaction.
- User provisioning and administrator mapping are opt-in settings. Unknown groups grant no access.
- Administrator removal happens at the user's next successful login. Existing sessions may need to be revoked separately for immediate access removal.
- Avatar import supports JPEG and PNG from the Pocket ID issuer host. Other formats and external image hosts are skipped.
- Jellyfin's unauthenticated login-page button, explicit account linking, device login, periodic group refresh, and production threat review remain future work.
