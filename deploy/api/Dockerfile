# The Lakeflow control API and the web console it serves. Built by compose.yaml at the repository root.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Lakeflow.Contracts/ src/Lakeflow.Contracts/
COPY src/Lakeflow.Console.Core/ src/Lakeflow.Console.Core/
COPY src/Lakeflow.Console.Web/ src/Lakeflow.Console.Web/
COPY src/Lakeflow.ControlApi/ src/Lakeflow.ControlApi/
RUN dotnet publish src/Lakeflow.ControlApi -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./
# Its own keys (master key, CA) and the built-in collector's enrollment file live on volumes owned by the app user.
RUN mkdir -p /var/lib/lakeflow-api /var/lib/lakeflow-bootstrap && chown app:app /var/lib/lakeflow-api /var/lib/lakeflow-bootstrap
USER app
EXPOSE 8080 8443 8444
VOLUME ["/var/lib/lakeflow-api"]
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && grep -q "200 OK" <&3'
ENTRYPOINT ["dotnet", "Lakeflow.ControlApi.dll"]
