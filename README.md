# Support Ticketing System

Enterprise support ticketing platform for internal IT, ERP application support, customer
support and supplier support. ASP.NET Core 10 Web API, SQL Server, Clean Architecture.

> **Current state: Phases 1 to 5 complete, front and back.**
> Authentication, master data, the full ticket lifecycle, the SLA engine with business
> calendars, the escalation ladder, notifications over SignalR, dashboards, the
> knowledge base, satisfaction ratings, ERP record links and optional AI assistance are
> implemented and covered by tests that run against a real SQL Server database.
> Analytical reporting with CSV export, the audit log viewer and the full
> administration section — users, roles and permissions, teams, catalogue and the
> priority matrix, SLA policies and business calendars, AI assistance and system
> settings — are built and usable. Every navigation destination now leads to a working
> screen; none is a placeholder.
> **Email intake is not built.**
> See [Delivery status](#delivery-status) for exactly what exists.

---

## Prerequisites

| Tool | Version used | Notes |
|---|---|---|
| .NET SDK | 10.0.400 | `dotnet --list-sdks` |
| SQL Server | 2025 Developer (17.0) | Any edition from 2019 onward should work |
| `dotnet-ef` | 10.0.11 | `dotnet tool update --global dotnet-ef --version 10.0.11` |
| Node.js | 24.x | For the frontend, once it exists |

---

## Deploying it

[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) covers installing on a server: configuration
reference, creating the schema, the first-run bootstrap, database permissions, health
checks, backups, upgrades, and a runbook for the failures an operator will actually
meet. What follows here is local development.

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

AI assistance is optional. Without a key the system runs identically — every AI call
reports itself unavailable and the deterministic answer stands. To enable it:

```bash
dotnet user-secrets set "OpenAi:ApiKey" "<your key>" --project src/SupportTicketing.Api
```

The key stays on the server. It is never sent to the browser, never stored in the
database, and the React app has no code path that could reach a provider directly.
Capabilities remain off until an administrator turns them on at **Administration → AI
assistance**, and each organization has its own switches.

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

### Landing page

`/` serves a public landing page to a visitor and redirects a signed-in user straight
to their dashboard — a marketing page is an obstacle between somebody and their queue.
It is code-split for the same reason the dashboard is: a daily user should never
download a page they will not see.

The hero and the sign-in panel share a
[3D particle field](frontend/src/components/visual/ParticleField.jsx): a rotating point
cloud with genuine perspective projection, depth-driven size, opacity and connections,
and a shallow pointer parallax. Hand-written rather than pulled in — a WebGL library is
around 150KB gzipped on the one page every user waits for, and this is roughly six. It
reads its palette from CSS so it follows the theme, holds one static frame under
reduced motion, and stops entirely while the tab is hidden.

### Motion

GSAP drives the interface's movement through one shared vocabulary in
[`src/motion`](frontend/src/motion), so the whole application moves in a single accent
rather than each screen inventing its own. Durations are 90–380ms: this is a tool
people use all day, and motion here exists to show where something came from, not to
be admired.

Two rules the implementation is built around, both learned by breaking it:

- **The DOM always carries the truth.** A counting KPI renders its real figure and the
  animation walks it *backwards* to the starting point. Rendering zero and letting the
  tween supply the number means any failure leaves a dashboard confidently reporting
  no open tickets when there are five.
- **Do not animate what nobody is looking at.** Browsers stop servicing
  `requestAnimationFrame` in a hidden tab, so an entrance begun there writes its
  opening state — opacity zero — and never advances. Motion is skipped entirely when
  the page is hidden, which leaves everything in its natural, visible state.

`prefers-reduced-motion` is honoured in both CSS and GSAP; for some people motion is
not a preference but a migraine.

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
| POST | `/api/v1/auth/change-password` | Bearer | Requires the current password; revokes every session |
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
| POST | `/api/v1/tickets/{id}/related-records` | `ticket.link_records` | Links a purchase order, shipment, style… |
| DELETE | `/api/v1/tickets/{id}/related-records/{recordId}` | `ticket.link_records` | Archives the link, never erases it |
| GET | `/api/v1/tickets/by-record` | Bearer | Other tickets against the same ERP record |
| GET | `/api/v1/tickets/{id}/sla` | Bearer | Clock state, targets, elapsed and remaining |
| GET | `/api/v1/tickets/{id}/sla/events` | Bearer | Every pause, resume, breach and settlement |
| GET | `/api/v1/sla/policies` | `sla.view` | Policies with per-priority targets |
| GET | `/api/v1/sla/escalations` | `escalation.view` | Escalations fired, with recipients |
| GET | `/api/v1/dashboard` | Bearer | One endpoint; content follows the caller's data scope |
| GET | `/api/v1/reports/sla-compliance` | `reports.view` | By priority, team and category; settled clocks only |
| GET | `/api/v1/reports/agent-performance` | `reports.view` | Throughput beside reopens, breaches and CSAT |
| GET | `/api/v1/reports/volume-trend` | `reports.view` | Raised, resolved, reopened and the resulting backlog |
| GET | `/api/v1/reports/satisfaction` | `reports.view` | Distribution, response rate, by agent, with comments |
| POST | `/api/v1/reports/export` | `reports.export` | CSV. Report name is an allowlist; the download is audited |
| GET | `/api/v1/audit` | `audit.view` | Search the append-only log, denied actions included |
| GET | `/api/v1/audit/filters` | `audit.view` | The values that actually occur, for the filter controls |
| GET | `/api/v1/audit/entities/{id}` | `audit.view` | One entity's history, oldest first |
| GET | `/api/v1/admin/users` | `users.manage` | Search accounts with roles, teams and load |
| POST | `/api/v1/admin/users` | `users.manage` | Generates a one-time password, shown once |
| PUT | `/api/v1/admin/users/{id}/roles` | `roles.manage` | Replaces the set wholesale |
| POST | `/api/v1/admin/users/{id}/active` | `users.manage` | Deactivate — revokes every session |
| POST | `/api/v1/admin/users/{id}/reset-password` | `users.manage` | New one-time password, signs out everywhere |
| GET | `/api/v1/admin/roles` | `roles.manage` | Roles with permissions and holder counts |
| GET | `/api/v1/admin/permissions` | `roles.manage` | The catalogue, read from the table |
| PUT | `/api/v1/admin/roles/{id}/permissions` | `roles.manage` | Refused if it would orphan role management |
| GET | `/api/v1/admin/teams` | `teams.manage` | Teams, members, capacity weights, load |
| PUT | `/api/v1/admin/teams/{id}/members` | `teams.manage` | Add or re-weight a member |
| GET | `/api/v1/admin/catalog/categories` | `catalog.manage` | Categories, subcategories, ticket counts |
| GET | `/api/v1/admin/catalog/applications` | `catalog.manage` | Applications and modules |
| PUT | `/api/v1/admin/catalog/priority-matrix` | `catalog.manage` | All sixteen cells or nothing |
| GET | `/api/v1/admin/sla/policies` | `sla.manage` | Policies, targets, running-clock counts |
| GET | `/api/v1/admin/sla/calendars` | `calendars.manage` | Working hours and holidays |
| GET | `/api/v1/admin/settings` | `system.configure` | Runtime configuration, secrets masked |
| GET | `/api/v1/admin/reference` | any admin | Every dropdown an admin form needs, in one call |
| GET | `/api/v1/tickets/{id}/feedback` | Bearer | Satisfaction rating, if given |
| POST | `/api/v1/tickets/{id}/feedback` | requester | One rating per ticket, editable by its author |
| GET | `/api/v1/knowledge/articles` | `knowledge.view` | Search, filter, page |
| POST | `/api/v1/knowledge/articles` | `knowledge.author` | Draft; publishing is a separate transition |
| GET | `/api/v1/knowledge/suggestions` | `knowledge.view` | Relevant articles for ticket text |
| POST | `/api/v1/ai/tickets/{id}/priority-recommendation` | `ai.use` | Suggestion only; the matrix still decides |
| GET | `/api/v1/ai/status` | `ai.configure` | Capability flags and this month's usage and cost |
| PUT | `/api/v1/ai/configuration` | `ai.configure` | Per-organization switches, audited |
| GET | `/api/v1/categories` | Bearer | Categories with subcategories |
| GET | `/api/v1/applications` | Bearer | Applications with modules |
| GET | `/api/v1/agents` | `ticket.assign` | Assignable agents with open-ticket counts |
| GET | `/health/live` | Anonymous | Liveness |
| GET | `/health/ready` | Anonymous | Readiness, checks the database |

95 endpoints across sixteen controllers; the table above lists the ones worth knowing
about. Errors are RFC 7807 Problem Details with a stable `code` and a `correlationId`.
Stack traces and SQL are never serialised.

---

## Testing

```bash
dotnet test
```

**288 backend tests and 30 frontend tests, all passing** as of the last run:

| Project | Tests | Covers |
|---|---|---|
| `SupportTicketing.UnitTests` | 113 | Password hashing, priority matrix, workflow graph, business-hours arithmetic including DST, SLA state machine |
| `SupportTicketing.ArchitectureTests` | 7 | Layer dependencies, no entities on controllers, anonymous-endpoint allowlist, tenant-filter bypass allowlist, no SQL Server provider types in Application |
| `SupportTicketing.IntegrationTests` | 168 | Auth, ticket lifecycle, tenant isolation, SLA and escalation sweeps, report scoping, CSV export and formula neutralisation, audit filtering, the whole administration surface including permission guards and secret masking, knowledge base, satisfaction, ERP links and the AI fallback path — against a real SQL Server database |
| `frontend` (Vitest) | 30 | Token store, single-flight refresh, navigation filtering, SLA formatting, and the motion fail-safe — that a figure is correct even when no animation runs |

Integration tests use a **real SQL Server database** (`SupportTicketing_IntegrationTests`,
dropped and recreated per run), not the in-memory provider. Every defect found while
building this system reproduced only against a provider that actually applies global
query filters — an in-memory double would have passed while the API was broken.

---

## Delivery status

### Built and verified

**Platform**

- Solution scaffold, central package management, layering enforced by tests
- 54 tables applied to a real SQL Server database through six EF Core migrations
- JWT authentication with rotating refresh tokens and reuse detection
- Permission-based authorization: 55 permission keys, a runtime-materialised policy
  provider, and six data scopes. Nothing in the codebase branches on a role name
- Global tenant filtering, fail-closed, plus soft deletion and append-only history
- Serilog structured logging, correlation IDs, Problem Details, health checks,
  rate limiting, security headers, CORS allowlist, Swagger
- **Production bootstrap**: brings a migrated-but-empty database to the point where
  somebody can sign in — the permission catalogue, the seven system roles, one
  organization and one administrator with a one-time password printed once. Idempotent,
  runs in every environment, and additive on upgrade so a release that introduces a
  permission grants it to Super Admin rather than leaving it unreachable
- **Start-up refusal** in non-development environments on a missing, short or
  placeholder signing key, an empty or wildcard CORS list, demo seeding left on, or a
  plaintext origin. Each of these otherwise produces an application that runs perfectly
  well and is quietly insecure
- **Forwarded-header handling** with a known-proxy allowlist, so behind IIS or nginx the
  audit log records the person rather than the load balancer, and the sign-in rate
  limiter does not treat the whole organization as one client
- Development-only seeder, double-gated, with two tenants and thirteen users
- Self-service password change, and confinement of any account still using a password
  an administrator issued: such a session can reach only its own profile, the change
  endpoint and sign-out until it sets its own password

**Phase 1–2 — identity and the ticket lifecycle**

- Organizations, offices, departments, users, roles, permissions, teams, skills,
  categories, applications, modules, the priority matrix, tags and settings
- Ticket creation with matrix-calculated priority, concurrency-safe numbering
  (`TKT-2026-000001`, allocated by an atomic `UPDATE … OUTPUT`), category-driven
  routing, assignment and acceptance, the full status workflow with transition
  validation, conversations with internal notes, resolution, requester confirmation
  and reopening — each step writing append-only history
- Timeline reconstruction attributing every change to a person, a rule, AI or a job

**Phase 3 — service levels**

- SLA policies and per-priority response and resolution targets
- Business calendars: working hours, weekends and holidays, with DST-correct
  arithmetic. An empty calendar means continuous cover rather than no cover
- Clocks that pause while a ticket waits on the requester or a third party, and
  recalculate on priority change without ever rebasing the start
- An escalation ladder whose recipients are resolved by role at firing time, with
  idempotency enforced by unique index rather than by hoping the sweep runs once
- Notifications with per-user preferences, delivery records and SignalR push

**Phase 4 — insight**

- One dashboard endpoint whose content follows the caller's data scope: twelve KPIs,
  volume over time, breakdowns by priority, status and category, and agent workload
  weighted by priority
- Every chart segment drills through to the ticket list using the same filter it counted
- Knowledge base with versioned articles, a review workflow, feedback and
  ticket-text suggestions
- Satisfaction ratings, one per ticket, editable only by their author
- **Four analytical reports** — SLA compliance broken down by priority, team and
  category; agent performance showing throughput beside reopens, breaches and CSAT;
  volume and backlog over time anchored to the real opening position; and satisfaction
  with its response rate. Compliance counts settled clocks only, so a running clock
  neither flatters nor damns the period
- **CSV export** of any report or of the underlying ticket rows. The report name is
  an allowlist rather than anything that reaches a table or column, values that a
  spreadsheet would execute as a formula are neutralised, and every download is
  written to the audit log
- **Audit log viewer**: search by action, entity, person, correlation identifier or
  free text, with denied actions kept and each row expandable to the field values,
  reason, IP address and correlation identifier recorded with it

**Administration**

- **Users**: search, create, edit, deactivate and restore, replace roles, reset a
  password, sign an account out everywhere. Accounts are never deleted. Creation and
  reset generate a one-time password the administrator never chooses and the system
  never stores readably; deactivation and reset revoke every refresh token the person
  holds, which is what actually ends access
- **Roles and permissions**: a checklist of all 55 keys grouped by area, editable on
  system roles too — the seeded roles are a starting point, not a contract. The one
  edit that is refused is the one nobody recovers from without database access:
  removing role management from the last role that has it. Unknown keys are rejected
  rather than silently dropped
- **Teams**: membership with per-member capacity weight, team lead, escalation target
  and acceptance timeout. A team cannot escalate to itself, and somebody who still owns
  open work for the team cannot be removed from it
- **Catalogue**: categories, subcategories, applications and modules, plus the
  impact-by-urgency **priority matrix** edited as a grid. All sixteen cells are
  required — a partial save is refused, because a half-configured grid is the kind of
  thing nobody notices until a Critical ticket comes out Medium
- **SLA policies** with per-priority response and resolution targets, and **business
  calendars** with weekly hours and holidays. Time zones are validated at configuration
  time rather than failing later inside a background sweep; a resolution target inside
  its response target is refused as unsatisfiable
- **System settings**: per-organization overrides of runtime configuration, with
  sensitive values masked in the API, in the UI and in the audit trail. Sending a
  masked value back leaves the stored secret untouched — otherwise opening the page and
  pressing save would destroy every credential on it
- **AI assistance** settings (see Phase 5)

**Phase 5 — business context and AI**

- ERP record links: purchase order, style, customer, supplier, factory, merchant,
  production order, inspection, shipment, invoice, debit note, commission invoice and
  Digital Product Passport. References and optional deep links are stored — never a
  copy of ERP data, so there is no second source of truth to drift
- "Which other tickets concern this purchase order?" answered through the scoped
  ticket list, so it cannot reveal tickets the caller may not see
- Optional AI assistance behind per-organization switches that are **off by default**:
  a provider client with a circuit breaker, timeout and schema validation; usage and
  cost recorded per call including failures; and prompts that carry only subject,
  description, impact and urgency. Only a hash of the input is stored, never the text
- The deterministic priority matrix runs **first** and remains the answer of record.
  AI never writes to the database, never bypasses authorization, and reports itself
  unavailable rather than silently substituting the rule's answer

**Frontend**

- Sign-in, role-aware dashboard with Recharts, ticket list with URL-backed filters,
  a creation form that shows calculated priority without letting anyone pick it,
  ticket detail with conversation, SLA panel, escalation history, business context,
  AI suggestions and satisfaction, an escalations queue, the knowledge base, a profile
  with session revocation, and the AI settings screen
- Permission-filtered navigation, light and dark themes, responsive layout

### Not built yet

- **Email intake.** Nothing reads a mailbox or turns a reply into a comment. This was
  in the Phase 5 brief and is not implemented; `TicketSource.Email` exists in the
  domain but no ingester writes it.
- **A malware scanner.** Uploads are recorded as `Skipped` rather than claiming a
  clean result nobody produced. Wiring one in is an `IAttachmentPolicy` change plus a
  scan step; the state machine already has `Pending`, `Infected` and `ScanFailed`.
- **Email verification.** A user changing their own address is not asked to confirm
  it, because nothing can send the confirmation. They must supply their password and
  the change is audited with both addresses, but somebody can set an address they do
  not own.
- **Approvals and parent–child tickets**, and **SMTP delivery** — notifications are
  stored and shown in the app, but nothing is emailed, so an escalation reaches only
  somebody who is already looking.
- **Scheduled report delivery.** Reports are pulled on demand and exported by hand;
  nothing emails a weekly summary.

### Known limitations

- **The OpenAI code path has never run against the live API.** No key is configured on
  this machine, so every AI test exercises the unavailable branch — which is the branch
  that matters for correctness, but the success path is unproven against a real
  provider. Treat it as written-and-reviewed, not verified.
- **Seven EF Core navigation warnings are suppressed**, with the reasoning recorded at
  the suppression. Every `Include` in the codebase is rooted at the principal, so the
  warned scenario cannot arise; the alternative was editing the tenant-isolation
  machinery to remove log noise. The cost is that a future entity of the same shape
  gets no warning — the isolation tests are what guard that.
- **Permissions are embedded in the access token.** A permission revoked mid-session
  stays effective until the token expires, which is why the lifetime defaults to 15
  minutes. Immediate revocation requires deactivating the user or revoking their
  refresh-token families.
- **The refresh token is held in `localStorage`**, which is readable by any script that
  achieves XSS. The access token is kept in memory only. A `Secure`/`HttpOnly` cookie
  would be stronger and needs a same-site deployment topology to be practical.
- **The Docker images are written but unverified.** Docker Desktop is installed here
  and its engine would not start without an interactive desktop session, so
  `docker compose up` has never actually run on this machine. The Dockerfiles, compose
  file and nginx configuration are reviewed and their YAML parses; treat them as
  unproven until somebody runs them.

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

Six migrations applied:

| Migration | Purpose |
|---|---|
| `InitialIdentityAndMasterData` | 22 tables, indexes, constraints |
| `FixSystemSettingsUniqueIndex` | Removes EF's automatic `IS NOT NULL` filter so duplicate global settings keys are actually prevented |
| `TicketingCore` | 11 ticket tables — tickets, comments, mentions, attachments, assignments, status and priority history, work logs, related records, tags, number sequences |
| `SlaEscalationsAndNotifications` | Business calendars, hours, holidays, SLA policies, targets, ticket clocks, SLA events, escalation policies, steps and history, notifications, deliveries and preferences |
| `KnowledgeBaseAndSatisfaction` | Knowledge articles, versions, feedback and satisfaction ratings |
| `AiAssistance` | AI configuration, prompt templates, recommendations and append-only usage records |

To roll back:

```bash
dotnet ef database update InitialIdentityAndMasterData --project src/SupportTicketing.Infrastructure --startup-project src/SupportTicketing.Api
```

Backup before any destructive migration:

```bash
sqlcmd -S . -E -Q "BACKUP DATABASE SupportTicketing TO DISK='C:\backups\SupportTicketing.bak' WITH INIT, COMPRESSION"
```
