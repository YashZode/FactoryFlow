using FactoryFlow.Core.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace FactoryFlow.Core.Models
{
    public class Machine
    {
        public int MachineId { get; set; }
        public string Name { get; set; } = "";
        public MachineStatus Status { get; set; }
        public double Temperature { get; set; }
        public bool IsConnected { get; set; }
    }
}

