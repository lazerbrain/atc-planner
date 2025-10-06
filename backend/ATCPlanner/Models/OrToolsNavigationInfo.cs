namespace ATCPlanner.Models
{
    public class OrToolsNavigationInfo
    {
        public bool CanNavigatePrevious { get; set; }   
        public bool CanNavigateNext { get; set; }
        public int CurrentRunNumber { get; set; }   
        public int TotalRuns { get; set; }
        public string CurrentRunDescription { get; set; } = string.Empty;
        public DateTime CurrentRunTimestamp { get; set; }
        public string SolverStatus { get; set; } = string.Empty;
        public double ObjectiveValue { get; set; }  
        public double SuccessRate { get; set; }
        public int SlotsWithShortage { get; set; }  
    }
}
