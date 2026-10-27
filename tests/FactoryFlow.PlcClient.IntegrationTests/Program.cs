using FactoryFlow.PlcClient.Services;
using Opc.Ua;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

internal static class Program
{
    private static int _passed;
    private static string _root = "";
    private static Process? _server;
    private static string _serverExe = "";
    private const int Port = 4842;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            _root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../.."));
            _serverExe = args.Length > 1 ? Path.GetFullPath(args[1]) :
                Path.Combine(_root, "FactoryFlow.PlcSimulator/bin/Debug/net8.0/FactoryFlow.PlcSimulator.exe");
            if (!File.Exists(_serverExe)) throw new FileNotFoundException("Build the simulator first.", _serverExe);
            QualityTests();
            // Never touch a pre-existing server. This harness owns only its child on dedicated port 4842.
            await StartServer();
            var pki = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pki");
            using var events = new EventCounters();
            var client = new PlcCommunicationService($"opc.tcp://localhost:{Port}", pki, "FactoryFlow.ClientIntegrationTests");

            try
            {
                try { await client.ConnectAsync(); }
                catch (Exception ex) { Console.WriteLine("Initial trust setup: " + ex.Message); }
                await client.DisconnectAsync();
                var serverPki = Path.Combine(Path.GetDirectoryName(_serverExe)!, "pki");
                TrustPublicCertificate(serverPki, "CN=FactoryFlow.PlcSimulator, O=FactoryFlow", pki);
                TrustPublicCertificate(pki, "CN=FactoryFlow.ClientIntegrationTests", serverPki);
                await StopServer();
                await StartServer();
                events.Attach(client);
                await Task.WhenAll(client.ConnectAsync(), client.ConnectAsync(), client.ConnectAsync());
                Check(client.IsConnected && events.Connected == 1, "Secure connection and duplicate-connect prevention");
                Check(client.NamespaceIndex > 0, "Namespace URI resolved");
                Check(client.Snapshot.ValuesValid && client.Snapshot.CanStart, "Initial state and interlocks read immediately");
                Check((await client.ReadTagAsync(PlcNodeIds.Status)).WrappedValue.TryGetValue(out int state) && state == 0, "Read status using full stable tag name");
                Check((await client.ReadTagAsync("ActualWeight")).WrappedValue.TryGetValue(out double weight) && weight == 0, "Read weight using short tag name");
                await ExpectFailure(() => client.WriteTagAsync("Status", true), "Read-only write rejected", StatusCodes.BadNotWritable);
                await ExpectFailure(() => client.WriteTagAsync("StartRequest", new Variant(42)), "Wrong data type rejected", StatusCodes.BadTypeMismatch);
                Check(client.IsConnected, "Bad operation does not tear down a healthy session");
                await client.WriteTagAsync("EStopActive", true);
                await Until(() => client.Snapshot.EmergencyStop == true, "Emergency-stop subscription", 5000);
                await ExpectFailure(() => client.StartBatchAsync(), "Unsafe start blocked locally");
                await client.WriteTagAsync("StartRequest", true);
                await client.RefreshMachineValuesAsync();
                Check(client.Snapshot.Status == 0, "Successful command write is not assumed to mean Running");
                await client.WriteTagAsync("EStopActive", false);
                await client.WriteTagAsync("GuardClosed", false);
                await Until(() => client.Snapshot.GuardClosed == false, "Guard subscription", 5000);
                Check(!client.Snapshot.CanStart, "Open guard disables Start");
                await client.WriteTagAsync("GuardClosed", true);
                await Until(() => client.Snapshot.CanStart, "Interlock recovery", 5000);
                await client.StartBatchAsync();
                Check(client.Snapshot.Status == 1 && client.Snapshot.CanStop, "Start command reads actual Running state");
                await Until(() => events.DistinctWeights.Count >= 4, "Multiple live weight notifications", 5000);
                Check(events.Statuses.Contains(1), "Running status event");
                await client.StopBatchAsync();
                Check(client.Snapshot.Status == 0 && !client.Snapshot.CanStop, "Stop command reads actual Idle state");

                // Trigger state through another secure client: proves StatusChanged is subscription-driven, not command readback.
                await using (var observer = new PlcCommunicationService($"opc.tcp://localhost:{Port}", pki, "FactoryFlow.ClientIntegrationTests"))
                {
                    await observer.ConnectAsync();
                    await observer.StartBatchAsync();
                    await Until(() => client.Snapshot.Status == 1, "Status subscription from another client command", 5000);
                    await observer.StopBatchAsync();
                    await Until(() => client.Snapshot.Status == 0, "Idle subscription from another client command", 5000);
                }

                // Complete a real batch, then start another through the same service/session.
                await client.StartBatchAsync();
                await Until(() => client.Snapshot.Status == 2 && client.Snapshot.Weight == 500,
                    "First batch completes at 500 kg", 35000);
                Check(client.Snapshot.CanStart && !client.Snapshot.CanStop,
                    "Completed enables Start and disables Stop");
                await client.WriteTagAsync("EStopActive", true);
                await Until(() => client.Snapshot.EmergencyStop == true, "Completed E-stop update", 5000);
                Check(!client.Snapshot.CanStart, "E-stop still blocks restart from Completed");
                await client.WriteTagAsync("StartRequest", true);
                await client.RefreshMachineValuesAsync();
                Check(client.Snapshot.Status == 2 && client.Snapshot.Weight == 500,
                    "PLC rejects unsafe restart without clearing completed result");
                await client.WriteTagAsync("EStopActive", false);
                await client.WriteTagAsync("GuardClosed", false);
                await Until(() => client.Snapshot.GuardClosed == false, "Completed guard update", 5000);
                Check(!client.Snapshot.CanStart, "Open guard still blocks restart from Completed");
                await client.WriteTagAsync("StartRequest", true);
                await client.RefreshMachineValuesAsync();
                Check(client.Snapshot.Status == 2 && client.Snapshot.Weight == 500,
                    "PLC rejects open-guard restart without clearing completed result");
                await client.WriteTagAsync("GuardClosed", true);
                await Until(() => client.Snapshot.CanStart, "Completed restart eligibility restored", 5000);
                await client.StartBatchAsync();
                Check(client.Snapshot.Status == 1 && client.Snapshot.Weight < 500,
                    "Next batch starts with weight reset");
                await Until(() => client.Snapshot.Status == 2 && client.Snapshot.Weight == 500,
                    "Second consecutive batch completes without server restart", 35000);
                Check(client.Snapshot.CanStart, "Further batches remain available");

                int connections = events.Connected;
                await StopServer();
                await Until(() => !client.IsConnected && !client.Snapshot.ValuesValid, "Connection loss invalidates machine values", 15000);
                Check(!client.Snapshot.CanStart && !client.Snapshot.CanStop, "Commands disabled while disconnected");
                await ExpectFailure(() => client.StartBatchAsync(), "Offline Start is rejected, not queued");
                await Task.Delay(2500);
                await StartServer();
                await Until(() => client.IsConnected, "Automatic reconnect after server restart", 30000);
                Check(events.Connected == connections + 1, "Only one reconnect succeeded");
                Check(client.Snapshot.Status == 0 && client.Snapshot.Weight == 0 && client.Snapshot.CanStart, "Reconnect refreshes all values without replaying Start");
                events.DistinctWeights.Clear();
                await client.StartBatchAsync();
                await Until(() => events.DistinctWeights.Count >= 4, "Weight subscription restored after reconnect", 5000);
                await client.StopBatchAsync();
                await client.DisconnectAsync();
                Check(!client.IsConnected && client.NamespaceIndex == 0 && !client.Snapshot.ValuesValid, "Explicit disconnect clears session and values");
                int afterDisconnect = events.Total;
                await Task.Delay(1000);
                Check(events.Total == afterDisconnect, "No leaked callbacks after disconnect");
                await client.ConnectAsync();
                Check(client.IsConnected, "Explicit connect works after clean disconnect");
            }
            finally { await client.DisposeAsync(); }
            await client.DisposeAsync();
            Check(!client.IsConnected, "Async disposal is idempotent");
            await ExpectFailure(() => client.ConnectAsync(), "Disposed client refuses connection");

            // First connection failure remains recoverable, and closing during backoff cancels retries.
            await StopServer();
            var unavailable = new PlcCommunicationService($"opc.tcp://localhost:{Port}", pki, "FactoryFlow.ClientIntegrationTests");
            try { await unavailable.ConnectAsync(); } catch { }
            await StartServer();
            await Until(() => unavailable.IsConnected, "Initial connection failure recovers automatically", 30000);
            await StopServer();
            await Until(() => !unavailable.IsConnected, "Second loss detected before shutdown", 15000);
            var stopWatch = Stopwatch.StartNew();
            await unavailable.DisposeAsync();
            Check(stopWatch.Elapsed < TimeSpan.FromSeconds(8), "Shutdown cancels reconnect backoff promptly");
            Console.WriteLine($"RESULT: {_passed} passed, 0 failed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.WriteLine($"RESULT: {_passed} passed, 1 failed");
            return 1;
        }
        finally { await StopServer(); }
    }

    private static void QualityTests()
    {
        var cache = new PlcValueCache();
        var now = DateTime.UtcNow;
        DataValue Value(Variant value, DateTime time, StatusCode quality = default) => new(value, quality, time);
        cache.Apply(PlcNodeIds.Status, Value(0, now), now);
        cache.Apply(PlcNodeIds.ActualWeight, Value(0.0, now), now);
        cache.Apply(PlcNodeIds.EStopActive, Value(false, now), now);
        cache.Apply(PlcNodeIds.GuardClosed, Value(true, now), now);
        Check(cache.Complete, "Quality cache accepts valid typed values");
        Check(!cache.Apply(PlcNodeIds.Status, Value(1, now.AddSeconds(-1)), now) && cache.Status == 0, "Out-of-order notification ignored");
        cache.Apply(PlcNodeIds.Status, Value(999, now), now);
        Check(!cache.Complete && cache.Status == null, "Unknown status invalidates command eligibility");
        cache.Apply(PlcNodeIds.Status, Value(0, now, StatusCodes.BadNoCommunication), now);
        Check(cache.Status == null, "Bad quality invalidates value");
        cache.Apply(PlcNodeIds.Status, Value(0, now, StatusCodes.Uncertain), now);
        Check(cache.Status == null, "Uncertain quality invalidates value");
        cache.Apply(PlcNodeIds.ActualWeight, Value(double.NaN, now), now);
        Check(cache.Weight == null, "NaN weight invalidates value");
        cache.Apply(PlcNodeIds.GuardClosed, Value(true, now.AddSeconds(-10)), now);
        Check(cache.GuardClosed == null, "Stale source timestamp invalidates value");
        Check(cache.HasExpired(now.AddSeconds(6)), "Local notification watchdog detects stale data");
        cache.Clear();
        Check(!cache.Complete, "Disconnect invalidates cache");
    }

    private static void TrustPublicCertificate(string source, string subject, string destination)
    {
        var files = Directory.GetFiles(Path.Combine(source, "own/certs"), "*.der");
        var verified = files.Where(path =>
        {
            using var certificate = new X509Certificate2(path);
            return certificate.Subject == subject && !certificate.HasPrivateKey && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow;
        }).ToArray();
        if (verified.Length != 1) throw new InvalidOperationException("Expected exactly one verified public certificate for " + subject);
        using var cert = new X509Certificate2(verified[0]);
        Console.WriteLine($"TRUST PUBLIC CERTIFICATE: {cert.Subject}; SHA1 thumbprint={cert.Thumbprint}; {verified[0]}");
        var trusted = Path.Combine(destination, "trusted/certs");
        Directory.CreateDirectory(trusted);
        File.Copy(verified[0], Path.Combine(trusted, Path.GetFileName(verified[0])), true);
    }

    private static async Task StartServer()
    {
        // Binding is an availability check only; if another process wins the race the child will fail startup.
        var probe = new TcpListener(IPAddress.Loopback, Port);
        try { probe.Start(); } finally { probe.Stop(); }
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server = new Process { StartInfo = new ProcessStartInfo(_serverExe, "--port " + Port)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_serverExe)!
        }, EnableRaisingEvents = true };
        _server.OutputDataReceived += (_, e) => { if (e.Data?.StartsWith("Endpoint:") == true) ready.TrySetResult(true); };
        _server.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("SERVER: " + e.Data); };
        _server.Exited += (_, _) => ready.TrySetException(new Exception("Owned simulator exited before readiness."));
        _server.Start();
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
        if (await Task.WhenAny(ready.Task, Task.Delay(15000)) != ready.Task) throw new TimeoutException("Simulator did not start.");
        await ready.Task;
    }

    private static async Task StopServer()
    {
        if (_server == null) return;
        if (!_server.HasExited) _server.Kill(); // Only the exact child this harness created.
        while (!_server.HasExited) await Task.Delay(50);
        _server.Dispose();
        _server = null;
    }

    private static async Task Until(Func<bool> condition, string description, int timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeout) await Task.Delay(50);
        Check(condition(), description);
    }

    private static async Task ExpectFailure(Func<Task> action, string description, StatusCode? expected = null)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Check(expected == null || ex is ServiceResultException ua && ua.StatusCode == expected, description);
            return;
        }
        throw new Exception("FAIL: " + description);
    }

    private static void Check(bool success, string description)
    {
        if (!success) throw new Exception("FAIL: " + description);
        _passed++;
        Console.WriteLine("PASS: " + description);
    }

    private sealed class EventCounters : IDisposable
    {
        private PlcCommunicationService? _client;
        private int _connected;
        private int _total;
        internal int Connected => Volatile.Read(ref _connected);
        internal int Total => Volatile.Read(ref _total);
        internal ConcurrentDictionary<double, bool> DistinctWeights { get; } = new();
        internal ConcurrentBag<int> Statuses { get; } = new();
        internal void Attach(PlcCommunicationService client)
        {
            _client = client;
            client.WeightChanged += OnWeight;
            client.StatusChanged += OnStatus;
            client.ConnectionChanged += OnConnection;
        }
        private void OnWeight(double value) { DistinctWeights.TryAdd(value, true); Interlocked.Increment(ref _total); }
        private void OnStatus(int value) { Statuses.Add(value); Interlocked.Increment(ref _total); }
        private void OnConnection(string value) { if (value == "CONNECTED") Interlocked.Increment(ref _connected); }
        public void Dispose()
        {
            if (_client == null) return;
            _client.WeightChanged -= OnWeight; _client.StatusChanged -= OnStatus; _client.ConnectionChanged -= OnConnection;
        }
    }
}

