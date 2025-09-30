# Learn about building .NET container images:
# https://github.com/dotnet/dotnet-docker/blob/main/samples/README.md
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /source

# Copy project file and restore as distinct layers
COPY --link *.csproj .
RUN dotnet restore -a $TARGETARCH

# Copy source code and publish app
COPY --link . .
RUN dotnet publish -a $TARGETARCH --no-restore -o /app


# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
EXPOSE 8080
WORKDIR /app
COPY --link --from=build /app .

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

# Install gosu for proper user switching
RUN apt-get update && apt-get install -y gosu ffmpeg && rm -rf /var/lib/apt/lists/*

# Define volumes for persistent data
VOLUME ["/app/music_index", "/app/hls_segments"]

ENTRYPOINT ["/entrypoint.sh", "./repos"]
