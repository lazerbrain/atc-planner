namespace ATCPlanner.Models
{
    public class OrToolsOptimizationSession
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string Smena { get; set; } = string.Empty;
        public DateTime Datum { get; set; }
        public List<OrToolsOptimizationRun> OptimizationRuns { get; set; } = new();
        public int CurrentRunIndex { get; set; } = -1;

        public OrToolsOptimizationRun? GetCurrentRun()
        {
            if (CurrentRunIndex >= 0 && CurrentRunIndex < OptimizationRuns.Count)
            {
                return OptimizationRuns[CurrentRunIndex];
            }
            return null;
        }

        public bool CanNavigateNext()
        {
            return CurrentRunIndex < OptimizationRuns.Count - 1;
        }

        public bool CanNavigatePrevious()
        {
            return CurrentRunIndex > 0;
        }

        public OrToolsOptimizationRun? NavigateNext()
        {
            if (CanNavigateNext())
            {
                CurrentRunIndex++;
                return GetCurrentRun();
            }
            return null;
        }

        public OrToolsOptimizationRun? NavigatePrevious()
        {
            if (CanNavigatePrevious())
            {
                CurrentRunIndex--;
                return GetCurrentRun();
            }
            return null;
        }

        public void AddOptimizationRun(OrToolsOptimizationRun run)
        {
            run.Id = OptimizationRuns.Count + 1;
            OptimizationRuns.Add(run);
            CurrentRunIndex = OptimizationRuns.Count - 1;
        }

        public OrToolsOptimizationRun? GetBestRun()
        {
            return OptimizationRuns
                .Where(r => r.Response.Statistics.SolutionStatus == "Optimal" || r.Response.Statistics.SolutionStatus == "Feasible")
                .OrderByDescending(r => r.Response.Statistics.SuccessRate)
                .ThenBy(r => r.Response.Statistics.SlotsWithShortage)
                .ThenBy(r => r.ObjectiveValue)
                .FirstOrDefault();
        }
    }
}
