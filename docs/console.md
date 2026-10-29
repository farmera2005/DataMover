# Lakeflow Console

A web app at `https://<control-api>/console`, served by the control API. Sign-in is Entra ID; what you can do follows
your Lakeflow group: **Viewer** (read), **Operator** (also pause, resume, resnapshot), **Admin** (also sites,
collectors, sources, collect logs, restart).

The logic (API client, view models) is `src/Lakeflow.Console.Core`, tested against the real API and a running
collector; `src/Lakeflow.Console.Web` is the Blazor WebAssembly layer that renders it.

## Pages

- **Fleet:** every site green, amber or red, worst first, with the reason, worst lag, buffer, last heartbeat and
  collector version. Refreshes every 30 s; filter by name or state.
- **Site:** the collector serving it, each table's state, method, lag, rows/min and last night's reconciliation,
  recent commands and events. Pause, resume, resnapshot (confirmed first); collect logs (download from the command
  list); restart the collector.
- **Add source (wizard):** engine and ids; the connection as the collector reaches it and a read-only login (the
  password is sealed in your browser to the collector's certificate; the console and API never keep it); **Test
  connection** has the collector connect; it then lists the tables the login can read and suggests a capture method
  for each that needs no change to the source; choose tables and **Save and start snapshot**.
- **Sites and collectors:** add or edit sites and choose the collector that reads each; enroll a central collector
  (or an agent at a site the centre cannot reach) and get its one-time command.

## Setting up (once)

1. In Entra ID, register a single-page application for the console with redirect URI
   `https://<control-api>/console/authentication/login-callback`, and grant it the control API's delegated scope
   (e.g. `api://lakeflow-control/access_as_user`). Users get their Lakeflow role from the API's app roles.
2. Set the API's app settings `Console__ClientId` (that registration) and `Console__ApiScope` (that scope). The console
   reads them from `/console/appsettings.json`; nothing is built in.

The console pages are static and hold no data; every call goes to the API with the user's token. They are served with
a strict content security policy (scripts from the site only, sign-in frames from Entra only, no embedding).
