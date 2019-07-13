using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace NuGet.CatalogReader
{
    public class Program
    {
        public static void Main(string[] args)
        {
            try
            {
                var log = new ConsoleLogger();

                string outputPath = GetOutputPath(log);

                var reader = new CatalogReader(new Uri("https://api.nuget.org/v3/index.json"), TimeSpan.FromHours(0), log);
                var entries = reader.GetFlattenedEntriesAsync(DateTimeOffset.Parse("2017-01-02"), DateTimeOffset.Parse("2017-01-03"), CancellationToken.None).Result;

                foreach (var group in entries.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
                {
                    var entry = group.First();

                    entry.DownloadNupkgAsync(outputPath);
                    entry.DownloadNuspecAsync(outputPath);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private static string GetOutputPath(ConsoleLogger log)
        {
            string root;
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (isWindows)
            {
                root = "d:";
            }
            else
            {
                var codeBase = Assembly.GetExecutingAssembly().CodeBase;
                var uri = new Uri(codeBase);
                var directory = Path.GetDirectoryName(uri.LocalPath);
                root = directory;
            }
            var outputPath = Path.Combine(root, "tmp", "out");
            log.Log(NuGet.Common.LogLevel.Debug, string.Format("Output Path: {0}", outputPath));
            return outputPath;
        }
    }
}
