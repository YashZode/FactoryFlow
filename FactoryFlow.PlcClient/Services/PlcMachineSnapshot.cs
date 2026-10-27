namespace FactoryFlow.PlcClient.Services
{
    /// <summary>An immutable view of one connection generation. Null means unknown/invalid.</summary>
    public sealed class PlcMachineSnapshot
    {
        public long Revision { get; }
        public string Connection { get; }
        public string? Error { get; }
        public int? Status { get; }
        public double? Weight { get; }
        public bool? EmergencyStop { get; }
        public bool? GuardClosed { get; }
        public bool StateAndWeightCoherent { get; }
        public bool IsConnected => Connection == "CONNECTED";
        public bool ValuesValid => Status.HasValue && Weight.HasValue && EmergencyStop.HasValue && GuardClosed.HasValue;
        public bool CanStart => IsConnected && ValuesValid && (Status == 0 || Status == 2) && EmergencyStop == false && GuardClosed == true;
        public bool CanStop => IsConnected && ValuesValid && Status == 1;
        public bool CanReset => IsConnected && ValuesValid && Status == 3 && EmergencyStop == false && GuardClosed == true;

        internal PlcMachineSnapshot(long revision, string connection, string? error,
            int? status, double? weight, bool? emergencyStop, bool? guardClosed, bool stateAndWeightCoherent = false)
        {
            Revision = revision;
            Connection = connection;
            Error = error;
            Status = status;
            Weight = weight;
            EmergencyStop = emergencyStop;
            GuardClosed = guardClosed;
            StateAndWeightCoherent = stateAndWeightCoherent;
        }
    }
}
