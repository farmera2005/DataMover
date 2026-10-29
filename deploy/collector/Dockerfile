# The Lakeflow central collector. Build from the repository root:
#   docker build -f deploy/collector/Dockerfile -t lakeflow-collector .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Lakeflow.Contracts/ src/Lakeflow.Contracts/
COPY src/Lakeflow.Connectors.SqlServer/ src/Lakeflow.Connectors.SqlServer/
COPY src/Lakeflow.Agent/ src/Lakeflow.Agent/
RUN dotnet publish src/Lakeflow.Agent -c Release -r linux-x64 --self-contained false -o /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out ./
# Data (per-site buffers, checkpoints, the collector's identity) and the master key live on separate volumes.
RUN mkdir -p /var/lib/lakeflow /var/lib/lakeflow-secrets && chown app:app /var/lib/lakeflow /var/lib/lakeflow-secrets
USER app
VOLUME ["/var/lib/lakeflow", "/var/lib/lakeflow-secrets"]
ENTRYPOINT ["dotnet", "Lakeflow.Agent.dll"]
