# syntax=docker/dockerfile:1

# Built from the repository root, not from this directory:
#   docker build -f src/SupportTicketing.Api/Dockerfile .
# The solution uses central package management, so Directory.Packages.props has to be
# in context or every restore resolves nothing.

# ----------------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, on their own layer. Restore is the slow step and it depends
# only on these, so editing a .cs file reuses the cached packages instead of
# downloading them again.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/SupportTicketing.Domain/*.csproj src/SupportTicketing.Domain/
COPY src/SupportTicketing.Contracts/*.csproj src/SupportTicketing.Contracts/
COPY src/SupportTicketing.Application/*.csproj src/SupportTicketing.Application/
COPY src/SupportTicketing.Infrastructure/*.csproj src/SupportTicketing.Infrastructure/
COPY src/SupportTicketing.Workers/*.csproj src/SupportTicketing.Workers/
COPY src/SupportTicketing.Api/*.csproj src/SupportTicketing.Api/

RUN dotnet restore src/SupportTicketing.Api/SupportTicketing.Api.csproj

COPY src/ src/

RUN dotnet publish src/SupportTicketing.Api/SupportTicketing.Api.csproj \
    -c Release -o /app --no-restore

# ---------------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl, for the health check below. The aspnet image ships neither curl nor wget, and
# a HEALTHCHECK that cannot run reports unhealthy forever — which compose then treats
# as a container that never became ready.
USER root
RUN apt-get update     && apt-get install -y --no-install-recommends curl     && rm -rf /var/lib/apt/lists/*

# The image ships a non-root "app" user. Running as root inside a container
# is not the isolation people assume it is: a container escape lands as root on the
# host, and nothing here needs the privilege.
# Created and handed over before dropping privileges: a volume mounted over a path
# the app user cannot write to fails on the first upload, not at start-up.
RUN mkdir -p /app/app-data/attachments && chown -R app:app /app/app-data

USER app

# Attachments live on a volume, not in the image layer. Without the mount they would
# vanish on every redeploy, which is the classic way a container deployment quietly
# loses uploaded files.
VOLUME ["/app/app-data"]

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Storage__RootPath=/app/app-data/attachments

EXPOSE 8080

# Checked against the readiness endpoint, which touches the database — a process that
# is up but cannot reach SQL Server is not ready to take traffic, and compose uses
# this to hold the frontend back until it is.
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
    CMD curl --fail --silent --show-error http://localhost:8080/health/ready || exit 1

COPY --from=build /app .

ENTRYPOINT ["dotnet", "SupportTicketing.Api.dll"]
