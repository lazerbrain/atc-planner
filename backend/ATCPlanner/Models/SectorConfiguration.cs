namespace ATCPlanner.Models
{
    public class SectorConfiguration
    {
        public required string ConfigCode { get; set; } // sifra konfiguracije
        public DateTime Start { get; set; } // vreme pocetka
        public DateTime End { get; set; }  // vreme zavrsetka

        public required string Type { get; set; } // klaster
        public required List<string> Sectors { get; set; }
    }
}
