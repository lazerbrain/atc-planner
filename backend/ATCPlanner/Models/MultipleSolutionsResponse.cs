namespace ATCPlanner.Models
{
    public class MultipleSolutionsResponse
    {
        public List<OptimizationResponse> AllSolutions { get; set; } = new();
        public OptimizationResponse BestSolution { get; set; } = new();
        public List<OptimizationResponse> UniqueSolutions { get; set; } = new();
        public MultipleSolutionsStatistics Statistics { get; set; } = new();
    }
}
