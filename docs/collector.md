# The collector

The collector reads the site databases and sends their changes to Event Hubs. One central collector runs in Docker
on a server inside the WAN that can reach every site's SQL Server, the same kind of host as GrainTime's collector.

## Network

Outbound only; nothing listens.

| To | Port | For |
| --- | --- | --- |
| The control API (App Service) | 443 | config, heartbeats, events (client certificate) |
| Event Hubs namespace | 443 | change batches (AMQP over WebSockets, Send-only SAS) |
| Each site's SQL Server | 1433 or the instance's port (UDP 1434 for SQL Server Browser) | read-only queries |

## Install

1. In the console: **Sites and collectors → Enroll a central collector**. Copy the command it shows.
2. On the Docker host, from `deploy/collector`:
   ```bash
   LAKEFLOW_CONTROL_API="https://<control-api>/" LAKEFLOW_ENROLLMENT_TOKEN="<token>" docker compose up -d
   ```
   The token works once, within 24 hours. Once enrolled, later starts are `docker compose up -d`.
3. In the console, edit each site and choose the collector under **Read by collector**. The collector starts the site
   within a minute; add its sources with **Add source** on the site page.

## Volumes

- `data` (`/var/lib/lakeflow`): the collector's identity and cached config, and one folder per site (buffer,
  checkpoints, credentials), all in SQLCipher-encrypted databases.
- `secrets` (`/var/lib/lakeflow-secrets`): `master.key`, created on first start, owner-only. It protects the database
  keys and the passwords. Keep it on a separate volume and back it up separately: without it the data volume cannot
  be read (by anyone, including a restored collector).

Back up both together to restore a collector; or enroll a new one and assign the sites to it (passwords are entered
again, and each site carries on from where the control API's config says, with a fresh snapshot).

## Isolation between sites

Each site has its own pipeline, store, back-off and buffer cap. A site whose server is down backs off on its own
(up to 15 minutes) and turns red in the fleet view; the others are not slowed. Unassigning a site stops it; its data
stays on disk, so reassigning it resumes from its checkpoint.

## Logins

Use a read-only SQL login per source (see `docs/sqlserver-source-setup.md`). Windows (domain) logins work only with
Kerberos set up on the Docker host; prefer SQL logins.

## A site the centre cannot reach

Generate a token for that site (**Token for an agent at this site**) and install the agent on a PC there (Windows
service; the MSI comes with step 7). It is the same program serving that one site.
