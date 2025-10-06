namespace ATCPlanner.Models
{
    public class OptimizationStatistics
    {
        public double SuccessRate { get; set; }
        public int EmployeesWithShortage { get; set; }
        public int SlotsWithShortage { get; set; }
        public int SlotsWithExcess { get; set; }
        public double BreakCompliance { get; set; }
        public double RotationCompliance { get; set; }
        public double MaxWorkHourDifference { get; set; }
        public string SolutionStatus { get; set; }
        public double ObjectiveValue { get; set; }
        public int MissingExecutors { get; set; }
        public double WallTime { get; set; }

        // Pomoćne metode za formatiranje
        public string FormattedSuccessRate => $"{SuccessRate:F2}%";
        public string FormattedBreakCompliance => $"{BreakCompliance:F2}%";
        public string FormattedRotationCompliance => $"{RotationCompliance:F2}%";
        public string FormattedMaxWorkHourDifference => $"{MaxWorkHourDifference:F2} sati";
        public string FormattedObjectiveValue => $"{ObjectiveValue:F2}";
        public string FormattedWallTime => $"{WallTime:F2} sekundi";

        public OptimizationStatistics()
        {
            SolutionStatus = string.Empty;
        }
    }
}
