using System.Linq.Expressions;

namespace ATCPlanner.Models
{
    public class OptimizationRequest
    {
        public string? Smena { get; set; }
        public DateTime Datum { get; set; }
        public int MaxExecTime { get; set; }
        public int? MaxOptimalSolutions { get; set; }
        public int? MaxZeroShortageSlots { get; set; }
        public bool UseLNS { get; set; }
        public bool UseSimulatedAnnealing { get; set; } = false; // default je OR-Tools
        public bool UseManualAssignments { get; set; } = true;
        public List<string>? SelectedOperativeWorkplaces { get; set; } = [];
        public List<string>? SelectedEmployees { get; set; } = [];
        public List<ConfigurationUpdate>? UpdatedConfigurations { get; set; } = [];
        public int? RandomSeed { get; set; }
        public bool? UseRandomization { get; set; } = true;
    }
}
