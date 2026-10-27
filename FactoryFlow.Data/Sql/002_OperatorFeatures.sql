SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.Recipes','IsActive') IS NULL ALTER TABLE dbo.Recipes ADD IsActive bit NOT NULL CONSTRAINT DF_Recipes_IsActive DEFAULT 1;
IF COL_LENGTH('dbo.Batches','OperatorAccount') IS NULL ALTER TABLE dbo.Batches ADD OperatorAccount nvarchar(256) NULL;
IF COL_LENGTH('dbo.Batches','RecipeNameSnapshot') IS NULL ALTER TABLE dbo.Batches ADD RecipeNameSnapshot nvarchar(100) NULL;
IF COL_LENGTH('dbo.MachineEvents','OperatorAccount') IS NULL ALTER TABLE dbo.MachineEvents ADD OperatorAccount nvarchar(256) NULL;
IF COL_LENGTH('dbo.MachineEvents','Source') IS NULL ALTER TABLE dbo.MachineEvents ADD Source nvarchar(50) NOT NULL CONSTRAINT DF_MachineEvents_Source DEFAULT N'Legacy';
IF OBJECT_ID('dbo.Operators','U') IS NULL
CREATE TABLE dbo.Operators(
    OperatorId int IDENTITY PRIMARY KEY, WindowsAccount nvarchar(256) NOT NULL UNIQUE,
    DisplayName nvarchar(100) NOT NULL, Role nvarchar(20) NOT NULL CHECK(Role IN ('Operator','Supervisor')),
    IsEnabled bit NOT NULL DEFAULT 1, CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
IF OBJECT_ID('dbo.Alarms','U') IS NULL
CREATE TABLE dbo.Alarms(
    AlarmId bigint IDENTITY PRIMARY KEY, EventKey uniqueidentifier NOT NULL UNIQUE,
    LineId int NOT NULL, AlarmCode nvarchar(50) NOT NULL, Message nvarchar(200) NOT NULL,
    RaisedAt datetime2 NOT NULL, ClearedAt datetime2 NULL, AcknowledgedAt datetime2 NULL,
    AcknowledgedBy nvarchar(256) NULL, BatchCorrelationId uniqueidentifier NULL);
IF OBJECT_ID('dbo.AlarmSignalState','U') IS NULL
CREATE TABLE dbo.AlarmSignalState(LineId int NOT NULL, AlarmCode nvarchar(50) NOT NULL,
    IsActive bit NOT NULL, ObservedAt datetime2 NOT NULL, PRIMARY KEY(LineId,AlarmCode));
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Batches') AND name='IX_Batches_LineStarted')
    CREATE INDEX IX_Batches_LineStarted ON dbo.Batches(LineId,StartedAt DESC,BatchId DESC) INCLUDE(Status,RecipeId,ActualWeight,TargetWeight,EndedAt,CompletedAt);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.MachineEvents') AND name='IX_MachineEvents_LineTime')
    CREATE INDEX IX_MachineEvents_LineTime ON dbo.MachineEvents(MachineId,OccurredAt DESC) INCLUDE(EventType,BatchCorrelationId);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Alarms') AND name='UX_Alarms_ActiveSignal')
    CREATE UNIQUE INDEX UX_Alarms_ActiveSignal ON dbo.Alarms(LineId,AlarmCode) WHERE ClearedAt IS NULL;
COMMIT;
GO
CREATE OR ALTER VIEW dbo.vwFactoryFlowBatchHistory AS
SELECT b.BatchId,b.LineId,l.LineName,b.RecipeId,COALESCE(b.RecipeNameSnapshot,r.RecipeName) AS RecipeName,
    b.Status,b.TargetWeight,b.ActualWeight,b.StartedAt,b.EndedAt,b.CompletedAt,b.OperatorAccount,b.CorrelationId
FROM dbo.Batches b JOIN dbo.Recipes r ON r.RecipeId=b.RecipeId JOIN dbo.ProductionLines l ON l.LineId=b.LineId;
GO
CREATE OR ALTER VIEW dbo.vwFactoryFlowAlarmHistory AS
SELECT AlarmId,LineId,AlarmCode,Message,RaisedAt,ClearedAt,AcknowledgedAt,AcknowledgedBy,
    CAST(CASE WHEN ClearedAt IS NULL THEN 1 ELSE 0 END AS bit) AS IsActive,BatchCorrelationId FROM dbo.Alarms;
GO
CREATE OR ALTER PROCEDURE dbo.usp_FactoryFlowBatchHistory
    @LineId int=1,@FromUtc datetime2,@ToUtc datetime2,@Status nvarchar(30)=NULL,@Limit int=1000
AS
BEGIN
    SET NOCOUNT ON;
    IF @Limit<1 OR @Limit>5000 OR @ToUtc<=@FromUtc THROW 51100,'Invalid history filter.',1;
    SELECT TOP(@Limit) * FROM dbo.vwFactoryFlowBatchHistory
    WHERE LineId=@LineId AND StartedAt>=@FromUtc AND StartedAt<@ToUtc AND (@Status IS NULL OR Status=@Status)
    ORDER BY StartedAt DESC,BatchId DESC OPTION(RECOMPILE);
END;
GO
CREATE OR ALTER PROCEDURE dbo.usp_FactoryFlowAlarmHistory @LineId int=1,@IncludeClosed bit=0 AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP(1000) * FROM dbo.vwFactoryFlowAlarmHistory
    WHERE LineId=@LineId AND (@IncludeClosed=1 OR IsActive=1 OR AcknowledgedAt IS NULL)
    ORDER BY RaisedAt DESC,AlarmId DESC;
END;
GO
CREATE OR ALTER PROCEDURE dbo.usp_FactoryFlowApplyAlarmSignal
    @LineId int,@Code nvarchar(50),@Active bit,@OccurredAt datetime2,@EventKey uniqueidentifier,
    @Message nvarchar(200),@CorrelationId uniqueidentifier=NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @WasActive bit=NULL,@ObservedAt datetime2;
    SELECT @WasActive=IsActive,@ObservedAt=ObservedAt FROM dbo.AlarmSignalState WITH(UPDLOCK,HOLDLOCK)
        WHERE LineId=@LineId AND AlarmCode=@Code;
    IF @ObservedAt IS NOT NULL AND @OccurredAt<=@ObservedAt RETURN;
    IF @WasActive IS NULL INSERT dbo.AlarmSignalState VALUES(@LineId,@Code,@Active,@OccurredAt);
    ELSE UPDATE dbo.AlarmSignalState SET IsActive=@Active,ObservedAt=@OccurredAt WHERE LineId=@LineId AND AlarmCode=@Code;
    IF @Active=1 AND ISNULL(@WasActive,0)=0
        INSERT dbo.Alarms(EventKey,LineId,AlarmCode,Message,RaisedAt,BatchCorrelationId)
        VALUES(@EventKey,@LineId,@Code,@Message,@OccurredAt,@CorrelationId);
    IF @Active=0 UPDATE dbo.Alarms SET ClearedAt=@OccurredAt WHERE LineId=@LineId AND AlarmCode=@Code AND ClearedAt IS NULL;
END;
GO
CREATE OR ALTER PROCEDURE dbo.usp_FactoryFlowAcknowledgeAlarm @AlarmId bigint,@ActorAccount nvarchar(256) AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRANSACTION;
    IF @ActorAccount<>ORIGINAL_LOGIN() OR NOT EXISTS(SELECT 1 FROM dbo.Operators WHERE WindowsAccount=@ActorAccount AND IsEnabled=1)
        THROW 51101,'A registered Windows operator is required.',1;
    DECLARE @LineId int,@CorrelationId uniqueidentifier;
    SELECT @LineId=LineId,@CorrelationId=BatchCorrelationId FROM dbo.Alarms WITH(UPDLOCK) WHERE AlarmId=@AlarmId;
    IF @LineId IS NULL THROW 51102,'Alarm not found.',1;
    UPDATE dbo.Alarms SET AcknowledgedAt=SYSUTCDATETIME(),AcknowledgedBy=@ActorAccount WHERE AlarmId=@AlarmId AND AcknowledgedAt IS NULL;
    IF @@ROWCOUNT=1 INSERT dbo.MachineEvents(MachineId,EventType,EventValue,OccurredAt,EventKey,BatchCorrelationId,OperatorAccount,Source)
        VALUES(@LineId,N'AlarmAcknowledged',CONVERT(nvarchar(40),@AlarmId),SYSUTCDATETIME(),NEWID(),@CorrelationId,@ActorAccount,N'HMI');
    COMMIT;
END;
GO
IF TYPE_ID('dbo.FactoryFlowTelemetryRows') IS NULL
EXEC(N'CREATE TYPE dbo.FactoryFlowTelemetryRows AS TABLE(EventKey uniqueidentifier NOT NULL,LineId int NOT NULL,
    EventType nvarchar(100) NOT NULL,EventValue nvarchar(200) NULL,OccurredAt datetime2 NOT NULL,Source nvarchar(50) NOT NULL)');
GO
CREATE OR ALTER PROCEDURE dbo.usp_FactoryFlowAppendTelemetry @Rows dbo.FactoryFlowTelemetryRows READONLY AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRANSACTION;
    INSERT dbo.MachineEvents(MachineId,EventType,EventValue,OccurredAt,EventKey,Source)
    SELECT r.LineId,r.EventType,r.EventValue,r.OccurredAt,r.EventKey,r.Source FROM @Rows r
    WHERE NOT EXISTS(SELECT 1 FROM dbo.MachineEvents e WITH(UPDLOCK,HOLDLOCK) WHERE e.EventKey=r.EventKey);
    DECLARE @Line int,@Kind nvarchar(100),@Value nvarchar(200),@Time datetime2,@Key uniqueidentifier,@Code nvarchar(50),@Active bit;
    DECLARE signals CURSOR LOCAL FAST_FORWARD FOR SELECT LineId,EventType,EventValue,OccurredAt,EventKey FROM @Rows
        WHERE EventType IN(N'Telemetry.EStopActive',N'Telemetry.GuardClosed',N'Telemetry.Status') ORDER BY OccurredAt;
    OPEN signals; FETCH NEXT FROM signals INTO @Line,@Kind,@Value,@Time,@Key;
    WHILE @@FETCH_STATUS=0
    BEGIN
        SET @Code=CASE @Kind WHEN N'Telemetry.EStopActive' THEN N'EStop' WHEN N'Telemetry.GuardClosed' THEN N'GuardOpen' ELSE N'MachineFault' END;
        SET @Active=CASE WHEN (@Kind=N'Telemetry.EStopActive' AND @Value=N'True') OR (@Kind=N'Telemetry.GuardClosed' AND @Value=N'False') OR (@Kind=N'Telemetry.Status' AND @Value=N'3') THEN 1 ELSE 0 END;
        EXEC dbo.usp_FactoryFlowApplyAlarmSignal @Line,@Code,@Active,@Time,@Key,@Code,NULL;
        FETCH NEXT FROM signals INTO @Line,@Kind,@Value,@Time,@Key;
    END;
    CLOSE signals; DEALLOCATE signals; COMMIT;
END;
