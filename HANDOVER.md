# Handover

Start here. This page gets the system running on your server; `docs/DEPLOYMENT.md`
has the detail behind every step.

---

## What this is

An internal support desk: staff raise tickets, the desk works them against an SLA,
and the system escalates when a deadline is at risk. ASP.NET Core 10 API, React
front end, SQL Server.

Two other documents travel with it:

| Document | For |
|---|---|
| `docs/DEPLOYMENT.md` | You. Configuration reference, runbook, backups, upgrades |
| `docs/Support-Desk-Handbook.pdf` | Everyone who uses the system, once it is up |

---

## What you are receiving, and what you are not

You have the **source repository**. You build it. That is the supported path, and it
is why `bin/`, `obj/`, `node_modules/` and `dist/` are not in the repo.

You are deliberately **not** receiving:

- **Any credentials.** No signing key, no mail password, no connection string. You
  generate your own. The `.env.example` files show which settings exist, with the
  values left blank.
- **A database.** You create an empty one and the migrations build the schema. There
  is no data to import, and none of the development test data can follow you here —
  the demo seeder refuses to run outside Development.

---

## Before you start

| Requirement | Version | Needed for |
|---|---|---|
| ASP.NET Core Runtime | 10.0 | Running the API. The SDK too, if you build on the server |
| SQL Server | 2019 or later | Express is enough to begin. Azure SQL works unchanged |
| Node.js | 24.x | **Building** the front end only. Never needed to serve it |
| TLS certificate | — | Both for the site and for the database connection |

The database does not have to be on the same machine as the application.

---

## The eight steps

### 1. Create an empty database

Name it `SupportTicketing`. Create a login for the application with
`db_datareader`, `db_datawriter` and `EXECUTE`.

It does **not** need `db_owner` at runtime — only the migration in step 3 does, and
only that once.

### 2. Set the configuration

Environment variables on the server, never in `appsettings.json` — that file is in
source control. A double underscore maps to configuration nesting, so
`Jwt__SigningKey` becomes `Jwt:SigningKey`.

```
ConnectionStrings__SupportTicketingDb=Server=...;Database=SupportTicketing;User Id=...;Password=...;Encrypt=True
Jwt__SigningKey=<at least 32 random characters — openssl rand -base64 48>
Cors__AllowedOrigins__0=https://support.yourcompany.com
Bootstrap__Organization__Name=Your Company Limited
Bootstrap__Organization__Code=YCL
Bootstrap__Organization__TimeZone=Asia/Karachi
Bootstrap__Administrator__Email=it.manager@yourcompany.com
```

Two things the application will refuse to start without, by design:

- A **placeholder signing key** copied from the documentation. Anyone who has read
  the repository could otherwise forge a token for any user.
- `TrustServerCertificate=True` against a **non-local** database. That disables
  validation of the database's certificate and permits an interception between the
  application and its data. Install a real certificate on SQL Server and use
  `Encrypt=True`.

It also refuses to start if `Seed__EnableDemoAccounts` or `Seed__EnableRoleAccounts`
are true outside Development, so the development test users cannot reach production.

### 3. Create the schema

```bash
dotnet ef database update \
  --project src/SupportTicketing.Infrastructure \
  --startup-project src/SupportTicketing.Api \
  --connection "Server=...;Database=SupportTicketing;User Id=...;Password=...;Encrypt=True"
```

55 tables from empty.

Migrations are **not** applied automatically on start-up. Two instances starting at
once would race, and a schema change should be a decision somebody makes rather than
a side effect of a restart.

### 4. Build

```bash
dotnet publish src/SupportTicketing.Api -c Release -o ./publish
```

```bash
VITE_API_BASE_URL=https://support.yourcompany.com/api/v1
VITE_SIGNALR_URL=https://support.yourcompany.com/hubs
npm ci --prefix frontend && npm run build --prefix frontend
```

> **The API address is compiled into the front end.** Build it with the wrong value
> and the application loads but every request fails — and no amount of editing files
> on the server will fix it. You have to rebuild.

`frontend/dist` is then plain static files.

### 5. Serve both from one origin

Put `frontend/dist` at `/` and reverse-proxy `/api` and `/hubs` to the API on port
5180, on the same domain.

Same origin means no CORS at all. Split them across two hostnames and
`Cors:AllowedOrigins` has to be kept exactly in step for ever — and a mismatch fails
in the browser at runtime, not at start-up where you would notice.

`frontend/nginx.conf` does this and sets the security headers, including a content
security policy that permits no inline script.

> **If you serve the front end from IIS or a CDN instead, those headers do not come
> with it.** Replicate them in `web.config` or at the CDN, or that protection is
> silently gone.

### If the API is on IIS

`deploy/iis/Deploy-Api.ps1` does the whole server side in one elevated run: pool,
site, permissions, SQL login, health check, and the second restart. Read
`deploy/iis/README.md` first. Three things it protects you from, which are worth
knowing even if you set IIS up by hand:

- **Never copy `web.config` from your machine over the server's.** `dotnet publish`
  generates a bare one; the server's holds the connection string, signing key and
  CORS list. The script keeps the server's copy on every run after the first.
- **Uploads and logs go outside the web root** (`Storage__RootPath`,
  `Serilog__WriteTo__1__Args__path`). Inside it, IIS would serve uploads directly,
  bypassing every authorisation check, and the next deploy would delete the logs.
- **The application pool must not idle out.** The email dispatcher and SLA scanner
  are background services; IIS's default stops the worker after twenty quiet
  minutes and both silently stop with it.

Swagger is off outside Development. `Swagger__Enabled=true` turns it on for an
internal host; leave it off anywhere the public can reach.

### If the front end is on Vercel

Vercel builds from the repository. Set **Root Directory** to `frontend` in the
project settings, and set the two build-time variables there:

```
VITE_API_BASE_URL=https://<api host>/api/v1
VITE_SIGNALR_URL=https://<api host>/hubs
```

They are compiled into the bundle, so changing them means redeploying. Then add
the Vercel origin — `https://<project>.vercel.app`, no trailing slash — to
`Cors__AllowedOrigins__N` on the API. The API must be reachable over HTTPS from the
public internet for this to work at all; a browser on a Vercel page cannot reach a
server that only exists on an office network.

`frontend/vercel.json` already routes deep links to `index.html`.

### 6. Start it, and capture the password

On first start the application creates the permission catalogue, the seven system
roles, the organization, and one administrator — then prints a one-time password
**once**:

```
Bootstrap complete. Organization 'Your Company Limited' created with 7 system roles.
  Sign in as: it.manager@yourcompany.com
  One-time password: HUXXS4M6-WF8VXRRD
```

Take it from the console or the log. It is not recoverable. That account can reach
nothing but the change-password screen until it sets its own.

### 7. Restart once more

The escalation ladder is created on the *second* start, not the first. Skip this and
escalations never fire.

### 8. Configure the desk

Sign in as that administrator and work through **Administration** in this order:

1. **Business calendar** — working days, hours, public holidays, `Asia/Karachi`.
2. **SLA policy** — a response and resolution target per priority. **Without a
   policy no ticket gets an SLA clock at all**, and with no clock nothing escalates.
3. **Categories** — what requesters choose from, and which team each routes to.
4. **Teams** — members, team leads, capacity.
5. **Users** — the real accounts and their roles.

Steps 1 and 2 are not optional. The SLA and escalation machinery is the core of the
product and does nothing until they exist.

---

## Checking it works

| Check | Expected |
|---|---|
| `GET /health/live` | 200 — the process is up |
| `GET /health/ready` | 200 — it can reach the database |
| Start-up log | `Email is configured as: ...` and no `ERR` line beneath it |
| Sign in | The administrator is forced to change password |

---

## Email

Optional. Without it the system runs normally and notifications stay in-app.

```
Email__Enabled=true
Email__Host=smtp.office365.com
Email__Port=587
Email__UseStartTls=true
Email__UserName=support@yourcompany.com
Email__Password=<mailbox or app password>
Email__FromAddress=support@yourcompany.com
Email__FromName=Support Desk
```

Send from a **shared mailbox**, never a person's account — the desk should keep
working when that person leaves.

The start-up log states what it picked up, including whether a password is present,
and names the problem if the settings are obviously wrong. If mail is not arriving,
read that line first.

> `Email:RedirectAllTo` sends **every** message to one address regardless of
> recipient. Set it on a staging copy loaded with real data, so testing cannot email
> actual customers. Never set it in production — it silently swallows everything.

---

## Backups

Whoever hosts the database needs a **restore** routine, not merely a backup one.
Nightly full backup, transaction log backups through the day, and a restore actually
tested once. A backup nobody has restored is a hope, not a plan.

---

## Known gaps

Real limitations, worth reading before somebody discovers them in month two. The
handbook covers these for end users too.

- **Two-factor authentication cannot be enrolled.** The sign-in check exists; the
  screen to register an authenticator app does not. Accounts are password-only.
- **No malware scanning.** Every upload is stored with the scan state `Skipped`. The
  hooks exist and nothing is connected to them. Decide on this before staff open
  attachments from outside the company.
- **@-mentions notify nobody.** The mention is stored; no notification is raised.
- **Reopening does not alert the assignee.** They find out by looking.
- **Nothing auto-closes a ticket** the requester stopped responding to. It stays open.
- **No email intake.** Nothing reads a mailbox; tickets arrive through the portal or
  the API.
- **No screen for escalation ladders.** The default four rungs are created at first
  start and can only be changed in the database.
- **Routing is three fallbacks, not a rules engine** — the subcategory's default
  team, then the category's, then the affected application's owning team. Automatic
  assignment strategies are named in the code but not implemented; assignment is
  manual or self-service.
- **Approvals and parent–child tickets** are not implemented.
- **The AI provider path has never run against a live API.** Every test exercises the
  unavailable branch. It is written and reviewed, not verified. It stays off unless
  an administrator supplies a key.

---

## Making changes after go-live

Two loops, and they are not the same speed.

**Front end.** Edit under `frontend/src`, run `npm test` and `npm run lint`, commit,
push. Vercel builds and deploys on its own.

**API.** Edit, run `dotnet test -c Release` (it must stay green), `dotnet publish`,
then on the server: back up the database, apply migrations if the change has any,
run `deploy/iis/Deploy-Api.ps1` with the new payload. `docs/DEPLOYMENT.md`
section 7 is the ordered version.

If a front-end change needs a new API endpoint, deploy the API first. A front end
calling an endpoint that does not exist yet fails in the browser, not on the server.

Never patch a file on the server. It is lost on the next deploy and invisible to
the repository.

---

## If something goes wrong

`docs/DEPLOYMENT.md` section 8 is a runbook: nobody can sign in, somebody has left
the company, SLA clocks are not moving, notifications are not arriving, rotating the
signing key.

Every security- and business-significant action is in the audit log, including the
ones that were refused — sign-ins, permission changes, exports, attachment
downloads. When somebody asks what happened, that is where to look.
