# Decisions that differ from SPEC.md

SPEC.md is the starting brief. These are the places the build deliberately departs from it, and why. Newest first.

## 2026-10-29: Central collector instead of an agent at every site

**Decision.** One collector, in Docker on a server inside the WAN (like GrainTime's collector), reads every site's
databases directly. It is the same program as the agent: an enrolled identity serving the sites assigned to it in the
console, each site in its own pipeline, store and loops. The per-site install remains for a site the centre cannot
reach, or for local files (Access databases, CSV drop folders): an agent enrolled for one site.

**Why.** The central servers can already reach every grain, feed and agronomy SQL Server. Nothing to install, update or
enroll at 21 to 40 sites.

**Kept from the spec.** Outbound only from the collector (HTTPS to the control API and Event Hubs, SQL to the sites);
no inbound rules at the sites or the FortiGate. Read-only logins, bounded queries, throttling. No loss: a site whose
link drops simply lags; its data waits at the source and is caught up (Change Tracking within its retention, else a
resnapshot).

**Changed.** The site-compromise assumption moves: the collector holds every site's read-only login, so its host is
the asset to protect (separate secrets volume, nothing listening, patched host). Windows (domain) logins need
Kerberos on Linux; SQL logins are the default. Moving a site to another collector drops its passwords (they were
sealed to the old collector) and they are entered again.

## 2026-10-29: Web console instead of WinUI 3

**Decision.** The console is a Blazor WebAssembly app served by the control API at `/console`, signed in with Entra
ID in the browser. Same pages and roles as the spec's console.

**Why.** Nothing to package, sign or deploy to IT laptops; the logic was already a tested library.

**Kept.** Passwords are sealed in the browser (WebCrypto) to the collector's certificate; the API and its database
never see them in clear (the spec's "never stored in the control plane in clear text").

## 2026-10-28: Passwords sealed to the collector, not Key Vault references

The setup wizard sealing passwords to the collector's certificate replaces "stored as a Key Vault reference": it keeps
passwords out of the control plane entirely and needs no Azure identity on the collector. Key Vault still holds the
control plane's own secrets (agent CA, hub keys).

## 2026-10-28: Raw hub landing in Fabric

Eventstream lands each division hub in one raw table (`bronze.<division>_events`); the 5-minute notebook writes the
spec's `bronze.<division>_<source>_<table>_changes` tables and silver. New sites and tables need no Eventstream edits.

## 2026-10-28: Silver keeps source values as text, with tombstones

Silver columns are the agent's exact encoding (numbers keep every digit); typing is left to the semantic model and
Tableau, in line with the spec's "no transformations beyond raw landing". Deletes leave tombstones for 8 days so
at-least-once replays cannot resurrect rows. Envelopes gained `source_system`, `key_id`, and the `snapshot_complete`
and `reconcile` markers (nightly reconciliation compares at an exact point in the stream).

## 2026-10-28: Polling until vendors approve Change Tracking

The pilot uses rowversion or timestamp polling; the collector never enables Change Tracking or CDC itself. Agreed
with the business owner.
