# Build context is the repository root, not this directory, because the build
# needs Directory.Build.props and Directory.Packages.props.
#   docker build -f src/OpcMonitor.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Project files first, so a change to source code does not invalidate the
# restore layer. There is deliberately no NuGet.config to copy: this restore
# succeeding with only the default nuget.org feed is the proof that every
# dependency is public.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/OpcMonitor.Domain/OpcMonitor.Domain.csproj src/OpcMonitor.Domain/
COPY src/OpcMonitor.Infrastructure/OpcMonitor.Infrastructure.csproj src/OpcMonitor.Infrastructure/
COPY src/OpcMonitor.Api/OpcMonitor.Api.csproj src/OpcMonitor.Api/
RUN dotnet restore src/OpcMonitor.Api/OpcMonitor.Api.csproj

COPY src/ src/
RUN dotnet publish src/OpcMonitor.Api/OpcMonitor.Api.csproj \
    -c Release \
    -o /app \
    --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# curl is here for the container healthcheck below and for debugging a running
# container; it is the only thing added to the base image.
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# The OPC UA application instance certificate is generated on first run and must
# outlive the container, otherwise the client presents a new identity to the
# server on every restart and any trust it was granted is lost.
ENV Opc__PkiRoot=/var/opt/opcmonitor/pki
RUN mkdir -p /var/opt/opcmonitor/pki && chown -R app:app /var/opt/opcmonitor
VOLUME ["/var/opt/opcmonitor/pki"]

# The base image provides a non-root 'app' user; use it.
USER app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "OpcMonitor.Api.dll"]
