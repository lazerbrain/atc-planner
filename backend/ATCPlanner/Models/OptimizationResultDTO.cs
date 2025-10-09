namespace ATCPlanner.Models
{
    public class OptimizationResultDTO
    {
        public string? Sifra { get; set; }

        public string? PrezimeIme { get; set; }
        public string? Smena { get; set; }
        public DateTime Datum { get; set; }
        public DateTime DatumOd { get; set; }
        public DateTime DatumDo { get; set; }
        public string? Sektor { get; set; }
        public string? ORM { get; set; }
        public string? Flag { get; set; }
        public DateTime VremeStart { get; set; }
        public int Redosled { get; set; }
        public string? Par { get; set; }
    }
}