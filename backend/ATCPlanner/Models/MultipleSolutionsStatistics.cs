namespace ATCPlanner.Models
{
    public class MultipleSolutionsStatistics
    {
        public int TotalSolutionsFound { get; set; }
        public int UniqueSolutionsFound { get; set; }
        public double BestObjectiveValue { get; set; }
        public string SolverStatus { get; set; } = string.Empty;
    }
}
