# syntax=docker/dockerfile:1.6
#
# Multi-stage pro PoV de busca de extrato do Itaú.
#
# Build:  docker build -t itau-extrato-pov .
# Run:    docker run --rm -p 5218:5218 \
#           -e REDIS_URL='redis://your-host:6379' \
#           -e OPENAI_API_KEY='sk-...' \
#           itau-extrato-pov
#
# Em produção (docker compose) o REDIS_URL aponta pro service redis interno.

# ------------------------------------------------------------------
# Build
# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Itau.Extrato.sln ./
COPY src/Itau.Extrato.Search/Itau.Extrato.Search.csproj src/Itau.Extrato.Search/
COPY src/Itau.Extrato.Seed/Itau.Extrato.Seed.csproj src/Itau.Extrato.Seed/
COPY src/Itau.Extrato.Api/Itau.Extrato.Api.csproj src/Itau.Extrato.Api/
RUN dotnet restore Itau.Extrato.sln

COPY src/ src/
RUN dotnet publish src/Itau.Extrato.Api/Itau.Extrato.Api.csproj \
        -c Release -o /publish --no-restore /p:UseAppHost=false

# ------------------------------------------------------------------
# Runtime
# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
USER app

COPY --from=build --chown=app:app /publish ./
COPY --chown=app:app seeds/ ./seeds/

ENV ASPNETCORE_URLS=http://+:5218 \
    SEEDS_DIR=/app/seeds \
    DOTNET_PRINT_TELEMETRY_MESSAGE=false \
    DOTNET_NOLOGO=true

EXPOSE 5218

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD wget -qO- http://127.0.0.1:5218/api/health || exit 1

ENTRYPOINT ["dotnet", "Itau.Extrato.Api.dll"]
