#!/bin/bash
set -e

SQLCMD="/opt/mssql-tools18/bin/sqlcmd -C"
PRIMARY="mssql-primary"
REPLICA="mssql-replica"
SA_PWD="Str0ng!Passw0rd"

wait_for_sql() {
    local server=$1
    echo "Waiting for $server to be ready..."
    until $SQLCMD -S "$server" -U sa -P "$SA_PWD" -Q "SELECT 1" &>/dev/null; do
        sleep 2
    done
    echo "$server is ready"
}

wait_for_agent() {
    local server=$1
    echo "Waiting for SQL Agent on $server..."
    until $SQLCMD -S "$server" -U sa -P "$SA_PWD" -Q "SELECT 1 FROM sys.dm_exec_sessions WHERE program_name LIKE 'SQLAgent%'" 2>/dev/null | grep -q '1'; do
        sleep 3
    done
    echo "SQL Agent is running on $server"
}

# ---- PRIMARY SETUP ----
wait_for_sql $PRIMARY
wait_for_agent $PRIMARY

echo "=== Creating schema on primary ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -i /scripts/init/01-schema.sql

echo "=== Seeding data on primary ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -i /scripts/init/02-seed.sql

echo "=== Verifying primary data ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -d JobManager -Q "SELECT COUNT(*) AS PrimaryJobCount FROM Jobs"

echo "=== Verifying partition distribution (JobExecutions) ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -i /scripts/init/03-verify-partitions.sql

# ---- REPLICA SETUP ----
wait_for_sql $REPLICA
wait_for_agent $REPLICA

echo "=== Creating database, schema and seed data on replica ==="
$SQLCMD -S $REPLICA -U sa -P "$SA_PWD" -Q "IF DB_ID('JobManager') IS NULL CREATE DATABASE JobManager;"
$SQLCMD -S $REPLICA -U sa -P "$SA_PWD" -i /scripts/init/01-schema.sql
$SQLCMD -S $REPLICA -U sa -P "$SA_PWD" -i /scripts/init/02-seed.sql

echo "=== Verifying replica schema (pre-replication) ==="
$SQLCMD -S $REPLICA -U sa -P "$SA_PWD" -d JobManager -Q "SELECT COUNT(*) AS ReplicaTableCount FROM sys.tables WHERE name IN ('Jobs','JobSchedules','JobExecutions')"
# NOTE: partition verification on the replica is intentionally deferred until
# AFTER the replication snapshot fires (see post-snapshot section below).
# The snapshot uses @pre_creation_cmd = N'drop', which drops and recreates
# JobExecutions; verifying now would check the pre-snapshot state only.

# ---- REPLICATION ----
echo "=== Setting up replication (distributor + publication + subscription) ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -i /scripts/replication/setup-replication.sql

echo "=== Replication agent diagnostics (after setup) ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -Q "
SELECT TOP 20
       job_name  = j.name,
       step_name = h.step_name,
       outcome   = CASE h.run_status WHEN 0 THEN 'FAILED' WHEN 1 THEN 'SUCCEEDED' WHEN 2 THEN 'RETRY' WHEN 3 THEN 'CANCELED' ELSE CAST(h.run_status AS VARCHAR) END,
       run_date  = h.run_date,
       run_time  = h.run_time,
       message   = h.message
FROM   msdb.dbo.sysjobhistory h
JOIN   msdb.dbo.sysjobs j ON j.job_id = h.job_id
WHERE  j.category_id IN (SELECT category_id FROM msdb.dbo.syscategories WHERE name LIKE 'REPL%')
  AND  h.run_status <> 1
ORDER  BY h.instance_id DESC;" 2>/dev/null || true

echo "=== Snapshot agent errors (distribution.dbo.MSsnapshot_history) ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -d distribution -Q "
SELECT TOP 10 runstatus = CASE runstatus WHEN 0 THEN 'IDLE' WHEN 1 THEN 'RUNNING' WHEN 2 THEN 'SUCCEEDED' WHEN 3 THEN 'FAILED' WHEN 5 THEN 'RETRY' ELSE CAST(runstatus AS VARCHAR) END, error_id, comments, start_time
FROM MSsnapshot_history ORDER BY start_time DESC;" 2>/dev/null || true

echo "=== Distribution agent errors (distribution.dbo.MSdistribution_history) ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -d distribution -Q "
SELECT TOP 10 runstatus = CASE runstatus WHEN 0 THEN 'IDLE' WHEN 1 THEN 'RUNNING' WHEN 2 THEN 'SUCCEEDED' WHEN 3 THEN 'FAILED' WHEN 5 THEN 'RETRY' ELSE CAST(runstatus AS VARCHAR) END, error_id, comments, start_time
FROM MSdistribution_history ORDER BY start_time DESC;" 2>/dev/null || true

# ---- VERIFY REPLICATION ----
# Insert a canary row on the primary AFTER replication is set up.
# Only replication (not the earlier seed) can make this row appear on the replica.
echo "=== Inserting canary row on primary to verify live replication ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -d JobManager -Q "
SET QUOTED_IDENTIFIER ON;
INSERT INTO Jobs (Name, Frequency, ExecutionTime, ApiEndpoint, CreatedAt, UpdatedAt)
VALUES (N'__replication_canary__', N'Once', '00:00:00', N'http://canary', GETUTCDATE(), GETUTCDATE());"

echo "=== Waiting for canary row to appear on replica ==="
for i in $(seq 1 90); do
    CANARY=$($SQLCMD -S $REPLICA -U sa -P "$SA_PWD" -d JobManager -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM Jobs WHERE Name = '__replication_canary__'" 2>/dev/null | head -1 | tr -d '[:space:]')
    if [ "$CANARY" = "1" ]; then
        echo "Replication CONFIRMED: canary row found on replica after $i attempt(s)."
        break
    fi
    echo "Waiting for replication... (attempt $i/90)"
    sleep 5
done

if [ "$CANARY" != "1" ]; then
    echo "WARNING: Canary row did NOT appear on replica after 90 attempts — replication may not be working."
fi

echo "=== Cleaning up canary row from primary ==="
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -d JobManager -Q "
DELETE FROM Jobs WHERE Name = N'__replication_canary__';"

# ---- POST-SNAPSHOT: ENSURE REPLICA PARTITIONING ----
# The snapshot (@pre_creation_cmd = N'drop') has now been confirmed applied.
# Run the idempotent safety net: if the snapshot recreated JobExecutions
# without the partition scheme (schema_option 0x0000000004000000 was not
# honoured by the SQL Server on Linux snapshot agent), this script rebuilds
# the clustered PK onto PS_JobExecutions_ByMonth without data loss.
echo "=== Ensuring JobExecutions is partitioned on replica (post-snapshot) ==="
$SQLCMD -S $REPLICA -U sa -P "$SA_PWD" -i /scripts/init/04-ensure-replica-partitioning.sql

echo "=== Post-snapshot: verifying partition function and scheme on replica ==="
$SQLCMD -S $REPLICA -U sa -P "$SA_PWD" -d JobManager -Q "
SELECT
    pf.name  AS partition_function,
    ps.name  AS partition_scheme,
    CASE pf.boundary_value_on_right WHEN 1 THEN 'RANGE RIGHT' ELSE 'RANGE LEFT' END AS range_type,
    COUNT(prv.boundary_id) AS boundary_count
FROM sys.partition_functions   pf
JOIN sys.partition_schemes     ps  ON ps.function_id    = pf.function_id
JOIN sys.partition_range_values prv ON prv.function_id  = pf.function_id
WHERE pf.name = 'PF_JobExecutions_ByMonth'
GROUP BY pf.name, ps.name, pf.boundary_value_on_right;"

echo "=== Post-snapshot: verifying partition distribution on replica ==="
$SQLCMD -S $REPLICA -U sa -P "$SA_PWD" -i /scripts/init/03-verify-partitions.sql

echo ""
echo "=== Final status ==="
echo "Primary (mssql-primary -> localhost:1435):"
$SQLCMD -S $PRIMARY -U sa -P "$SA_PWD" -d JobManager -Q "SELECT COUNT(*) AS JobCount FROM Jobs; SELECT COUNT(*) AS ScheduleCount FROM JobSchedules; SELECT COUNT(*) AS ExecutionCount FROM JobExecutions"
echo ""
echo "Replica (mssql-replica -> localhost:1434):"
$SQLCMD -S $REPLICA -U sa -P "$SA_PWD" -d JobManager -Q "SELECT COUNT(*) AS JobCount FROM Jobs; SELECT COUNT(*) AS ScheduleCount FROM JobSchedules; SELECT COUNT(*) AS ExecutionCount FROM JobExecutions" 2>/dev/null || echo "Tables not yet available on replica"

echo ""
echo "=== Setup complete ==="
sleep 5
