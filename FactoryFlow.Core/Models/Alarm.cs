using System;
using System.Collections.Generic;
using System.Text;

namespace FactoryFlow.Core.Models
{
    public class Alarm
    {
        public int AlarmId { get; set; }

        public int MachineId { get; set; }

        public int AlarmCode { get; set; }

        public string Message { get; set; } = "";

        public DateTime RaisedAt { get; set; }
    }
}

