# Learn about building .NET container images:
# https://github.com/dotnet/dotnet-docker/blob/main/samples/README.md
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /source

# Copy project files and restore as distinct layers
COPY --link *.sln .
COPY --link Purewwk/Purewwk.csproj ./Purewwk/
COPY --link PluginBase/*.csproj ./PluginBase/
COPY --link HlsPlugin/*.csproj ./HlsPlugin/
COPY --link FluidsynthPlugin/*.csproj ./FluidsynthPlugin/
COPY --link CuePlugin/*.csproj ./CuePlugin/
COPY --link FileWatcherPlugin/*.csproj ./FileWatcherPlugin/
RUN dotnet restore purewwk.sln -a $TARGETARCH

# Copy source code
COPY --link . .

# Publish main app
FROM build AS publish-main
RUN dotnet publish Purewwk/Purewwk.csproj -a $TARGETARCH --no-restore -o /app

# Publish HlsPlugin
FROM build AS publish-hls
RUN dotnet publish HlsPlugin/HlsPlugin.csproj -a $TARGETARCH --no-restore -o /app/plugins/

# Publish FluidsynthPlugin
FROM build AS publish-fluidsynth
RUN dotnet publish FluidsynthPlugin/FluidsynthPlugin.csproj -a $TARGETARCH --no-restore -o /app/plugins/

# Publish CuePlugin
FROM build AS publish-cue
RUN dotnet publish CuePlugin/CuePlugin.csproj -a $TARGETARCH --no-restore -o /app/plugins/

# Publish FileWatcherPlugin
FROM build AS publish-filewatcher
RUN dotnet publish FileWatcherPlugin/FileWatcherPlugin.csproj -a $TARGETARCH --no-restore -o /app/plugins/

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0

# Install gosu for proper user switching and fluidsynth
RUN apt-get update && apt-get install -y gosu ffmpeg fluidsynth && rm -rf /var/lib/apt/lists/*

EXPOSE 8080
WORKDIR /app

# Copy published artifacts from each stage
COPY --link --from=publish-main /app .
COPY --link --from=publish-hls /app/plugins ./plugins
COPY --link --from=publish-fluidsynth /app/plugins ./plugins
COPY --link --from=publish-cue /app/plugins ./plugins
COPY --link --from=publish-filewatcher /app/plugins ./plugins

# Create entrypoint script to fix volume permissions and initialize
RUN echo '#!/bin/bash\n\
# Fix permissions as root\n\
if [ -d "/app/music_index" ]; then\n\
  chown -R $APP_UID:$APP_UID /app/music_index\n\
  chmod -R 755 /app/music_index\n\
fi\n\
if [ -d "/app/hls_segments" ]; then\n\
  chown -R $APP_UID:$APP_UID /app/hls_segments\n\
  chmod -R 755 /app/hls_segments\n\
fi\n\
# Switch to app user and execute the application\n\
exec gosu $APP_UID "$@"' > /entrypoint.sh && chmod +x /entrypoint.sh

# Define volumes for persistent data
VOLUME ["/app/music_index", "/app/hls_segments"]

ENTRYPOINT ["/entrypoint.sh", "./Purewwk"]
