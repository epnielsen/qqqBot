# ==========================================================
# STAGE 1: Build
# ==========================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy csproj and restore as distinct layers for cache efficiency
COPY ["qqqBot.csproj", "./"]
RUN dotnet restore "qqqBot.csproj" --runtime linux-musl-x64

# Copy everything else and build
COPY . .
RUN dotnet publish "qqqBot.csproj" -c Release -o /app/publish \
    --runtime linux-musl-x64 \
    --self-contained false \
    /p:UseAppHost=false \
    /p:PublishReadyToRun=true

# ==========================================================
# STAGE 2: Runtime (Alpine for minimal footprint ~100MB)
# ==========================================================
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS final
WORKDIR /app

# Install timezone data for proper market hours handling
RUN apk add --no-cache tzdata

# Create a non-root user for security
RUN adduser --disabled-password --gecos "" appuser && \
    mkdir -p /app/data && \
    chown -R appuser:appuser /app /app/data
USER appuser

# Copy build artifacts
COPY --from=build --chown=appuser:appuser /app/publish .

# Define Volume for persistence (Trading State & Logs)
# When running: -v /your/host/path:/app/data
VOLUME /app/data

# Set working directory for state file location
WORKDIR /app/data

# Health check (optional - verifies process is running)
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD pgrep -f "dotnet.*qqqBot" || exit 1

# Entrypoint points back to the /app directory for the DLL
ENTRYPOINT ["dotnet", "/app/qqqBot.dll"]

# Example usage:
# docker build -t qqqbot:latest .
# docker run -d --restart unless-stopped \
#   --name qqqbot \
#   -v $(pwd)/data:/app/data \
#   -e Alpaca__ApiKey="PK..." \
#   -e Alpaca__ApiSecret="..." \
#   -e TZ="America/New_York" \
#   qqqbot:latest -lowlatency -limit