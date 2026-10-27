-- Additive migration: preserves existing tables and records. Run in FactoryFlowDB.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource=N'FactoryFlow.ProductionHistory.Schema', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
IF @lockResult < 0 THROW 51000, 'Cannot acquire FactoryFlow schema lock.', 1;
IF OBJECT_ID(N'dbo.Batches', N'U') IS NULL OR OBJECT_ID(N'dbo.MachineEvents', N'U') IS NULL
    OR OBJECT_ID(N'dbo.Recipes', N'U') IS NULL OR OBJECT_ID(N'dbo.ProductionLines', N'U') IS NULL
    THROW 51001, 'FactoryFlow requires the existing Batches, MachineEvents, Recipes and ProductionLines tables.', 1;
IF COL_LENGTH('dbo.Batches','CorrelationId') IS NULL ALTER TABLE dbo.Batches ADD CorrelationId uniqueidentifier NULL;
IF COL_LENGTH('dbo.Batches','ActualWeight') IS NULL ALTER TABLE dbo.Batches ADD ActualWeight float NULL;
IF COL_LENGTH('dbo.Batches','TargetWeight') IS NULL ALTER TABLE dbo.Batches ADD TargetWeight float NULL;
IF COL_LENGTH('dbo.Batches','EndedAt') IS NULL ALTER TABLE dbo.Batches ADD EndedAt datetime2 NULL;
IF COL_LENGTH('dbo.MachineEvents','EventKey') IS NULL ALTER TABLE dbo.MachineEvents ADD EventKey uniqueidentifier NULL;
IF COL_LENGTH('dbo.MachineEvents','BatchCorrelationId') IS NULL ALTER TABLE dbo.MachineEvents ADD BatchCorrelationId uniqueidentifier NULL;
-- Dynamic SQL compiles after the new columns exist, including on the first migration.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Batches') AND name='UX_Batches_CorrelationId')
    EXEC(N'CREATE UNIQUE INDEX UX_Batches_CorrelationId ON dbo.Batches(CorrelationId) WHERE CorrelationId IS NOT NULL');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.MachineEvents') AND name='UX_MachineEvents_EventKey')
    EXEC(N'CREATE UNIQUE INDEX UX_MachineEvents_EventKey ON dbo.MachineEvents(EventKey) WHERE EventKey IS NOT NULL');
COMMIT TRANSACTION;
