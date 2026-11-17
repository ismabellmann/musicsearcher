# syntax=docker/dockerfile:1

ARG DOTNET_SDK_TAG=8.0-bookworm-slim
ARG DOTNET_RUNTIME_TAG=8.0-bookworm-slim

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG} AS build
WORKDIR /src

COPY global.json ./
COPY MusicSync/Core/MusicSync.Core.csproj MusicSync/Core/
COPY MusicSync/Infrastructure/MusicSync.Infrastructure.csproj MusicSync/Infrastructure/
COPY MusicSync/Worker/MusicSync.Worker.csproj MusicSync/Worker/
COPY MusicSync.Api/MusicSync.Api.csproj MusicSync.Api/
RUN dotnet restore MusicSync.Api/MusicSync.Api.csproj

COPY . .
RUN dotnet publish MusicSync.Api/MusicSync.Api.csproj \
    --configuration Release \
    --output /app \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_TAG} AS runtime

# Pinned tool versions. Update these together with the checksums when bumping.
ARG YTDLP_VERSION=2026.10.26
ARG YTDLP_SHA256=767ba9dce7052aa196f8b12652f433d76de128ba168966fa84b6394d5e6c6795
ARG DENO_VERSION=2.9.8
ARG DENO_SHA256=e890975bc2549807bbcc0b584ffafda72cc7c93349dd99c80483dd784f6c4609

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        ffmpeg \
        unzip \
    && rm -rf /var/lib/apt/lists/*

RUN curl --fail --silent --show-error --location \
        "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux" \
        --output /usr/local/bin/yt-dlp \
    && echo "${YTDLP_SHA256}  /usr/local/bin/yt-dlp" | sha256sum --check --strict - \
    && chmod 0755 /usr/local/bin/yt-dlp

RUN curl --fail --silent --show-error --location \
        "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/deno-x86_64-unknown-linux-gnu.zip" \
        --output /tmp/deno.zip \
    && echo "${DENO_SHA256}  /tmp/deno.zip" | sha256sum --check --strict - \
    && unzip -q /tmp/deno.zip -d /usr/local/bin \
    && rm /tmp/deno.zip \
    && chmod 0755 /usr/local/bin/deno

# TrueNAS apps use UID/GID 568. The container never runs as root.
RUN groupadd --gid 568 apps \
    && useradd --uid 568 --gid 568 --create-home --shell /usr/sbin/nologin apps

WORKDIR /app
COPY --from=build /app ./

RUN mkdir -p /config /music /music/.musicsync-staging \
    && chown -R 568:568 /config /music /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:28080 \
    Config__Dir=/config \
    Data__DatabasePath=/config/musicsync.db \
    Library__Root=/music \
    Library__StagingDirectory=/music/.musicsync-staging \
    Tools__Ffmpeg=/usr/bin/ffmpeg \
    Tools__Ffprobe=/usr/bin/ffprobe \
    Tools__YtDlp=/usr/local/bin/yt-dlp

USER 568:568
EXPOSE 28080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:28080/api/health || exit 1

ENTRYPOINT ["dotnet", "MusicSync.Api.dll"]
