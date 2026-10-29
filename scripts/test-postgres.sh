#!/usr/bin/env bash
# Starts PostgreSQL 16 in Docker for the control API tests and prints the variable to export.
# The password is generated per container and only lives in this shell's output.
set -euo pipefail
name=lakeflow-test-postgres
port=${POSTGRES_TEST_PORT:-55432}
if ! docker ps --format '{{.Names}}' | grep -qx "$name"; then
  docker rm -f "$name" >/dev/null 2>&1 || true
  password="$(openssl rand -hex 12)"
  docker run -d --name "$name" -e POSTGRES_PASSWORD="$password" -p 127.0.0.1:"$port":5432 \
    --label lakeflow.pw="$password" postgres:16-alpine >/dev/null
fi
password=$(docker inspect -f '{{index .Config.Labels "lakeflow.pw"}}' "$name")
for _ in $(seq 1 30); do
  if docker exec "$name" pg_isready -U postgres >/dev/null 2>&1; then
    echo "export POSTGRES_TEST_CONNECTION='Host=127.0.0.1;Port=$port;Username=postgres;Password=$password'"
    exit 0
  fi
  sleep 1
done
echo "PostgreSQL did not start; see: docker logs $name" >&2
exit 1
