# Lakeflow Console

A WinUI 3 desktop app (MSIX) for the IT team. Sign-in is Entra ID through the Windows account broker; what you can
do follows your Lakeflow group: **Viewer** (read), **Operator** (also pause, resume, resnapshot), **Admin** (also add
sites and sources, enroll agents, collect logs, restart agents).

The logic (API client, view models) is in `src/Lakeflow.Console.Core` and is tested on Linux against the real control
API and a running agent; `src/Lakeflow.Console` is the thin WinUI layer that binds to it.

## Pages

- **Fleet:** every site green, amber or red (the alert thresholds), worst first, with the reason, worst table lag,
  buffer depth, last heartbeat and agent version. Refreshes every 30 s; filter by name or state.
- **Site detail:** the agent, each table's state, method, lag, rows/min and last night's reconciliation; recent
  commands and events. Pause, resume and resnapshot per table (confirmed first); collect logs (download the zip from
  the command list) and restart.
- **Add a source (wizard):**
  1. Engine (SQL Server now; Db2 for i in phase 3; the others in phase 4), source id, source system, division.
  2. Connection as the scale PC sees it, and the read-only login. **Test connection** saves the source paused,
     seals the password to the site agent's certificate *in the console*, and has the agent connect. Neither the
     console nor the API keeps the password; only the agent can open it.
  3. The agent lists the tables the login can read (row counts from the catalog, never by counting) and suggests a
     method for each: Change Tracking where the owner already enabled it, else rowversion polling, else timestamp
     polling on an indexed date/time column; nothing that changes the source. Choose tables, then **Save and start
     snapshot**.

  Testing and listing run on the agent (sites accept no inbound connections): while a command waits, the API has the
  agent poll every 10 s instead of 60 s, so each step takes a few seconds.
- **Sites and enrollment:** add a site and generate its one-time enrollment token and install command.

## Setting up (once)

1. Entra ID: register a public client app for the console (redirect URI
   `ms-appx-web://microsoft.aad.brokerplugin/<client id>`), grant it the control API's `access_as_user` scope.
2. Fill `src/Lakeflow.Console/appsettings.json` (API address, tenant id, client id; no secrets) and build
   `windows/Lakeflow.Console.sln` in Release for x64.
3. Sign the MSIX with the company code-signing certificate (the manifest's `Publisher` must match its subject) and
   deploy it with Intune or winget.

Replace the placeholder tiles in `src/Lakeflow.Console/Assets` with Mercer Landmark artwork.

## Not in this step

The map view (WinUI 3 has no built-in map control; the fleet data carries latitude and longitude for one), table
detail, the alerts page and the audit log page come with phase 2.
