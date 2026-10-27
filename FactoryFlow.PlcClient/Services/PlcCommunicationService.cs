using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FactoryFlow.PlcClient.Services
{
    /// <summary>
    /// Owns a secure session and its subscriptions. A single supervisor recreates both after loss.
    /// Callbacks run on SDK threads; consumers must marshal UI work to their Dispatcher.
    /// </summary>
    public sealed class PlcCommunicationService : IAsyncDisposable
    {
        private readonly object _stateLock = new();
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        private readonly SemaphoreSlim _io = new(1, 1);
        private readonly FactoryFlowClientTelemetry _telemetry = new();
        private readonly PlcValueCache _values = new();
        private readonly string _endpointUrl;
        private readonly string _applicationName;
        private readonly string _namespaceUri;
        private readonly IReadOnlyDictionary<string, string> _tagMap;
        private readonly List<MonitoredItem> _items = new();
        private ApplicationInstance? _application;
        private ISession? _session;
        private Subscription? _subscription;
        private ushort _namespaceIndex;
        private CancellationTokenSource? _runCancellation;
        private Task? _supervisor;
        private TaskCompletionSource<bool>? _firstConnection;
        private TaskCompletionSource<bool>? _connectionLost;
        private long _generation;
        private long _revision;
        private bool _disposed;
        private string _connection = "DISCONNECTED";
        private string? _error;

        public string CertificateStorePath { get; }
        public bool IsConnected => Snapshot.IsConnected;
        public ushort NamespaceIndex { get { lock (_stateLock) return _namespaceIndex; } }
        public PlcMachineSnapshot Snapshot { get { lock (_stateLock) return MakeSnapshot(); } }
        public event Action<int>? StatusChanged;
        public event Action<double>? WeightChanged;
        public event Action<bool>? EmergencyStopChanged;
        public event Action<bool>? GuardClosedChanged;
        public event Action<string>? ConnectionChanged;
        public event Action<PlcMachineSnapshot>? SnapshotChanged;
        public event Action<string>? Diagnostic;

        public PlcCommunicationService(string endpointUrl = "opc.tcp://localhost:4840",
            string? certificateStorePath = null, string applicationName = "FactoryFlow.HMI",
            string namespaceUri = PlcNodeIds.NamespaceUri, IReadOnlyDictionary<string, string>? tagMap = null)
        {
            _endpointUrl = endpointUrl;
            _applicationName = applicationName;
            _namespaceUri = namespaceUri;
            _tagMap = tagMap ?? new Dictionary<string, string>();
            CertificateStorePath = certificateStorePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pki");
        }

        public async Task ConnectAsync()
        {
            Task first;
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PlcCommunicationService));
                if (IsConnected) return;
                if (_supervisor == null || _supervisor.IsCompleted)
                {
                    _runCancellation?.Dispose();
                    _runCancellation = new CancellationTokenSource();
                    _firstConnection = NewSignal();
                    // Certificate loading/crypto must not block the WPF dispatcher either.
                    _supervisor = Task.Run(() => SuperviseAsync(_runCancellation.Token));
                }
                first = _firstConnection!.Task;
            }
            finally { _lifecycle.Release(); }
            await first.ConfigureAwait(false);
        }

        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PrepareCertificateAsync()
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _supervisor != null) throw new InvalidOperationException("Prepare certificates before connecting.");
                await InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (_application != null) { await _application.DisposeAsync().ConfigureAwait(false); _application = null; }
                _lifecycle.Release();
            }
        }

        private async Task InitializeAsync(CancellationToken token)
        {
            var pki = CertificateStorePath;
            var config = new ApplicationConfiguration(_telemetry)
            {
                ApplicationName = _applicationName,
                ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:{_applicationName}",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificates = new[] { new CertificateIdentifier
                    {
                        StoreType = "Directory", StorePath = Path.Combine(pki, "own"),
                        SubjectName = "CN=" + _applicationName,
                        CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
                    } },
                    TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "trusted") },
                    TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "issuers") },
                    RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = "Directory", StorePath = Path.Combine(pki, "rejected") },
                    AutoAcceptUntrustedCertificates = false,
                    RejectSHA1SignedCertificates = true,
                    MinimumCertificateKeySize = 2048
                },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 15000 },
                TransportQuotas = new TransportQuotas { OperationTimeout = 5000 }
            };
            await config.ValidateAsync(ApplicationType.Client).ConfigureAwait(false);
            _application = new ApplicationInstance(config, _telemetry);
            if (!await _application.CheckApplicationInstanceCertificatesAsync(true, ct: token).ConfigureAwait(false))
                throw new InvalidOperationException("Client application certificate validation failed.");
        }

        private async Task SuperviseAsync(CancellationToken token)
        {
            int delaySeconds = 1;
            bool firstAttempt = true;
            try
            {
                ChangeConnection("CONNECTING", null, true);
                await InitializeAsync(token).ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    ChangeConnection(firstAttempt ? "CONNECTING" : "RECONNECTING", _error, true);
                    firstAttempt = false;
                    try
                    {
                        await _io.WaitAsync(token).ConfigureAwait(false);
                        try { await OpenSessionAsync(token).ConfigureAwait(false); }
                        finally { _io.Release(); }
                        token.ThrowIfCancellationRequested();
                        ChangeConnection("CONNECTED", null, false);
                        _firstConnection!.TrySetResult(true);
                        delaySeconds = 1;
                        Task loss;
                        lock (_stateLock) loss = _connectionLost!.Task;
                        // Local freshness watchdog, not PLC polling. Timestamp notifications arrive even when Idle.
                        while (!loss.IsCompleted)
                        {
                            await Task.WhenAny(loss, Task.Delay(1000, token)).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            bool expired;
                            lock (_stateLock) expired = _values.HasExpired(DateTime.UtcNow);
                            if (expired) SignalLoss(_session, "PLC subscription data has gone stale.");
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        ChangeConnection("DISCONNECTED", ex.Message, true);
                        _firstConnection!.TrySetException(ex);
                    }
                    finally
                    {
                        await _io.WaitAsync().ConfigureAwait(false);
                        try { await CloseSessionAsync().ConfigureAwait(false); }
                        finally { _io.Release(); }
                    }
                    if (token.IsCancellationRequested) break;
                    ChangeConnection("RECONNECTING", _error, true);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                    delaySeconds = Math.Min(delaySeconds * 2, 8);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { _firstConnection!.TrySetException(ex); ChangeConnection("DISCONNECTED", ex.Message, true); }
            finally
            {
                _firstConnection!.TrySetCanceled();
                if (_application != null)
                {
                    await _application.DisposeAsync().ConfigureAwait(false);
                    _application = null;
                }
                ChangeConnection("DISCONNECTED", _error, true);
            }
        }

        private async Task OpenSessionAsync(CancellationToken token)
        {
            var config = _application!.ApplicationConfiguration!;
            var endpoint = await CoreClientUtils.SelectEndpointAsync(config, _endpointUrl, true, _telemetry, token).ConfigureAwait(false);
            if (endpoint == null || endpoint.SecurityMode != MessageSecurityMode.SignAndEncrypt ||
                endpoint.SecurityPolicyUri != SecurityPolicies.Basic256Sha256)
                throw new InvalidOperationException("Basic256Sha256 / SignAndEncrypt endpoint required.");
            var session = await new DefaultSessionFactory(_telemetry).CreateAsync(config,
                new ConfiguredEndpoint(null, endpoint, EndpointConfigurationExtensions.Create(config)),
                false, _applicationName, 15000, new UserIdentity(new AnonymousIdentityToken()), default, token).ConfigureAwait(false);
            lock (_stateLock)
            {
                _session = session;
                _generation++;
                _connectionLost = NewSignal();
            }
            var index = session.NamespaceUris.GetIndex(_namespaceUri);
            if (index <= 0) throw new InvalidOperationException("FactoryFlow namespace is missing.");
            lock (_stateLock) _namespaceIndex = checked((ushort)index);
            session.KeepAliveInterval = 1000;
            session.KeepAlive += OnKeepAlive;
            _subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingInterval = 250, PublishingEnabled = true,
                KeepAliveCount = 4, LifetimeCount = 120, MinLifetimeInterval = 15000
            };
            long generation = _generation;
            foreach (var tag in PlcValueCache.Tags)
            {
                var item = new MonitoredItem(_telemetry)
                {
                    StartNodeId = GetNodeId(tag), DisplayName = tag, AttributeId = Attributes.Value,
                    SamplingInterval = 250, QueueSize = 10, DiscardOldest = true,
                    Filter = new DataChangeFilter { Trigger = DataChangeTrigger.StatusValueTimestamp },
                    Handle = generation
                };
                item.Notification += OnNotification;
                _items.Add(item);
                _subscription.AddItem(item);
            }
            session.AddSubscription(_subscription);
            await _subscription.CreateAsync(token).ConfigureAwait(false);
            foreach (var item in _items)
                if (ServiceResult.IsBad(item.Status.Error)) throw new ServiceResultException(item.Status.Error!);
            await RefreshCoreAsync(session, generation, token).ConfigureAwait(false);
            lock (_stateLock)
                if (!_values.Complete || _connectionLost!.Task.IsCompleted)
                    throw new InvalidOperationException("Initial PLC values are invalid or the connection was lost.");
        }

        private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs args)
        {
            foreach (var value in item.DequeueValues())
                if (item.Handle is long generation) AcceptValue(generation, item.DisplayName, value);
        }

        private void AcceptValue(long generation, string tag, DataValue value)
        {
            lock (_stateLock)
            {
                if (generation != _generation || _session == null || _connectionLost?.Task.IsCompleted == true) return;
                if (!StatusCode.IsGood(value.StatusCode)) Raise(Diagnostic, tag + ": " + value.StatusCode);
                if (!_values.Apply(tag, value, DateTime.UtcNow)) return;
                _revision++;
                // Serialize event delivery with invalidation. UI uses immutable snapshots and revision ordering.
                if (_values.Complete) _error = null;
                else _error = "PLC data quality/type/timestamp is invalid. Commands are disabled.";
                PublishSnapshot();
                switch (tag)
                {
                    case PlcNodeIds.Status: if (_values.Status.HasValue) Raise(StatusChanged, _values.Status.Value); break;
                    case PlcNodeIds.ActualWeight: if (_values.Weight.HasValue) Raise(WeightChanged, _values.Weight.Value); break;
                    case PlcNodeIds.EStopActive: if (_values.EmergencyStop.HasValue) Raise(EmergencyStopChanged, _values.EmergencyStop.Value); break;
                    case PlcNodeIds.GuardClosed: if (_values.GuardClosed.HasValue) Raise(GuardClosedChanged, _values.GuardClosed.Value); break;
                }
            }
        }

        private void OnKeepAlive(ISession session, KeepAliveEventArgs args)
        {
            if (ServiceResult.IsBad(args.Status)) SignalLoss(session, "PLC connection lost: " + args.Status);
        }

        private void SignalLoss(ISession? session, string error)
        {
            lock (_stateLock)
            {
                if (session == null || !ReferenceEquals(session, _session) || _connectionLost!.Task.IsCompleted) return;
                ChangeConnection("DISCONNECTED", error, true);
                _connectionLost.TrySetResult(true);
            }
        }

        private NodeId GetNodeId(string name)
        {
            var canonical = name.StartsWith("Line1.", StringComparison.Ordinal) || name == "Line1" ? name : "Line1." + name;
            var mapped = _tagMap.TryGetValue(canonical, out var identifier) ? identifier : canonical;
            if (mapped.StartsWith("i=", StringComparison.Ordinal) && uint.TryParse(mapped.Substring(2), out uint numeric))
                return new NodeId(numeric, _namespaceIndex);
            return new NodeId(mapped.StartsWith("s=", StringComparison.Ordinal) ? mapped.Substring(2) : mapped, _namespaceIndex);
        }

        public Task<DataValue> ReadTagAsync(string tagName) => WithSessionAsync(async (session, token) =>
        {
            var value = await session.ReadValueAsync(GetNodeId(tagName), token).ConfigureAwait(false);
            if (!StatusCode.IsGood(value.StatusCode)) throw new ServiceResultException(value.StatusCode);
            return value;
        });

        public Task WriteTagAsync(string tagName, bool value) => WriteTagAsync(tagName, new Variant(value));
        public Task WriteTagAsync(string tagName, Variant value) => WithSessionAsync(async (session, token) =>
        {
            await WriteCoreAsync(session, tagName, value, token).ConfigureAwait(false);
            return true;
        });

        private async Task WriteCoreAsync(ISession session, string tag, Variant value, CancellationToken token)
        {
            var response = await session.WriteAsync(null, new[] { new WriteValue
            {
                NodeId = GetNodeId(tag), AttributeId = Attributes.Value, Value = new DataValue(value)
            } }, token).ConfigureAwait(false);
            if (response.Results.Count != 1) throw new InvalidOperationException("PLC returned an invalid write response.");
            if (!StatusCode.IsGood(response.Results[0])) throw new ServiceResultException(response.Results[0]);
        }

        public Task StartBatchAsync() => CommandAsync(true);
        public Task StartBatchAsync(double targetWeight) => WithSessionAsync(async (session, token) =>
        {
            if (!Snapshot.CanStart) throw new InvalidOperationException("Start is not permitted by the PLC state/interlocks.");
            if (double.IsNaN(targetWeight) || double.IsInfinity(targetWeight) || targetWeight < 0.1 || targetWeight > 1000000)
                throw new ArgumentOutOfRangeException(nameof(targetWeight));
            var response = await session.CallAsync(null, new[] { new CallMethodRequest
            {
                ObjectId = GetNodeId("Line1"), MethodId = GetNodeId(PlcNodeIds.StartBatchWithTarget),
                InputArguments = new[] { new Variant(targetWeight) }
            } }, token).ConfigureAwait(false);
            if (response.Results.Count != 1 || !StatusCode.IsGood(response.Results[0].StatusCode))
                throw new ServiceResultException(response.Results.Count == 1 ? response.Results[0].StatusCode : StatusCodes.BadUnexpectedError);
            if (response.Results[0].OutputArguments.Count != 1 ||
                !response.Results[0].OutputArguments[0].TryGetValue(out bool accepted) || !accepted)
                throw new InvalidOperationException("PLC rejected the recipe/start handshake.");
            await RefreshCoreAsync(session, _generation, token).ConfigureAwait(false);
            return true;
        });

        public Task ResetFaultAsync() => WithSessionAsync(async (session, token) =>
        {
            if (!Snapshot.CanReset) throw new InvalidOperationException("Clear the E-stop and close the guard before resetting Fault.");
            await WriteCoreAsync(session, PlcNodeIds.ResetRequest, true, token).ConfigureAwait(false);
            await RefreshCoreAsync(session, _generation, token).ConfigureAwait(false);
            if (Snapshot.Status != 0) throw new InvalidOperationException("PLC did not confirm Idle after reset.");
            return true;
        });
        public Task StopBatchAsync() => CommandAsync(false);
        private Task CommandAsync(bool start) => WithSessionAsync(async (session, token) =>
        {
            var snapshot = Snapshot;
            if (start ? !snapshot.CanStart : !snapshot.CanStop)
                throw new InvalidOperationException("Command is not allowed by the current PLC state/interlocks/data quality.");
            await WriteCoreAsync(session, start ? PlcNodeIds.StartRequest : PlcNodeIds.StopRequest, true, token).ConfigureAwait(false);
            // A successful write only acknowledges the request. Read back the real PLC state.
            await RefreshCoreAsync(session, _generation, token).ConfigureAwait(false);
            return true;
        });

        public Task RefreshMachineValuesAsync() => WithSessionAsync(async (session, token) =>
        {
            await RefreshCoreAsync(session, _generation, token).ConfigureAwait(false);
            return true;
        });

        private async Task RefreshCoreAsync(ISession session, long generation, CancellationToken token)
        {
            var request = PlcValueCache.Tags.Select(tag => new ReadValueId { NodeId = GetNodeId(tag), AttributeId = Attributes.Value }).ToArray();
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, request, token).ConfigureAwait(false);
            if (response.Results.Count != request.Length) throw new InvalidOperationException("Invalid PLC read response.");
            for (int i = 0; i < request.Length; i++) AcceptValue(generation, PlcValueCache.Tags[i], response.Results[i]);
        }

        private async Task<T> WithSessionAsync<T>(Func<ISession, CancellationToken, Task<T>> operation)
        {
            // Pin the generation BEFORE waiting: queued commands must never run on a replacement session.
            ISession session;
            CancellationToken token;
            lock (_stateLock)
            {
                if (!IsConnected || _session == null) throw new InvalidOperationException("PLC is not connected.");
                session = _session;
                token = _runCancellation!.Token;
            }
            await _io.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!IsConnected || !ReferenceEquals(session, _session)) throw new InvalidOperationException("PLC session changed; request was not replayed.");
                return await operation(session, token).ConfigureAwait(false);
            }
            catch (ServiceResultException ex) when (IsConnectionError(ex.StatusCode))
            {
                SignalLoss(session, ex.Message);
                throw;
            }
            catch (Exception ex) { Raise(Diagnostic, "PLC operation failed: " + ex.Message); throw; }
            finally { _io.Release(); }
        }

        private static bool IsConnectionError(StatusCode code) =>
            code == StatusCodes.BadSessionClosed || code == StatusCodes.BadSessionIdInvalid ||
            code == StatusCodes.BadConnectionClosed || code == StatusCodes.BadCommunicationError ||
            code == StatusCodes.BadNotConnected || code == StatusCodes.BadSecureChannelClosed ||
            code == StatusCodes.BadTimeout || code == StatusCodes.BadServerNotConnected;

        private async Task CloseSessionAsync()
        {
            ISession? session;
            lock (_stateLock)
            {
                session = _session; _session = null; _namespaceIndex = 0; _generation++;
            }
            foreach (var item in _items) item.Notification -= OnNotification;
            _items.Clear();
            if (session != null)
            {
                session.KeepAlive -= OnKeepAlive;
                try
                {
                    // CloseSession(deleteSubscriptions=true) cleans up the server before local disposal.
                    await session.CloseAsync(2000, true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Session cleanup: " + ex.Message); }
                finally { session.Dispose(); }
            }
            _subscription?.Dispose();
            _subscription = null;
        }

        public async Task DisconnectAsync()
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try { await StopSupervisorAsync().ConfigureAwait(false); }
            finally { _lifecycle.Release(); }
        }

        private async Task StopSupervisorAsync()
        {
            _runCancellation?.Cancel();
            lock (_stateLock) _connectionLost?.TrySetResult(true);
            ChangeConnection("DISCONNECTED", null, true);
            if (_supervisor != null) await _supervisor.ConfigureAwait(false);
            _supervisor = null;
            _runCancellation?.Dispose();
            _runCancellation = null;
            ChangeConnection("DISCONNECTED", null, true);
        }

        public async ValueTask DisposeAsync()
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                await StopSupervisorAsync().ConfigureAwait(false);
            }
            finally { _lifecycle.Release(); }
        }

        private void ChangeConnection(string state, string? error, bool invalidate)
        {
            lock (_stateLock)
            {
                bool changed = _connection != state;
                _connection = state; _error = error;
                if (invalidate) _values.Clear();
                _revision++;
                PublishSnapshot();
                if (changed) Raise(ConnectionChanged, state);
            }
        }

        private PlcMachineSnapshot MakeSnapshot() => new(_revision, _connection, _error,
            _values.Status, _values.Weight, _values.EmergencyStop, _values.GuardClosed, _values.StateAndWeightCoherent);
        private void PublishSnapshot() => Raise(SnapshotChanged, MakeSnapshot());
        private static void Raise<T>(Action<T>? handlers, T value)
        {
            if (handlers == null) return;
            foreach (Action<T> handler in handlers.GetInvocationList())
                try { handler(value); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("PLC event consumer: " + ex); }
        }
    }
}

