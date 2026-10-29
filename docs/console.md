# Lakeflow Console

A web app served by the control API: `https://<server>:8443/` (or `http://<server>:8090/`). Everything is set up here;
nothing is configured on the command line or in files.

## First run

The setup wizard appears on a fresh install:

1. **Administrator.** A local account that always signs in, even when directory sign-in is on or the directory is
   down. It must be created within an hour of the stack starting, so nobody else on the network can claim a fresh
   install; if that passes, restart the `api` container and reload.
2. **Event Hubs.** For each division, paste the connection string of a shared access policy on its hub with only
   **Send** (Azure portal: the hub, then Shared access policies). Can be skipped; sites wait until their hub is set.
3. **First site.** Id, name and time zone. The stack's built-in collector reads it.
4. **Done.** Go straight to the source wizard for that site.

## Roles

**Viewer** (read), **Operator** (also pause, resume, resnapshot), **Admin** (also sites, sources, collectors, users,
settings, collect logs, restart). Local accounts get their role in Configuration, then Users; directory users get theirs
from their groups at every sign-in.

## Pages

- **Fleet:** every site green, amber or red, worst first, with the reason, worst lag, buffer, last heartbeat and
  collector version. Refreshes every 30 s; filter by name or state.
- **Site:** the collector serving it, each table's state, method, lag, rows/min and last night's reconciliation,
  recent commands and events. Pause, resume, resnapshot (confirmed first); collect logs; restart the collector.
- **Add source (wizard):** engine and ids; the connection as the collector reaches it and a read-only login; **Test
  connection** has the collector connect; it lists the tables the login can read and suggests a capture method for each
  that needs no change to the source; choose tables and **Save and start snapshot**.
- **Configuration** (Admins), with a side menu:
  - **Sites and collectors:** add or edit sites and choose the collector that reads each (the built-in one by default);
    create the install file for a collector on another server (for sites this server cannot reach), or a token for an
    agent installed at a site.
  - **Destinations:** each division's Event Hub; the Fabric SQL endpoint for reconciliation results (with a Test).
  - **Users:** local accounts (create, role, disable, reset password); directory users after their first sign-in.
  - **Sign-in & LDAP:** directory sign-in (URL, CA certificate, service account, where users are, the Admin, Operator and
    Viewer groups), with a test sign-in that saves nothing.
  - **Server & certificate:** the server's name (used in certificates and install files); the certificate browsers see:
    upload your own (PFX), or trust the Lakeflow CA on PCs (download it here; IT can deploy it by group policy).

## HTTP or HTTPS

Both work. On HTTPS the browser seals source passwords itself before sending them; on plain HTTP passwords (including
sign-in) cross the network unencrypted, and the API seals source passwords on arrival.
Prefer HTTPS: trust the Lakeflow CA or upload a certificate and the browser warning goes away.

## How it is built

The logic (API client, view models) is `src/Lakeflow.Console.Core`, tested against the real API and a running
collector; `src/Lakeflow.Console.Web` is the Blazor WebAssembly layer that renders it. The pages are static and hold no
data; every call goes to the API with the session cookie. They are served with a strict content security policy
(scripts from this server only, no framing, no outside connections).
