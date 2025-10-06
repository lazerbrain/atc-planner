namespace ATCPlanner.Models
{
    public class Controller
    {
        public required string Code { get; set; } // sifra kontrolora
        public string? Name { get; set; } // ime kontrolora
        public string? Type { get; set; } // ORM
        public string? Shift { get; set; }
        public string? Core { get; set; } // koji klaster moze da pokriva
        public string? Spouse { get; set; }
        public bool? IsYellow { get; set; } // da li je zut (dosao sa GO)
        public bool? IsSupervisor { get; set; }
        public bool? IsShiftLeader { get; set; }
    }
}
