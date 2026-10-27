using FactoryFlow.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FactoryFlow.Data
{
    /// <summary>
    /// Records observed PLC transitions, not optimistic button clicks. SQL work runs off the UI thread.
    /// A small disk outbox retains events until their idempotent SQL transaction commits.
    /// </summary>
    public sealed class ProductionHistoryService
    {
        private readonly SqlProductionRepository _repository;
        private readonly string _outbox;
        private readonly object _sync = new();
        private readonly SemaphoreSlim _initialize = new(1, 1);
        private readonly SemaphoreSlim _flush = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private volatile bool _initialized;
        private FileStream? _ownership;
        private bool _ready;
        private bool _disposed;
        private string? _error;
        private Guid? _active;
        private bool _hasRun;
        private bool _preparing;
        private long _lastRevision = -1;
        private string? _connection;
        private int? _status;
        private bool? _emergency;
        private bool? _guard;
        private long _lastFileTicks;
        private double _targetWeight;
        private string? _operatorAccount;
        public event Action? Changed;
        public bool IsReady { get { lock (_sync) return _ready && !_disposed; } }
        public bool CanBeginBatch { get { lock (_sync) return _ready && !_disposed && !_active.HasValue && !_preparing; } }
        public string StatusText { get { lock (_sync) return _error ?? (_ready ? "Connected — production logging active" : "Not connected"); } }
        public int PendingCount => Directory.Exists(_outbox) ? Directory.GetFiles(_outbox, "*.json").Length : 0;
        public Guid? ActiveBatch { get { lock (_sync) return _active; } }

        public ProductionHistoryService(SqlProductionRepository repository, string outboxDirectory)
        {
            _repository = repository;
            _outbox = outboxDirectory;
            _worker = Task.Run(WorkAsync);
        }

        public async Task<IReadOnlyList<Recipe>> InitializeAsync()
        {
            await _initialize.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ProductionHistoryService));
                lock (_sync)
                {
                    Directory.CreateDirectory(_outbox);
                    _ownership ??= new FileStream(Path.Combine(_outbox, "recorder.lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                await _repository.InitializeAsync(_stop.Token).ConfigureAwait(false);
                var recipes = await _repository.GetRecipesAsync(_stop.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    if (!_initialized)
                    {
                        Directory.CreateDirectory(_outbox);
                        // A previous HMI may have stopped while the PLC continued. Never invent a completion.
                        var marker = Path.Combine(_outbox, "active.batch");
                        if (File.Exists(marker) && Guid.TryParse(File.ReadAllText(marker), out var previous))
                        {
                            Record(new ProductionActivity
                            {
                                BatchCorrelationId = previous, EventType = "BatchInterrupted", BatchStatus = "Interrupted",
                                EndsBatch = true, EventValue = "HMI restarted; previous batch outcome is unverified."
                            });
                            File.Delete(marker);
                        }
                        _initialized = true;
                    }
                    _ready = true; _error = null;
                }
                Notify();
                return recipes;
            }
            catch (Exception ex) { Fail(ex); throw; }
            finally { _initialize.Release(); }
        }

        public async Task BeginBatchAsync(Recipe recipe)
        {
            if (double.IsNaN(recipe.TargetWeight) || double.IsInfinity(recipe.TargetWeight) || recipe.TargetWeight < 0.1 || recipe.TargetWeight > 1000000)
                throw new InvalidOperationException("Recipe target must be 0.1–1000000 kg.");
            var id = Guid.NewGuid();
            lock (_sync)
            {
                if (!CanBeginBatch) throw new InvalidOperationException("Production logging is not ready or a batch is already active.");
                _preparing = true;
            }
            try
            {
                // Persist the start request BEFORE sending a PLC command. A failed SQL connection blocks Start.
                // First commit the previous terminal event so an immediate next job does not race the outbox.
                await FlushAsync(_stop.Token).ConfigureAwait(false);
                await _repository.BeginBatchAsync(id, recipe.RecipeId, _stop.Token, recipe.TargetWeight).ConfigureAwait(false);
                lock (_sync)
                {
                    _active = id; _hasRun = false;
                    _targetWeight = recipe.TargetWeight;
                    _operatorAccount = SqlProductionRepository.CurrentWindowsAccount;
                    File.WriteAllText(Path.Combine(_outbox, "active.batch"), id.ToString());
                }
            }
            catch (Exception ex) { Fail(ex); throw; }
            finally { lock (_sync) _preparing = false; Notify(); }
        }

        public void Observe(ProductionObservation observation)
        {
            lock (_sync)
            {
                if (!_initialized || _disposed || observation.Revision <= _lastRevision) return;
                _lastRevision = observation.Revision;
                if (observation.Connection != _connection)
                {
                    _connection = observation.Connection;
                    if (_connection == "CONNECTED") _status = null; // Restore the persisted line state after reconnect.
                    Record(new ProductionActivity { EventType = "PlcConnection", EventValue = _connection,
                        LineStatus = _connection == "CONNECTED" ? null : "Disconnected" });
                    if (_connection != "CONNECTED" && _active.HasValue)
                        Finish("Interrupted", null, "PLC monitoring was lost; final outcome is unknown.");
                }
                if (_connection != "CONNECTED") return;
                if (observation.EmergencyStop.HasValue && observation.EmergencyStop != _emergency)
                {
                    _emergency = observation.EmergencyStop;
                    Record(new ProductionActivity { EventType = _emergency.Value ? "EmergencyStopActivated" : "EmergencyStopCleared", EventValue = _emergency.Value ? "Active" : "Inactive" });
                }
                if (observation.GuardClosed.HasValue && observation.GuardClosed != _guard)
                {
                    _guard = observation.GuardClosed;
                    Record(new ProductionActivity { EventType = _guard.Value ? "GuardClosed" : "GuardOpened", EventValue = _guard.Value ? "Closed" : "Open" });
                }
                if (!observation.Status.HasValue) return;
                if (observation.Status != _status)
                {
                    _status = observation.Status;
                    string state = StateName(_status.Value);
                    Record(new ProductionActivity { EventType = _status == 3 ? "MachineFault" : "MachineStatus", EventValue = state, LineStatus = state });
                    if (_status == 1 && !_active.HasValue)
                        Record(new ProductionActivity { EventType = "UntrackedBatchRunning", EventValue = "Batch started outside this HMI; recipe/batch identity unknown." });
                }
                if (!_active.HasValue) return;
                if (observation.Status == 1 && !_hasRun)
                {
                    _hasRun = true;
                    Record(new ProductionActivity { EventType = "BatchRunning", BatchStatus = "Running", EventValue = "PLC Running state confirmed." });
                }
                // Status and weight can arrive in separate monitored-item callbacks. Do not save an old weight.
                if (!observation.StateAndWeightCoherent || !observation.Weight.HasValue) return;
                if (observation.Status == 3)
                {
                    Finish("Fault", observation.Weight, "PLC entered Fault.");
                    return;
                }
                if (!_hasRun) return;
                if (observation.Status == 2 && observation.Weight == _targetWeight)
                    Finish("Completed", observation.Weight, "PLC completed the selected recipe target.");
                else if (observation.Status == 0)
                    Finish("Stopped", observation.Weight, "PLC returned to Idle before completion.");
            }
        }

        public void ConfirmStartResult(ProductionObservation observation)
        {
            Observe(observation);
            lock (_sync)
                if (_active.HasValue && !_hasRun && observation.Connection == "CONNECTED" && observation.Status.HasValue && observation.Status != 1)
                    Finish("Rejected", null, "PLC did not enter Running after the start request.");
        }

        public void CommandFailed(string command, string message)
        {
            lock (_sync)
            {
                Record(new ProductionActivity { EventType = "CommandFailed", EventValue = Limit(command + ": " + message) });
                if (command == "Start" && _active.HasValue && !_hasRun)
                    Finish("Unconfirmed", null, "Start outcome could not be confirmed; no command replay.");
            }
        }

        public void StopRequested()
        {
            lock (_sync) Record(new ProductionActivity { EventType = "BatchStopRequested", EventValue = "Operator requested Stop." });
        }

        public void RecordOperatorAction(string kind, string message, string account)
        {
            if (!string.Equals(account, SqlProductionRepository.CurrentWindowsAccount, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Operator must match the Windows identity.");
            lock (_sync) Record(new ProductionActivity { EventType = kind, EventValue = Limit(message), OperatorAccount = account });
        }

        private void Finish(string status, double? weight, string reason)
        {
            Record(new ProductionActivity { EventType = "Batch" + status, BatchStatus = status,
                EndsBatch = true, FinalWeight = weight, EventValue = reason });
            File.Delete(Path.Combine(_outbox, "active.batch"));
            _active = null; _hasRun = false;
            Notify();
        }

        private void Record(ProductionActivity activity)
        {
            try
            {
            activity.LineId = _repository.LineId;
            activity.BatchCorrelationId ??= _active;
            activity.OperatorAccount ??= _operatorAccount;
            Directory.CreateDirectory(_outbox);
            // FIFO filenames, even if the system clock steps backwards.
            _lastFileTicks = Math.Max(DateTime.UtcNow.Ticks, _lastFileTicks + 1);
            var path = Path.Combine(_outbox, _lastFileTicks.ToString("D19") + "_" + activity.EventKey.ToString("N") + ".json");
            using (var stream = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(ProductionActivity)).WriteObject(stream, activity);
                stream.Flush(true);
            }
            File.Move(path + ".tmp", path);
            }
            catch (Exception ex) { Fail(ex); throw; }
        }

        public async Task FlushAsync(CancellationToken token = default)
        {
            await _flush.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(_outbox)) return;
                foreach (var file in Directory.GetFiles(_outbox, "*.json").OrderBy(path => path, StringComparer.Ordinal))
                {
                    ProductionActivity activity;
                    using (var stream = File.OpenRead(file))
                        activity = (ProductionActivity)new DataContractJsonSerializer(typeof(ProductionActivity)).ReadObject(stream)!;
                    await _repository.ApplyAsync(activity, token).ConfigureAwait(false);
                    File.Delete(file);
                }
                if (!_ready) await _repository.GetRecipesAsync(token).ConfigureAwait(false);
                lock (_sync) { _ready = true; _error = null; }
                Notify();
            }
            catch (Exception ex) { Fail(ex); throw; }
            finally { _flush.Release(); }
        }

        private async Task WorkAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (_initialized)
                    {
                        try { await FlushAsync(_stop.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                        catch (Exception) { /* Fail() exposes the error; pending files remain for retry. */ }
                    }
                    await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        public async Task DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (_active.HasValue)
                    try { Finish("Interrupted", null, "HMI closed; PLC batch outcome is no longer monitored."); }
                    catch (Exception ex) { Fail(ex); /* Preserve active.batch for recovery on the next launch. */ }
                _disposed = true;
            }
            _stop.Cancel();
            await _worker.ConfigureAwait(false);
            // A bounded final flush; anything left remains on disk for the next HMI launch.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await FlushAsync(timeout.Token).ConfigureAwait(false); }
            catch (Exception) { }
            _ownership?.Dispose();
            _stop.Dispose();
        }

        private void Fail(Exception ex)
        {
            lock (_sync) { _ready = false; _error = "Logging delayed: " + ex.Message; }
            Notify();
        }
        private void Notify()
        {
            try { Changed?.Invoke(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        }
        private static string Limit(string value) => value.Length <= 200 ? value : value.Substring(0, 200);
        private static string StateName(int value) => value == 0 ? "Idle" : value == 1 ? "Running" : value == 2 ? "Completed" : "Fault";
    }
}

