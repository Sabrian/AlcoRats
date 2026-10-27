# AlcoRats.Bot — Release image (build context = repository root)
#   docker build -f AlcoRats.Bot/Dockerfile -t alcorats-bot:latest .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Directory.Packages.props", "."]
COPY ["AlcoRats.Bot/AlcoRats.Bot.csproj", "AlcoRats.Bot/"]
RUN dotnet restore "./AlcoRats.Bot/AlcoRats.Bot.csproj"

COPY ["AlcoRats.Bot/", "AlcoRats.Bot/"]
WORKDIR "/src/AlcoRats.Bot"
RUN dotnet publish "./AlcoRats.Bot.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final

# Ubuntu apt "chromium" is a snap stub and does not work in Docker.
# Install Google Chrome Stable instead (real binary + deps).
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        fonts-liberation \
        fonts-noto-color-emoji \
        gnupg \
        wget \
    && wget -q -O /tmp/google-chrome.deb https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb \
    && apt-get install -y --no-install-recommends /tmp/google-chrome.deb \
    && rm -f /tmp/google-chrome.deb \
    && rm -rf /var/lib/apt/lists/* \
    && printf '%s\n' \
        'pcm.!default { type null }' \
        'ctl.!default { type null }' \
        > /etc/asound.conf

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    ChromiumExecutablePath=/usr/bin/google-chrome-stable \
    JitsiUserDataDir=/data/chromium-profile \
    JitsiRemoteDebuggingPort=9333 \
    JitsiHeadless=true

WORKDIR /app
COPY --from=build /app/publish .

RUN mkdir -p /data/chromium-profile /data/logs \
    && chown -R $APP_UID:$APP_UID /app /data

USER $APP_UID

VOLUME ["/data"]
EXPOSE 9333

ENTRYPOINT ["dotnet", "AlcoRats.Bot.dll"]
