# Support Ticketing System

Enterprise support ticketing platform for internal IT, ERP application support, customer
support and supplier support. ASP.NET Core 10 Web API, SQL Server, Clean Architecture.

> **Current state: Phases 1 and 2 complete, with a working React frontend.**
> Authentication, master data, and the full ticket lifecycle — creation, priority
> calculation, routing, assignment, conversations with internal notes, resolution,
> confirmation and reopening — are built and verified end to end in a browser.
> SLA, escalation, notifications, reporting, the knowledge base and AI assistance are
> **not built yet**. See [Delivery status](#delivery-status) for exactly what exists.

---

## Prerequisites

| Tool | Version used | Notes |
|---|---|---|
| .NET SDK | 10.0.400 | `dotnet --list-sdks` |
| SQL Server | 2025 Developer (17.0) | Any edition from 2019 onward should work |
| `dotnet-ef` | 10.0.11 | `dotnet tool update --global dotnet-ef --version 10.0.11` |
| Node.js | 24.x | For the frontend, once it exists |

---

## Getting started

### 1. Configure secrets

Nothing sensitive lives in source control. Both values below come from user-secrets
locally and from environment variables everywhere else.

```bash
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random characters>" --project src/SupportTicketing.Api
```

```bash
dotnet user-secrets set "Seed:DemoPassword" "<password for the demo accounts>" --project src/SupportTicketing.Api
```

If `Seed:DemoPassword` is omitted the seeder generates a random password and prints it
to the console once. It never falls back to a guessable default.

The connection string defaults to `Server=.;Database=SupportTicketing;Trusted_Connection=True`
in `appsettings.Development.json`. Override it with the
`ConnectionStrings__SupportTicketingDb` environment variable.

### 2. Create the database

```bash
dotnet ef database update --project src/SupportTicketing.Infrastructure --startup-project src/SupportTicketing.Api
```

### 3. Run

```bash
dotnet run --project src/SupportTicketing.Api
```

Swagger is at `/swagger`. The demo seeder runs on startup in Development and logs what
it created.

### 4. Sign in

```bash
curl -X POST http://localhost:5180/api/v1/auth/login -H "Content-Type: application/json" -d "{\"email\":\"agent@itg.test\",\"password\":\"<Seed:DemoPassword>\"}"
```

---

### 5. Run the frontend

In a second terminal:

```bash
npm install --prefix frontend
```

```bash
npm run dev --prefix frontend
```

Open `http://localhost:5173` and sign in with any demo account below. The API's CORS
allowlist already includes this origin in Development.

The frontend reads `VITE_API_BASE_URL` (see `frontend/.env.example`), defaulting to
`http://localhost:5180/api/v1`. Only `VITE_`-prefixed variables reach the browser
bundle, and none of them is a secret — the OpenAI key stays on the backend.

---

## Frontend

React 19 with Vite 8, React Router 7, TanStack Query 5, React Hook Form with Zod
validation, Recharts, and CSS Modules over a design-token layer.

```
frontend/src/
  app/          query client configuration
  components/   ui/ (button, field, card, badge, dialog, states)
                layout/ (app shell, permission-aware navigation)
                ErrorBoundary
  contexts/     Auth, Theme, Toast
  features/     auth/, dashboard/, profile/
  pages/        NotFound, NotImplemented
  routes/       router, ProtectedRoute
  services/     apiClient, authService, tokenStore
  styles/       tokens.css, global.css
  test/         setup and provider harness
  utils/        permission keys and scope labels
```

### Decisions worth knowing

**The access token lives in memory; only the refresh token is persisted.** An XSS bug
cannot read the access token out of storage, and it is gone when the tab closes. The
refresh token does go to localStorage because the API returns it in the response body
and sessions must survive a reload — a documented trade-off, with moving it to an
HttpOnly cookie tracked as a hardening item.

**Refresh is single-flight.** A dashboard can fire several requests at once, and if
each retried its own 401 independently, the second rotation would look like token
reuse and revoke the whole session. All callers share one in-flight refresh promise.
There is a test for exactly this.

**Nothing is mocked.** Screens whose backend does not exist yet route to a page that
says so, names the phase that delivers them, and lists the planned endpoints. A
placeholder table with fake rows would make an unfinished system look finished and
waste a tester's time proving a mock button does nothing.

**Permission-aware, not permission-enforced.** Navigation and controls are filtered by
the permission list from `/auth/me`, and `ProtectedRoute` blocks direct URL access.
Both are usability measures; the API re-checks every permission server-side.

**Light and dark themes** resolve before first paint via a small inline script, so a
dark-mode user never sees a white flash. All colour lives in `tokens.css`.

### Verified in a browser

Signed in as `lead@itg.test` and `requester@itg.test` against the live API:

| Behaviour | Result |
|---|---|
| Sign-in returns a real profile and renders it | Team Lead: 35 permissions, IT Support team, Asia/Karachi |
| Navigation reflects the user's permissions | Requester sees 5 items; Team Lead sees 9; neither sees Administration |
| Direct URL to a forbidden route is blocked | `/admin/users` as Team Lead shows an access-denied state |
| Session survives a full page reload | Restored from the refresh token, no bounce to sign-in |
| Unbuilt routes are honest | `/tickets` states the phase and planned endpoints |
| Console errors | None |
| Mobile at 375×812 | No horizontal scroll, off-canvas drawer, hamburger visible |

### Frontend tests

```bash
npm test --prefix frontend
```

**20 tests passing** across three files: API client behaviour (token attachment,
Problem Details parsing, refresh-and-retry, single-flight rotation, session-expiry
event, network-error distinction), permission-driven navigation filtering, and the
login form (validation, generic credential failure, two-factor reveal, lockout,
unreachable server, `aria-invalid`).

---

## Demo users

Created only when **both** `ASPNETCORE_ENVIRONMENT=Development` and
`Seed:EnableDemoAccounts=true`. The seeder also aborts if the database already contains
an organization, so it can never overwrite real data.

All accounts share the `Seed:DemoPassword` value. The `.test` TLD is reserved by
RFC 6761, so no message can reach a real mailbox.

### ITG Group (primary tenant)

| Email | Role | Permissions | Team |
|---|---|---|---|
| `requester@itg.test` | Requester | 10 | — |
| `requester2@itg.test` | Requester | 10 | — |
| `agent@itg.test` | Support Agent | 23 | IT Support |
| `agent2@itg.test` | Support Agent | 23 | IT Support |
| `erpagent@itg.test` | Support Agent | 23 | ERP Support |
| `lead@itg.test` | Team Lead | 35 | IT Support |
| `specialist@itg.test` | Technical Specialist | 25 | ERP Support |
| `manager@itg.test` | Manager | 42 | — |
| `admin@itg.test` | Administrator | 17 | — |
| `superadmin@itg.test` | Super Admin | 55 | — |

### Fabrikam Trading (second tenant, for isolation testing)

`requester@fab.test`, `agent@fab.test`, `admin@fab.test`

A full QA reference with expected behaviours is in
[docs/QA-Test-Credentials.pdf](docs/QA-Test-Credentials.pdf).

---

## Architecture

A modular monolith following Clean Architecture. Dependencies point inward, and the
rule is enforced by tests rather than convention.

```
SupportTicketing.Domain          entities, enums, invariants — depends on nothing
        ▲
SupportTicketing.Application     commands, queries, handlers, abstractions
        ▲                        (references Contracts; never Infrastructure)
SupportTicketing.Infrastructure  EF Core, SQL Server, JWT, hashing, seeding
        ▲
SupportTicketing.Api             controllers, middleware, authorization policies
SupportTicketing.Workers         background services (scaffolded, no jobs yet)
SupportTicketing.Contracts       wire DTOs shared with clients
```

### Design decisions worth knowing

**No mediator library.** A ~100-line `Dispatcher` resolves handlers and runs the
behaviour pipeline (validation → logging → transaction). This avoids the licensing
questions now attached to the popular mediator packages and keeps the flow readable.

**Tenant isolation is a global query filter**, driven by the JWT's `org` claim and
never by anything in a request. Entities implementing `ITenantOwned` are filtered
automatically the moment they join the model. The filter **fails closed**: with no
principal it matches nothing rather than everything.

**Authentication needs a tenant before one exists.** `BeginTenantScope(organizationId)`
pins the filter to the organization established from verified credentials, so sign-in
and refresh run against correctly scoped data instead of disabling isolation.
`IgnoreTenantFilter` has exactly two permitted callers, asserted by an architecture test.

**Permissions, not roles.** Nothing branches on a role name. Every check asks whether
the principal holds a permission key, and role→permission mappings are database rows an
administrator can edit at runtime.

**404 rather than 403 for unauthorized resources.** A 403 confirms the record exists,
which is what an attacker enumerating identifiers wants to learn.

**History is append-only.** `AuditLog` implements `IAppendOnly`; the SaveChanges
interceptor throws if such an entity is ever modified or deleted. Deleting a
soft-deletable entity is rewritten as an archive.

**Auth commands opt out of the ambient transaction** via `IManagesOwnTransaction`. They
deliberately persist evidence and then throw — a failed sign-in writes an audit row and
increments the lockout counter, and refresh-token reuse revokes the whole token family.
Under rollback-on-throw every one of those writes was discarded by the very exception
reporting the failure.

---

## Role and permission model

Two independent questions, deliberately kept apart:

1. **The verb** — does this principal hold `ticket.resolve`? Answered by permission
   claims, enforced by `[HasPermission(...)]`.
2. **The rows** — *which* tickets? Answered by `DataScope`, compiled into a query
   predicate.

| Scope | Value | Meaning |
|---|---|---|
| `Own` | 1 | Only tickets the user raised |
| `Assigned` | 2 | Tickets assigned to the user |
| `Team` | 3 | Every ticket belonging to a team they are in |
| `Department` | 4 | Their department, including descendants |
| `Organization` | 5 | Their whole organization |
| `All` | 6 | Every organization — Super Admin only, audited |

A per-user override can grant or deny a single permission. **A deny always wins**, so
removing one capability from one person never requires inventing a new role.

**Administrator does not receive `ticket.view_all`.** Managing users and configuration
does not imply a right to read support conversations; granting it is an explicit,
audited decision. This is asserted by an integration test.

---

## API

| Method | Path | Auth | Notes |
|---|---|---|---|
| POST | `/api/v1/auth/login` | Anonymous | Rate limited, 10/min/IP by default |
| POST | `/api/v1/auth/refresh` | Anonymous | Single-use rotation; replay revokes the family |
| POST | `/api/v1/auth/logout` | Bearer | Current session, or all sessions |
| GET | `/api/v1/auth/me` | Bearer | Profile, roles, effective permissions |
| GET | `/api/v1/tickets` | Bearer | Filter, sort, page. Rows limited by data scope |
| POST | `/api/v1/tickets` | `ticket.create` | Priority calculated, never supplied |
| GET | `/api/v1/tickets/{id}` | Bearer | 404 outside scope, never 403 |
| POST | `/api/v1/tickets/{id}/accept` | `ticket.accept` | Claims an unassigned ticket |
| POST | `/api/v1/tickets/{id}/assign` | `ticket.assign` | Records previous and new owner |
| POST | `/api/v1/tickets/{id}/status` | `ticket.change_status` | Validated against the workflow graph |
| POST | `/api/v1/tickets/{id}/priority` | `ticket.change_priority` | Override needs a reason |
| POST | `/api/v1/tickets/{id}/resolve` | `ticket.resolve` | Summary mandatory |
| POST | `/api/v1/tickets/{id}/close` | `ticket.close` / confirm | Requester confirmation closes it |
| POST | `/api/v1/tickets/{id}/reopen` | `ticket.reopen` / confirm | Reopens the same ticket |
| GET | `/api/v1/tickets/{id}/comments` | Bearer | Internal notes filtered at the database |
| POST | `/api/v1/tickets/{id}/comments` | reply / note | `isInternal` needs `ticket.internal_note` |
| GET | `/api/v1/tickets/{id}/timeline` | Bearer | Lifecycle rebuilt from append-only history |
| GET | `/api/v1/categories` | Bearer | Categories with subcategories |
| GET | `/api/v1/applications` | Bearer | Applications with modules |
| GET | `/api/v1/agents` | `ticket.assign` | Assignable agents with open-ticket counts |
| GET | `/health/live` | Anonymous | Liveness |
| GET | `/health/ready` | Anonymous | Readiness, checks the database |

Errors are RFC 7807 Problem Details with a stable `code` and a `correlationId`. Stack
traces and SQL are never serialised.

---

## Testing

```bash
dotnet test
```

**46 tests, all passing** as of the last run:

| Project | Tests | Covers |
|---|---|---|
| `SupportTicketing.UnitTests` | 18 | Password hashing, TOTP validation |
| `SupportTicketing.ArchitectureTests` | 7 | Layer dependencies, no entities on controllers, anonymous-endpoint allowlist, tenant-filter bypass allowlist |
| `SupportTicketing.IntegrationTests` | 21 | Full auth flow against a real SQL Server database |

Integration tests use a **real SQL Server database** (`SupportTicketing_IntegrationTests`,
dropped and recreated per run), not the in-memory provider. Every defect found while
building this feature reproduced only against a provider that actually applies global
query filters — an in-memory double would have passed while the API was broken.

---

## Delivery status

### Built and verified

- Solution scaffold, central package management, layering enforced by tests
- Domain model: organizations, offices, departments, users, roles, permissions,
  teams, skills, categories, applications, priority matrix, audit log, settings
- 22 tables, 23 foreign keys, 21 unique indexes, 4 check constraints, applied to a
  real SQL Server database via two EF Core migrations
- JWT authentication with rotating refresh tokens and reuse detection
- Permission-based authorization with a runtime-materialised policy provider
- Global tenant filtering and soft deletion
- Serilog structured logging, correlation IDs, Problem Details, health checks,
  rate limiting, security headers, CORS allowlist, Swagger
- Development-only seeder with two tenants and thirteen users
- **Ticket lifecycle**: creation with matrix-calculated priority, concurrency-safe
  numbering, category-driven routing, assignment and acceptance, the full status
  workflow with transition validation, conversations with internal notes, resolution,
  requester confirmation and reopening — each step writing append-only history
- **Timeline reconstruction** attributing every change to a person, a rule or a job
- React frontend: sign-in, role-aware dashboard, ticket list with URL-backed filters,
  a creation form that shows calculated priority without letting anyone pick it,
  ticket detail with conversation and history, profile with session revocation,
  permission-filtered navigation, light/dark themes, responsive layout

### Not built yet

Attachments, work logs, SLA engine, business calendars, escalations, notifications,
SignalR, approvals, knowledge base, satisfaction ratings, reporting, ERP-related
record links and AI assistance. Frontend routes for these exist and state plainly that
they are unimplemented.

### Known limitations

- **EF Core emits six warnings at startup** about required navigations on filtered
  entities (`RefreshToken`→`User`, `UserRole`→`Role`, and four similar). These are
  benign given `BeginTenantScope`, but the durable fix is to make the join entities
  tenant-owned so their filters match. Worth doing before ticketing lands.
- **Permissions are embedded in the access token.** A permission revoked mid-session
  stays effective until the token expires, which is why the lifetime defaults to 15
  minutes. Immediate revocation requires deactivating the user or revoking their
  refresh-token families.
- No Docker configuration — Docker is not installed on the build machine.
- `Workers` and `Contracts` are scaffolded but largely empty.

---

## Security notes

- Passwords: PBKDF2-HMAC-SHA512 via ASP.NET Core Identity's hasher, with
  rehash-on-login when the platform work factor increases.
- Refresh tokens: opaque 256-bit random values, stored only as SHA-256 hashes.
- Sign-in failures are indistinguishable between unknown email and wrong password,
  including hashing cost.
- Failed sign-ins, lockouts and token reuse are written to an append-only audit log
  that never records passwords, tokens or message bodies.
- Secrets come from user-secrets or environment variables; `.gitignore` excludes
  `.env`, `appsettings.Production.json` and `secrets.json`.

---

## Database

Two migrations applied:

| Migration | Purpose |
|---|---|
| `InitialIdentityAndMasterData` | 22 tables, indexes, constraints |
| `FixSystemSettingsUniqueIndex` | Removes EF's automatic `IS NOT NULL` filter so duplicate global settings keys are actually prevented |
| `TicketingCore` | 11 ticket tables — tickets, comments, mentions, attachments, assignments, status and priority history, work logs, related records, tags, number sequences |

To roll back:

```bash
dotnet ef database update InitialIdentityAndMasterData --project src/SupportTicketing.Infrastructure --startup-project src/SupportTicketing.Api
```

Backup before any destructive migration:

```bash
sqlcmd -S . -E -Q "BACKUP DATABASE SupportTicketing TO DISK='C:\backups\SupportTicketing.bak' WITH INIT, COMPRESSION"
```
