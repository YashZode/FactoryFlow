using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

var telemetry = new FactoryFlow.PlcSimulator.SimulatorTelemetry();
var pki = Path.Combine(AppContext.BaseDirectory, "pki");
var config = new ApplicationConfiguration(telemetry)
{
    ApplicationName = "FactoryFlow.SmokeTest",
    ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:FactoryFlow:SmokeTest",
    ApplicationType = ApplicationType.Client,
    SecurityConfiguration = new SecurityConfiguration
    {
        ApplicationCertificates = [new CertificateIdentifier
        {
            StoreType = "Directory", StorePath = Path.Combine(pki, "own"),
            SubjectName = "CN=FactoryFlow.SmokeTest",
            CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
        }],
        TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "trusted") },
        TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "issuers") },
        RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = "Directory", StorePath = Path.Combine(pki, "rejected") },
        AutoAcceptUntrustedCertificates = false
    },
    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
    TransportQuotas = new TransportQuotas { OperationTimeout = 10000 }
};
await config.ValidateAsync(ApplicationType.Client);
await using var application = new ApplicationInstance(config, telemetry);
if (!await application.CheckApplicationInstanceCertificatesAsync(true)) throw new Exception("Client certificate failed.");
if (args.Contains("--prepare"))
{
    Console.WriteLine($"Client PKI: {pki}");
    return;
}
var endpoint = await CoreClientUtils.SelectEndpointAsync(config, "opc.tcp://localhost:4840", true, telemetry);
using var session = await new DefaultSessionFactory(telemetry).CreateAsync(config,
    new ConfiguredEndpoint(null, endpoint!, EndpointConfigurationExtensions.Create(config)), false,
    "FactoryFlow smoke test", 60000, new UserIdentity(new AnonymousIdentityToken()), default);
Console.WriteLine($"Connected: {endpoint!.EndpointUrl} | {endpoint.SecurityMode}");
var ns = session.NamespaceUris.GetIndex("urn:factoryflow:plc:line1");
Check(ns > 0, "Namespace resolved");
NodeId Id(string tag) => new($"Line1.{tag}", (ushort)ns);
async Task<DataValue> Read(string tag) => await session.ReadValueAsync(Id(tag));
async Task Write(string tag, Variant value, StatusCode expected = default)
{
    var response = await session.WriteAsync(null, [new WriteValue
    {
        NodeId = Id(tag), AttributeId = Attributes.Value, Value = new DataValue(value)
    }], default);
    Check(response.Results[0] == expected, $"Write {tag}: {response.Results[0]}");
}
async Task State(int expected)
{
    var value = await Read("Status");
    Check(value.WrappedValue.TryGetValue(out int state) && state == expected, $"Status = {expected}");
}
await State(0);
foreach (var tag in new[] { "Status", "ActualWeight", "StartRequest", "StopRequest", "EStopActive", "GuardClosed" })
{
    var value = await Read(tag);
    Check(StatusCode.IsGood(value.StatusCode), $"Read {Id(tag)} = {value}");
}
await Write("Status", 1, StatusCodes.BadNotWritable);
await Write("ActualWeight", 12.0, StatusCodes.BadNotWritable);
await Write("StartRequest", 1, StatusCodes.BadTypeMismatch);
await Write("EStopActive", true);
await Write("StartRequest", true);
await State(0);
await Write("EStopActive", false);
await Write("GuardClosed", false);
await Write("StartRequest", true);
await State(0);
await Write("GuardClosed", true);

var notifications = new System.Collections.Concurrent.ConcurrentQueue<DataValue>();
using var subscription = new Subscription(session.DefaultSubscription)
{
    PublishingInterval = 100, PublishingEnabled = true
};
var item = new MonitoredItem(telemetry)
{
    StartNodeId = Id("ActualWeight"), AttributeId = Attributes.Value,
    SamplingInterval = 100, QueueSize = 100, DiscardOldest = true,
    Filter = new DataChangeFilter { Trigger = DataChangeTrigger.StatusValueTimestamp }
};
item.Notification += (_, _) => { foreach (var value in item.DequeueValues()) notifications.Enqueue(value); };
subscription.AddItem(item);
session.AddSubscription(subscription);
await subscription.CreateAsync();
await Write("StartRequest", true);
await State(1);
await Task.Delay(1300);
Check((await Read("ActualWeight")).WrappedValue.TryGetValue(out double weight) && weight > 0, "Tick increased weight");
Check((await Read("StartRequest")).WrappedValue.TryGetValue(out bool start) && !start, "Start pulse reset");
Check(notifications.Count >= 2, "Subscription delivered multiple updates");
var samples = notifications.ToArray();
Check(samples.First().SourceTimestamp != samples.Last().SourceTimestamp, "Subscription timestamps advance");
await Write("StopRequest", true);
await State(0);
await Write("StartRequest", true);
await State(1);
await Write("GuardClosed", false);
await Task.Delay(600);
await State(3);
await Write("GuardClosed", true);
await Write("StopRequest", true);
await State(3); // Existing state machine deliberately cannot reset Fault.
await subscription.DeleteAsync(true);
await session.CloseAsync();
Console.WriteLine("PASS: encrypted session, all six nodes, access/type checks, commands, interlocks, ticks, subscriptions and timestamps.");

static void Check(bool passed, string description)
{
    if (!passed) throw new Exception($"FAIL: {description}");
    Console.WriteLine($"PASS: {description}");
}
internal sealed class TestTelemetry() : TelemetryContextBase(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);



