using FactoryFlow.Core.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FactoryFlow.Data
{
    /// <summary>Short-lived pooled connections; parameterized SQL; one transaction per production event.</summary>
    public sealed partial class SqlProductionRepository
    {
        private readonly string _connectionString;
        public int LineId { get; }
        public SqlProductionRepository(string connectionString, int lineId = 1)
        {
            _connectionString = connectionString;
            LineId = lineId;
        }

        public async Task InitializeAsync(CancellationToken token = default)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var stream = typeof(SqlProductionRepository).Assembly.GetManifestResourceStream("FactoryFlow.Data.Sql.001_ProductionHistory.sql")
                ?? throw new InvalidOperationException("Production schema migration resource missing.");
            using var reader = new StreamReader(stream);
            using var command = new SqlCommand(await reader.ReadToEndAsync().ConfigureAwait(false), connection) { CommandTimeout = 15 };
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await ApplyOperatorSchemaAsync(connection, token).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken token = default)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = new SqlCommand("SELECT RecipeId,RecipeName,TargetWeight FROM dbo.Recipes WHERE IsActive=1 ORDER BY RecipeId;", connection) { CommandTimeout = 5 };
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var recipes = new List<Recipe>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                recipes.Add(new Recipe { RecipeId = reader.GetInt32(0), RecipeName = reader.GetString(1), TargetWeight = reader.GetDouble(2) });
            return recipes;
        }

        public async Task BeginBatchAsync(Guid correlationId, int recipeId, CancellationToken token = default, double? expectedTarget = null)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = new SqlCommand(@"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource=@LockName, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000;
IF @lockResult < 0 THROW 51002, 'Another batch start is in progress.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.Batches WHERE CorrelationId=@CorrelationId)
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Batches WHERE LineId=@LineId AND CorrelationId IS NOT NULL AND Status IN ('Starting','Running'))
        THROW 51003, 'An unfinished recorded batch exists for this line. Review it before starting another batch.', 1;
    IF NOT EXISTS(SELECT 1 FROM dbo.Operators WHERE WindowsAccount=@ActorAccount AND @ActorAccount=ORIGINAL_LOGIN() AND IsEnabled=1) THROW 51101, 'Registered Windows operator required.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.Recipes WHERE RecipeId=@RecipeId AND IsActive=1 AND TargetWeight>=0.1 AND TargetWeight<=1000000 AND (@ExpectedTarget IS NULL OR TargetWeight=@ExpectedTarget))
        THROW 51004, 'Recipe is inactive, invalid, or changed; reload recipes.', 1;
    INSERT dbo.Batches(RecipeId,LineId,Status,StartedAt,CorrelationId,TargetWeight,OperatorAccount,RecipeNameSnapshot)
        SELECT RecipeId,@LineId,N'Starting',SYSUTCDATETIME(),@CorrelationId,TargetWeight,@ActorAccount,RecipeName FROM dbo.Recipes WHERE RecipeId=@RecipeId;
    INSERT dbo.MachineEvents(MachineId,EventType,EventValue,OccurredAt,EventKey,BatchCorrelationId,OperatorAccount,Source)
        VALUES(@LineId,N'BatchStartRequested',N'Start request prepared; awaiting actual PLC Running state.',SYSUTCDATETIME(),@CorrelationId,@CorrelationId,@ActorAccount,N'HMI');
END
COMMIT TRANSACTION;", connection) { CommandTimeout = 10 };
            command.Parameters.Add("@LockName", SqlDbType.NVarChar, 255).Value = "FactoryFlow.BatchStart.Line" + LineId;
            command.Parameters.Add("@CorrelationId", SqlDbType.UniqueIdentifier).Value = correlationId;
            command.Parameters.Add("@RecipeId", SqlDbType.Int).Value = recipeId;
            command.Parameters.Add("@ActorAccount", SqlDbType.NVarChar, 256).Value = CurrentWindowsAccount;
            command.Parameters.Add("@ExpectedTarget", SqlDbType.Float).Value = (object?)expectedTarget ?? DBNull.Value;
            command.Parameters.Add("@LineId", SqlDbType.Int).Value = LineId;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        public async Task ApplyAsync(ProductionActivity activity, CancellationToken token = default)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = new SqlCommand(@"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
-- EventKey makes retries safe if SQL committed just before the connection was lost.
IF NOT EXISTS (SELECT 1 FROM dbo.MachineEvents WITH (UPDLOCK,HOLDLOCK) WHERE EventKey=@EventKey)
BEGIN
    IF @BatchStatus IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.Batches WHERE CorrelationId=@CorrelationId)
        THROW 51005, 'Production event references an unknown batch.', 1;
    INSERT dbo.MachineEvents(MachineId,EventType,EventValue,OccurredAt,EventKey,BatchCorrelationId,OperatorAccount,Source)
        VALUES(@LineId,@EventType,@EventValue,@OccurredAt,@EventKey,@CorrelationId,@ActorAccount,@Source);
    DECLARE @AlarmCode nvarchar(50),@AlarmActive bit;
    SET @AlarmCode=CASE WHEN @EventType IN (N'EmergencyStopActivated',N'EmergencyStopCleared') THEN N'EStop' WHEN @EventType IN(N'GuardOpened',N'GuardClosed') THEN N'GuardOpen' WHEN @EventType IN(N'MachineFault',N'MachineStatus') THEN N'MachineFault' END;
    SET @AlarmActive=CASE WHEN @EventType IN(N'EmergencyStopActivated',N'GuardOpened',N'MachineFault') THEN 1 ELSE 0 END;
    IF @AlarmCode IS NOT NULL EXEC dbo.usp_FactoryFlowApplyAlarmSignal @LineId,@AlarmCode,@AlarmActive,@OccurredAt,@EventKey,@EventValue,@CorrelationId;
    IF @BatchStatus IS NOT NULL
        UPDATE dbo.Batches SET Status=@BatchStatus,
            StartedAt=CASE WHEN @BatchStatus=N'Running' THEN @OccurredAt ELSE StartedAt END,
            ActualWeight=CASE WHEN @EndsBatch=1 THEN @FinalWeight ELSE ActualWeight END,
            EndedAt=CASE WHEN @EndsBatch=1 THEN @OccurredAt ELSE EndedAt END,
            CompletedAt=CASE WHEN @BatchStatus=N'Completed' THEN @OccurredAt ELSE CompletedAt END
        WHERE CorrelationId=@CorrelationId AND Status IN (N'Starting',N'Running');
    IF @LineStatus IS NOT NULL UPDATE dbo.ProductionLines SET Status=@LineStatus WHERE LineId=@LineId;
END
COMMIT TRANSACTION;", connection) { CommandTimeout = 10 };
            command.Parameters.Add("@EventKey", SqlDbType.UniqueIdentifier).Value = activity.EventKey;
            command.Parameters.Add("@ActorAccount", SqlDbType.NVarChar, 256).Value = (object?)activity.OperatorAccount ?? DBNull.Value;
            command.Parameters.Add("@Source", SqlDbType.NVarChar, 50).Value = activity.Source ?? "HMI";
            command.Parameters.Add("@CorrelationId", SqlDbType.UniqueIdentifier).Value = (object?)activity.BatchCorrelationId ?? DBNull.Value;
            command.Parameters.Add("@LineId", SqlDbType.Int).Value = activity.LineId;
            command.Parameters.Add("@EventType", SqlDbType.NVarChar, 100).Value = activity.EventType;
            command.Parameters.Add("@EventValue", SqlDbType.NVarChar, 200).Value = (object?)activity.EventValue ?? DBNull.Value;
            command.Parameters.Add("@OccurredAt", SqlDbType.DateTime2).Value = activity.OccurredAtUtc;
            command.Parameters.Add("@BatchStatus", SqlDbType.NVarChar, 30).Value = (object?)activity.BatchStatus ?? DBNull.Value;
            command.Parameters.Add("@LineStatus", SqlDbType.NVarChar, 30).Value = (object?)activity.LineStatus ?? DBNull.Value;
            command.Parameters.Add("@FinalWeight", SqlDbType.Float).Value = (object?)activity.FinalWeight ?? DBNull.Value;
            command.Parameters.Add("@EndsBatch", SqlDbType.Bit).Value = activity.EndsBatch;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }
}



