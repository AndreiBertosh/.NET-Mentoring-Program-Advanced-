-- 02-seed.sql
-- Minimal seed data for demo purposes (5 records per table)

SET QUOTED_IDENTIFIER ON;
GO

USE JobManager;
GO

IF NOT EXISTS (SELECT 1 FROM Jobs)
BEGIN
    DECLARE @Job1 UNIQUEIDENTIFIER = NEWID();
    DECLARE @Job2 UNIQUEIDENTIFIER = NEWID();
    DECLARE @Job3 UNIQUEIDENTIFIER = NEWID();
    DECLARE @Job4 UNIQUEIDENTIFIER = NEWID();
    DECLARE @Job5 UNIQUEIDENTIFIER = NEWID();

    INSERT INTO Jobs (Id, Name, Frequency, ExecutionTime, ApiEndpoint) VALUES
        (@Job1, 'Daily Sales Report',   'Daily',   '06:00:00', 'https://api.example.com/reports/sales'),
        (@Job2, 'Hourly Health Check',  'Hourly',  '00:00:00', 'https://api.example.com/health'),
        (@Job3, 'Weekly Data Sync',     'Weekly',  '02:00:00', 'https://api.example.com/sync/data'),
        (@Job4, 'Monthly Cleanup',      'Monthly', '03:00:00', 'https://api.example.com/maintenance/cleanup'),
        (@Job5, 'Daily Backup',         'Daily',   '23:00:00', 'https://api.example.com/backup/run');

    INSERT INTO JobSchedules (JobId, NextRunTime, Status) VALUES
        (@Job1, DATEADD(HOUR, 6,  CAST(DATEADD(DAY, 1, CAST(GETUTCDATE() AS DATE)) AS DATETIME2)), 'Pending'),
        (@Job2, DATEADD(HOUR, 1, SYSUTCDATETIME()), 'Pending'),
        (@Job3, DATEADD(DAY, 7, SYSUTCDATETIME()), 'Pending'),
        (@Job4, DATEADD(DAY, 30, SYSUTCDATETIME()), 'Pending'),
        (@Job5, DATEADD(HOUR, 23, CAST(DATEADD(DAY, 1, CAST(GETUTCDATE() AS DATE)) AS DATETIME2)), 'Pending');

    INSERT INTO JobExecutions (JobId, StartedAt, FinishedAt, Result) VALUES
        (@Job1, DATEADD(DAY, -1, SYSUTCDATETIME()), DATEADD(MINUTE, -1430, SYSUTCDATETIME()), 'Success'),
        (@Job2, DATEADD(HOUR, -1, SYSUTCDATETIME()), DATEADD(MINUTE, -59, SYSUTCDATETIME()), 'Success'),
        (@Job2, DATEADD(HOUR, -2, SYSUTCDATETIME()), DATEADD(MINUTE, -119, SYSUTCDATETIME()), 'Failed'),
        (@Job4, DATEADD(DAY, -30, SYSUTCDATETIME()), DATEADD(DAY, -30, DATEADD(MINUTE, 5, SYSUTCDATETIME())), 'Success'),
        (@Job5, DATEADD(DAY, -1, SYSUTCDATETIME()), DATEADD(MINUTE, -1420, SYSUTCDATETIME()), 'Success');
END
GO
