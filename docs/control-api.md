# Control API

ASP.NET Core on Azure App Service, Azure SQL through EF Core. Agents and the console talk only to it.

## Routes

| Route | Who | Purpose |
| --- | --- | --- |
| `POST /agents/enroll` | anonymous, rate limited | one-time token + CSR → client certificate |
| `POST /agents/{id}/renew` | agent cert | new certificate for a new key (agent renews 30 days before expiry) |
| `GET /agents/{id}/config` | agent cert | sources, tables, hubs + SAS tokens, commands, sealed credentials |
| `POST /agents/{id}/heartbeat`, `/events`, `/logs` | agent cert | health, events (command results, credential receipts), log bundles |
| `GET /status` | Viewer | fleet rollup, green/amber/red with reasons |
| `GET/PUT/DELETE /sites/{site}`, `.../sources/{source}`, `.../tables/{table}` | Viewer reads, Admin writes | configuration, validated with the agent's own rules |
| `PUT /sites/{site}/sources/{source}/credential` | Admin | a password sealed in the console to the agent's certificate |
| `POST /sites/{site}/enrollment-tokens` | Admin | token (24 h, single use) and the install command |
| `GET /agents`, `GET /agents/{id}`, `POST /agents/{id}/revoke` | Viewer / Admin | agents and their certificates |
| `POST /agents/{id}/commands` | Operator (pause, resume, resnapshot), Admin (restart, collect logs) | queued, delivered on the next config pull |
| `GET /events`, `GET /audit`, `GET /logs/{id}` | Viewer / Viewer / Admin | agent events, audit trail, log bundles |

Every write is audited in the same transaction (who, what, before, after; token hashes and sealed values left out),
and every change to a site's configuration moves its `config_version`. Audit is kept a year, events 90 days, log
bundles 30 days.

## Security model

- **Agents** authenticate with a client certificate issued by the Lakeflow agent CA at enrollment. The private key is
  generated on the agent and never leaves it. The API also checks the certificate against the agents table, so
  revoking an agent (or enrolling a replacement for its site) cuts it off at once. An agent can only reach its own
  `/agents/{id}` routes.
- **Event Hubs:** each config carries SAS tokens signed with the hub's Send-only policy key, valid 24 hours and
  reissued once half that has passed. A revoked agent loses Event Hubs access when its last token expires (at most
  24 hours later); rotate the hub policy key to cut that short.
- **Source passwords:** the console encrypts them to the agent's certificate (`SealedSecret`: RSA-OAEP-256 +
  AES-256-GCM). The API stores only that ciphertext, refuses anything else, and deletes it once the agent confirms it
  stored the password (DPAPI). Neither the API nor its database can read a password.
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
4. **App settings** (no secrets): `Entra__TenantId`, `Entra__Audience`, `KeyVault__VaultUri`, `EventHubs__Namespace`,
   `PublicBaseUrl`, and `ConnectionStrings__Lakeflow` with `Authentication=Active Directory Default` so the managed
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
