# syntax=docker/dockerfile:1
#
# Charter control plane. One container, one port, one artifact (spec sections 2.3 and 3.1).
#
#   docker build \
#     --build-arg VERSION="$(git describe --tags --always)" \
#     --build-arg COMMIT_SHA="$(git rev-parse HEAD)" \
#     --build-arg SOURCE_URL="https://github.com/binn/Charter" \
#     --build-arg BUILD_DATE="$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
#     -t charter:local .

# ------------------------------------------------------------------------------------------------
# Stage 1: build the SPA. Kept separate because the .NET SDK image carries no Node runtime.
# ------------------------------------------------------------------------------------------------
FROM node:24-bookworm-slim AS client

WORKDIR /src/ClientApp

# Copy manifests first so the dependency layer is cached until the lockfile changes.
COPY src/Charter/ClientApp/package.json src/Charter/ClientApp/package-lock.json ./

# Lockfile-only, no install scripts (spec section 16.2).
RUN npm ci --ignore-scripts

COPY src/Charter/ClientApp/ ./

# vite.config.ts writes to ../wwwroot, so the output lands at /src/wwwroot.
# The SPA falls back to its in-repo mock unless this is set, so a built image would otherwise
# ship a UI that never talks to the server it is bundled into.
ENV VITE_CHARTER_LIVE_API=true
RUN npm run build


# ------------------------------------------------------------------------------------------------
# Stage 2: build and publish the .NET control plane.
# ------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

# Compiled into the assembly for the AGPL section 13 source link (section 24) and the update
# check (section 28). Kept below the restore layer so a version bump does not bust the NuGet cache.
ARG VERSION=0.1.0-dev
ARG COMMIT_SHA=unknown
ARG SOURCE_URL=https://github.com/binn/Charter
ARG BUILD_DATE=unknown

WORKDIR /src

# Restore against manifests only, so this layer survives every source-only change.
COPY Directory.Build.props ./
COPY src/Charter/Charter.csproj src/Charter/
RUN dotnet restore src/Charter/Charter.csproj

COPY src/Charter/ src/Charter/

# The SPA was built in stage 1; SkipClientAppBuild stops MSBuild reaching for an npm that is not here.
COPY --from=client /src/wwwroot/ src/Charter/wwwroot/

RUN dotnet publish src/Charter/Charter.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish \
      -p:SkipClientAppBuild=true \
      -p:CharterVersion="${VERSION}" \
      -p:CharterCommitSha="${COMMIT_SHA}" \
      -p:CharterSourceUrl="${SOURCE_URL}" \
      -p:CharterBuildDate="${BUILD_DATE}"


# ------------------------------------------------------------------------------------------------
# Stage 3: runtime.
# ------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

ARG VERSION=0.1.0-dev
ARG COMMIT_SHA=unknown
ARG SOURCE_URL=https://github.com/binn/Charter
ARG BUILD_DATE=unknown

LABEL org.opencontainers.image.title="Charter" \
      org.opencontainers.image.description="File a feature request in plain language; get a preview you can click." \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT_SHA}" \
      org.opencontainers.image.source="${SOURCE_URL}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.licenses="AGPL-3.0-only"

# curl is here for the compose healthcheck; git is here because nothing else in the slim image
# provides it and the control plane shells out for repository metadata.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish/ ./

# Agent adapters are data, not code (section 12b), so they ship as YAML beside the app rather than
# being compiled in. Without this the container fails at boot with "no adapter directory was found".
# Operators add their own by mounting a directory and pointing CHARTER_ADAPTERS_PATH at it.
COPY adapters/ ./adapters/

# Section 2.3: one HTTP port, all config from environment variables.
ENV PORT=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1
EXPOSE 8080

# The aspnet image ships a non-root `app` user (uid 1654). Charter never needs root: section 2.3
# rules out durable local disk, so there is nothing on this filesystem worth writing to.
USER app

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl --fail --silent --show-error http://127.0.0.1:${PORT}/health || exit 1

ENTRYPOINT ["dotnet", "Charter.dll"]
