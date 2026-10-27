namespace FactoryFlow.Core.Models
{
    public sealed class OperatorProfile
    {
        public string WindowsAccount { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = "Operator";
        public bool IsEnabled { get; set; }
        public bool IsSupervisor => IsEnabled && Role == "Supervisor";
    }
}

