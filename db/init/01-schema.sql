-- 01-schema.sql
-- Database and table schema for Job Manager / Orchestrator
-- JobExecutions uses RANGE RIGHT monthly partitioning on StartedAt.
-- See: src/ReplicationDemo.DAL/PartitioningSetup.md for full rationale.

SET QUOTED_IDENTIFIER ON;
GO

IF DB_ID('JobManager') IS NULL CREATE DATABASE JobManager;
GO

USE JobManager;
GO

-- ============================================================
-- Jobs  (reference / configuration data — unbounded is small)
-- ============================================================
IF OBJECT_ID('dbo.Jobs', 'U') IS NULL
BEGIN
    CREATE TABLE Jobs (
        Id            UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        Name          NVARCHAR(256)    NOT NULL,
        Frequency     NVARCHAR(50)     NOT NULL,
        ExecutionTime TIME             NOT NULL,
        ApiEndpoint   NVARCHAR(2048)   NOT NULL,
        CreatedAt     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt     DATETIME2        NULL,
        CONSTRAINT PK_Jobs PRIMARY KEY CLUSTERED (Id)
    );
END
GO

-- ============================================================
-- JobSchedules  (bounded rolling state — one row per active job)
-- ============================================================
IF OBJECT_ID('dbo.JobSchedules', 'U') IS NULL
BEGIN
    CREATE TABLE JobSchedules (
        Id          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        JobId       UNIQUEIDENTIFIER NOT NULL,
        NextRunTime DATETIME2        NOT NULL,
        Status      NVARCHAR(50)     NOT NULL DEFAULT 'Pending',
        CreatedAt   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_JobSchedules PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_JobSchedules_Jobs
            FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
    );
END
GO

-- ============================================================
-- Partition function — RANGE RIGHT, one boundary per calendar month.
-- Partition layout (RANGE RIGHT means the boundary value belongs
-- to the RIGHT / higher partition):
--   Partition 1 : StartedAt <  '2026-01-01'  (pre-history / catch-all)
--   Partition 2 : '2026-01-01' <= StartedAt < '2026-02-01'  (January)
--   Partition 3 : '2026-02-01' <= StartedAt < '2026-03-01'  (February)
--   Partition 4 : '2026-03-01' <= StartedAt < '2026-04-01'  (March)
--   Partition 5 : '2026-04-01' <= StartedAt < '2026-05-01'  (April)
--   Partition 6 : '2026-05-01' <= StartedAt < '2026-06-01'  (May)
--   Partition 7 : '2026-06-01' <= StartedAt < '2026-07-01'  (June — current)
--   Partition 8 : StartedAt >= '2026-07-01'                 (future buffer)
--
-- Add next month before it arrives:
--   ALTER PARTITION FUNCTION PF_JobExecutions_ByMonth() SPLIT RANGE ('YYYY-MM-01');
--   ALTER PARTITION SCHEME   PS_JobExecutions_ByMonth   NEXT USED [PRIMARY];
-- ============================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.partition_functions WHERE name = 'PF_JobExecutions_ByMonth'
)
EXEC (N'
CREATE PARTITION FUNCTION PF_JobExecutions_ByMonth (DATETIME2)
AS RANGE RIGHT FOR VALUES (
    ''2026-01-01'',
    ''2026-02-01'',
    ''2026-03-01'',
    ''2026-04-01'',
    ''2026-05-01'',
    ''2026-06-01'',
    ''2026-07-01''
)');
GO

-- ============================================================
-- Partition scheme — all partitions on [PRIMARY].
-- In production, map older partitions to a secondary filegroup
-- on cheaper storage (NL-SAS / object storage) for tiered archival.
-- ============================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.partition_schemes WHERE name = 'PS_JobExecutions_ByMonth'
)
EXEC (N'
CREATE PARTITION SCHEME PS_JobExecutions_ByMonth
AS PARTITION PF_JobExecutions_ByMonth
ALL TO ([PRIMARY])');
GO

-- ============================================================
-- JobExecutions — partitioned by StartedAt (monthly range).
--
-- Clustered PK: (StartedAt, Id)
--   • SQL Server requires the partition key to be part of every
--     unique / primary key index on a partitioned table.
--   • StartedAt leads so time-range scans stay within a single
--     partition (sequential I/O, partition elimination).
--
-- UX_JobExecutions_Id (UNIQUE NONCLUSTERED, non-aligned):
--   • Allows EF Core / ad-hoc queries to look up a single row
--     by Guid Id without supplying StartedAt.
--
-- IX_JobExecutions_JobId_StartedAt (NONCLUSTERED, partition-aligned):
--   • Supports: WHERE JobId = @id ORDER BY StartedAt DESC
--     (GetExecutionsByJobIdAsync). Partition elimination applies
--     when a StartedAt range predicate is added.
-- ============================================================
IF OBJECT_ID('dbo.JobExecutions', 'U') IS NULL
BEGIN
    CREATE TABLE JobExecutions (
        Id         UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        JobId      UNIQUEIDENTIFIER NOT NULL,
        StartedAt  DATETIME2        NOT NULL,
        FinishedAt DATETIME2        NULL,
        Result     NVARCHAR(50)     NOT NULL DEFAULT 'Running',
        CreatedAt  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_JobExecutions PRIMARY KEY CLUSTERED (StartedAt, Id)
            ON PS_JobExecutions_ByMonth (StartedAt),
        CONSTRAINT FK_JobExecutions_Jobs
            FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
    );
END
GO

-- Unique non-clustered index on Id (non-aligned — spans all partitions).
-- Required so EF Core entity-by-Guid lookups work without knowing StartedAt.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE  object_id = OBJECT_ID('dbo.JobExecutions') AND name = 'UX_JobExecutions_Id'
)
    CREATE UNIQUE NONCLUSTERED INDEX UX_JobExecutions_Id
        ON dbo.JobExecutions (Id);
GO

-- Per-job lookup index aligned to the partition scheme.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE  object_id = OBJECT_ID('dbo.JobExecutions') AND name = 'IX_JobExecutions_JobId_StartedAt'
)
    CREATE NONCLUSTERED INDEX IX_JobExecutions_JobId_StartedAt
        ON dbo.JobExecutions (JobId, StartedAt DESC)
        ON PS_JobExecutions_ByMonth (StartedAt);
GO

-- Supporting indexes on non-partitioned tables
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSchedules_JobId')
    CREATE NONCLUSTERED INDEX IX_JobSchedules_JobId
        ON JobSchedules (JobId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSchedules_NextRunTime')
    CREATE NONCLUSTERED INDEX IX_JobSchedules_NextRunTime
        ON JobSchedules (NextRunTime) WHERE Status = 'Pending';
GO
