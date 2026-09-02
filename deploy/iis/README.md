# Deploying the API to IIS

One script, run from an elevated PowerShell on the server:

```powershell
dotnet publish src/SupportTicketing.Api -c Release -o publish
.\deploy\iis\Deploy-Api.ps1 -Payload .\publish -Name SupportTicketingApi -Port 8080
```

Omit `-Port` to attach it under the Default Web Site at `/SupportTicketingApi`
instead of creating a standalone site.

## What it does

| Step | Why it matters |
|---|---|
| Checks for the **Hosting Bundle** | The runtime is not enough. Without the bundle IIS returns 500.19 to everything. |
| Copies the build, **keeping the server's `web.config`** | `dotnet publish` generates a bare one. The server's copy holds the connection string, signing key and CORS list. |
| Pool: No Managed Code, AlwaysRunning, no idle timeout | The email dispatcher and SLA scanner are background services; the default idle timeout silently stops them after 20 quiet minutes. |
| Grants the pool identity read on the app, modify on the data folder | A compromised request cannot rewrite the binaries it runs from. |
| Creates a SQL login for the pool identity | Reader, writer, EXECUTE. Not `db_owner`. |
| Starts, checks `/health/ready`, restarts once more | The escalation ladder is created on the second start. |

## First run

The first run stops after copying, because `web.config` still holds template
values. Edit it — connection string, `Jwt__SigningKey`, `Cors__AllowedOrigins`,
`Bootstrap__*`, and the two paths the script prints — then run the same command
again.

Uploads and logs live under `-DataRoot` (default `C:\SupportTicketingData`),
outside the web root on purpose. Uploads inside the site folder would be served
directly by IIS, bypassing every authorisation check; logs there are destroyed
by the next deploy.

## Every later run

Same command. The script sees `web.config` already exists and leaves it alone.

Apply migrations **before** a build that needs them, with your own credentials,
not the pool's — see `docs/DEPLOYMENT.md` section 7.

## If it stops

- **500.19** — Hosting Bundle not installed, or `web.config` unreadable.
- **500.30** — the process started and died. `web.config` values are wrong;
  read the log folder, or set `stdoutLogEnabled="true"` for one run.
- **502.5** — `dotnet` not on the PATH the pool sees. Reboot after installing
  the bundle, or give `processPath` the full path.
- **Health 200 but nothing escalates** — the second restart was skipped.
