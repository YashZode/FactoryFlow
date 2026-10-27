namespace FactoryFlow.Core.Models
{
    public class PlcTag
    {
        public string NodeId { get; set; } = "";

        public string Name { get; set; } = "";

        public object? Value { get; set; }

        public DateTime Timestamp { get; set; }

        public string Quality { get; set; } = "";
    }
}

