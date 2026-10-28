# AlcoRats.Bot — Release image (build context = repository root)
#   docker build -f AlcoRats.Bot/Dockerfile -t alcorats-bot:latest .
#
# Self-contained publish → Ubuntu 22.04 + Google Chrome (real binary, not snap).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Directory.Packages.props", "."]
COPY ["AlcoRats.Bot/AlcoRats.Bot.csproj", "AlcoRats.Bot/"]
RUN dotnet restore "./AlcoRats.Bot/AlcoRats.Bot.csproj" -r linux-x64

COPY ["AlcoRats.Bot/", "AlcoRats.Bot/"]
WORKDIR "/src/AlcoRats.Bot"
RUN dotnet publish "./AlcoRats.Bot.csproj" \
    -c $BUILD_CONFIGURATION \
    -r linux-x64 \
    --self-contained true \
    -o /app/publish \
    /p:PublishTrimmed=false

FROM ubuntu:22.04 AS final

ARG APP_UID=1654
ENV DEBIAN_FRONTEND=noninteractive \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    ChromiumExecutablePath=/opt/google/chrome/chrome \
    JitsiUserDataDir=/data/chromium-profile \
    JitsiRemoteDebuggingPort=9333 \
    JitsiHeadless=true \
    LiteDbPath=/data/data.db \
    LogDirectory=/data/logs \
    PiperCacheDir=/app/Speech \
    PiperVoice=dmitri \
    DBUS_SESSION_BUS_ADDRESS=/dev/null \
    DBUS_SYSTEM_BUS_ADDRESS=/dev/null

USER root

# ca-certificates + libicu70: self-contained .NET
# wget: required to fetch Chrome .deb (Chrome also Depends on wget — do not remove it)
# google-chrome-stable: real browser binary (Ubuntu "chromium" apt package is a snap stub)
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates libicu70 wget; \
    wget -O /tmp/google-chrome.deb \
        https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb; \
    apt-get install -y --no-install-recommends /tmp/google-chrome.deb; \
    rm -f /tmp/google-chrome.deb; \
    rm -rf /var/lib/apt/lists/*; \
    test -x /opt/google/chrome/chrome; \
    /opt/google/chrome/chrome --version; \
    ln -sf /opt/google/chrome/chrome /usr/local/bin/chromium; \
    printf '%s\n' \
        'pcm.!default { type null }' \
        'ctl.!default { type null }' \
        > /etc/asound.conf; \
    groupadd --gid "$APP_UID" app; \
    useradd -l --uid "$APP_UID" --gid app --create-home --shell /usr/sbin/nologin app

WORKDIR /app
COPY --from=build /app/publish .

RUN mkdir -p /data/chromium-profile /data/logs /data/piper \
    && chown -R $APP_UID:$APP_UID /app /data \
    && chmod +x /app/AlcoRats.Bot

USER $APP_UID

VOLUME ["/data"]
EXPOSE 9333

ENTRYPOINT ["./AlcoRats.Bot"]
