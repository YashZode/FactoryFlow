using FactoryFlow.Collector;
using FactoryFlow.PlcClient.Services;
using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    internal static string Root => Environment.GetEnvironmentVariable("FACTORYFLOW_COLLECTOR_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryFlow", "Collector");
    internal static CollectorEngine Create()
    {
        var connection = Environment.GetEnvironmentVariable("FACTORYFLOW_SQL_CONNECTION") ?? ConfigurationManager.ConnectionStrings["FactoryFlowDB"].ConnectionString;
        var sources = (Environment.GetEnvironmentVariable("FACTORYFLOW_COLLECTOR_SOURCES") ?? ConfigurationManager.AppSettings["Sources"])
            .Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).Select(text =>
            {
                var parts = text.Split('|'); if (parts.Length < 2) throw new ConfigurationErrorsException("Sources must be lineId|endpoint|namespaceUri, separated by semicolons.");
                return new CollectorSource { LineId = int.Parse(parts[0]), Endpoint = parts[1], NamespaceUri = parts.Length > 2 ? parts[2] : PlcNodeIds.NamespaceUri };
            }).ToArray();
        var engine = new CollectorEngine(connection, sources, Root,
            TimeSpan.FromSeconds(double.Parse(ConfigurationManager.AppSettings["WeightSampleSeconds"] ?? "5", System.Globalization.CultureInfo.InvariantCulture)),
            PlcTagMap.Load(ConfigurationManager.AppSettings["TagMapPath"]));
        engine.Diagnostic += Log;
        return engine;
    }
    private static readonly object LogLock = new();
    internal static void Log(string text)
    {
        lock (LogLock)
        {
            var line = DateTime.UtcNow.ToString("O") + " " + text;
            Console.WriteLine(line);
            try
            {
                Directory.CreateDirectory(Root);
                var path = Path.Combine(Root, "collector.log");
                if (File.Exists(path) && new FileInfo(path).Length > 5000000) File.Move(path, path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        }
    }
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--prepare-certificates")) { await Create().PrepareCertificatesAsync(); return 0; }
            {
                using var stop = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                int durationIndex = Array.IndexOf(args, "--duration");
                if (durationIndex >= 0) stop.CancelAfter(TimeSpan.FromSeconds(int.Parse(args[durationIndex + 1])));
                Log("Collector console mode started.");
                await Create().RunAsync(stop.Token);
                Log("Collector stopped cleanly."); return 0;
            }
        }
        catch (Exception ex) { Log(ex.ToString()); return 1; }
    }
}

