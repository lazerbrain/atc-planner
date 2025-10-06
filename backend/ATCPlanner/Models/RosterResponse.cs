namespace ATCPlanner.Models
{
    public class RosterResponse
    {
        public required InitialRoster InitialRoster { get; set; }
        public required OptimizedRoster OptimizedRoster { get; set; }
    }

        public class InitialRoster
        {
            public DateTime ShiftStart { get; set; }
            public DateTime ShiftEnd { get; set; }
            public List<RosterEntry> Roster { get; set; } = [];
            public List<ConfigurationEntry> ConfigurationSchedule { get; set; } = [];

        }

        public class RosterEntry
        {
            public string? Sifra { get; set; }
            public string? PrezimeIme { get; set; }
            public string? Smena { get; set; }
            public string? ORM { get; set; }
            public int Redosled { get; set; }
            public string? Par { get; set; }
            public DateTime Datum { get; set; }
            public DateTime VremeStart { get; set; }
            public DateTime DatumOd { get; set; }
            public DateTime DatumDo { get; set; }
            public string? Sektor { get; set; }
            public string? Flag { get; set; }
        }

        public class ConfigurationEntry
        {
            public DateTime DatumOd { get; set; }
            public DateTime DatumDo { get; set; }
            public required string OznakaKonfiguracije { get; set; }
        }

        public class OptimizedRoster
        {
            public DateTime ShiftStart { get; set; }
            public DateTime ShiftEnd { get; set; }
            public List<RosterEntry> Roster { get; set; } = [];
            public List<ConfigurationEntry> ConfigurationSchedule { get; set; } = [];

        }

    }

