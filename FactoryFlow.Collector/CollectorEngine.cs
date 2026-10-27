using FactoryFlow.Data;
using FactoryFlow.PlcClient.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FactoryFlow.Collector
{
    public sealed class CollectorSource
    {
        public int LineId { get; set; }
        public string Endpoint { get; set; } = "";
        public string NamespaceUri { get; set; } = PlcNodeIds.NamespaceUri;
    }

    /// <summary>One asynchronous PLC supervisor per source; one ordered durable SQL outbox for the collector.</summary>
    public sealed class CollectorEngine
    {
        private readonly SqlProductionRepository _repository;
        private readonly List<PlcCommunicationService> _clients = new();
        private readonly string _root;
        private readonly string _outbox;
        private readonly IReadOnlyList<CollectorSource> _sources;
        private readonly IReadOnlyDictionary<string, string> _tagMap;
        private readonly TimeSpan _weightInterval;
        private readonly object _journalLock = new();
        private long _ticks;
        public event Action<string>? Diagnostic;
        public CollectorEngine(string connectionString, IReadOnlyList<CollectorSource> sources, string root,
            TimeSpan weightInterval, IReadOnlyDictionary<string, string>? tagMap = null)
        {
            if (sources.Count == 0 || sources.Any(s => s.LineId <= 0) || sources.Select(s => s.LineId).Distinct().Count() != sources.Count)
                throw new ArgumentException("Collector sources must have unique positive line IDs.");
            if (weightInterval < TimeSpan.FromMilliseconds(250)) throw new ArgumentOutOfRangeException(nameof(weightInterval));
            _repository = new SqlProductionRepository(connectionString); _sources = sources; _root = root;
            _outbox = Path.Combine(root, "telemetry-outbox"); _weightInterval = weightInterval;
            _tagMap = tagMap ?? new Dictionary<string, string>();
        }

        public async Task PrepareCertificatesAsync()
        {
            foreach (var source in _sources)
            {
                await using var client = CreateClient(source);
                await client.PrepareCertificateAsync().ConfigureAwait(false);
                Diagnostic?.Invoke("Prepared public certificate in " + client.CertificateStorePath);
            }
        }

        private PlcCommunicationService CreateClient(CollectorSource source) => new(source.Endpoint,
            Path.Combine(_root, "pki", "line" + source.LineId), "FactoryFlow.Collector.Line" + source.LineId, source.NamespaceUri, _tagMap);

        public async Task RunAsync(CancellationToken token)
        {
            Directory.CreateDirectory(_outbox);
            using var owner = new FileStream(Path.Combine(_root, "collector.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (var source in _sources)
            {
                var client = CreateClient(source); _clients.Add(client);
                int? status = null; bool? emergency = null; bool? guard = null;
                DateTime weightAt = DateTime.MinValue; string? connection = null;
                client.SnapshotChanged += snapshot =>
                {
                    try
                    {
                        var events = new List<ProductionActivity>();
                        void Add(string kind, string value) => events.Add(new ProductionActivity
                        { LineId = source.LineId, EventType = "Telemetry." + kind, EventValue = value, Source = "Collector" });
                        if (connection != snapshot.Connection) { connection = snapshot.Connection; Add("Connection", connection); }
                        if (snapshot.IsConnected && snapshot.ValuesValid)
                        {
                            if (status != snapshot.Status) { status = snapshot.Status; Add("Status", status!.Value.ToString(CultureInfo.InvariantCulture)); }
                            if (emergency != snapshot.EmergencyStop) { emergency = snapshot.EmergencyStop; Add("EStopActive", emergency!.Value.ToString()); }
                            if (guard != snapshot.GuardClosed) { guard = snapshot.GuardClosed; Add("GuardClosed", guard!.Value.ToString()); }
                            if (DateTime.UtcNow - weightAt >= _weightInterval)
                            { weightAt = DateTime.UtcNow; Add("Weight", snapshot.Weight!.Value.ToString("R", CultureInfo.InvariantCulture)); }
                        }
                        if (events.Count > 0) Journal(events.ToArray());
                    }
                    catch (Exception ex) { Diagnostic?.Invoke("Journal error: " + ex.Message); }
                };
                client.Diagnostic += text =>
                {
                    Diagnostic?.Invoke("Line " + source.LineId + ": " + text);
                    try { Journal(new[] { new ProductionActivity { LineId = source.LineId, EventType = "Telemetry.Diagnostic", EventValue = text.Length <= 200 ? text : text.Substring(0, 200), Source = "Collector" } }); }
                    catch (Exception ex) { Diagnostic?.Invoke(ex.Message); }
                };
            }
            try
            {
                await Task.WhenAll(_clients.Select(async client =>
                {
                    try { await client.ConnectAsync().ConfigureAwait(false); }
                    catch (Exception ex) { Diagnostic?.Invoke("Connection pending; client will reconnect: " + ex.Message); }
                })).ConfigureAwait(false);
                int retrySeconds = 1;
                while (!token.IsCancellationRequested)
                {
                    try { await FlushAsync(token).ConfigureAwait(false); retrySeconds = 1; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception ex) { Diagnostic?.Invoke("SQL unavailable; durable events retained: " + ex.Message); retrySeconds = Math.Min(retrySeconds * 2, 8); }
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                foreach (var client in _clients) await client.DisposeAsync().ConfigureAwait(false);
                _clients.Clear();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await FlushAsync(timeout.Token).ConfigureAwait(false); } catch (Exception ex) { Diagnostic?.Invoke("Shutdown left pending events: " + ex.Message); }
            }
        }

        private void Journal(ProductionActivity[] events)
        {
            lock (_journalLock)
            {
                _ticks = Math.Max(DateTime.UtcNow.Ticks, _ticks + 1);
                var path = Path.Combine(_outbox, _ticks.ToString("D19") + "_" + Guid.NewGuid().ToString("N") + ".json");
                using (var stream = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { new DataContractJsonSerializer(typeof(ProductionActivity[])).WriteObject(stream, events); stream.Flush(true); }
                File.Move(path + ".tmp", path);
            }
        }
        private async Task FlushAsync(CancellationToken token)
        {
            foreach (var file in Directory.GetFiles(_outbox, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                ProductionActivity[] events;
                using (var stream = File.OpenRead(file)) events = (ProductionActivity[])new DataContractJsonSerializer(typeof(ProductionActivity[])).ReadObject(stream)!;
                await _repository.AppendTelemetryAsync(events, token).ConfigureAwait(false);
                File.Delete(file);
            }
        }
    }
}
