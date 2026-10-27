using System;
using System.Collections.Generic;
using System.Text;

namespace FactoryFlow.Core.Models
{
    public class ProductionBatch
    {
        public int BatchId { get; set; }
        public int RecipeId { get; set; }
        public int LineId { get; set; }
        public string Status { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}

