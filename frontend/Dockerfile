# syntax=docker/dockerfile:1

# Built from this directory:
#   docker build -f frontend/Dockerfile frontend

# ----------------------------------------------------------------------- build
FROM node:24-alpine AS build
WORKDIR /app

# The lockfile alone first, so a source edit reuses the installed modules layer.
COPY package.json package-lock.json ./

# npm's defaults assume a good, unmetered link. Inside Docker Desktop the NAT
# stalls under the several hundred parallel connections npm opens by default,
# and the install dies on whichever tarball happened to be in flight — observed
# twice on the same package, at the same 136 seconds, while that tarball fetched
# in three seconds on its own. Capping concurrency and retrying is the
# difference between a build that works on a client's machine and one that
# depends on their network being unusually kind.
RUN npm config set maxsockets 8 \
 && npm config set fetch-retries 5 \
 && npm config set fetch-retry-mintimeout 20000 \
 && npm config set fetch-retry-maxtimeout 180000 \
 && npm config set fetch-timeout 900000 \
 && npm ci --no-audit --no-fund

COPY . .

# Vite inlines these at build time — they are not read at runtime, so an image built
# for one environment cannot be pointed at another by changing an environment
# variable. Pass them with --build-arg, and build one image per environment.
ARG VITE_API_BASE_URL=/api/v1
ARG VITE_SIGNALR_URL=/hubs
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL \
    VITE_SIGNALR_URL=$VITE_SIGNALR_URL

RUN npm run build

# ---------------------------------------------------------------------- runtime
FROM nginx:1.27-alpine AS runtime

COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80

HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://localhost/ || exit 1
