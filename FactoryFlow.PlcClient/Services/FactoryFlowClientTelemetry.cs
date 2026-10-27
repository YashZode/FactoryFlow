using Opc.Ua;

namespace FactoryFlow.PlcClient.Services
{
    public sealed class FactoryFlowClientTelemetry
        : TelemetryContextBase
    {
        public FactoryFlowClientTelemetry()
            : base(
                Microsoft.Extensions.Logging.Abstractions
                    .NullLoggerFactory.Instance)
        {
        }
    }
}