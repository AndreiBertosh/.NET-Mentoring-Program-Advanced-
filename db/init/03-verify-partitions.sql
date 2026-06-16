-- 03-verify-partitions.sql
-- Diagnostic queries to verify partition layout and data distribution
-- for the JobExecutions table.  Run after 02-seed.sql.

USE JobManager;
GO

-- ============================================================
-- 1. Partition function boundaries
-- ============================================================
PRINT '-- Partition function boundaries (PF_JobExecutions_ByMonth) --';
SELECT
    pf.name                                          AS partition_function,
    prv.boundary_id,
    CAST(prv.value AS DATETIME2)                     AS boundary_date,
    CASE pf.boundary_value_on_right
        WHEN 1 THEN 'RANGE RIGHT (boundary belongs to RIGHT partition)'
        ELSE        'RANGE LEFT  (boundary belongs to LEFT  partition)'
    END                                              AS range_type
FROM sys.partition_functions  pf
JOIN sys.partition_range_values prv ON prv.function_id = pf.function_id
WHERE pf.name = 'PF_JobExecutions_ByMonth'
ORDER BY prv.boundary_id;
GO

-- ============================================================
-- 2. Partition scheme → filegroup mapping
-- ============================================================
PRINT '-- Partition scheme to filegroup mapping (PS_JobExecutions_ByMonth) --';
SELECT
    ps.name        AS partition_scheme,
    dds.destination_id AS partition_number,
    fg.name        AS filegroup
FROM sys.partition_schemes  ps
JOIN sys.destination_data_spaces dds ON dds.partition_scheme_id = ps.data_space_id
JOIN sys.filegroups fg               ON fg.data_space_id        = dds.data_space_id
WHERE ps.name = 'PS_JobExecutions_ByMonth'
ORDER BY dds.destination_id;
GO

-- ============================================================
-- 3. Row count and size per partition
-- ============================================================
PRINT '-- Row count and size per partition of JobExecutions --';
SELECT
    p.partition_number,
    CASE
        WHEN p.partition_number = 1
            THEN 'Before 2026-01-01 (catch-all)'
        WHEN p.partition_number = 8
            THEN '2026-07-01 and later (future buffer)'
        ELSE
            CONVERT(NVARCHAR(10), DATEADD(MONTH, p.partition_number - 2, '2026-01-01'), 120)
            + ' to '
            + CONVERT(NVARCHAR(10), DATEADD(MONTH, p.partition_number - 1, '2026-01-01'), 120)
    END                                              AS partition_range,
    p.rows                                           AS row_count,
    STR(a.total_pages * 8 / 1024.0, 10, 2)          AS size_mb
FROM sys.partitions            p
JOIN sys.allocation_units      a  ON a.container_id = p.hobt_id
JOIN sys.indexes               i  ON i.object_id    = p.object_id
                                 AND i.index_id      = p.index_id
WHERE p.object_id = OBJECT_ID('dbo.JobExecutions')
  AND i.type      = 1  -- clustered index only
ORDER BY p.partition_number;
GO

-- ============================================================
-- 4. Row count per partition per job (distribution sanity check)
-- ============================================================
PRINT '-- Execution count per partition per job --';
SELECT
    $PARTITION.PF_JobExecutions_ByMonth(e.StartedAt) AS partition_number,
    CASE $PARTITION.PF_JobExecutions_ByMonth(e.StartedAt)
        WHEN 1 THEN 'Pre-2026'
        WHEN 2 THEN 'Jan 2026'
        WHEN 3 THEN 'Feb 2026'
        WHEN 4 THEN 'Mar 2026'
        WHEN 5 THEN 'Apr 2026'
        WHEN 6 THEN 'May 2026'
        WHEN 7 THEN 'Jun 2026'
        ELSE       'Future'
    END                                              AS month_label,
    j.Name                                           AS job_name,
    COUNT(*)                                         AS executions,
    SUM(CASE WHEN e.Result = 'Success' THEN 1 ELSE 0 END) AS succeeded,
    SUM(CASE WHEN e.Result = 'Failed'  THEN 1 ELSE 0 END) AS failed,
    SUM(CASE WHEN e.Result = 'Running' THEN 1 ELSE 0 END) AS running
FROM dbo.JobExecutions e
JOIN dbo.Jobs          j ON j.Id = e.JobId
GROUP BY
    $PARTITION.PF_JobExecutions_ByMonth(e.StartedAt),
    j.Name
ORDER BY
    partition_number,
    job_name;
GO

-- ============================================================
-- 5. Grand totals
-- ============================================================
PRINT '-- Grand totals --';
SELECT
    COUNT(*)                                           AS total_executions,
    COUNT(DISTINCT $PARTITION.PF_JobExecutions_ByMonth(StartedAt)) AS partitions_with_data,
    MIN(StartedAt)                                     AS earliest_execution,
    MAX(StartedAt)                                     AS latest_execution
FROM dbo.JobExecutions;
GO
