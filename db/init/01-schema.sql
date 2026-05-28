-- 01-schema.sql
-- Database and table schema for Job Manager / Orchestrator

SET QUOTED_IDENTIFIER ON;
GO

IF DB_ID('JobManager') IS NULL CREATE DATABASE JobManager;
GO

USE JobManager;
GO

IF OBJECT_ID('dbo.Jobs', 'U') IS NULL
BEGIN
    CREATE TABLE Jobs (
        Id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        Name NVARCHAR(256) NOT NULL,
        Frequency NVARCHAR(50) NOT NULL,
        ExecutionTime TIME NOT NULL,
        ApiEndpoint NVARCHAR(2048) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT PK_Jobs PRIMARY KEY CLUSTERED (Id)
    );
END
GO

IF OBJECT_ID('dbo.JobSchedules', 'U') IS NULL
BEGIN
    CREATE TABLE JobSchedules (
        Id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        JobId UNIQUEIDENTIFIER NOT NULL,
        NextRunTime DATETIME2 NOT NULL,
        Status NVARCHAR(50) NOT NULL DEFAULT 'Pending',
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_JobSchedules PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_JobSchedules_Jobs FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID('dbo.JobExecutions', 'U') IS NULL
BEGIN
    CREATE TABLE JobExecutions (
        Id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        JobId UNIQUEIDENTIFIER NOT NULL,
        StartedAt DATETIME2 NOT NULL,
        FinishedAt DATETIME2 NULL,
        Result NVARCHAR(50) NOT NULL DEFAULT 'Running',
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_JobExecutions PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_JobExecutions_Jobs FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSchedules_JobId')
    CREATE NONCLUSTERED INDEX IX_JobSchedules_JobId ON JobSchedules(JobId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobExecutions_JobId')
    CREATE NONCLUSTERED INDEX IX_JobExecutions_JobId ON JobExecutions(JobId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSchedules_NextRunTime')
    CREATE NONCLUSTERED INDEX IX_JobSchedules_NextRunTime ON JobSchedules(NextRunTime) WHERE Status = 'Pending';
GO
