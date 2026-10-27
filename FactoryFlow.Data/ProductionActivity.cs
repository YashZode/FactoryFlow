using System;
using System.Runtime.Serialization;

namespace FactoryFlow.Data
{
    // The same record is stored locally until its SQL transaction commits.
    [DataContract]
    public sealed class ProductionActivity
    {
        [DataMember] public Guid EventKey { get; set; } = Guid.NewGuid();
        [DataMember] public Guid? BatchCorrelationId { get; set; }
        [DataMember] public int LineId { get; set; }
        [DataMember] public string EventType { get; set; } = "";
        [DataMember] public string? EventValue { get; set; }
        [DataMember] public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
        [DataMember] public string? BatchStatus { get; set; }
        [DataMember] public string? LineStatus { get; set; }
        [DataMember] public double? FinalWeight { get; set; }
        [DataMember] public bool EndsBatch { get; set; }
        [DataMember] public string? OperatorAccount { get; set; }
        [DataMember] public string Source { get; set; } = "HMI";
    }

    public sealed class ProductionObservation
    {
        public long Revision { get; set; }
        public string Connection { get; set; } = "DISCONNECTED";
        public int? Status { get; set; }
        public double? Weight { get; set; }
        public bool? EmergencyStop { get; set; }
        public bool? GuardClosed { get; set; }
        public bool StateAndWeightCoherent { get; set; }
    }
}
