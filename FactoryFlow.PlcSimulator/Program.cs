using FactoryFlow.PlcSimulator;
using Opc.Ua;
using Opc.Ua.Configuration;

Console.WriteLine("=== FactoryFlow OPC UA PLC Simulator ===");
var port = 4840;
if (args.Length != 0 && (args.Length != 2 || args[0] != "--port" || !int.TryParse(args[1], out port) || port < 1 || port > 65535))
    throw new ArgumentException("Usage: FactoryFlow.PlcSimulator [--port 4840]");
var telemetry = new SimulatorTelemetry();
var pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki");
var config = new ApplicationConfiguration(telemetry)
{
    ApplicationName = "FactoryFlow.PlcSimulator",
    ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:FactoryFlow:PlcSimulator",
    ProductUri = "urn:factoryflow:plc-simulator",
    ApplicationType = ApplicationType.Server,
    SecurityConfiguration = new SecurityConfiguration
    {
        ApplicationCertificates = [new CertificateIdentifier
        {
            StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "own"),
            SubjectName = "CN=FactoryFlow.PlcSimulator, O=FactoryFlow",
            CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
        }],
        TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "trusted") },
        TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "issuers") },
        RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "rejected") },
        AutoAcceptUntrustedCertificates = false,
        RejectSHA1SignedCertificates = true,
        MinimumCertificateKeySize = 2048
    },
    TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
    ServerConfiguration = new ServerConfiguration
    {
        BaseAddresses = [$"opc.tcp://localhost:{port}"],
        SecurityPolicies = [new ServerSecurityPolicy
        {
            SecurityMode = MessageSecurityMode.SignAndEncrypt,
            SecurityPolicyUri = SecurityPolicies.Basic256Sha256
        }],
        UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
        DiagnosticsEnabled = true,
        MaxRegistrationInterval = 0
    }
};
await config.ValidateAsync(ApplicationType.Server);
await using var application = new ApplicationInstance(config, telemetry);
if (!await application.CheckApplicationInstanceCertificatesAsync(true))
    throw new InvalidOperationException("Cannot create or validate the application certificate.");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var server = new FactoryFlowServer(new SimulatedPlc(), telemetry);
try
{
    await application.StartAsync(server);
    foreach (var endpoint in (server.GetEndpoints().ToArray() ?? []))
        Console.WriteLine($"Endpoint: {endpoint.EndpointUrl} | {endpoint.SecurityMode} | {endpoint.SecurityPolicyUri}");
    Console.WriteLine($"Namespace URI: {PlcTagNames.NamespaceUri}");
    Console.WriteLine($"Certificate stores: {pkiRoot}");
    Console.WriteLine("Trust the client certificate in pki/trusted/certs, then reconnect. Certificate verification is enabled.");
    Console.WriteLine("Simulation tick: 250 ms. Press Ctrl+C to stop.");
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    while (await timer.WaitForNextTickAsync(cancellation.Token))
        server.FactoryFlowNodes!.TickAndPublish();
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
finally
{
    await application.StopAsync();
}







