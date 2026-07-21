# syntax=docker/dockerfile:1
ARG DOTNET_VERSION=8.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore dependencies first for better layer caching
COPY Directory.Build.props ./
COPY src/Dbosoft.ConfigService/Dbosoft.ConfigService.csproj src/Dbosoft.ConfigService/
COPY src/Dbosoft.ConfigService.Host/Dbosoft.ConfigService.Host.csproj src/Dbosoft.ConfigService.Host/
RUN dotnet restore src/Dbosoft.ConfigService.Host/Dbosoft.ConfigService.Host.csproj

# Build and publish
COPY src/ src/
RUN dotnet publish src/Dbosoft.ConfigService.Host/Dbosoft.ConfigService.Host.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# Data directory mounted as a volume so values, credentials, and audit log
# survive container restarts.
VOLUME ["/data"]
ENV ConfigService__DataDirectory=/data

# Serves plain HTTP on 8080. The HMAC Authorization header carries only a
# signature (never the shared secret), but request/response bodies — i.e. the
# configuration values themselves — travel in clear text. Terminate TLS at a
# reverse proxy / ingress in front of this container, or expose it only on a
# trusted network.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "Dbosoft.ConfigService.Host.dll"]
