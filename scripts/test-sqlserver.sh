#!/usr/bin/env bash
# Starts SQL Server 2022 in Docker for the connector integration tests and prints the variable to export.
# The SA password is generated per container and only lives in this shell's output.
set -euo pipefail
name=lakeflow-test-mssql
port=${MSSQL_TEST_PORT:-14333}
if ! docker ps --format '{{.Names}}' | grep -qx "$name"; then
  docker rm -f "$name" >/dev/null 2>&1 || true
  password="Lf1!$(openssl rand -hex 12)"
  docker run -d --name "$name" -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$password" -e MSSQL_PID=Developer \
    -p 127.0.0.1:"$port":1433 --label lakeflow.sa="$password" mcr.microsoft.com/mssql/server:2022-latest >/dev/null
fi
password=$(docker inspect -f '{{index .Config.Labels "lakeflow.sa"}}' "$name")
for _ in $(seq 1 60); do
  if docker exec "$name" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$password" -Q "SELECT 1" >/dev/null 2>&1; then
    echo "export MSSQL_TEST_CONNECTION='Server=tcp:127.0.0.1,$port;User ID=sa;Password=$password;TrustServerCertificate=true;Encrypt=true'"
    exit 0
  fi
  sleep 2
done
echo "SQL Server did not start; see: docker logs $name" >&2
exit 1
