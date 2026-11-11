# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build

WORKDIR /src
COPY . .

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
        --configuration Release \
        --runtime linux-x64 \
        --self-contained true \
        --output /out

# LinuxServer.io Jellyfin 12.4.0 (2026-11-10). The binaries are replaced with the
# patched build, while the base image keeps providing the web client, ffmpeg and
# the /config layout used by the running deployment.
FROM lscr.io/linuxserver/jellyfin@sha256:2f708001387aa9853e744a4a4d572d9970927c479c56d6c9e199f789c5173535

COPY --from=build /out/ /usr/lib/jellyfin/bin/
