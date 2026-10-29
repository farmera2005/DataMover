# Control API

ASP.NET Core on Azure App Service, Azure SQL through EF Core. Collectors and the console talk only to it, and it
serves the web console at `/console`.

## Routes

| Route | Who | Purpose |
| --- | --- | --- |
| `POST /agents/enroll` | anonymous, rate limited | one-time token + CSR → client certificate (central collector, or agent for one site) |
| `POST /agents/{id}/renew` | agent cert | new certificate for a new key (agent renews 30 days before expiry) |
| `GET /agents/{id}/config` | collector cert | per assigned site: sources, tables, hubs + SAS tokens, commands, sealed credentials |
| `POST /agents/{id}/heartbeat`, `/events`, `/logs` | collector cert | per-site health, events (command results, credential receipts), log bundles; only for sites assigned to it |
| `GET /status` | Viewer | fleet rollup, green/amber/red with reasons |
| `GET/PUT/DELETE /sites/{site}`, `.../sources/{source}`, `.../tables/{table}` | Viewer reads, Admin writes | configuration (incl. the site's `collector_id`), validated with the collector's own rules |
| `PUT /sites/{site}/sources/{source}/credential` | Admin | a password sealed in the browser to the site's collector |
| `POST /enrollment-tokens` | Admin | token (24 h, single use) for a collector (`name`) or a site agent (`site_id`), and the install command |
| `GET /agents`, `GET /agents/{id}`, `POST /agents/{id}/revoke` | Viewer / Admin | collectors, their sites and certificates (public key for sealing) |
| `POST/GET /sites/{site}/commands` | Operator (pause, resume, resnapshot), Admin (restart, collect logs, test connection, browse) | queued for the site's collector, delivered on its next pull (every 10 s while one waits) |
| `GET /reconciliation` | Viewer | latest nightly source-vs-silver results (from the Fabric SQL endpoint) |
| `GET /console/...` | anonymous (static) | the web console; `/console/appsettings.json` gives it its sign-in settings |
| `GET /events`, `GET /audit`, `GET /logs/{id}` | Viewer / Viewer / Admin | agent events, audit trail, log bundles |

Every write is audited in the same transaction (who, what, before, after; token hashes and sealed values left out),
and every change to a site's configuration moves its `config_version`. Audit is kept a year, events 90 days, log
bundles 30 days.

## Security model

- **Collectors** authenticate with a client certificate issued by the Lakeflow agent CA at enrollment. The private key
  is generated on the collector and never leaves it. The API also checks the certificate against the agents table, so
  revoking a collector cuts it off at once. A collector can only reach its own `/agents/{id}` routes and report on the
  sites assigned to it.
- **Event Hubs:** each config carries SAS tokens signed with the hub's Send-only policy key, valid 24 hours and
  reissued once half that has passed. A revoked agent loses Event Hubs access when its last token expires (at most
  24 hours later); rotate the hub policy key to cut that short.
- **Source passwords:** the browser encrypts them to the site's collector certificate (`SealedSecret`: RSA-OAEP-256 +
  AES-256-GCM, via WebCrypto). The API stores only that ciphertext, refuses anything else, and deletes it once the
  collector confirms it stored the password. Neither the API nor its database can read a password.
- **Console users:** Entra ID tokens with app roles `Lakeflow.Admin`, `Lakeflow.Operator` and `Lakeflow.Viewer`.

## Setting up (once)

1. **Entra ID app registration** for the API: expose an application ID URI (e.g. `api://lakeflow-control`), define
   the three app roles, and in the enterprise application assign the IT groups to them so roles follow group
   membership. Require MFA for the app with a Conditional Access policy.
2. **App Service:** client certificates *Optional* for the whole app (or *Required* with `/sites`, `/status` and the
   other console paths excluded), HTTPS only, minimum TLS 1.2. The API reads the certificate App Service forwards in
   `X-ARR-ClientCert`, so it must only be reachable through App Service.
3. **Key Vault** (the API's managed identity needs *get* on secrets):
   - `agent-ca`: the agent CA as a Key Vault certificate (its secret is the PFX with the private key).
   - `eventhubs-<hub>-send-key`: for each division hub, the key of a hub-level shared access policy named `agent-send`
     with only the Send claim.
4. **App settings** (no secrets): `Entra__TenantId`, `Entra__Audience`, `Console__ClientId`, `Console__ApiScope`,
   `KeyVault__VaultUri`, `EventHubs__Namespace`, `Fabric__SqlEndpointConnectionString`, `PublicBaseUrl`, and `ConnectionStrings__Lakeflow` with `Authentication=Active Directory Default` so the managed
   identity signs in to Azure SQL. `APPLICATIONINSIGHTS_CONNECTION_STRING` turns on telemetry.

The database schema is applied by EF Core migrations at startup (`Database__MigrateOnStartup`, default true).
Step 7's Bicep creates all of this.

## Running locally

```bash
eval "$(scripts/test-sqlserver.sh)"
ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Lakeflow="${MSSQL_TEST_CONNECTION};Database=lakeflow_dev" \
  dotnet run --project src/Lakeflow.ControlApi
```

Development generates a throw-away agent CA and hub keys at startup.
