using Opc.Ua;
using Opc.Ua.Server;

namespace FactoryFlow.PlcSimulator;

public sealed class FactoryFlowServer(SimulatedPlc plc, ITelemetryContext telemetry) : StandardServer(telemetry)
{
    public FactoryFlowNodeManager? FactoryFlowNodes { get; private set; }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        FactoryFlowNodes = new FactoryFlowNodeManager(server, configuration, plc);
        return new MasterNodeManager(server, configuration, null,
            new INodeManager[] { FactoryFlowNodes });
    }
}
