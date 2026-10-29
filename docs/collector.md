# The collector

The collector reads the site databases and sends their changes to Event Hubs. The Docker stack includes one (the
built-in collector), which enrolls on its own at first start and reads every site its server can reach. Add a collector
on another server only for sites it cannot reach.

## Network

Outbound only; nothing listens.

| To | Port | For |
| --- | --- | --- |
| The control API's collectors' port | 8444 | config, heartbeats, events (client certificate) |
| Event Hubs namespace | 443 | change batches (AMQP over WebSockets, Send-only SAS) |
| Each site's SQL Server | 1433 or the instance's port (UDP 1434 for SQL Server Browser) | read-only queries |

## Install

The built-in collector needs nothing: `docker compose up -d --build` starts it, and it enrolls through a volume only it
and the control API share. It appears under Configuration, Sites and collectors, and new sites default to it.

A collector on another server:

1. In the console: **Configuration → Sites and collectors → Collector on another server**. Download
   `lakeflow-collector.env`; it holds the address, a one-time token (24 hours) and the SHA-256 of your stack's CA.
2. On that server, with this repository's `deploy/collector` folder and the file next to its `compose.yaml`:
   ```bash
   docker compose --env-file lakeflow-collector.env up -d --build
   ```
   It downloads the stack's CA, checks it against that SHA-256, enrolls, and from then on trusts only that CA. Later
   starts are `docker compose up -d`; the file can be deleted.
3. In the console, edit each site it should read and choose it under **Read by collector**.

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

Install the agent on a Windows PC at the site: the same program, as a Windows service serving that one site.

1. In the console: **Configuration → Sites and collectors**, edit the site, **Token for an agent at this site**. It
   shows the command, with a one-time token (24 hours) and the SHA-256 of your stack's CA.
2. On the PC, as an administrator, with `LakeflowAgent.msi` (from CI's `LakeflowAgent-msi` artifact):
   ```
   msiexec /i LakeflowAgent.msi /qn CONTROL_API="https://<server>:8444/" ENROLLMENT_TOKEN="<token>" CA_SHA256="<sha-256>"
   ```
3. The service (**Lakeflow Agent**) starts, enrolls, and the site switches to it.

What the MSI sets up (`installer/Package.wxs`):

- the service `LakeflowAgent`, automatic (delayed) start, restarted by Windows if it stops with an error, running as its
  own virtual account `NT SERVICE\LakeflowAgent` (no password, no rights beyond its own folders);
- `C:\ProgramData\Lakeflow\Agent` for its buffer, checkpoints, credentials (DPAPI) and 14 days of logs, readable only by
  that account, SYSTEM and Administrators;
- the token in a registry key only they can read; the agent enrolls with it and deletes it. The token is a hidden MSI
  property, so it never appears in an MSI log.

Network: outbound only, 8444 to the Lakeflow server, 443 to Event Hubs, and SQL to the site's databases.
Uninstalling removes the program and the service but keeps the data folder, whose buffer may hold changes not yet sent.
Upgrades: install a newer MSI over it (major upgrade; data and enrollment are kept).

The MSI is signed when CI has a code-signing certificate (secrets `AGENT_SIGNING_PFX`, base64, and
`AGENT_SIGNING_PASSWORD`); without them it is built unsigned. CI installs it on a Windows runner and checks all of the
above, then uninstalls it.
