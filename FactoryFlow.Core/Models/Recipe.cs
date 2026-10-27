using System;
using System.Collections.Generic;
using System.Text;

namespace FactoryFlow.Core.Models
{
    public class Recipe
    {
        public int RecipeId { get; set; }

        public string RecipeName { get; set; } = "";

        public double TargetWeight { get; set; }
        public bool IsActive { get; set; } = true;
        public string DisplayName => $"{RecipeName} - {TargetWeight:0.##} kg";
    }
}


