namespace ATCPlanner.Models
{
    public class ControllerInfo
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ShiftType { get; set;} = string.Empty;
        public string ORM { get; set; } = string.Empty;
        public DateTime VremeStart { get; set; }
        public DateTime ShiftStart { get; set; }
        public DateTime ShiftEnd { get; set; }
        public bool IsShiftLeader { get; set; } // sef smene
        public bool IsSupervisor { get; set; } // supervizor;
        public bool HasLicense { get; set; } // kontrolorska dozvola
    }
}
