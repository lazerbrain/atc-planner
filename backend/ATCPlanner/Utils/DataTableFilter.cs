using Microsoft.Extensions.Logging;
using System.Data;

namespace ATCPlanner.Utils
{
    public class DataTableFilter(ILogger<DataTableFilter> logger)
    {
        private readonly ILogger<DataTableFilter> _logger = logger;

        public DataTable FilterBySelectedOperationWorkplaces(DataTable inicijalni_raspored, List<string>? selectedOperativeWorkplaces)
        {
            if (selectedOperativeWorkplaces == null || !selectedOperativeWorkplaces.Any())
            {
                _logger.LogInformation("No selected operative workplaces, returning original schedule");
                return inicijalni_raspored;
            }

            DataTable filteredTable = inicijalni_raspored.Clone();
            var rows = inicijalni_raspored.AsEnumerable()
                .Where(row => selectedOperativeWorkplaces.Contains(row.Field<string>("ORM")!));

            foreach (var row in rows)
            {
                filteredTable.ImportRow(row);
            }

            return filteredTable;
        }

        public DataTable FilterBySelectedEmployees(DataTable inicijalni_raspored, List<string> selectedEmployees)
        {
            if (selectedEmployees == null || !selectedEmployees.Any())
            {
                _logger.LogInformation("No selected employees, returning original schedule");
                return inicijalni_raspored;
            }

            DataTable filteredTable = inicijalni_raspored.Clone();
            var rows = inicijalni_raspored.AsEnumerable()
                .Where(row => selectedEmployees.Contains(row.Field<string>("sifra")!));

            foreach (var row in rows)
            {
                filteredTable.ImportRow(row);
            }

            _logger.LogInformation("Filtered schedule by employees rows: {Count}", filteredTable.Rows.Count);
            return filteredTable;
        }
    }
}
