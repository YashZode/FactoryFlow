using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FactoryFlow.Data
{
    public static class CsvReport
    {
        public static void Write(DataTable data, string path)
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.WriteLine(string.Join(",", data.Columns.Cast<DataColumn>().Select(c => Escape(c.ColumnName))));
            foreach (DataRow row in data.Rows)
                writer.WriteLine(string.Join(",", row.ItemArray.Select(value => Escape(value is DateTime date
                    ? DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""))));
        }
        private static string Escape(string value)
        {
            // Export text as text when opened in spreadsheet software (formula injection protection).
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
