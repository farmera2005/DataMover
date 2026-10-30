# Control API

ASP.NET Core with PostgreSQL (EF Core), in the Docker stack (`compose.yaml`). Collectors and the console talk only to
it; it serves the web console too. Nothing is configured by file or environment variable: its keys are created on
first start and everything else is entered in the console.

## Ports

| Container port (published by default) | For |
| --- | --- |
| 8080 (8090) | the console over plain HTTP |
| 8443 (8443) | the console over HTTPS (certificate: uploaded, or from the Lakeflow CA) |
| 8444 (8444) | collectors only: HTTPS with client certificates, certificate always from the Lakeflow CA |

Collector routes answer only on 8444 and console routes only on 8080/8443 (404 otherwise). Change the published
ports with `LAKEFLOW_HTTP_PORT`, `LAKEFLOW_HTTPS_PORT` and `LAKEFLOW_COLLECTOR_PORT` when starting compose (optional).

## Routes

| Route | Who | Purpose |
| --- | --- | --- |
| `GET /auth/setup`, `POST /auth/setup` | anonymous, rate limited | first-run status; creates the first administrator (no user yet, within an hour of start) |
| `POST /auth/sign-in`, `/auth/sign-out`, `GET /auth/me` | anonymous / signed in | session cookie; local accounts first, then the directory |
| `POST /auth/password` | signed in (local accounts) | change your password (needs the current one) |
| `GET/POST/PUT/DELETE /users` | Admin | local accounts; directory users' status (their role comes from groups) |
| `/settings/event-hubs/{division}` | Admin | the hub per division, from a Send-only policy's connection string (key write-only) |
| `/settings/fabric` (+ `/test`) | Admin | SQL analytics endpoint and service principal for reconciliation (secret write-only) |
| `/settings/snowflake` (+ `/key`, `/hubs/{division}`, `/test`, `/status`) | Admin | Snowflake account, server-generated key pair (private key never returned), per-hub `snowflake-listen` keys (write-only), forwarder status |
| `/settings/ldap` (+ `/test`) | Admin | directory sign-in (bind password write-only; never empty) |
| `/settings/server` (+ `/certificate`) | Admin | the server's name; upload or remove the browser certificate |
| `GET /settings/server/ca.crt` | signed in | the Lakeflow CA certificate (public), to trust on PCs |
| `GET /agents/ca` | anonymous, collectors' port | the CA certificate, which a new collector checks against the SHA-256 in its token |
| `POST /agents/enroll` | anonymous, rate limited, collectors' port | one-time token + CSR → client certificate |
| `POST /agents/{id}/renew` | collector cert | new certificate for a new key (renewed 30 days before expiry) |
| `GET /agents/{id}/config` | collector cert | per assigned site: sources, tables, hubs + SAS tokens, commands, sealed credentials |
| `POST /agents/{id}/heartbeat`, `/events`, `/logs` | collector cert | per-site health, events, log bundles; only for sites assigned to it |
| `GET /status` | Viewer | fleet rollup, green/amber/red with reasons |
| `GET /alerts?state=open\|resolved\|all&siteId=` | Viewer | alerts with acknowledgements and notes |
| `POST /alerts/{id}/acknowledge`, `POST /alerts/{id}/notes` | Operator | acknowledge (optional note), add a note; audited |
| `GET/PUT/DELETE /sites/{site}`, `.../sources/{source}`, `.../tables/{table}` | Viewer reads, Admin writes | configuration (incl. the site's `collector_id`), validated with the collector's own rules |
| `PUT /sites/{site}/sources/{source}/credential` | Admin | a password for the site's collector: sealed in the browser, or sealed here on arrival |
| `POST /enrollment-tokens` | Admin | token (24 h, single use) and install file for a collector on another server, or for a site agent |
| `GET /agents`, `GET /agents/{id}`, `POST /agents/{id}/revoke` | Viewer / Admin | collectors, their sites and certificates |
| `POST/GET /sites/{site}/commands` | Operator (pause, resume, resnapshot), Admin (restart, logs, test connection, browse) | queued for the site's collector (it polls every 10 s while one waits) |
| `GET /reconciliation` | Viewer | latest nightly source-vs-silver results (from the Fabric SQL endpoint) |
| `GET /events`, `GET /audit`, `GET /logs/{id}` | Viewer / Viewer / Admin | collector events, audit trail, log bundles |

A test walks every mapped route against an explicit list of what Viewers and Operators may call; anything else must be
Admin-only, so a new route cannot slip through with the wrong role.

Every write is audited in the same transaction (who, what, before, after); password hashes and setting secrets appear
only as "(changed)", token hashes and sealed values not at all. Every change to a site's configuration moves its
`config_version`. Audit is kept a year, events 90 days, log bundles 30 days.

## Security model

- **Keys** (`api-data` volume, owner-only files): `master.key` encrypts setting secrets in the database (AES-256-GCM);
  `collector-ca.pfx` is the Lakeflow CA (RSA 3072, 20 years), which issues collector client certificates and the TLS
  certificates; `session-keys/` signs session cookies (encrypted to the CA's key). A copy of the database alone reveals
  no secret. Back up `api-data` with the database.
- **Console users:** a session cookie (HttpOnly, SameSite=Strict, Secure on HTTPS, 12 h sliding) re-checked against the
  user on every request, so disabling a user or changing their role or password takes effect at once. Changes also need
  the console's `X-Lakeflow-Request` header. Local passwords: at least 12 characters, PBKDF2 (ASP.NET Core Identity's
  hasher). Sign-in is rate limited per address.
- **Directory sign-in:** LDAPS or StartTLS only; the server certificate is checked (against the CA you paste, or the
  system's) including its name; the service account finds the user, the user's own password is checked by binding as
  them, and the role is the highest Lakeflow group they are in (AD nested groups count). A local account of the same
  name always wins and is never taken over. Empty passwords are refused before anything is sent.
- **Collectors** authenticate with a client certificate from the Lakeflow CA; the private key is generated on the
  collector and never leaves it. The API also checks the certificate against the agents table, so revoking a collector
  cuts it off at once. A collector can only reach its own `/agents/{id}` routes and report on its own sites. Collectors
  trust only the Lakeflow CA for the API: a new one downloads it and checks it against the SHA-256 that came with its
  token before sending anything.
- **Event Hubs:** each config carries SAS tokens signed with the hub's Send-only policy key, valid 24 hours and reissued
  once half that has passed; the key itself never leaves the API. A revoked collector loses Event Hubs access when its
  last token expires (at most 24 hours); rotate the policy key to cut that short.
- **Source passwords:** sealed to the site's collector certificate (RSA-OAEP-256 + AES-256-GCM). On an HTTPS page the
  browser seals them (WebCrypto); a plain-HTTP page has no WebCrypto, so the API seals them on arrival. Only the sealed
  value is stored, and it is deleted once the collector confirms it stored the password.

## Running and testing

```bash
docker compose up -d --build                  # the whole stack; open https://<host>:8443
eval "$(scripts/test-postgres.sh)"            # PostgreSQL for the API tests
eval "$(scripts/test-sqlserver.sh)"           # SQL Server for connector, collector and reconciliation tests
dotnet test
```
