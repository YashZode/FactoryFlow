using FactoryFlow.Core.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FactoryFlow.Data
{
    public sealed partial class SqlProductionRepository
    {
        public static string CurrentWindowsAccount => WindowsIdentity.GetCurrent().Name;

        private static async Task ApplyOperatorSchemaAsync(SqlConnection connection, CancellationToken token)
        {
            using var stream = typeof(SqlProductionRepository).Assembly.GetManifestResourceStream("FactoryFlow.Data.Sql.002_OperatorFeatures.sql")!;
            using var reader = new StreamReader(stream);
            foreach (var batch in Regex.Split(await reader.ReadToEndAsync().ConfigureAwait(false), @"(?im)^\s*GO\s*$"))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                using var command = new SqlCommand(batch, connection) { CommandTimeout = 30 };
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        public async Task<OperatorProfile?> GetCurrentOperatorAsync(CancellationToken token = default)
        {
            var data = await QueryAsync("SELECT WindowsAccount,DisplayName,Role,IsEnabled FROM dbo.Operators WHERE WindowsAccount=@Actor AND @Actor=ORIGINAL_LOGIN() AND IsEnabled=1", false,
                new[] { new SqlParameter("@Actor", SqlDbType.NVarChar, 256) { Value = CurrentWindowsAccount } }, token).ConfigureAwait(false);
            if (data.Rows.Count == 0) return null;
            var row = data.Rows[0];
            return new OperatorProfile { WindowsAccount = (string)row[0], DisplayName = (string)row[1], Role = (string)row[2], IsEnabled = (bool)row[3] };
        }

        public async Task RequireOperatorAsync(bool supervisor = false, CancellationToken token = default)
        {
            var user = await GetCurrentOperatorAsync(token).ConfigureAwait(false);
            if (user == null || supervisor && !user.IsSupervisor)
                throw new UnauthorizedAccessException(supervisor ? "Supervisor access is required." : "Sign in with a registered Windows operator account.");
        }

        public async Task BootstrapFirstSupervisorAsync(CancellationToken token = default)
        {
            await ExecuteAsync(@"
SET XACT_ABORT ON; BEGIN TRANSACTION;
DECLARE @Count int; SELECT @Count=COUNT(*) FROM dbo.Operators WITH(UPDLOCK,HOLDLOCK);
IF @Count<>0 THROW 51103,'Operator setup is already complete; ask a Supervisor to register this account.',1;
IF @Actor<>ORIGINAL_LOGIN() THROW 51104,'Windows and SQL identities must match.',1;
INSERT dbo.Operators(WindowsAccount,DisplayName,Role) VALUES(@Actor,@Actor,N'Supervisor');
INSERT dbo.MachineEvents(MachineId,EventType,EventValue,OccurredAt,EventKey,OperatorAccount,Source)
VALUES(@Line,N'OperatorBootstrap',N'First Windows supervisor registered.',SYSUTCDATETIME(),NEWID(),@Actor,N'HMI');
COMMIT;", new[] { Actor(), new SqlParameter("@Line", SqlDbType.Int) { Value = LineId } }, token).ConfigureAwait(false);
        }

        public Task<DataTable> GetOperatorsAsync(CancellationToken token = default) => QueryAsync(
            "SELECT WindowsAccount,DisplayName,Role,IsEnabled,CreatedAt FROM dbo.Operators ORDER BY WindowsAccount", false, Array.Empty<SqlParameter>(), token);

        public async Task SaveOperatorAsync(string account, string displayName, string role, bool enabled, CancellationToken token = default)
        {
            await RequireOperatorAsync(true, token).ConfigureAwait(false);
            account = (account ?? "").Trim(); displayName = (displayName ?? "").Trim();
            if (account.Length == 0 || account.Length > 256 || displayName.Length == 0 || displayName.Length > 100 || role != "Operator" && role != "Supervisor")
                throw new ArgumentException("Provide a valid Windows account, display name and role.");
            // Resolve a real Windows principal, not an arbitrary HMI-only name or a reusable password.
            await Task.Run(() => new NTAccount(account).Translate(typeof(SecurityIdentifier)), token).ConfigureAwait(false);
            await ExecuteAsync(@"
SET XACT_ABORT ON; BEGIN TRANSACTION;
IF NOT EXISTS(SELECT 1 FROM dbo.Operators WITH(UPDLOCK,HOLDLOCK) WHERE WindowsAccount=@Actor AND @Actor=ORIGINAL_LOGIN() AND Role=N'Supervisor' AND IsEnabled=1)
THROW 51105,'Supervisor access was revoked.',1;
UPDATE dbo.Operators SET DisplayName=@Name,Role=@Role,IsEnabled=@Enabled WHERE WindowsAccount=@Account;
IF @@ROWCOUNT=0 INSERT dbo.Operators(WindowsAccount,DisplayName,Role,IsEnabled) VALUES(@Account,@Name,@Role,@Enabled);
IF NOT EXISTS(SELECT 1 FROM dbo.Operators WHERE Role=N'Supervisor' AND IsEnabled=1) THROW 51106,'At least one enabled Supervisor must remain.',1;
INSERT dbo.MachineEvents(MachineId,EventType,EventValue,OccurredAt,EventKey,OperatorAccount,Source)
VALUES(@Line,N'OperatorUpdated',LEFT(@Account+N'; role='+@Role,200),SYSUTCDATETIME(),NEWID(),@Actor,N'HMI');
COMMIT;", new[] { Actor(), new SqlParameter("@Line", SqlDbType.Int) { Value = LineId },
                new SqlParameter("@Account", SqlDbType.NVarChar, 256) { Value = account }, new SqlParameter("@Name", SqlDbType.NVarChar, 100) { Value = displayName },
                new SqlParameter("@Role", SqlDbType.NVarChar, 20) { Value = role }, new SqlParameter("@Enabled", SqlDbType.Bit) { Value = enabled } }, token).ConfigureAwait(false);
        }

        public async Task SaveRecipeAsync(int? recipeId, string name, double target, bool active, CancellationToken token = default)
        {
            await RequireOperatorAsync(true, token).ConfigureAwait(false);
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 100 || double.IsNaN(target) || double.IsInfinity(target) || target < 0.1 || target > 1000000)
                throw new ArgumentException("Recipe name must be 1–100 characters; target must be 0.1–1000000 kg.");
            await ExecuteAsync(@"
SET XACT_ABORT ON; BEGIN TRANSACTION;
IF NOT EXISTS(SELECT 1 FROM dbo.Operators WHERE WindowsAccount=@Actor AND @Actor=ORIGINAL_LOGIN() AND Role=N'Supervisor' AND IsEnabled=1)
THROW 51105,'Supervisor access was revoked.',1;
IF @Id IS NULL INSERT dbo.Recipes(RecipeName,TargetWeight,IsActive) VALUES(@Name,@Target,@Active);
ELSE BEGIN
    UPDATE dbo.Recipes SET RecipeName=@Name,TargetWeight=@Target,IsActive=@Active WHERE RecipeId=@Id;
    IF @@ROWCOUNT=0 THROW 51107,'Recipe not found.',1;
END
INSERT dbo.MachineEvents(MachineId,EventType,EventValue,OccurredAt,EventKey,OperatorAccount,Source)
VALUES(@Line,N'RecipeSaved',LEFT(@Name+N'; target='+CONVERT(nvarchar(40),@Target),200),SYSUTCDATETIME(),NEWID(),@Actor,N'HMI');
COMMIT;", new[] { Actor(), new SqlParameter("@Line", SqlDbType.Int) { Value = LineId }, new SqlParameter("@Id", SqlDbType.Int) { Value = (object?)recipeId ?? DBNull.Value },
                new SqlParameter("@Name", SqlDbType.NVarChar, 100) { Value = name }, new SqlParameter("@Target", SqlDbType.Float) { Value = target },
                new SqlParameter("@Active", SqlDbType.Bit) { Value = active } }, token).ConfigureAwait(false);
        }

        public Task<DataTable> GetAllRecipesAsync(CancellationToken token = default) => QueryAsync(
            "SELECT RecipeId,RecipeName,TargetWeight,IsActive FROM dbo.Recipes ORDER BY RecipeId", false, Array.Empty<SqlParameter>(), token);

        public Task<DataTable> GetBatchHistoryAsync(DateTime fromUtc, DateTime toUtc, string? status = null, int limit = 1000, CancellationToken token = default) =>
            QueryAsync("dbo.usp_FactoryFlowBatchHistory", true, new[] {
                new SqlParameter("@LineId", SqlDbType.Int) { Value = LineId }, new SqlParameter("@FromUtc", SqlDbType.DateTime2) { Value = fromUtc },
                new SqlParameter("@ToUtc", SqlDbType.DateTime2) { Value = toUtc }, new SqlParameter("@Status", SqlDbType.NVarChar, 30) { Value = (object?)status ?? DBNull.Value },
                new SqlParameter("@Limit", SqlDbType.Int) { Value = limit } }, token);

        public Task<DataTable> GetAlarmsAsync(bool includeClosed = false, CancellationToken token = default) => QueryAsync("dbo.usp_FactoryFlowAlarmHistory", true,
            new[] { new SqlParameter("@LineId", SqlDbType.Int) { Value = LineId }, new SqlParameter("@IncludeClosed", SqlDbType.Bit) { Value = includeClosed } }, token);

        public async Task AcknowledgeAlarmAsync(long alarmId, CancellationToken token = default)
        {
            await RequireOperatorAsync(false, token).ConfigureAwait(false);
            await QueryAsync("dbo.usp_FactoryFlowAcknowledgeAlarm", true,
                new[] { Actor("@ActorAccount"), new SqlParameter("@AlarmId", SqlDbType.BigInt) { Value = alarmId } }, token).ConfigureAwait(false);
        }

        public async Task AppendTelemetryAsync(IReadOnlyList<ProductionActivity> samples, CancellationToken token = default)
        {
            if (samples.Count == 0) return;
            using var data = new DataTable();
            data.Columns.Add("EventKey", typeof(Guid)); data.Columns.Add("LineId", typeof(int)); data.Columns.Add("EventType", typeof(string));
            data.Columns.Add("EventValue", typeof(string)); data.Columns.Add("OccurredAt", typeof(DateTime)); data.Columns.Add("Source", typeof(string));
            foreach (var sample in samples) data.Rows.Add(sample.EventKey, sample.LineId, sample.EventType,
                (object?)sample.EventValue ?? DBNull.Value, sample.OccurredAtUtc, sample.Source);
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = new SqlCommand("dbo.usp_FactoryFlowAppendTelemetry", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
            command.Parameters.Add(new SqlParameter("@Rows", SqlDbType.Structured) { TypeName = "dbo.FactoryFlowTelemetryRows", Value = data });
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        private async Task<DataTable> QueryAsync(string text, bool procedure, SqlParameter[] parameters, CancellationToken token)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = new SqlCommand(text, connection) { CommandTimeout = 15, CommandType = procedure ? CommandType.StoredProcedure : CommandType.Text };
            command.Parameters.AddRange(parameters);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var data = new DataTable(); data.Load(reader); return data;
        }
        private async Task ExecuteAsync(string text, SqlParameter[] parameters, CancellationToken token)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = new SqlCommand(text, connection) { CommandTimeout = 15 };
            command.Parameters.AddRange(parameters);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        private static SqlParameter Actor(string name = "@Actor") => new(name, SqlDbType.NVarChar, 256) { Value = CurrentWindowsAccount };
    }
}
