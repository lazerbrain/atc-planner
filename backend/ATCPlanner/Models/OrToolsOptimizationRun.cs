namespace ATCPlanner.Models
{
    public class OrToolsOptimizationRun
    {
        public int Id { get; set; } 
        public DateTime CreatedAt { get; set; }
        public OptimizationRequest Request { get; set; } = new();
        public OptimizationResponse Response { get; set; } = new();
        public string? Description {  get; set; }
        public OrToolsParameters Parameters { get; set; } = new();
        public string SolverStatus { get; set; } = string.Empty;
        public double ObjectiveValue { get; set; }
        public double SolvingTime { get; set; }
    }
}
