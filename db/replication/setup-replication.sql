-- setup-replication.sql
-- Configures transactional replication on the PRIMARY server.
-- The PRIMARY acts as both Publisher and Distributor.
-- A push subscription sends data to the REPLICA.

-- ============================================================
-- PART 1: Configure distribution (on primary)
-- ============================================================
USE master;
GO

EXEC sp_adddistributor
    @distributor = N'mssql-primary',
    @password = N'Str0ng!Passw0rd';
GO

EXEC sp_adddistributiondb
    @database = N'distribution',
    @security_mode = 0,
    @login = N'sa',
    @password = N'Str0ng!Passw0rd';
GO

-- /var/opt/mssql/data already exists and is writable by the mssql user.
-- xp_cmdshell is not available on SQL Server for Linux, so we use this
-- existing directory instead of creating a new ReplData subfolder.
EXEC sp_adddistpublisher
    @publisher = N'mssql-primary',
    @distribution_db = N'distribution',
    @security_mode = 0,
    @login = N'sa',
    @password = N'Str0ng!Passw0rd',
    @working_directory = N'/var/opt/mssql/data';
GO

-- ============================================================
-- PART 2: Configure publication (on primary)
-- ============================================================
USE JobManager;
GO

EXEC sp_replicationdboption
    @dbname = N'JobManager',
    @optname = N'publish',
    @value = N'true';
GO

EXEC sp_addpublication
    @publication = N'JobManagerPublication',
    @status = N'active',
    @allow_push = N'true',
    @allow_pull = N'false',
    @independent_agent = N'true';
GO

EXEC sp_addpublication_snapshot
    @publication = N'JobManagerPublication',
    @frequency_type = 1,
    @frequency_interval = 1,
    @publisher_security_mode = 0,
    @publisher_login = N'sa',
    @publisher_password = N'Str0ng!Passw0rd';
GO

-- Add all tables as articles
EXEC sp_addarticle
    @publication = N'JobManagerPublication',
    @article = N'Jobs',
    @source_object = N'Jobs',
    @source_owner = N'dbo',
    @type = N'logbased',
    @pre_creation_cmd = N'drop',
    @schema_option = 0x000000000803509F;
GO

EXEC sp_addarticle
    @publication = N'JobManagerPublication',
    @article = N'JobSchedules',
    @source_object = N'JobSchedules',
    @source_owner = N'dbo',
    @type = N'logbased',
    @pre_creation_cmd = N'drop',
    @schema_option = 0x000000000803509F;
GO

-- JobExecutions is partitioned. schema_option includes 0x0000000004000000
-- ("script filegroup/partition-scheme clause on ON keyword") so the snapshot
-- agent emits ON PS_JobExecutions_ByMonth (StartedAt) when it recreates the
-- table on the subscriber.  The partition function PF_JobExecutions_ByMonth
-- and scheme PS_JobExecutions_ByMonth are pre-created on the replica by
-- 01-schema.sql before replication setup runs, so they are available when
-- the snapshot fires.
-- 0x0000000004000000 | 0x000000000803509F = 0x000000000C03509F
EXEC sp_addarticle
    @publication = N'JobManagerPublication',
    @article = N'JobExecutions',
    @source_object = N'JobExecutions',
    @source_owner = N'dbo',
    @type = N'logbased',
    @pre_creation_cmd = N'drop',
    @schema_option = 0x000000000C03509F;
GO

-- ============================================================
-- PART 3: (subscription must be added first — see PART 4)
-- The snapshot is triggered after sp_changesubscription sets SA
-- credentials so the distribution agent can reach the replica.
-- ============================================================

-- ============================================================
-- PART 4: Configure push subscription (subscriber = replica)
-- ============================================================
USE JobManager;
GO

-- In SQL Server 2022, sp_addsubscription for push subscriptions implicitly
-- creates the distribution agent job (running as the SQL Server Agent service
-- account). sp_changesubscription is then used to switch it to SQL Server
-- Authentication (sa) so the agent can reach the replica over the Docker network.
EXEC sp_addsubscription
    @publication = N'JobManagerPublication',
    @subscriber = N'mssql-replica',
    @destination_db = N'JobManager',
    @subscription_type = N'push',
    @sync_type = N'automatic',
    @article = N'all',
    @update_mode = N'read only',
    @subscriber_type = 0;
GO

-- Switch to SQL Server Authentication for the subscriber connection.
EXEC sp_changesubscription
    @publication   = N'JobManagerPublication',
    @article       = N'all',
    @subscriber    = N'mssql-replica',
    @destination_db = N'JobManager',
    @property      = N'subscriber_security_mode',
    @value         = 0;
GO

EXEC sp_changesubscription
    @publication   = N'JobManagerPublication',
    @article       = N'all',
    @subscriber    = N'mssql-replica',
    @destination_db = N'JobManager',
    @property      = N'subscriber_login',
    @value         = N'sa';
GO

EXEC sp_changesubscription
    @publication   = N'JobManagerPublication',
    @article       = N'all',
    @subscriber    = N'mssql-replica',
    @destination_db = N'JobManager',
    @property      = N'subscriber_password',
    @value         = N'Str0ng!Passw0rd';
GO

-- Now that the subscription exists and SA credentials are set,
-- trigger the snapshot. The snapshot agent sees the subscription
-- that needs initialization and generates the snapshot files.
-- The distribution agent then picks them up and applies to the replica.
USE JobManager;
GO

EXEC sp_startpublication_snapshot
    @publication = N'JobManagerPublication';
GO

-- Allow time for the snapshot agent to finish generating files.
WAITFOR DELAY '00:01:00';
GO
