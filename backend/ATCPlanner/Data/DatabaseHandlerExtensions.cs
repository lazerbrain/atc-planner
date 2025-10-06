using ATCPlanner.Models.ML;
using Dapper;
using System.Data.Odbc;

namespace ATCPlanner.Data
{
    public static class DatabaseHandlerExtensions
    {
        private static string GetConnectionString(this DatabaseHandler handler)
        {
            return handler._connectionString;
        }

        /// <summary>
        /// Učitava istorijske rasporede iz baze podataka
        /// </summary>
        public static async Task<List<HistoricalScheduleData>> GetHistoricalSchedules(this DatabaseHandler handler, DateTime startDate, DateTime endDate, string? shiftType = null)
        {
            try
            {
                using var connection = new OdbcConnection(handler.GetConnectionString());
                await connection.OpenAsync();

                // Formatiraj datume za korišćenje u upitu
                string formattedStartDate = startDate.ToString("yyyy-MM-dd HH:mm:ss");
                string formattedEndDate = endDate.ToString("yyyy-MM-dd HH:mm:ss");
                string shiftFilter = shiftType != null ? $"AND rs.smena = '{shiftType}'" : "";

                // Upit za dobijanje podataka o rasporedima i dodelama
                // Povezujemo sa tabelom Smene uzimajući najnoviju definiciju smene
                string assignmentsQuery = $@"
                            SELECT 
                                rs.sifra AS ControllerId,
                                z.Prezime+' '+z.Ime AS ControllerName,
                                rs.smena AS ShiftType,
                                z.OperativnoRM AS ControllerType,
                                rs.datum AS ScheduleDate,
                                rs.datumOd AS TimeSlotStart,
                                rs.datumDo AS TimeSlotEnd,
                                rs.sektor AS AssignedSector,
                                rs.Flag,
                                CASE WHEN rs.sektor IS NULL OR rs.sektor = '' THEN 1 ELSE 0 END AS IsBreak,
                                CONVERT(varchar, rs.datum, 112) + rs.smena AS ScheduleKey,
                                CONVERT(varchar(8), sm.Od, 108) AS ShiftStart,
                                CONVERT(varchar(8), sm.Do, 108) AS ShiftEnd
                            FROM 
                                RasporedSektori rs
                                JOIN Zaposleni z on rs.sifra = z.sifra
                            LEFT JOIN (
                                SELECT sm1.*
                                FROM Smene sm1
                                INNER JOIN (
                                    SELECT OznakaSmene, MAX(VaziOd) AS MaxVaziOd
                                    FROM Smene
                                    WHERE VaziOd <= '{formattedEndDate}'
                                    GROUP BY OznakaSmene
                                ) sm2 ON sm1.OznakaSmene = sm2.OznakaSmene AND sm1.VaziOd = sm2.MaxVaziOd
                            ) sm ON rs.smena = sm.OznakaSmene
                            WHERE 
                                rs.datum BETWEEN '{formattedStartDate}' AND '{formattedEndDate}'
                                {shiftFilter}
                            ORDER BY 
                                rs.datum, rs.datumOd, rs.sifra";

                // Upit za dobijanje informacija o konfiguracijama,
                // koristi najnoviju definiciju smene za određivanje kojem rasporedu pripada konfiguracija
                string configQuery = $@"
                        SELECT DISTINCT
                            rk.datumOd,
                            rk.datumDo,
                            kTX.konfiguracija AS TXConfigCode,
                            kLU.konfiguracija AS LUConfigCode,
                            CONVERT(varchar, CAST(rk.datumOd AS DATE), 112) + 
                            (
                                SELECT TOP 1 sm.OznakaSmene
                                FROM Smene sm
                                WHERE CAST(CONVERT(varchar(8), CAST(rk.datumOd AS TIME), 108) AS TIME) BETWEEN sm.Od AND sm.Do
                                AND sm.VaziOd = (
                                    SELECT MAX(VaziOd) 
                                    FROM Smene 
                                    WHERE OznakaSmene = sm.OznakaSmene 
                                    AND VaziOd <= CAST(rk.datumOd AS DATE)
                                )
                            ) AS ScheduleKey
                        FROM 
                            raspored_konfiguracija rk
                        LEFT JOIN 
                            konfiguracije kTX ON rk.idKonfiguracijeTX = kTX.id
                        LEFT JOIN 
                            konfiguracije kLU ON rk.idKonfiguracijeLU = kLU.id
                        WHERE 
                            rk.datumOd BETWEEN '{formattedStartDate}' AND '{formattedEndDate}'";

                var assignmentRows = await connection.QueryAsync(assignmentsQuery);
                var configRows = await connection.QueryAsync(configQuery);

                // organizovati podatke po kljucu rasporeda (datum+smena)
                var schedulesByKey = new Dictionary<string, HistoricalScheduleData>();
                var result = new List<HistoricalScheduleData>();

                // kreiranje objekata rasporeda za svaki jedinstveni datum+smena
                foreach (var row in assignmentRows)
                {
                    string scheduleKey = row.ScheduleKey.ToString();
                    DateTime scheduleDate = row.ScheduleDate is DateTime date ? date : DateTime.Parse(row.ScheduleDate.ToString());
                    shiftType = row.ShiftType.ToString();

                    if (!schedulesByKey.TryGetValue(scheduleKey, out var schedule))
                    {
                        schedule = new HistoricalScheduleData
                        {
                            ScheduleId = schedulesByKey.Count + 1,
                            Date = scheduleDate,
                            ShiftType = shiftType,
                            Assignments = new List<ScheduleAssignment>(),
                            Configurations = new List<string>()
                        };

                        schedulesByKey[scheduleKey] = schedule;
                        result.Add(schedule);
                    }

                    // Dodajemo dodelu kontrolora
                    DateTime timeSlotStart = row.TimeSlotStart is DateTime startTime ? startTime : DateTime.Parse(row.TimeSlotStart.ToString());

                    schedule.Assignments.Add(new ScheduleAssignment
                    {
                        ControllerId = row.ControllerId.ToString(),
                        ControllerName = row.ControllerName?.ToString() ?? row.ControllerId.ToString(),
                        AssignedSector = row.AssignedSector?.ToString(),
                        TimeSlot = timeSlotStart,
                        IsBreak = Convert.ToBoolean(row.IsBreak),
                        ORM = row.ControllerType?.ToString(),
                        FlagS = row.Flag?.ToString() == "S",
                        WasManuallyAdjusted = false // Pretpostavljamo da inicijalno nisu ručno prilagođeni
                    });

                }

                // Dodajemo informacije o konfiguracijama
                foreach (var row in configRows)
                {
                    string scheduleKey = row.ScheduleKey?.ToString();

                    if (!string.IsNullOrEmpty(scheduleKey) && schedulesByKey.TryGetValue(scheduleKey, out var schedule))
                    {
                        // Dodaj TX konfiguraciju ako postoji
                        if (row.TXConfigCode != null)
                        {
                            string txConfig = $"TX:{row.TXConfigCode}";
                            if (!schedule.Configurations.Contains(txConfig))
                            {
                                schedule.Configurations.Add(txConfig);
                            }
                        }

                        // Dodaj LU konfiguraciju ako postoji
                        if (row.LUConfigCode != null)
                        {
                            string luConfig = $"LU:{row.LUConfigCode}";
                            if (!schedule.Configurations.Contains(luConfig))
                            {
                                schedule.Configurations.Add(luConfig);
                            }
                        }
                    }
                }

                return result;

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetHistoricalSchedules: {ex.Message}");
                return new List<HistoricalScheduleData>();
            }
        }
    }
}
