# syntax=docker/dockerfile:1

# Built from this directory:
#   docker build -f frontend/Dockerfile frontend

# ----------------------------------------------------------------------- build
FROM node:24-alpine AS build
WORKDIR /app

# The lockfile alone first, so a source edit reuses the installed modules layer.
COPY package.json package-lock.json ./
RUN npm ci

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
