# Deployment

> New to this system? Read [`HANDOVER.md`](../HANDOVER.md) first — it is the
> ordered walk-through. This page is the reference behind it.

How to stand this system up on a server, and how to keep it running.

Written to be host-agnostic: the steps are the same whether the application runs under
IIS, on a Linux box behind nginx, or in Azure App Service. Where a host differs, it is
called out.

---

## 1. What the server needs

| | Minimum | Notes |
|---|---|---|
| .NET runtime | ASP.NET Core Runtime 10.0 | The SDK is only needed if you build on the server |
| SQL Server | 2019 or later | Express is sufficient for a small deployment; Azure SQL works unchanged |
| Node.js | 24.x | Only to build the frontend. The built output is static files |
| TLS certificate | — | Terminated at the proxy or at Kestrel. Do not run this over plain HTTP |

The database and the application do **not** need to be on the same machine.

---

## 2. Configuration

Nothing sensitive belongs in `appsettings.json`. Every value below is read from
environment variables, and the double underscore maps to configuration nesting:
`Jwt__SigningKey` becomes `Jwt:SigningKey`.

### Required

```bash
ASPNETCORE_ENVIRONMENT=Production

ConnectionStrings__SupportTicketingDb="Server=sql01;Database=SupportTicketing;User Id=svc_ticketing;Password=…;Encrypt=True"

# At least 32 characters. Generate with: openssl rand -base64 48
Jwt__SigningKey="…"
Jwt__Issuer=SupportTicketing.Api
Jwt__Audience=SupportTicketing.Spa

# The exact origin the frontend is served from. No wildcards — credentials are sent.
Cors__AllowedOrigins__0=https://support.yourcompany.com
```

The application **refuses to start** in Production if the signing key is missing, too
short, or one of the placeholder values from the documentation; if the CORS list is
empty or contains `*`; or if demo seeding is switched on. These are failures that would
otherwise leave a perfectly functional but quietly insecure deployment, so they are
made loud.

### First run only

Creates the organization and the first administrator. Both are ignored once an
organization exists.

```bash
Bootstrap__Organization__Name="Your Company Limited"
Bootstrap__Organization__Code=YCL
Bootstrap__Organization__TicketPrefix=TKT
Bootstrap__Organization__TimeZone="Asia/Karachi"
Bootstrap__Administrator__Email=it.manager@yourcompany.com
Bootstrap__Administrator__FirstName=Yasmin
Bootstrap__Administrator__LastName=Rahim
```

### Behind a reverse proxy

Required under IIS, nginx, HAProxy or any load balancer. Without it every audit row
records the proxy's address instead of the person's, and the sign-in rate limiter
treats the whole organization as one client.

```bash
# The proxy's address, or the network it sits in. Leave both unset when Kestrel is
# exposed directly — accepting X-Forwarded-For from an arbitrary caller would let that
# caller choose what the audit log says about them.
ForwardedHeaders__KnownProxies__0=10.0.0.4
# or
ForwardedHeaders__KnownNetworks__0=10.0.0.0/24
```

### Optional

```bash
# Sign-in budget is per client address; the global budget is per user.
RateLimiting__Auth__PermitLimit=10
RateLimiting__Auth__WindowSeconds=60
RateLimiting__Global__PermitLimit=300
RateLimiting__Global__WindowSeconds=60

Auth__MaxFailedAccessAttempts=5
Auth__LockoutMinutes=15

# AI assistance. Without a key the system runs identically and reports itself
# unavailable; capabilities stay off until an administrator enables them.
OpenAi__ApiKey=
OpenAi__Model=gpt-4o-mini
```

---

## 2b. Email

Notifications appear in the application whether or not this is configured. Without it
they appear *only* there, so a requester learns their ticket was answered by signing in
and looking.

```
Email__Enabled=true
Email__Host=smtp.yourprovider.com
Email__Port=587
Email__UseStartTls=true
Email__UserName=<the mailbox or API user>
Email__Password=<its password>
Email__FromAddress=support@yourcompany.com
Email__FromName=Support Desk
```

Port 465 instead? Set `Email__UseSsl=true` and `Email__UseStartTls=false`.

**Never put the password in a committed file.** Locally:

```bash
dotnet user-secrets set "Email:Password" "<the password>" --project src/SupportTicketing.Api
```

In production it is an environment variable like the others. The application reads it
once at startup and never writes it to a log; an authentication failure logs that
credentials were refused and nothing more.

Set `Email__Enabled=false`, or leave the host blank, and the sender stands down with a
warning at startup. There is no fallback to localhost — a desk that quietly posts mail
through whatever happens to be listening fails invisibly, which is worse than one that
plainly does not send.

### Testing it without emailing real people

On a staging system loaded with a copy of production data:

```
Email__RedirectAllTo=you@yourcompany.com
```

Every message goes to that address instead, with the intended recipient named in the
subject line so a redirected inbox is still readable.

### When something does not arrive

Delivery is a queue drained every thirty seconds, not part of the request that raised the
notification — assigning a ticket must not fail because a mail server is slow. Each
attempt is recorded:

| State | Meaning |
|---|---|
| Pending | Queued, not yet attempted |
| Sent | Accepted by the mail server |
| Failed | A transient problem; it will be retried |
| DeadLettered | Given up on after five attempts, or refused outright |
| Suppressed | The recipient's account was deleted |

```sql
SELECT State, COUNT(*), MAX(FailureReason)
FROM NotificationDeliveries WHERE Channel = 2 GROUP BY State;
```

Retries back off, doubling each time, so a mail server that restarts is not hammered by
the whole backlog. A dead-lettered row keeps its reason, so you can see that somebody is
not receiving mail rather than assuming they read it.

## 3. Install

### Build

```bash
dotnet publish src/SupportTicketing.Api -c Release -o ./publish
```

```bash
npm ci --prefix frontend && npm run build --prefix frontend
```

The frontend reads its API address at build time, so set it before building:

```bash
VITE_API_BASE_URL=https://api.yourcompany.com/api/v1
VITE_SIGNALR_URL=https://api.yourcompany.com/hubs
```

`frontend/dist` is then static files — serve them from IIS, nginx, or any CDN.

### Create the schema

```bash
dotnet ef database update --project src/SupportTicketing.Infrastructure --startup-project src/SupportTicketing.Api
```

Migrations are **not** applied automatically at start-up. That is deliberate: two
application instances starting at once would race, and a schema change is a decision
somebody should make on purpose rather than a side effect of a restart.

### Start it

The first start creates the permission catalogue, the seven system roles, the
organization and the administrator, then prints a one-time password **once**:

```
Bootstrap complete. Organization 'Your Company Limited' created with 7 system roles.
  Sign in as: it.manager@yourcompany.com
  One-time password: HUXXS4M6-WF8VXRRD
```

Capture it from the console or the log. It is not recoverable, and the account can
reach nothing but the change-password screen until it sets its own.

### Then, in the application

Sign in as that administrator and work through **Administration**:

1. **Users** — create the real accounts and assign roles.
2. **Teams** — create teams, add members, set capacity weights.
3. **Categories** — the catalogue requesters choose from, and the priority matrix.
4. **SLA policies and calendars** — targets, working hours, public holidays.
5. **AI assistance** — leave off unless a provider key is configured.

---

## 4. Database permissions

The application's login needs `db_datareader`, `db_datawriter` and `EXECUTE`. It does
**not** need `db_owner` at runtime — only the migration step does.

Deny `UPDATE` and `DELETE` on `AuditLogs` for the application's login. The persistence
layer already refuses to modify an audit row, but a grant the application does not need
is a grant that can be misused:

```sql
DENY UPDATE, DELETE ON dbo.AuditLogs TO svc_ticketing;
```

---

## 5. Health checks

| Endpoint | Meaning | Use for |
|---|---|---|
| `/health/live` | The process is up | Container restart policy |
| `/health/ready` | The database answers | Load-balancer rotation |

Point the load balancer at `/health/ready`. A node whose database connection has failed
will remove itself rather than serving errors.

---

## 6. Backups

The database is the only durable state. There is nothing on the application server
worth restoring.

- **Full** backup nightly, **differential** every few hours, **transaction log** every
  fifteen minutes if the recovery point objective is tight.
- **Test a restore.** An untested backup is a hypothesis.
- Retain in line with the audit-log retention the client's policy requires — the audit
  table is append-only, so it only ever grows, and it is the table most likely to be
  wanted years later.

The JWT signing key is not in the database. Store it wherever the client keeps secrets,
and note that losing it signs everybody out; it does not lose data.

---

## 7. Upgrading

```bash
# 1. Back up the database. Not optional.
# 2. Stop the application.
# 3. Deploy the new build.
# 4. Apply migrations.
dotnet ef database update --project src/SupportTicketing.Infrastructure --startup-project src/SupportTicketing.Api
# 5. Start the application.
```

The bootstrapper runs on every start and is additive: a release that introduces a new
permission creates it and grants it to Super Admin, so the account that has to configure
the new capability can reach it. Roles an administrator has edited are left alone.

---

## 8. Runbook

### Nobody can sign in

1. `/health/ready` — is the database reachable?
2. Check the start-up log for `Refusing to start`. The message lists exactly what is
   wrong with the configuration.
3. Check for `Bootstrap skipped: N migration(s) are pending` — the schema is behind the
   code. Apply migrations.
4. Check for `No organization exists and Bootstrap is not configured`. Set the
   `Bootstrap__*` variables and restart.

### Somebody has left the company

**Administration → Users → Delete**, which only a Super Admin can do. What happens next
depends on whether the account left anything behind, and the confirmation dialog says
which before you commit to it.

An account that owns nothing is removed outright. An account that raised tickets, was
assigned them, resolved them, wrote comments or authored knowledge articles is
**anonymised** instead: the name becomes "Deleted user", the email an address that can
never be delivered to, and the password is destroyed rather than merely disabled, so
nobody can sign in as them again. The row itself stays, because every ticket points at
it — a system whose whole claim is that changes are attributable cannot answer "who
raised this?" with a dangling reference.

The practical effect on the ticket list is that the person's name is replaced by
"Deleted user" everywhere it appeared, and they disappear from every list where somebody
picks an assignee. The tickets, the comments and the resolutions are untouched.

Audit entries are the exception, deliberately: they store the actor's name and email as
a snapshot rather than a link, so an investigation can still reconstruct who did what
before the account was removed. That is the point of an audit trail, and it is why the
deletion is recorded in one as well.

**This cannot be undone.** There is no restore. If you only want to stop somebody signing
in — someone on leave, a contract between renewals — use **Deactivate** instead, which is
reversible and keeps the name on their tickets.

### One person cannot sign in

Their account is probably locked after failed attempts, or deactivated. As an
administrator: **Administration → Users**, find them, and either **Restore** (which
clears the lockout) or **Reset password**.

### They sign in but see "Your password must be changed"

Working as intended. The account is on a password an administrator issued. They set
their own and the block lifts.

### SLA clocks are not moving

The sweep runs in a background service inside the API process.

1. Look for `SlaMonitorService` lines in the log.
2. Confirm the ticket has an SLA instance at all — a ticket whose category maps to no
   policy has no clock, by design.
3. A paused clock is not a stopped clock: waiting on the requester or a third party
   pauses it deliberately.

### Notifications are not arriving

Expected today: only the in-app channel exists. Notifications are written to the
database and shown in the bell, and nothing is emailed. SMTP is a known gap.

### Something changed and nobody knows who

**Administration → Audit log**. Filter by entity, person, or the correlation identifier
from the error the user saw. Every row expands to the field values recorded with it.
Denied actions are kept alongside successful ones.

### An error the user reported

Every error response carries a `correlationId`. Search the logs for it — the same
identifier ties together every log line and audit row from that one request.

### Rotating the signing key

Set the new `Jwt__SigningKey` and restart. Every access token becomes invalid
immediately and every user signs in again. Refresh tokens are unaffected: they are
random values stored as hashes, not signed by this key.

---

## 9. What is not built

State these plainly to the client rather than discovering them together later.

- **Email intake does not exist.** Nothing reads a mailbox; tickets arrive through the
  portal or the API. Outbound email does work — see section 2b.
- **Two-factor authentication cannot be enrolled.** The sign-in check exists; the screen
  to register an authenticator app does not. Accounts are password-only.
- **No malware scanning.** Every upload is stored with the scan state `Skipped`. The
  hooks exist and nothing is connected to them.
- **@-mentions notify nobody.** The mention is stored and no notification is raised.
- **Reopening a ticket does not alert the assignee.** They find out by looking.
- **Nothing auto-closes a ticket** whose requester stopped responding. The workflow
  permits it and the closure reason exists, but no job performs it.
- **No screen for escalation ladders.** The default four rungs are created at first
  start and can only be changed in the database.
- **Routing is three fallbacks, not a rules engine** — the subcategory's default team,
  then the category's, then the affected application's owning team. The automatic
  assignment strategies named in `AssignmentMethod` are not implemented; assignment is
  manual or self-service.
- **Approvals and parent–child tickets** are not implemented.
- **No CI pipeline.** Docker files exist (`docker-compose.yml` and a Dockerfile per
  service); the API image builds, the frontend image has not been verified.
- **The AI provider path has never run against the live API.** Every test exercises the
  unavailable branch. It is written and reviewed, not verified.
