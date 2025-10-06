namespace ATCPlanner.Models
{
    public class ConfigurationDTO
    {
        public int Id { get; set; }
        public string Konfiguracija { get; set; } = string.Empty;
        public string Cluster { get; set; } = string.Empty;
        public string? Naziv { get; set; }
        public string? Vrsta { get; set; }
        public DateTime VaziOd { get; set; }
        public DateTime? VaziDo { get; set; }
        public List<string> Sektori { get; set; } = new List<string>();
    }
}
