namespace ATCPlanner.Models
{
    public class OptimizationResponse
    {
        public List<OptimizationResultDTO> OptimizedResults { get; set; }
        public List<OptimizationResultDTO> NonOptimizedResults { get; set; }
        public List<OptimizationResultDTO> AllResults { get; set; }
        public OptimizationStatistics Statistics { get; set; }
        public Dictionary<string, int> SlotShortages { get; set; }
        public List<InitialAssignmentDTO> InitialAssignments { get; set; }
        public Dictionary<string, string> ConfigurationLabels { get; set; }

        public OptimizationResponse()
        {
            OptimizedResults = [];
            NonOptimizedResults = [];
            AllResults = [];
            Statistics = new();
            SlotShortages = [];
            InitialAssignments = [];
            ConfigurationLabels = [];
        }
    }

    public class InitialAssignmentDTO
    {
        public string Sifra { get; set; } = string.Empty;
        public string Smena { get; set; } = string.Empty;
        public string Flag { get; set; } = string.Empty;
        public DateTime DatumOd { get; set; }
        public DateTime DatumDo { get; set; }
    }
}
