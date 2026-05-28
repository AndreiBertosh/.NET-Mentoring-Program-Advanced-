#!/bin/bash
set -e

SQLCMD="/opt/mssql-tools18/bin/sqlcmd -C"
SA_PWD="Str0ng!Passw0rd"

wait_for_sql() {
  local server=$1
  echo "Waiting for $server..."
  until $SQLCMD -S "$server" -U sa -P "$SA_PWD" -Q "SELECT 1" &>/dev/null; do
    sleep 2
  done
  echo "$server is ready"
}

init_db() {
  local server=$1
  echo "=== Initializing $server ==="
  $SQLCMD -S "$server" -U sa -P "$SA_PWD" -i /scripts/00-create-db.sql
  $SQLCMD -S "$server" -U sa -P "$SA_PWD" -i /scripts/01-schema.sql
  $SQLCMD -S "$server" -U sa -P "$SA_PWD" -i /scripts/02-seed.sql
}

wait_for_sql mssql-db1
wait_for_sql mssql-db2

init_db mssql-db1
init_db mssql-db2

echo "✅ Both databases initialized identically"
