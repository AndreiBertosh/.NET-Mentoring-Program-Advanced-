-- 02-seed.sql
-- Seed data: 5 Jobs, 5 JobSchedules, 90 JobExecutions.
--
-- Partition key: StartedAt (DATETIME2) on JobExecutions.
-- Partition function: PF_JobExecutions_ByMonth (RANGE RIGHT, monthly).
--
-- Cold historical partitions (Jan–May 2026, partitions 2–6):
--   Static timestamps demonstrate that older data is isolated in its
--   own partition shard.  These partitions are candidates for archival
--   (partition switch-out) once the retention window expires.
--
-- Hot current partition (Jun 2026, partition 7):
--   Timestamps are derived from SYSUTCDATETIME() so the seed always
--   places the most-recent rows in the current month's partition.
--   One row uses SYSUTCDATETIME() directly (Result='Running') to show
--   a live write landing in the hot partition automatically.
--
-- Partition layout matches PF_JobExecutions_ByMonth in 01-schema.sql.

SET QUOTED_IDENTIFIER ON;
GO

USE JobManager;
GO

IF NOT EXISTS (SELECT 1 FROM Jobs)
BEGIN
    -- --------------------------------------------------------
    -- Jobs (5 records)
    -- --------------------------------------------------------
    DECLARE @Job1 UNIQUEIDENTIFIER = NEWID();   -- Daily Sales Report
    DECLARE @Job2 UNIQUEIDENTIFIER = NEWID();   -- Hourly Health Check
    DECLARE @Job3 UNIQUEIDENTIFIER = NEWID();   -- Weekly Data Sync
    DECLARE @Job4 UNIQUEIDENTIFIER = NEWID();   -- Monthly Cleanup
    DECLARE @Job5 UNIQUEIDENTIFIER = NEWID();   -- Daily Backup

    INSERT INTO Jobs (Id, Name, Frequency, ExecutionTime, ApiEndpoint) VALUES
        (@Job1, 'Daily Sales Report',   'Daily',   '06:00:00', 'https://api.example.com/reports/sales'),
        (@Job2, 'Hourly Health Check',  'Hourly',  '00:00:00', 'https://api.example.com/health'),
        (@Job3, 'Weekly Data Sync',     'Weekly',  '02:00:00', 'https://api.example.com/sync/data'),
        (@Job4, 'Monthly Cleanup',      'Monthly', '03:00:00', 'https://api.example.com/maintenance/cleanup'),
        (@Job5, 'Daily Backup',         'Daily',   '23:00:00', 'https://api.example.com/backup/run');

    -- --------------------------------------------------------
    -- JobSchedules (next upcoming run per job)
    -- --------------------------------------------------------
    INSERT INTO JobSchedules (JobId, NextRunTime, Status) VALUES
        (@Job1, DATEADD(HOUR,  6,  CAST(DATEADD(DAY, 1, CAST(GETUTCDATE() AS DATE)) AS DATETIME2)), 'Pending'),
        (@Job2, DATEADD(HOUR,  1,  SYSUTCDATETIME()),                                                'Pending'),
        (@Job3, DATEADD(DAY,   7,  SYSUTCDATETIME()),                                                'Pending'),
        (@Job4, DATEADD(DAY,  30,  SYSUTCDATETIME()),                                                'Pending'),
        (@Job5, DATEADD(HOUR, 23,  CAST(DATEADD(DAY, 1, CAST(GETUTCDATE() AS DATE)) AS DATETIME2)), 'Pending');

    -- --------------------------------------------------------
    -- JobExecutions — 90 rows across 6 monthly partitions
    --
    -- PARTITION 2 → Jan 2026  (10 rows)
    -- PARTITION 3 → Feb 2026  (12 rows)
    -- PARTITION 4 → Mar 2026  (15 rows)
    -- PARTITION 5 → Apr 2026  (18 rows)
    -- PARTITION 6 → May 2026  (20 rows)
    -- PARTITION 7 → Jun 2026  (15 rows — partial month through Jun 11)
    -- --------------------------------------------------------
    INSERT INTO JobExecutions (Id, JobId, StartedAt, FinishedAt, Result) VALUES

    -- ========================================================
    -- PARTITION 2: January 2026  (10 rows)
    -- ========================================================
    -- Monthly Cleanup (Job4) — 1 monthly run
    (NEWID(), @Job4, '2026-01-01 03:00:00', '2026-01-01 03:24:37', 'Success'),
    -- Hourly Health Check (Job2) — 4 sampled runs
    (NEWID(), @Job2, '2026-01-03 08:00:00', '2026-01-03 08:00:41', 'Success'),
    (NEWID(), @Job2, '2026-01-07 14:00:00', '2026-01-07 14:00:38', 'Failed'),
    (NEWID(), @Job2, '2026-01-15 10:00:00', '2026-01-15 10:00:33', 'Success'),
    (NEWID(), @Job2, '2026-01-22 16:00:00', '2026-01-22 16:00:44', 'Success'),
    -- Weekly Data Sync (Job3) — 1 weekly run
    (NEWID(), @Job3, '2026-01-07 02:00:00', '2026-01-07 02:11:22', 'Success'),
    -- Daily Sales Report (Job1) — 2 daily runs
    (NEWID(), @Job1, '2026-01-10 06:00:00', '2026-01-10 06:04:51', 'Success'),
    (NEWID(), @Job1, '2026-01-20 06:00:00', '2026-01-20 06:05:12', 'Failed'),
    -- Daily Backup (Job5) — 2 daily runs
    (NEWID(), @Job5, '2026-01-10 23:00:00', '2026-01-10 23:16:07', 'Success'),
    (NEWID(), @Job5, '2026-01-25 23:00:00', '2026-01-25 23:18:33', 'Success'),

    -- ========================================================
    -- PARTITION 3: February 2026  (12 rows)
    -- ========================================================
    -- Monthly Cleanup (Job4) — 1 monthly run
    (NEWID(), @Job4, '2026-02-01 03:00:00', '2026-02-01 03:27:09', 'Success'),
    -- Hourly Health Check (Job2) — 5 sampled runs
    (NEWID(), @Job2, '2026-02-02 06:00:00', '2026-02-02 06:00:39', 'Success'),
    (NEWID(), @Job2, '2026-02-08 12:00:00', '2026-02-08 12:00:42', 'Success'),
    (NEWID(), @Job2, '2026-02-14 08:00:00', '2026-02-14 08:00:37', 'Failed'),
    (NEWID(), @Job2, '2026-02-20 18:00:00', '2026-02-20 18:00:40', 'Success'),
    (NEWID(), @Job2, '2026-02-26 22:00:00', '2026-02-26 22:00:35', 'Success'),
    -- Weekly Data Sync (Job3) — 2 weekly runs
    (NEWID(), @Job3, '2026-02-04 02:00:00', '2026-02-04 02:10:45', 'Success'),
    (NEWID(), @Job3, '2026-02-18 02:00:00', '2026-02-18 02:13:11', 'Success'),
    -- Daily Sales Report (Job1) — 2 daily runs
    (NEWID(), @Job1, '2026-02-05 06:00:00', '2026-02-05 06:04:28', 'Success'),
    (NEWID(), @Job1, '2026-02-19 06:00:00', '2026-02-19 06:05:03', 'Success'),
    -- Daily Backup (Job5) — 2 daily runs
    (NEWID(), @Job5, '2026-02-05 23:00:00', '2026-02-05 23:17:22', 'Success'),
    (NEWID(), @Job5, '2026-02-22 23:00:00', '2026-02-22 23:19:48', 'Failed'),

    -- ========================================================
    -- PARTITION 4: March 2026  (15 rows)
    -- ========================================================
    -- Monthly Cleanup (Job4) — 1 monthly run
    (NEWID(), @Job4, '2026-03-01 03:00:00', '2026-03-01 03:22:54', 'Success'),
    -- Hourly Health Check (Job2) — 7 sampled runs
    (NEWID(), @Job2, '2026-03-01 09:00:00', '2026-03-01 09:00:41', 'Success'),
    (NEWID(), @Job2, '2026-03-05 13:00:00', '2026-03-05 13:00:38', 'Success'),
    (NEWID(), @Job2, '2026-03-10 17:00:00', '2026-03-10 17:00:44', 'Failed'),
    (NEWID(), @Job2, '2026-03-14 07:00:00', '2026-03-14 07:00:36', 'Success'),
    (NEWID(), @Job2, '2026-03-18 11:00:00', '2026-03-18 11:00:40', 'Success'),
    (NEWID(), @Job2, '2026-03-24 15:00:00', '2026-03-24 15:00:33', 'Success'),
    (NEWID(), @Job2, '2026-03-29 21:00:00', '2026-03-29 21:00:39', 'Failed'),
    -- Weekly Data Sync (Job3) — 3 weekly runs
    (NEWID(), @Job3, '2026-03-04 02:00:00', '2026-03-04 02:09:57', 'Success'),
    (NEWID(), @Job3, '2026-03-11 02:00:00', '2026-03-11 02:12:33', 'Failed'),
    (NEWID(), @Job3, '2026-03-25 02:00:00', '2026-03-25 02:11:08', 'Success'),
    -- Daily Sales Report (Job1) — 2 daily runs
    (NEWID(), @Job1, '2026-03-08 06:00:00', '2026-03-08 06:04:44', 'Success'),
    (NEWID(), @Job1, '2026-03-22 06:00:00', '2026-03-22 06:05:31', 'Success'),
    -- Daily Backup (Job5) — 2 daily runs
    (NEWID(), @Job5, '2026-03-08 23:00:00', '2026-03-08 23:15:52', 'Success'),
    (NEWID(), @Job5, '2026-03-28 23:00:00', '2026-03-28 23:17:14', 'Success'),

    -- ========================================================
    -- PARTITION 5: April 2026  (18 rows)
    -- ========================================================
    -- Monthly Cleanup (Job4) — 1 monthly run
    (NEWID(), @Job4, '2026-04-01 03:00:00', '2026-04-01 03:26:18', 'Success'),
    -- Hourly Health Check (Job2) — 9 sampled runs
    (NEWID(), @Job2, '2026-04-01 10:00:00', '2026-04-01 10:00:42', 'Success'),
    (NEWID(), @Job2, '2026-04-04 14:00:00', '2026-04-04 14:00:38', 'Success'),
    (NEWID(), @Job2, '2026-04-08 08:00:00', '2026-04-08 08:00:41', 'Failed'),
    (NEWID(), @Job2, '2026-04-11 18:00:00', '2026-04-11 18:00:35', 'Success'),
    (NEWID(), @Job2, '2026-04-15 12:00:00', '2026-04-15 12:00:39', 'Success'),
    (NEWID(), @Job2, '2026-04-18 06:00:00', '2026-04-18 06:00:43', 'Success'),
    (NEWID(), @Job2, '2026-04-22 20:00:00', '2026-04-22 20:00:37', 'Failed'),
    (NEWID(), @Job2, '2026-04-25 16:00:00', '2026-04-25 16:00:40', 'Success'),
    (NEWID(), @Job2, '2026-04-29 22:00:00', '2026-04-29 22:00:38', 'Success'),
    -- Weekly Data Sync (Job3) — 3 weekly runs
    (NEWID(), @Job3, '2026-04-01 02:00:00', '2026-04-01 02:10:22', 'Success'),
    (NEWID(), @Job3, '2026-04-15 02:00:00', '2026-04-15 02:12:47', 'Success'),
    (NEWID(), @Job3, '2026-04-29 02:00:00', '2026-04-29 02:11:33', 'Failed'),
    -- Daily Sales Report (Job1) — 3 daily runs
    (NEWID(), @Job1, '2026-04-05 06:00:00', '2026-04-05 06:04:37', 'Success'),
    (NEWID(), @Job1, '2026-04-15 06:00:00', '2026-04-15 06:05:08', 'Success'),
    (NEWID(), @Job1, '2026-04-28 06:00:00', '2026-04-28 06:04:52', 'Failed'),
    -- Daily Backup (Job5) — 2 daily runs
    (NEWID(), @Job5, '2026-04-10 23:00:00', '2026-04-10 23:16:43', 'Success'),
    (NEWID(), @Job5, '2026-04-24 23:00:00', '2026-04-24 23:18:09', 'Success'),

    -- ========================================================
    -- PARTITION 6: May 2026  (20 rows)
    -- ========================================================
    -- Monthly Cleanup (Job4) — 1 monthly run
    (NEWID(), @Job4, '2026-05-01 03:00:00', '2026-05-01 03:25:41', 'Success'),
    -- Hourly Health Check (Job2) — 10 sampled runs
    (NEWID(), @Job2, '2026-05-01 07:00:00', '2026-05-01 07:00:40', 'Success'),
    (NEWID(), @Job2, '2026-05-04 11:00:00', '2026-05-04 11:00:37', 'Success'),
    (NEWID(), @Job2, '2026-05-07 15:00:00', '2026-05-07 15:00:43', 'Failed'),
    (NEWID(), @Job2, '2026-05-10 09:00:00', '2026-05-10 09:00:39', 'Success'),
    (NEWID(), @Job2, '2026-05-13 13:00:00', '2026-05-13 13:00:41', 'Success'),
    (NEWID(), @Job2, '2026-05-16 17:00:00', '2026-05-16 17:00:38', 'Success'),
    (NEWID(), @Job2, '2026-05-19 21:00:00', '2026-05-19 21:00:36', 'Failed'),
    (NEWID(), @Job2, '2026-05-22 05:00:00', '2026-05-22 05:00:42', 'Success'),
    (NEWID(), @Job2, '2026-05-26 19:00:00', '2026-05-26 19:00:40', 'Success'),
    (NEWID(), @Job2, '2026-05-30 23:00:00', '2026-05-30 23:00:35', 'Success'),
    -- Weekly Data Sync (Job3) — 3 weekly runs
    (NEWID(), @Job3, '2026-05-06 02:00:00', '2026-05-06 02:09:48', 'Success'),
    (NEWID(), @Job3, '2026-05-13 02:00:00', '2026-05-13 02:11:55', 'Success'),
    (NEWID(), @Job3, '2026-05-27 02:00:00', '2026-05-27 02:10:23', 'Failed'),
    -- Daily Sales Report (Job1) — 3 daily runs
    (NEWID(), @Job1, '2026-05-07 06:00:00', '2026-05-07 06:04:29', 'Success'),
    (NEWID(), @Job1, '2026-05-16 06:00:00', '2026-05-16 06:05:17', 'Success'),
    (NEWID(), @Job1, '2026-05-28 06:00:00', '2026-05-28 06:04:41', 'Success'),
    -- Daily Backup (Job5) — 3 daily runs
    (NEWID(), @Job5, '2026-05-07 23:00:00', '2026-05-07 23:17:35', 'Success'),
    (NEWID(), @Job5, '2026-05-18 23:00:00', '2026-05-18 23:16:58', 'Failed'),
    (NEWID(), @Job5, '2026-05-29 23:00:00', '2026-05-29 23:18:44', 'Success');
    -- ^ Partitions 2-6 (Jan-May 2026) are now COLD historical partitions.
    --   Their pages will be evicted from the buffer pool over time and are
    --   candidates for archival / partition-switch-out once the retention
    --   threshold is reached (O(1) metadata operation, no data movement).

    -- ========================================================
    -- PARTITION 7: June 2026  — HOT / CURRENT PARTITION  (15 rows)
    --
    -- Partition key: StartedAt (DATETIME2)
    --
    -- Write pattern  — Rows use SYSUTCDATETIME()-relative offsets so that
    --   StartedAt always falls in the current calendar month.  SQL Server's
    --   partition function PF_JobExecutions_ByMonth routes each INSERT to
    --   partition 7 automatically; no explicit shard key is required in the
    --   application layer.  The final row (Result='Running') is timestamped
    --   to SYSUTCDATETIME() to represent a live in-flight write landing in
    --   the hot partition right now.
    --
    -- Read pattern   — Recent executions dominate query traffic.  Because
    --   partition 7 receives all new writes, its 8 KB pages stay in the SQL
    --   Server buffer pool and are served from memory, not disk.
    --
    -- Secondary index — WHERE JobId = @id ORDER BY StartedAt DESC uses
    --   IX_JobExecutions_JobId_StartedAt (partition-aligned composite index).
    --   Supplying a StartedAt range predicate in the query triggers partition
    --   elimination so only partition 7 pages are read (cold partitions are
    --   skipped entirely by the storage engine).
    -- ========================================================
    INSERT INTO JobExecutions (Id, JobId, StartedAt, FinishedAt, Result) VALUES
    -- Monthly Cleanup (Job4) — monthly run fires at the start of the month
    (NEWID(), @Job4,
        DATEADD(DAY, -DATEPART(DAY, SYSUTCDATETIME()) + 1, CAST(GETUTCDATE() AS DATETIME2)),  -- 2026-06-01 00:00 UTC
        DATEADD(MINUTE, 24, DATEADD(DAY, -DATEPART(DAY, SYSUTCDATETIME()) + 1, CAST(GETUTCDATE() AS DATETIME2))),
        'Success'),
    -- Hourly Health Check (Job2) — 8 sampled runs spread across the month so far
    (NEWID(), @Job2, DATEADD(HOUR, -265, SYSUTCDATETIME()), DATEADD(SECOND, 41, DATEADD(HOUR, -265, SYSUTCDATETIME())), 'Success'),  -- ~11 days ago
    (NEWID(), @Job2, DATEADD(HOUR, -240, SYSUTCDATETIME()), DATEADD(SECOND, 38, DATEADD(HOUR, -240, SYSUTCDATETIME())), 'Success'),  -- ~10 days ago
    (NEWID(), @Job2, DATEADD(HOUR, -216, SYSUTCDATETIME()), DATEADD(SECOND, 44, DATEADD(HOUR, -216, SYSUTCDATETIME())), 'Failed'),   -- ~9 days ago
    (NEWID(), @Job2, DATEADD(HOUR, -168, SYSUTCDATETIME()), DATEADD(SECOND, 39, DATEADD(HOUR, -168, SYSUTCDATETIME())), 'Success'),  -- ~7 days ago
    (NEWID(), @Job2, DATEADD(HOUR, -120, SYSUTCDATETIME()), DATEADD(SECOND, 37, DATEADD(HOUR, -120, SYSUTCDATETIME())), 'Success'),  -- ~5 days ago
    (NEWID(), @Job2, DATEADD(HOUR,  -72, SYSUTCDATETIME()), DATEADD(SECOND, 42, DATEADD(HOUR,  -72, SYSUTCDATETIME())), 'Success'),  -- ~3 days ago
    (NEWID(), @Job2, DATEADD(HOUR,  -48, SYSUTCDATETIME()), DATEADD(SECOND, 36, DATEADD(HOUR,  -48, SYSUTCDATETIME())), 'Success'),  -- ~2 days ago
    -- ↓ Live write: StartedAt = now → routed to partition 7 by PF_JobExecutions_ByMonth.
    --   This row demonstrates the "all new inserts land in the latest partition" write pattern.
    (NEWID(), @Job2, SYSUTCDATETIME(), NULL, 'Running'),
    -- Weekly Data Sync (Job3) — 2 weekly runs
    (NEWID(), @Job3, DATEADD(HOUR, -192, SYSUTCDATETIME()), DATEADD(MINUTE, 11, DATEADD(HOUR, -192, SYSUTCDATETIME())), 'Success'),  -- ~8 days ago
    (NEWID(), @Job3, DATEADD(HOUR,  -24, SYSUTCDATETIME()), DATEADD(MINUTE, 11, DATEADD(HOUR,  -24, SYSUTCDATETIME())), 'Success'),  -- yesterday
    -- Daily Sales Report (Job1) — 2 daily runs
    (NEWID(), @Job1, DATEADD(HOUR, -168, SYSUTCDATETIME()), DATEADD(MINUTE, 5, DATEADD(HOUR, -168, SYSUTCDATETIME())), 'Success'),   -- ~7 days ago
    (NEWID(), @Job1, DATEADD(HOUR,  -48, SYSUTCDATETIME()), DATEADD(MINUTE, 5, DATEADD(HOUR,  -48, SYSUTCDATETIME())), 'Success'),   -- ~2 days ago
    -- Daily Backup (Job5) — 2 daily runs
    (NEWID(), @Job5, DATEADD(HOUR, -168, SYSUTCDATETIME()), DATEADD(MINUTE, 17, DATEADD(HOUR, -168, SYSUTCDATETIME())), 'Success'),  -- ~7 days ago
    (NEWID(), @Job5, DATEADD(HOUR,  -48, SYSUTCDATETIME()), DATEADD(MINUTE, 17, DATEADD(HOUR,  -48, SYSUTCDATETIME())), 'Success');  -- ~2 days ago
    -- All 15 rows above have StartedAt in the current month → every INSERT
    -- lands in partition 7 without any application-level routing logic.

END
GO
