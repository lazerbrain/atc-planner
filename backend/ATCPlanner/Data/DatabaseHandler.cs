using Microsoft.Data.SqlClient;
using System.Data;
using System.Data.Odbc;
using Dapper;
using ATCPlanner.Models.ML;
using System.Runtime.CompilerServices;
using ATCPlanner.Models;

namespace ATCPlanner.Data
{
    public class DatabaseHandler
    {
        private readonly ILogger<DatabaseHandler> _logger;

        public readonly string _connectionString;

        public DatabaseHandler(IConfiguration configuration, ILogger<DatabaseHandler> logger)
        {
            _logger = logger;

            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");


            _logger.LogInformation($"Initialized DatabaseHandler with server: {configuration["ConnectionStrings:DefaultConnection:Server"]}");
        }

        private SqlConnection GetConnection()
        {
            return new SqlConnection(_connectionString);
        }

        public async Task<List<Dictionary<string, object>>> ExecuteQueryAsync(string query, params object[] parameters)
        {
            // Formatiramo query sa parametrima
            var formattedQuery = string.Format(query, parameters.Select(p =>
                p is DateTime dt ? $"'{dt:yyyy-MM-dd HH:mm:ss}'" :
                p is string ? $"'{p}'" :
                p?.ToString() ?? "NULL"
            ).ToArray());

            return await ExecuteQueryAsync(formattedQuery);
        }

        public async Task<(DateTime? Start, DateTime? End)> GetSmenaDurationAsync(DateTime datumUlaz, string smena)
        {
            // Modifikujemo query da koristi string.Format za ODBC parametre
            var query = string.Format(@"
                       DECLARE @datumStart DATETIME, @datumEnd DATETIME;
                       EXEC spVratiTrajanjeSmene 
                           @datumUlaz = '{0}', 
                           @smena = '{1}', 
                           @datumStart = @datumStart OUTPUT, 
                           @datumEnd = @datumEnd OUTPUT;
                       SELECT @datumStart AS datumStart, @datumEnd AS datumEnd;",
                datumUlaz.ToString("yyyy-MM-dd HH:mm:ss"),  // Dodajemo pun datetime format
                smena);

            try
            {
                using var connection = new OdbcConnection(_connectionString);
                await connection.OpenAsync();

                var result = await connection.QueryAsync(query);

                if (result.Any())
                {
                    var row = result.First();
                    return ((DateTime?)row.datumStart, (DateTime?)row.datumEnd);
                }

                _logger.LogWarning($"No results returned for datum_ulaz: {datumUlaz:yyyy-MM-dd HH:mm:ss}, smena: {smena}");
                return (null, null);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in GetSmenaDurationAsync: {ex.Message}");
                _logger.LogError($"Query executed: {query}");
                _logger.LogError($"Parameters: datumUlaz={datumUlaz:yyyy-MM-dd HH:mm:ss}, smena={smena}");
                return (null, null);
            }
        }

        public async Task<DataTable?> FillByDatumAsync(DateTime datumStart, DateTime datumEnd)
        {
            // Modifikujemo query da koristi string.Format za ODBC parametre
            var query = string.Format(
                "EXEC spFillByDatum_Solver @datumOd = '{0}', @datumDo = '{1}';",
                datumStart.ToString("yyyy-MM-dd HH:mm:ss"),
                datumEnd.ToString("yyyy-MM-dd HH:mm:ss")
            );

            try
            {
                using var connection = new OdbcConnection(_connectionString);
                await connection.OpenAsync();

                var result = await connection.QueryAsync(query);
                _logger.LogInformation($"spFillByDatum executed successfully for period {datumStart:yyyy-MM-dd HH:mm:ss} to {datumEnd:yyyy-MM-dd HH:mm:ss}");

                var dataTable = new DataTable();
                var firstRow = result.FirstOrDefault();

                if (firstRow != null)
                {
                    // Dodajemo kolone
                    foreach (var column in ((IDictionary<string, object>)firstRow).Keys)
                    {
                        dataTable.Columns.Add(column);
                    }

                    // Dodajemo redove
                    foreach (var row in result)
                    {
                        var dataRow = dataTable.NewRow();
                        foreach (var item in (IDictionary<string, object>)row)
                        {
                            dataRow[item.Key] = item.Value ?? DBNull.Value; // Rukovanje null vrednostima
                        }
                        dataTable.Rows.Add(dataRow);
                    }

                    _logger.LogInformation($"Created DataTable with {dataTable.Rows.Count} rows and {dataTable.Columns.Count} columns");
                }
                else
                {
                    _logger.LogWarning($"No data returned for period {datumStart:yyyy-MM-dd HH:mm:ss} to {datumEnd:yyyy-MM-dd HH:mm:ss}");
                }

                return dataTable;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing spFillByDatum: {ex.Message}");
                _logger.LogError($"Query executed: {query}");
                _logger.LogError($"Parameters: datumStart={datumStart:yyyy-MM-dd HH:mm:ss}, datumEnd={datumEnd:yyyy-MM-dd HH:mm:ss}");
                return null;
            }
        }

        public async Task<DataTable> LoadTimeSlotConfigurationsAsync(DateTime datumStart, DateTime datumEnd)
        {
            try
            {
                using var connection = new OdbcConnection(_connectionString);
                await connection.OpenAsync();

                // Prvo proveravamo validnost konfiguracija
                var validationQuery = string.Format(
                    "EXEC spValidateShiftConfigurations '{0}', '{1}'",
                    datumStart.ToString("yyyy-MM-dd HH:mm:ss"),
                    datumEnd.ToString("yyyy-MM-dd HH:mm:ss"));

                var validationResult = (await connection.QueryAsync(validationQuery)).FirstOrDefault();

                if (validationResult == null || validationResult!.IsValid == 0) // Proveravamo int vrednost
                {
                    _logger.LogWarning($"Configuration validation failed: {validationResult?.ErrorMessage}");
                    return new DataTable();
                }

                // Učitavamo konfiguracije
                var configQuery = string.Format(
                    "EXEC spGetTimeSlotConfigurations '{0}', '{1}'",
                    datumStart.ToString("yyyy-MM-dd HH:mm:ss"),
                    datumEnd.ToString("yyyy-MM-dd HH:mm:ss"));

                var result = await connection.QueryAsync(configQuery);

                var dataTable = new DataTable();
                if (!result.Any())
                {
                    _logger.LogWarning($"No configurations found for period {datumStart:yyyy-MM-dd HH:mm:ss} to {datumEnd:yyyy-MM-dd HH:mm:ss}");
                    return dataTable;
                }

                // Add columns
                var firstRow = result.First() as IDictionary<string, object>;
                foreach (var column in firstRow!.Keys)
                {
                    var columnType = column.Contains("datum", StringComparison.OrdinalIgnoreCase)
                        ? typeof(string)
                        : typeof(string);
                    dataTable.Columns.Add(column, columnType);
                }

                if (!dataTable.Columns.Contains("Cluster"))
                {
                    dataTable.Columns.Add("Cluster", typeof(string));
                }

                // Add rows
                foreach (var row in result)
                {
                    var dataRow = dataTable.NewRow();
                    var rowDict = row as IDictionary<string, object>;
                    foreach (var item in rowDict!)
                    {
                        dataRow[item.Key] = item.Value ?? DBNull.Value;
                    }
                    dataTable.Rows.Add(dataRow);
                }

                _logger.LogInformation($"Successfully loaded {dataTable.Rows.Count} configurations");
                return dataTable;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in LoadTimeSlotConfigurationsAsync: {ex.Message}");
                _logger.LogError($"Parameters: datumStart={datumStart:yyyy-MM-dd HH:mm:ss}, datumEnd={datumEnd:yyyy-MM-dd HH:mm:ss}");
                throw;
            }
        }

        public async Task<List<string>> GetOperativeWorkplacesAsync(DateTime datumStart, DateTime datumEnd)
        {
            try
            {
                _logger.LogInformation($"Getting operative workplaces for period {datumStart:yyyy-MM-dd HH:mm:ss} to {datumEnd:yyyy-MM-dd HH:mm:ss}");

                var dataTable = await FillByDatumAsync(datumStart, datumEnd);

                if (dataTable == null || dataTable.Rows.Count == 0)
                {
                    _logger.LogWarning("No data returned from FillByDatumAsync");
                    return [];
                }

                if (!dataTable.Columns.Contains("ORM"))
                {
                    _logger.LogError("Column 'ORM' not found in the returned data. Available columns: " +
                        string.Join(", ", dataTable.Columns.Cast<DataColumn>().Select(c => c.ColumnName)));
                    return [];
                }

                var operativeWorkplaces = dataTable.AsEnumerable()
                    .Select(row => row.Field<string>("ORM"))
                    .Where(rm => !string.IsNullOrEmpty(rm))
                    .Distinct()
                    .OrderBy(rm => rm)
                    .ToList();

                _logger.LogInformation($"Found {operativeWorkplaces.Count} distinct operative workplaces");

                // Detaljnije logovanje ako ima rezultata
                if (operativeWorkplaces.Count != 0)
                {
                    _logger.LogDebug($"Operative workplaces: {string.Join(", ", operativeWorkplaces)}");
                }

                return operativeWorkplaces!;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in GetOperativeWorkplacesAsync: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    _logger.LogError($"Inner exception: {ex.InnerException.Message}");
                }

                return [];
            }
        }

        public async Task<List<string>> GetControllersWithLicenseAsync()
        {
            try
            {
                using var connection = new OdbcConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                        SELECT idZaposleni 
                        FROM DozvolaZaRad 
                        WHERE idTipDozvole = 1 AND status = 1";

                var result = await connection.QueryAsync<string>(query);

                return result.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in GetControllersWithLicenseAsync: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Dobavlja dostupne konfiguracije za dati datum
        /// </summary>
        public async Task<List<ConfigurationDTO>> GetAvailableConfigurationsAsync(DateTime date)
        {
            try
            {
                using var connection = new OdbcConnection(_connectionString);
                await connection.OpenAsync();

                // Formatiraj datum za korišćenje u upitu
                string formattedDate = date.ToString("yyyy-MM-dd");

                // Upit za dobijanje aktivnih konfiguracija - koristimo strukturu koja je već definisana
                string configQuery = $@"
                        SELECT id, Konfiguracija, Cluster, VaziOd, VaziDo
                        FROM konfiguracije
                        WHERE VaziOd <= '{formattedDate}'
                          AND (VaziDo IS NULL OR VaziDo >= '{formattedDate}')
                        ORDER BY Cluster, SortOrder";

                var configs = await connection.QueryAsync(configQuery);

                // Mapiraj rezultate u ConfigurationDTO objekte
                var result = new List<ConfigurationDTO>();
                foreach (var config in configs)
                {
                    // Upit za dobijanje sektora za ovu konfiguraciju
                    string sectorQuery = $@"
                            SELECT id, idkonfiguracije, oznaka as sektor
                            FROM konfiguracije_detalji
                            WHERE idkonfiguracije = {config.id}
                            ORDER BY sektor";

                    var sectorResults = await connection.QueryAsync(sectorQuery);
                    var sectors = new List<string>();
                    foreach (var sector in sectorResults)
                    {
                        // Bezbedno konvertujemo dinamički tip u string
                        if (sector.sektor != null)
                        {
                            sectors.Add(sector.sektor.ToString());
                        }
                    }

                    foreach (var sector in sectorResults)

                        result.Add(new ConfigurationDTO
                    {
                        Id = config.id,
                        Konfiguracija = config.Konfiguracija?.ToString() ?? "",
                        Naziv = config.Konfiguracija?.ToString() ?? "",
                        Vrsta = config.Cluster?.ToString() ?? "",
                        Cluster = config.Cluster?.ToString() ?? "",
                        VaziOd = config.VaziOd != null ? Convert.ToDateTime(config.VaziOd) : DateTime.Now,
                        VaziDo = config.VaziDo != null ? Convert.ToDateTime(config.VaziDo) : null,
                        Sektori = sectors
                    });
                }

                _logger.LogInformation($"Found {result.Count} configurations for date {formattedDate}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in GetAvailableConfigurationsAsync: {ex.Message}");
                return new List<ConfigurationDTO>();
            }
        }

        /// <summary>
        /// Ažurira konfiguraciju za određeni vremenski slot
        /// </summary>
        public async Task<bool> UpdateConfigurationAsync(DateTime startTime, DateTime endTime, string configType, string configCode)
        {
            try
            {
                using var connection = new OdbcConnection(_connectionString);
                await connection.OpenAsync();

                // Formatiraj datume za korišćenje u upitu
                string formattedStartTime = startTime.ToString("yyyy-MM-dd HH:mm:ss");
                string formattedEndTime = endTime.ToString("yyyy-MM-dd HH:mm:ss");

                // Prvo pronađi ID konfiguracije
                string getConfigQuery = $@"
                        SELECT id 
                        FROM konfiguracije 
                        WHERE Konfiguracija = '{configCode}' AND Cluster = '{configType}'";

                var configId = await connection.QueryFirstOrDefaultAsync<int?>(getConfigQuery);

                if (configId == null)
                {
                    _logger.LogWarning($"Configuration not found: {configType}:{configCode}");
                    return false;
                }

                // Proveri da li već postoji konfiguracijski slot za dato vreme
                string checkExistingQuery = $@"
                        SELECT id 
                        FROM raspored_konfiguracija
                        WHERE (
                            (datumOd <= '{formattedStartTime}' AND datumDo > '{formattedStartTime}')
                            OR 
                            (datumOd < '{formattedEndTime}' AND datumDo >= '{formattedEndTime}')
                            OR
                            (datumOd >= '{formattedStartTime}' AND datumDo <= '{formattedEndTime}')
                        )";

                var existingId = await connection.QueryFirstOrDefaultAsync<int?>(checkExistingQuery);

                if (existingId != null)
                {
                    // Ažuriraj postojeću konfiguraciju
                    string updateQuery;
                    if (configType == "TX")
                    {
                        updateQuery = $@"
                            UPDATE raspored_konfiguracija
                            SET idKonfiguracijeTX = {configId}, 
                                datumOd = '{formattedStartTime}', 
                                datumDo = '{formattedEndTime}'
                            WHERE id = {existingId}";
                    }
                    else if (configType == "LU")
                    {
                        updateQuery = $@"
                            UPDATE raspored_konfiguracija
                            SET idKonfiguracijeLU = {configId}, 
                                datumOd = '{formattedStartTime}', 
                                datumDo = '{formattedEndTime}'
                            WHERE id = {existingId}";
                    }
                    else // "ALL"
                    {
                        updateQuery = $@"
                            UPDATE raspored_konfiguracija
                            SET idKonfiguracijeTX = {configId}, 
                                idKonfiguracijeLU = {configId},
                                datumOd = '{formattedStartTime}', 
                                datumDo = '{formattedEndTime}'
                            WHERE id = {existingId}";
                    }

                    await connection.ExecuteAsync(updateQuery);
                    _logger.LogInformation($"Updated configuration {configType}:{configCode} for slot {startTime} - {endTime}");
                }
                else
                {
                    // Kreiraj novu konfiguraciju
                    string insertQuery;
                    if (configType == "TX")
                    {
                        insertQuery = $@"
                            INSERT INTO raspored_konfiguracija 
                            (idKonfiguracijeTX, datumOd, datumDo) 
                            VALUES ({configId}, '{formattedStartTime}', '{formattedEndTime}')";
                    }
                    else if (configType == "LU")
                    {
                        insertQuery = $@"
                            INSERT INTO raspored_konfiguracija 
                            (idKonfiguracijeLU, datumOd, datumDo) 
                            VALUES ({configId}, '{formattedStartTime}', '{formattedEndTime}')";
                    }
                    else // "ALL"
                    {
                        insertQuery = $@"
                            INSERT INTO raspored_konfiguracija 
                            (idKonfiguracijeTX, idKonfiguracijeLU, datumOd, datumDo) 
                            VALUES ({configId}, {configId}, '{formattedStartTime}', '{formattedEndTime}')";
                    }

                    await connection.ExecuteAsync(insertQuery);
                    _logger.LogInformation($"Created new configuration {configType}:{configCode} for slot {startTime} - {endTime}");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in UpdateConfigurationAsync: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Uklanja konfiguraciju za određeni vremenski slot
        /// </summary>
        public async Task<bool> RemoveConfigurationAsync(DateTime startTime, DateTime endTime)
        {
            try
            {
                using var connection = new OdbcConnection(_connectionString);
                await connection.OpenAsync();

                // Formatiraj datume za korišćenje u upitu
                string formattedStartTime = startTime.ToString("yyyy-MM-dd HH:mm:ss");
                string formattedEndTime = endTime.ToString("yyyy-MM-dd HH:mm:ss");

                // Proveri da li postoji konfiguracijski slot za dato vreme
                string checkExistingQuery = $@"
                        SELECT id 
                        FROM raspored_konfiguracija
                        WHERE (
                            (datumOd <= '{formattedStartTime}' AND datumDo > '{formattedStartTime}')
                            OR 
                            (datumOd < '{formattedEndTime}' AND datumDo >= '{formattedEndTime}')
                            OR
                            (datumOd >= '{formattedStartTime}' AND datumDo <= '{formattedEndTime}')
            )";

                var existingId = await connection.QueryFirstOrDefaultAsync<int?>(checkExistingQuery);

                if (existingId != null)
                {
                    // Ukloni konfiguraciju
                    string deleteQuery = $@"DELETE FROM raspored_konfiguracija WHERE id = {existingId}";
                    await connection.ExecuteAsync(deleteQuery);
                    _logger.LogInformation($"Removed configuration for slot {startTime} - {endTime}");
                    return true;
                }

                _logger.LogWarning($"No configuration found to remove for slot {startTime} - {endTime}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in RemoveConfigurationAsync: {ex.Message}");
                return false;
            }
        }


    }
}
