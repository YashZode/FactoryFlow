using FactoryFlow.Core.Models;
using FactoryFlow.Data;
using FactoryFlow.PlcClient.Services;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

internal static class Program
{
    private static int _passed;
    private static string _database = "";
    private static string _connection = "";
    private static Process? _server;
    private const string Master = "Server=localhost\\SQLEXPRESS;Database=master;Integrated Security=True;Connect Timeout=5;Encrypt=True;TrustServerCertificate=True;Application Name=FactoryFlow.DatabaseTests";
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--inspect"))
            {
                var inspectRoot = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../.."));
                await using var inspection = new PlcCommunicationService(certificateStorePath: Path.Combine(inspectRoot, "FactoryFlow.HMI/bin/Debug/net48/pki"));
                await inspection.ConnectAsync();
                var state = inspection.Snapshot;
                Console.WriteLine($"Current PLC: Status={state.Status}; Weight={state.Weight}; EmergencyStop={state.EmergencyStop}; GuardClosed={state.GuardClosed}");
                return 0;
            }
            if (args.Contains("--migrate"))
            {
                var migrationRepository = new SqlProductionRepository(Master.Replace("Database=master", "Database=FactoryFlowDB"));
                await migrationRepository.InitializeAsync();
                if (args.Contains("--bootstrap") && await migrationRepository.GetCurrentOperatorAsync() == null) await migrationRepository.BootstrapFirstSupervisorAsync();
                Console.WriteLine("Applied additive migration to FactoryFlowDB; production rows preserved.");
                return 0;
            }
            var root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../.."));
            var simulator = args.Length > 1 ? args[1] : Path.Combine(root, ".verification/repeat-job/simulator/FactoryFlow.PlcSimulator.exe");
            _database = "FactoryFlowDB_LoggingTests_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            Execute(Master, "CREATE DATABASE [" + _database + "];");
            _connection = Master.Replace("Database=master", "Database=" + _database);
            Execute(_connection, @"
CREATE TABLE dbo.Recipes(RecipeId int IDENTITY PRIMARY KEY, RecipeName nvarchar(100) NOT NULL, TargetWeight float NOT NULL);
CREATE TABLE dbo.ProductionLines(LineId int IDENTITY PRIMARY KEY, LineName nvarchar(100) NOT NULL, Status nvarchar(30) NOT NULL);
CREATE TABLE dbo.Batches(BatchId int IDENTITY PRIMARY KEY, RecipeId int NOT NULL REFERENCES dbo.Recipes(RecipeId), LineId int NOT NULL REFERENCES dbo.ProductionLines(LineId), Status nvarchar(30) NOT NULL, StartedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), CompletedAt datetime2 NULL);
CREATE TABLE dbo.MachineEvents(EventId bigint IDENTITY PRIMARY KEY, MachineId int NOT NULL, EventType nvarchar(100) NOT NULL, EventValue nvarchar(200) NULL, OccurredAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
INSERT dbo.Recipes VALUES(N'Corn Feed',500),(N'Soy Feed',250);
INSERT dbo.ProductionLines VALUES(N'Test line',N'Idle');
INSERT dbo.Batches(RecipeId,LineId,Status) VALUES(1,1,N'Pending');");
            var repository = new SqlProductionRepository(_connection);
            await repository.InitializeAsync();
            await repository.InitializeAsync();
            Check(Count("SELECT COUNT(*) FROM dbo.Batches WHERE BatchId=1 AND Status=N'Pending'") == 1, "Additive migration preserves existing batch and is repeatable");
            await repository.BootstrapFirstSupervisorAsync();
            var recipes = await repository.GetRecipesAsync();
            Check(recipes.Count == 2 && recipes[0].TargetWeight == 500 && recipes[1].TargetWeight == 250, "Recipes loaded from SQL Server");
            var id = Guid.NewGuid();
            await repository.BeginBatchAsync(id, 1);
            await repository.BeginBatchAsync(id, 1);
            Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{id}'") == 1, "Repeated start request is idempotent");
            var run = new ProductionActivity { LineId = 1, BatchCorrelationId = id, EventType = "BatchRunning", BatchStatus = "Running" };
            await repository.ApplyAsync(run);
            var completed = new ProductionActivity { LineId = 1, BatchCorrelationId = id, EventType = "BatchCompleted", BatchStatus = "Completed", EndsBatch = true, FinalWeight = 500 };
            await repository.ApplyAsync(completed);
            await repository.ApplyAsync(completed);
            Check(Count($"SELECT COUNT(*) FROM dbo.MachineEvents WHERE EventKey='{completed.EventKey}'") == 1, "Commit retries do not duplicate machine events");
            Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{id}' AND Status='Completed' AND ActualWeight=500 AND CompletedAt IS NOT NULL AND EndedAt IS NOT NULL AND TargetWeight=500") == 1, "Completion saves weight, target and UTC timestamps");
            await repository.ApplyAsync(new ProductionActivity { LineId = 1, BatchCorrelationId = id, EventType = "BatchInterrupted", BatchStatus = "Interrupted", EndsBatch = true });
            Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{id}' AND Status='Completed' AND ActualWeight=500") == 1, "Late recovery cannot overwrite a completed result");

            var outbox = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "outbox-" + _database);
            var history = new ProductionHistoryService(repository, outbox);
            await history.InitializeAsync();
            long revision = 0;
            void Observe(int status, double weight, bool coherent = true, bool stop = false, bool guard = true, string connection = "CONNECTED") => history.Observe(new ProductionObservation
            {
                Revision = ++revision, Connection = connection, Status = status, Weight = weight,
                EmergencyStop = stop, GuardClosed = guard, StateAndWeightCoherent = coherent
            });
            Observe(0, 0);
            await history.BeginBatchAsync(recipes[0]);
            var tracked = history.ActiveBatch;
            Observe(1, 0);
            Observe(2, 495, false);
            Check(history.ActiveBatch == tracked, "Separate status notification cannot finalize an old weight");
            Observe(2, 500);
            await history.FlushAsync();
            Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{tracked}' AND Status='Completed' AND ActualWeight=500") == 1, "Recorder persists coherent completed snapshot");
            await history.BeginBatchAsync(recipes[0]);
            tracked = history.ActiveBatch;
            Observe(1, 0); Observe(1, 25); Observe(0, 25);
            await history.FlushAsync();
            Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{tracked}' AND Status='Stopped' AND ActualWeight=25 AND CompletedAt IS NULL AND EndedAt IS NOT NULL") == 1, "Operator stop saves partial weight without false completion");
            await history.BeginBatchAsync(recipes[0]);
            tracked = history.ActiveBatch;
            Observe(1, 0); Observe(1, 5, stop: true); Observe(3, 5, stop: true);
            await history.FlushAsync();
            Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{tracked}' AND Status='Fault' AND ActualWeight=5 AND CompletedAt IS NULL") == 1, "Fault saves measured weight without marking completion");
            Check(Count("SELECT COUNT(*) FROM dbo.MachineEvents WHERE EventType='EmergencyStopActivated'") >= 1, "Safety alarm stored in MachineEvents");
            await history.BeginBatchAsync(recipes[0]);
            tracked = history.ActiveBatch;
            Observe(1, 0); Observe(1, 10); Observe(1, 10, connection: "DISCONNECTED");
            await history.FlushAsync();
            Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{tracked}' AND Status='Interrupted' AND ActualWeight IS NULL AND CompletedAt IS NULL") == 1, "Monitoring loss records unknown final outcome, not fabricated completion");
            await history.BeginBatchAsync(recipes[1]); tracked = history.ActiveBatch;
            Observe(1, 0); Observe(2, 250); await history.FlushAsync();
            Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{tracked}' AND Status='Completed' AND ActualWeight=250 AND TargetWeight=250") == 1, "250 kg recipe records its actual configured target");

            Execute(_connection, "CREATE TRIGGER dbo.TestDatabaseUnavailable ON dbo.MachineEvents INSTEAD OF INSERT AS THROW 51010, 'Simulated SQL outage in test database only.', 1;");
            Observe(0, 0, guard: false);
            await ExpectFailure(() => history.FlushAsync(), "SQL write failure is surfaced");
            Check(history.PendingCount > 0 && !history.IsReady, "Failed events retained on disk and new Starts disabled");
            await history.DisposeAsync();
            Execute(_connection, "DROP TRIGGER dbo.TestDatabaseUnavailable;");
            var recovered = new ProductionHistoryService(repository, outbox);
            await recovered.InitializeAsync();
            await recovered.FlushAsync();
            Check(recovered.PendingCount == 0 && Count("SELECT COUNT(*) FROM dbo.MachineEvents WHERE EventType='GuardOpened'") >= 1, "Outbox survives restart and replays after SQL recovery");
            await recovered.DisposeAsync();

            // Actual secure OPC UA -> the real client -> production recorder -> SQL Server.
            const int port = 4843;
            await StartServer(simulator, port);
            var pki = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pki");
            await using var plc = new PlcCommunicationService($"opc.tcp://localhost:{port}", pki, "FactoryFlow.DatabaseTests");
            try { await plc.ConnectAsync(); } catch (Exception ex) { Console.WriteLine("Trust preparation: " + ex.Message); }
            await plc.DisconnectAsync();
            var serverPki = Path.Combine(Path.GetDirectoryName(simulator)!, "pki");
            Trust(serverPki, "CN=FactoryFlow.PlcSimulator, O=FactoryFlow", pki);
            Trust(pki, "CN=FactoryFlow.DatabaseTests", serverPki);
            await StopServer(); await StartServer(simulator, port);
            var live = new ProductionHistoryService(repository, outbox + "-live");
            await live.InitializeAsync();
            void OnSnapshot(PlcMachineSnapshot s) => live.Observe(new ProductionObservation
            {
                Revision = s.Revision, Connection = s.Connection, Status = s.Status, Weight = s.Weight,
                EmergencyStop = s.EmergencyStop, GuardClosed = s.GuardClosed, StateAndWeightCoherent = s.StateAndWeightCoherent
            });
            plc.SnapshotChanged += OnSnapshot;
            try
            {
                await plc.ConnectAsync();
                await live.BeginBatchAsync(recipes[0]);
                tracked = live.ActiveBatch;
                await plc.StartBatchAsync(500);
                await Until(() => plc.Snapshot.Status == 2 && live.ActiveBatch == null, "Live PLC batch reaches Completed and is tracked", 35000);
                await live.FlushAsync();
                Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{tracked}' AND Status='Completed' AND ActualWeight=500") == 1, "Actual secure PLC completion persisted in SQL Server");
                await live.BeginBatchAsync(recipes[0]);
                tracked = live.ActiveBatch;
                await plc.StartBatchAsync(500);
                await Until(() => plc.Snapshot.Weight > 0 && plc.Snapshot.Weight < 500, "Next job starts directly from Completed", 5000);
                live.StopRequested(); await plc.StopBatchAsync();
                await Until(() => live.ActiveBatch == null, "Live Stop finalized from coherent values", 5000);
                await live.FlushAsync();
                Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{tracked}' AND Status='Stopped' AND ActualWeight>0 AND ActualWeight<500 AND CompletedAt IS NULL") == 1, "Actual partial batch persisted as Stopped");
                await live.BeginBatchAsync(recipes[0]); tracked = live.ActiveBatch;
                await plc.StartBatchAsync(500); await plc.WriteTagAsync("GuardClosed", false);
                await Until(() => plc.Snapshot.Status == 3 && live.ActiveBatch == null, "Live guard fault ends tracked batch", 5000);
                await live.FlushAsync();
                Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{tracked}' AND Status='Fault' AND CompletedAt IS NULL") == 1, "Actual guard fault persisted as Fault");
                Check(Count("SELECT COUNT(*) FROM dbo.MachineEvents WHERE EventType IN ('GuardOpened','MachineFault')") >= 2, "Actual alarm events recorded");
                var alarms = await repository.GetAlarmsAsync();
                var guardAlarm = alarms.Rows.Cast<System.Data.DataRow>().First(row => (string)row["AlarmCode"] == "GuardOpen" && (bool)row["IsActive"]);
                long alarmId = (long)guardAlarm["AlarmId"];
                await repository.AcknowledgeAlarmAsync(alarmId);
                await repository.AcknowledgeAlarmAsync(alarmId);
                Check(Count($"SELECT COUNT(*) FROM dbo.Alarms WHERE AlarmId={alarmId} AND ClearedAt IS NULL AND AcknowledgedAt IS NOT NULL") == 1, "Acknowledging does not clear an active alarm");
                Check(Count($"SELECT COUNT(*) FROM dbo.MachineEvents WHERE EventType='AlarmAcknowledged' AND EventValue='{alarmId}'") == 1, "Repeated alarm acknowledgement is idempotent");
                await ExpectFailure(() => plc.ResetFaultAsync(), "Fault reset is blocked while the guard is open");
                await plc.WriteTagAsync("GuardClosed", true);
                await Until(() => plc.Snapshot.CanReset, "Fault reset becomes available after interlocks clear", 5000);
                await plc.ResetFaultAsync();
                await Until(() => plc.Snapshot.Status == 0, "Reset handshake confirms actual Idle state", 5000);
                await live.FlushAsync();
                Check(Count($"SELECT COUNT(*) FROM dbo.Alarms WHERE AlarmId={alarmId} AND ClearedAt IS NOT NULL AND AcknowledgedAt IS NOT NULL") == 1, "Alarm history retains both acknowledgement and clearance");
                await live.BeginBatchAsync(recipes[1]); tracked = live.ActiveBatch;
                await plc.StartBatchAsync(250);
                await Until(() => plc.Snapshot.Status == 2 && live.ActiveBatch == null, "250 kg OPC UA recipe completes", 20000);
                await live.FlushAsync();
                Check(Count($"SELECT COUNT(*) FROM dbo.Batches WHERE CorrelationId='{tracked}' AND TargetWeight=250 AND ActualWeight=250 AND OperatorAccount IS NOT NULL") == 1, "Actual 250 kg job and operator are saved");
                int diagnostics = 0;
                plc.Diagnostic += _ => Interlocked.Increment(ref diagnostics);
                await ExpectFailure(() => plc.ReadTagAsync("MissingTag"), "Unknown PLC tag produces an operation error");
                Check(diagnostics > 0, "Tag-level operation diagnostic is emitted");
            }
            finally
            {
                plc.SnapshotChanged -= OnSnapshot;
                await live.DisposeAsync();
            }
            await FeatureTestsAsync(repository, root);
            await CollectorTestAsync(root, simulator, repository);
            Console.WriteLine($"RESULT: {_passed} passed, 0 failed; Database={_database}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.WriteLine($"RESULT: {_passed} passed, 1 failed; Database={_database}"); return 1; }
        finally { await StopServer(); }
    }

    private static async Task FeatureTestsAsync(SqlProductionRepository repository, string root)
    {
        var profile = await repository.GetCurrentOperatorAsync();
        Check(profile?.IsSupervisor == true, "Windows identity authenticates to its registered Supervisor role");
        await ExpectFailure(() => repository.BootstrapFirstSupervisorAsync(), "First-run setup cannot promote additional accounts later");
        await ExpectFailure(() => repository.SaveOperatorAsync(SqlProductionRepository.CurrentWindowsAccount, "Developer", "Operator", true), "Cannot remove the final enabled Supervisor");
        await repository.SaveOperatorAsync("NT AUTHORITY\\SYSTEM", "Test service principal", "Supervisor", true);
        await repository.SaveOperatorAsync(SqlProductionRepository.CurrentWindowsAccount, "Test operator", "Operator", true);
        await ExpectFailure(() => repository.SaveRecipeAsync(null, "Forbidden", 10, true), "Operator role cannot configure recipes");
        Execute(_connection, "UPDATE dbo.Operators SET Role='Supervisor' WHERE WindowsAccount=ORIGINAL_LOGIN();");
        await repository.SaveRecipeAsync(null, "Recipe ' parameter test", 12.5, true);
        var configured = await repository.GetRecipesAsync();
        var newRecipe = configured.First(recipe => recipe.TargetWeight == 12.5);
        Check(configured.Any(recipe => recipe.RecipeName == "Recipe ' parameter test"), "Recipe CRUD uses parameterized SQL");
        await repository.SaveRecipeAsync(newRecipe.RecipeId, newRecipe.RecipeName, 12.5, false);
        Check(!(await repository.GetRecipesAsync()).Any(recipe => recipe.RecipeId == newRecipe.RecipeId), "Inactive recipes cannot be selected for production");
        await ExpectFailure(() => repository.SaveRecipeAsync(null, "Invalid", double.NaN, true), "Invalid recipe target is rejected");
        var history = await repository.GetBatchHistoryAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), "Completed");
        Check(history.Rows.Count > 0 && history.Rows.Cast<System.Data.DataRow>().All(row => (string)row["Status"] == "Completed"), "History stored procedure filters completed jobs");
        await ExpectFailure(() => repository.GetBatchHistoryAsync(DateTime.UtcNow, DateTime.UtcNow.AddDays(1), limit: 5001), "History query enforces a bounded row limit");
        var csv = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "batch-report.csv");
        CsvReport.Write(history, csv);
        Check(File.ReadAllText(csv).Contains("OperatorAccount"), "CSV report includes operator and batch history");
        using (var dangerous = new System.Data.DataTable())
        {
            dangerous.Columns.Add("Recipe"); dangerous.Rows.Add("=2+3"); dangerous.Rows.Add("text,quote\"\nnext");
            var safeCsv = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "csv-safety.csv"); CsvReport.Write(dangerous, safeCsv);
            Check(File.ReadAllText(safeCsv).Contains("\"'=2+3\"") && File.ReadAllText(safeCsv).Contains("quote\"\""), "CSV handles formula-like text, commas, quotes and newlines");
        }
        var samples = Enumerable.Range(0, 25000).Select(index => new ProductionActivity
        { LineId = 1, Source = "Benchmark", EventType = "Telemetry.Weight", EventValue = index.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray();
        var ingestion = Stopwatch.StartNew();
        for (int offset = 0; offset < samples.Length; offset += 1000) await repository.AppendTelemetryAsync(samples.Skip(offset).Take(1000).ToArray());
        ingestion.Stop();
        await repository.AppendTelemetryAsync(samples.Take(1000).ToArray());
        Check(Count("SELECT COUNT(*) FROM dbo.MachineEvents WHERE Source='Benchmark'") == 25000, "25,000-event ingestion benchmark persists unique events without duplicate replay");
        Execute(_connection, @"INSERT dbo.Batches(RecipeId,LineId,Status,StartedAt,CompletedAt,EndedAt,TargetWeight,ActualWeight,CorrelationId,RecipeNameSnapshot)
SELECT TOP(10000) 1,1,N'Completed',DATEADD(minute,-ROW_NUMBER() OVER(ORDER BY (SELECT NULL)),SYSUTCDATETIME()),SYSUTCDATETIME(),SYSUTCDATETIME(),500,500,NEWID(),N'Benchmark fixture'
FROM sys.all_objects a CROSS JOIN sys.all_objects b;");
        await repository.GetBatchHistoryAsync(DateTime.UtcNow.AddDays(-14), DateTime.UtcNow.AddDays(1));
        var timings = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew(); await repository.GetBatchHistoryAsync(DateTime.UtcNow.AddDays(-14), DateTime.UtcNow.AddDays(1));
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        timings.Sort(); var p95 = timings[18];
        Console.WriteLine($"BENCHMARK: 25000 events in {ingestion.Elapsed.TotalSeconds:F3}s; history p95={p95:F3}ms; history fixture=10000 batches; returned rows=1000; samples=20.");
        Check(p95 < 200, "Local indexed history query p95 is under 200 ms");
    }

    private static async Task CollectorTestAsync(string root, string simulator, SqlProductionRepository repository)
    {
        var executable = Path.Combine(root, ".verification/readiness/collector/FactoryFlow.Collector.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Build Collector in .verification/readiness/collector before this test.", executable);
        var collectorRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "collector-" + _database);
        var sources = "1|opc.tcp://localhost:4843|urn:factoryflow:plc:line1";
        async Task Run(string arguments)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.EnvironmentVariables["FACTORYFLOW_SQL_CONNECTION"] = _connection;
            process.StartInfo.EnvironmentVariables["FACTORYFLOW_COLLECTOR_ROOT"] = collectorRoot;
            process.StartInfo.EnvironmentVariables["FACTORYFLOW_COLLECTOR_SOURCES"] = sources;
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            var watch = Stopwatch.StartNew();
            while (!process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
            if (!process.HasExited) { process.Kill(); throw new TimeoutException("Owned collector process timed out."); }
            Console.WriteLine(await output); Console.WriteLine(await error);
            Check(process.ExitCode == 0, "Collector process exits cleanly: " + arguments);
        }
        await Run("--prepare-certificates");
        var serverPki = Path.Combine(Path.GetDirectoryName(simulator)!, "pki");
        var collectorPki = Path.Combine(collectorRoot, "pki/line1");
        Trust(serverPki, "CN=FactoryFlow.PlcSimulator, O=FactoryFlow", collectorPki);
        Trust(collectorPki, "CN=FactoryFlow.Collector.Line1", serverPki);
        await StopServer(); await StartServer(simulator, 4843);
        await Run("--console --duration 8");
        Check(Count("SELECT COUNT(*) FROM dbo.MachineEvents WHERE Source='Collector' AND EventType='Telemetry.Weight'") >= 2, "Separate collector records periodic weights without the HMI");
        Check(Count("SELECT COUNT(*) FROM dbo.MachineEvents WHERE Source='Collector' AND EventType='Telemetry.Status'") >= 1, "Collector records independently observed machine state");
        Check(Directory.GetFiles(Path.Combine(collectorRoot, "telemetry-outbox"), "*.json").Length == 0, "Collector drains its durable outbox on clean shutdown");
    }
    private static int Count(string sql) => Convert.ToInt32(Scalar(_connection, sql));
    private static object Scalar(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString); connection.Open();
        using var command = new SqlCommand(sql, connection); return command.ExecuteScalar();
    }
    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString); connection.Open();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 }; command.ExecuteNonQuery();
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        _passed++; Console.WriteLine("PASS: " + description);
    }
    private static async Task ExpectFailure(Func<Task> action, string description)
    {
        try { await action(); } catch { Check(true, description); return; }
        throw new Exception("FAIL: " + description);
    }
    private static async Task Until(Func<bool> condition, string description, int timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeout) await Task.Delay(50);
        Check(condition(), description);
    }
    private static void Trust(string from, string subject, string to)
    {
        var files = Directory.GetFiles(Path.Combine(from, "own/certs"), "*.der").Where(path =>
        {
            using var cert = new X509Certificate2(path); return cert.Subject == subject && !cert.HasPrivateKey && cert.NotAfter > DateTime.Now;
        }).ToArray();
        if (files.Length != 1) throw new Exception("Expected one verified public certificate for " + subject);
        using var verified = new X509Certificate2(files[0]);
        Console.WriteLine("TRUST: " + verified.Subject + "; " + verified.Thumbprint);
        Directory.CreateDirectory(Path.Combine(to, "trusted/certs"));
        File.Copy(files[0], Path.Combine(to, "trusted/certs", Path.GetFileName(files[0])), true);
    }
    private static async Task StartServer(string executable, int port)
    {
        var probe = new TcpListener(IPAddress.Loopback, port); try { probe.Start(); } finally { probe.Stop(); }
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server = new Process { StartInfo = new ProcessStartInfo(executable, "--port " + port)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }, EnableRaisingEvents = true };
        _server.OutputDataReceived += (_, e) => { if (e.Data?.StartsWith("Endpoint:") == true) ready.TrySetResult(true); };
        _server.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("SERVER: " + e.Data); };
        _server.Exited += (_, _) => ready.TrySetException(new Exception("Owned simulator exited before readiness."));
        _server.Start(); _server.BeginOutputReadLine(); _server.BeginErrorReadLine();
        if (await Task.WhenAny(ready.Task, Task.Delay(15000)) != ready.Task) throw new TimeoutException("Simulator startup timed out.");
        await ready.Task;
    }
    private static async Task StopServer()
    {
        if (_server == null) return;
        if (!_server.HasExited) _server.Kill();
        while (!_server.HasExited) await Task.Delay(50);
        _server.Dispose(); _server = null;
    }
}





