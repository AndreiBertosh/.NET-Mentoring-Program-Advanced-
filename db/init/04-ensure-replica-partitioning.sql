-- 04-ensure-replica-partitioning.sql
-- Idempotent safety net: ensures JobExecutions on THIS database is partitioned
-- on PF_JobExecutions_ByMonth / PS_JobExecutions_ByMonth.
--
-- When to run: on the REPLICA (port 1434), AFTER the replication snapshot has
-- been applied.  The snapshot uses @pre_creation_cmd = N'drop', which drops and
-- recreates JobExecutions on the subscriber.  If the schema_option bit
-- 0x0000000004000000 did not cause the snapshot to emit the
-- ON PS_JobExecutions_ByMonth (StartedAt) clause, the recreated table is a
-- plain heap with no partitioning.  This script detects that condition and
-- rebuilds the clustered primary key onto the partition scheme — without data
-- loss and without touching the application.
--
-- Safe to run multiple times: the opening IF guard exits immediately when
-- JobExecutions is already partitioned.

USE JobManager;
GO

PRINT '=== 04-ensure-replica-partitioning: checking JobExecutions ===';
GO

-- ============================================================
-- Quick check: is the clustered index already on a partition scheme?
-- ============================================================
IF EXISTS (
    SELECT 1
    FROM   sys.indexes         i
    JOIN   sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
    WHERE  i.object_id = OBJECT_ID('dbo.JobExecutions')
      AND  i.type      = 1   -- clustered only
)
BEGIN
    PRINT 'JobExecutions is already partitioned on PS_JobExecutions_ByMonth — no action needed.';
END
ELSE
BEGIN
    PRINT 'JobExecutions clustered index is NOT on a partition scheme.';
    PRINT 'Rebuilding onto PS_JobExecutions_ByMonth (in-place, no data loss)...';

    -- ----------------------------------------------------------
    -- Step 1: Drop the partition-aligned secondary index
    --         (it must be recreated after the clustered index changes)
    -- ----------------------------------------------------------
    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE  object_id = OBJECT_ID('dbo.JobExecutions')
          AND  name      = 'IX_JobExecutions_JobId_StartedAt'
    )
    BEGIN
        DROP INDEX IX_JobExecutions_JobId_StartedAt ON dbo.JobExecutions;
        PRINT '  Dropped IX_JobExecutions_JobId_StartedAt.';
    END

    -- ----------------------------------------------------------
    -- Step 2: Locate and drop the existing clustered index / PK.
    --         The snapshot may have named the PK differently from
    --         the publisher (PK_JobExecutions) so we discover it
    --         dynamically.
    -- ----------------------------------------------------------
    DECLARE @clusterName NVARCHAR(256);
    DECLARE @isPk        BIT;
    DECLARE @dropSql     NVARCHAR(MAX);

    SELECT TOP 1
        @clusterName = name,
        @isPk        = is_primary_key
    FROM sys.indexes
    WHERE  object_id = OBJECT_ID('dbo.JobExecutions')
      AND  type      = 1;   -- clustered

    IF @clusterName IS NULL
    BEGIN
        PRINT '  No clustered index found — table is a heap. Will add PK directly.';
    END
    ELSE IF @isPk = 1
    BEGIN
        SET @dropSql = N'ALTER TABLE dbo.JobExecutions DROP CONSTRAINT ['
                     + @clusterName + N'];';
        EXEC (@dropSql);
        PRINT '  Dropped PK constraint: ' + @clusterName;
    END
    ELSE
    BEGIN
        SET @dropSql = N'DROP INDEX [' + @clusterName
                     + N'] ON dbo.JobExecutions;';
        EXEC (@dropSql);
        PRINT '  Dropped clustered index: ' + @clusterName;
    END

    -- ----------------------------------------------------------
    -- Step 3: Recreate the clustered PK on the partition scheme.
    --
    -- Column order: (StartedAt, Id)
    --   StartedAt leads so time-range scans stay in a single
    --   partition and SQL Server can do partition elimination.
    --   SQL Server requires the partition key to be part of every
    --   unique/PK index on a partitioned table.
    --
    -- If the snapshot created the table with PK (Id) only, that
    -- PK has been dropped above; we add the correct composite one.
    -- If PK (StartedAt, Id) existed but on [PRIMARY] (not the
    -- partition scheme), same applies.
    -- ----------------------------------------------------------
    ALTER TABLE dbo.JobExecutions
        ADD CONSTRAINT PK_JobExecutions
            PRIMARY KEY CLUSTERED (StartedAt, Id)
            ON PS_JobExecutions_ByMonth (StartedAt);
    PRINT '  Created PK_JobExecutions CLUSTERED (StartedAt, Id) ON PS_JobExecutions_ByMonth.';

    -- ----------------------------------------------------------
    -- Step 4: Recreate the partition-aligned secondary index.
    --         Aligning it to the partition scheme means partition
    --         elimination applies when a StartedAt range predicate
    --         is added to WHERE JobId = @id ORDER BY StartedAt DESC.
    -- ----------------------------------------------------------
    CREATE NONCLUSTERED INDEX IX_JobExecutions_JobId_StartedAt
        ON dbo.JobExecutions (JobId, StartedAt DESC)
        ON PS_JobExecutions_ByMonth (StartedAt);
    PRINT '  Created IX_JobExecutions_JobId_StartedAt (partition-aligned).';

    PRINT 'Partitioning applied successfully.';
END
GO

-- ============================================================
-- Verification — always printed so the shell log shows the
-- post-action state whether or not the fix was applied.
-- ============================================================
PRINT '-- Partitioned indexes on JobExecutions --';
SELECT
    i.name                                                  AS index_name,
    CASE i.is_primary_key WHEN 1 THEN 'YES' ELSE 'NO' END  AS is_pk,
    CASE i.type WHEN 1 THEN 'CLUSTERED' ELSE 'NONCLUSTERED' END AS index_type,
    ps.name                                                 AS partition_scheme,
    c.name                                                  AS partition_key_column
FROM sys.indexes             i
JOIN sys.partition_schemes   ps ON ps.data_space_id = i.data_space_id
JOIN sys.index_columns       ic ON ic.object_id     = i.object_id
                               AND ic.index_id       = i.index_id
                               AND ic.partition_ordinal > 0
JOIN sys.columns             c  ON c.object_id      = i.object_id
                               AND c.column_id       = ic.column_id
WHERE i.object_id = OBJECT_ID('dbo.JobExecutions')
ORDER BY i.index_id;
GO

PRINT '-- Row count per partition (JobExecutions on replica) --';
SELECT
    p.partition_number,
    CASE
        WHEN p.partition_number = 1 THEN 'Before 2026-01-01 (catch-all)'
        WHEN p.partition_number = 8 THEN '2026-07-01 and later (future buffer)'
        ELSE CONVERT(NVARCHAR(10), DATEADD(MONTH, p.partition_number - 2, '2026-01-01'), 120)
             + ' to '
             + CONVERT(NVARCHAR(10), DATEADD(MONTH, p.partition_number - 1, '2026-01-01'), 120)
    END                                                     AS partition_range,
    p.rows                                                  AS row_count
FROM sys.partitions p
JOIN sys.indexes    i ON i.object_id = p.object_id
                     AND i.index_id  = p.index_id
WHERE  p.object_id = OBJECT_ID('dbo.JobExecutions')
  AND  i.type      = 1   -- clustered index only
ORDER BY p.partition_number;
GO
