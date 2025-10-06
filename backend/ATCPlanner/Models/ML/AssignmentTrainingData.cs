using Microsoft.ML.Data;

namespace ATCPlanner.Models.ML
{
    /// <summary>
    /// Klase za ML model
    /// </summary>
    public class AssignmentTrainingData
    {
        public string ControllerId { get; set; } = "";
        public string ControllerType { get; set; } = "";
        public string SectorId { get; set; } = "";
        public string BaseSector { get; set; } = "";
        public string Position { get; set; } = "";
        public string TimeSlot { get; set; } = "";
        public string ShiftType { get; set; } = "";
        public string DayOfWeek { get; set; } = "";

        // specificni podaci za medjusmene
        public bool IsInterShift { get; set; }  
        public string ShiftContext { get; set; } = "";
        public int MinutesFromShiftStart { get; set; }
        public int MinutesToShiftEnd { get; set; }
        public bool IsBreak { get; set; }

        // za ponderisano ucenje
        public float Weight { get; set; } = 1.0f;

        // ciljna varijabla
        public float IsPreferred { get; set; } = 0.0f;
    }

    public class AssignmentPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool PredictedLabel { get; set; }

        [ColumnName("Probability")]
        public float Probability { get; set; }
    }
}
