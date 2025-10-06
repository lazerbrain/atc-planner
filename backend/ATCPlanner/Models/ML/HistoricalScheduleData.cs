namespace ATCPlanner.Models.ML
{

    /// <summary>
    /// Klase za istorijske podatke
    /// </summary>
    public class HistoricalScheduleData
    {
        public int ScheduleId { get; set; }
        public DateTime Date { get; set; }
        public string ShiftType { get; set; } = "";
        public List<ScheduleAssignment> Assignments { get; set; } = new List<ScheduleAssignment>();
        public List<string> Configurations { get; set; } = new List<string>();

        // za tezinsko ucenje
        public float Weight { get; set; } = 1.0f;

    }

    public class ScheduleAssignment
    {
        public string ControllerId { get; set; } = "";
        public string ControllerName { get; set; } = "";
        public string? AssignedSector { get; set; }
        public DateTime TimeSlot { get; set; }
        public bool IsBreak { get; set; }
        public bool WasManuallyAdjusted { get; set; }
        public string? ORM { get; set; }
        public bool FlagS { get; set; }

    }

    public class ControllerBasicInfo
    {
        public string ControllerId { get; set; }
        public string ControllerType { get; set; } = "KL";
    }
}
