using System.Data;

namespace ATCPlanner.Utils
{
    public static class TimeUtils
    {
        public static List<DateTime> CreateTimeSlots(DateTime startTime, DateTime endTime)
        {
            var timeSlots = new List<DateTime>();

            if (startTime == default || endTime == default)
            {
                Console.WriteLine($"Invalid smena data: Start time: {startTime}, End time: {endTime}");
                return timeSlots;
            }

            // remove timezone info if present
            startTime = startTime.Kind == DateTimeKind.Utc ? startTime.ToLocalTime() : startTime;
            endTime = endTime.Kind == DateTimeKind.Utc ? endTime.ToLocalTime() : endTime;

            startTime = DateTime.SpecifyKind(startTime, DateTimeKind.Unspecified);
            endTime = DateTime.SpecifyKind(endTime, DateTimeKind.Unspecified);

            // if endtime is before starttime, assume the shift crosses midnight
            if (endTime < startTime)
            {
                endTime = endTime.AddDays(1);
            }

            var currentTime = startTime;
            while (currentTime < endTime)
            {
                timeSlots.Add(currentTime);
                currentTime = currentTime.AddMinutes(30);
            }

            return timeSlots;
        }

        public static void ConvertDatesToDateTime(DataTable table, params string[] dateColumnNames)
        {
            foreach (var columnName in dateColumnNames)
            {
                if (table.Columns.Contains(columnName) && table.Columns[columnName]!.DataType == typeof(string))
                {
                    table.Columns.Add(columnName + "_temp", typeof(DateTime));
                    foreach (DataRow row in table.Rows)
                    {
                        if (DateTime.TryParse(row[columnName].ToString(), out DateTime dateValue))
                        {
                            row[columnName + "_temp"] = dateValue;
                        }
                        else
                        {
                            Console.WriteLine($"Nije moguće konvertovati vrednost '{row[columnName]}' u DateTime za kolonu {columnName}");
                            row[columnName + "_temp"] = DBNull.Value;
                        }
                    }
                    table.Columns.Remove(columnName);
                    table.Columns[columnName + "_temp"]!.ColumnName = columnName;
                }
            }
        }
    }
}
