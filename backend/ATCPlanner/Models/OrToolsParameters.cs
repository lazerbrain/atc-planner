namespace ATCPlanner.Models
{
    public class OrToolsParameters
    {
        public int MaxTimeInSeconds { get; set; } = 60;
        public int? MaxOptimalSolutions { get; set; }   
        public int? MaxZeroShortageSlots { get; set; }
        public bool UseLNS { get; set; }
        public bool UseManualAssignments { get; set; } = true;
        public List<string> SelectedOperativeWorkplaces { get; set; } = new();
        public List<string> SelectedEmployees { get; set; } = new();
        public string SolveParameters { get; set; } = string.Empty;
        public int? RandomSeed { get; set; }
        public bool UseRandomization { get; set; } = true; 
    }
}
