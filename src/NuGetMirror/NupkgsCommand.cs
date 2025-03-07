using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using NuGet.CatalogReader;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGetMirror.PackagePersisters;

namespace NuGetMirror
{
    /// <summary>
    /// Mirror nupkgs to a folder.
    /// </summary>
    internal static class NupkgsCommand
    {
        public static void Register(CommandLineApplication cmdApp, HttpSource httpSource, ILogger consoleLog)
        {
            cmdApp.Command("nupkgs", (cmd) => Run(cmd, httpSource, consoleLog), throwOnUnexpectedArg: true);
        }

        private static void Run(CommandLineApplication cmd, HttpSource httpSource, ILogger consoleLog)
        {
            cmd.Description = "Mirror nupkgs to a folder.";

            var output = cmd.Option("-o|--output", "Output directory for nupkgs.", CommandOptionType.SingleValue);
            var folderFormat = cmd.Option("--folder-format", "Output folder format. Defaults to v3. Options: (v2|v3)", CommandOptionType.SingleValue);
            var ignoreErrors = cmd.Option("--ignore-errors", "Continue on errors.", CommandOptionType.NoValue);
            var delay = cmd.Option("--delay", "Avoid downloading the very latest packages on the feed to avoid errors. This value is in minutes. Default: 10", CommandOptionType.SingleValue);
            var maxThreadsOption = cmd.Option("--max-threads", "Maximum number of concurrent downloads. Default: 8", CommandOptionType.SingleValue);
            var verbose = cmd.Option("--verbose", "Output additional network information.", CommandOptionType.NoValue);
            var includeIdOption = cmd.Option("-i|--include-id", "Include only these package ids or wildcards. May be provided multiple times.", CommandOptionType.MultipleValue);
            var excludeIdOption = cmd.Option("-e|--exclude-id", "Exclude these package ids or wildcards. May be provided multiple times.", CommandOptionType.MultipleValue);
            var additionalOutput = cmd.Option("--additional-output", "Additional output directory for nupkgs. The output path with the most free space will be used.", CommandOptionType.MultipleValue);
            var onlyLatestVersion = cmd.Option("--latest-only", "Include only the latest version of that package in the result", CommandOptionType.NoValue);
            var onlyStableVersion = cmd.Option("--stable-only", "Include only stable versions of that package in the result", CommandOptionType.NoValue);
            var startOption = cmd.Option("--start", "Beginning of the commit time range. Packages commited AFTER this time will be included. (The cursor value will not be used with this option.)", CommandOptionType.SingleValue);
            var endOption = cmd.Option("--end", "End of the commit time range. Packages commited at this time will be included.", CommandOptionType.SingleValue);
            var skipExistingOption = cmd.Option("--skip-existing", "Exclude packages that have already been downloaded. Useful for resuming aborted long-running operations. Could leave aborted files in invalid state.", CommandOptionType.NoValue);

            var argRoot = cmd.Argument(
                "[root]",
                "V3 feed index.json URI",
                multipleValues: false);

            cmd.HelpOption(Constants.HelpOption);

            cmd.OnExecute(async () =>
            {
                var timer = new Stopwatch();
                timer.Start();

                if (string.IsNullOrEmpty(argRoot.Value))
                {
                    throw new ArgumentException("Provide the full http url to a v3 nuget feed.");
                }

                var index = new Uri(argRoot.Value);

                if (!index.AbsolutePath.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Invalid feed url: '{argRoot.Value}'. Provide the full http url to a v3 nuget feed. For nuget.org use: https://api.nuget.org/v3/index.json");
                }

                // Create root
                var outputPath = Directory.GetCurrentDirectory();

                if (output.HasValue())
                {
                    outputPath = output.Value();
                }

                var tmpCachePath = Path.Combine(outputPath, ".tmp");

                var storagePaths = new HashSet<DirectoryInfo>()
                {
                    new DirectoryInfo(outputPath)
                };

                if (additionalOutput.Values?.Any() == true)
                {
                    storagePaths.UnionWith(additionalOutput.Values.Select(e => new DirectoryInfo(e)));
                }

                // Create all output folders
                foreach (var path in storagePaths)
                {
                    path.Create();
                }

                var delayTime = TimeSpan.FromMinutes(10);

                if (delay.HasValue())
                {
                    if (int.TryParse(delay.Value(), out var x))
                    {
                        var delayMinutes = Math.Max(0, x);
                        delayTime = TimeSpan.FromMinutes(delayMinutes);
                    }
                    else
                    {
                        throw new ArgumentException("Invalid --delay value. This must be an integer.");
                    }
                }

                var maxThreads = 8;

                if (maxThreadsOption.HasValue())
                {
                    if (int.TryParse(maxThreadsOption.Value(), out var x))
                    {
                        maxThreads = Math.Max(1, x);
                    }
                    else
                    {
                        throw new ArgumentException("Invalid --max-threads value. This must be an integer.");
                    }
                }

                var batchSize = 64;

                var outputRoot = new DirectoryInfo(outputPath);
                var outputFilesInfo = new FileInfo(Path.Combine(outputRoot.FullName, "updatedFiles.txt"));
                FileUtility.Delete(outputFilesInfo.FullName);

                var useV3Format = true;

                if (folderFormat.HasValue())
                {
                    switch (folderFormat.Value().ToLowerInvariant())
                    {
                        case "v2":
                            useV3Format = false;
                            break;
                        case "v3":
                            useV3Format = true;
                            break;
                        default:
                            throw new ArgumentException($"Invalid {folderFormat.LongName} value: '{folderFormat.Value()}'.");
                    }
                }

                DateTimeOffset start, end;
                if (startOption.HasValue())
                {
                    start = DateTimeOffset.Parse(startOption.Value());
                }
                else
                {
                    start = MirrorUtility.LoadCursor(outputRoot);
                }

                if (endOption.HasValue())
                {
                    end = DateTimeOffset.Parse(endOption.Value());
                }
                else
                {
                    end = DateTimeOffset.UtcNow.Subtract(delayTime);
                }

                var token = CancellationToken.None;
                var mode = DownloadMode.OverwriteIfNewer;

                var errorLogPath = Path.Combine(outputPath, "lastRunErrors.txt");
                FileUtility.Delete(errorLogPath);

                // Loggers
                // source -> deep -> file -> Console
                var log = new FileLogger(consoleLog, LogLevel.Error, errorLogPath);
                var deepLogger = new FilterLogger(log, LogLevel.Error);

                var packagePersisterOptions = new PackagePersisterOptions() {
                    SkipExisting = skipExistingOption.HasValue(),
                };

                IPackagePersister persister;
                if (useV3Format)
                {
                    persister = new NupkgV3PackagePersister(storagePaths, mode, log, deepLogger, packagePersisterOptions);
                }
                else
                {
                    persister = new NupkgV2PackagePersister(storagePaths, mode, log, deepLogger, packagePersisterOptions);
                }

                // Init
                log.LogInformation($"Mirroring {index.AbsoluteUri} -> {outputPath}");

                log.LogInformation($"Folder format:\t{persister.NameFormat}");

                log.LogInformation($"Cursor:\t\t{Path.Combine(outputPath, "cursor.json")}");
                log.LogInformation($"Change log:\t{outputFilesInfo.FullName}");
                log.LogInformation($"Error log:\t{errorLogPath}");

                log.LogInformation("Range start:\t" + start.ToString("o"));
                log.LogInformation("Range end:\t" + end.ToString("o"));
                log.LogInformation($"Batch size:\t{batchSize}");
                log.LogInformation($"Threads:\t{maxThreads}");

                // CatalogReader
                using (var cacheContext = new SourceCacheContext())
                {
                    cacheContext.SetTempRoot(tmpCachePath);

                    using (var catalogReader = new CatalogReader(index, httpSource, cacheContext, TimeSpan.Zero, deepLogger))
                    {
                        // Clear old cache files
                        catalogReader.ClearCache();

                        // Find the most recent entry for each package in the range
                        // Order by oldest first
                        IEnumerable<CatalogEntry> entryQuery = (await catalogReader
                            .GetFlattenedEntriesAsync(start, end, token));

                        // Remove all but includes if given
                        if (includeIdOption.HasValue())
                        {
                            var regex = includeIdOption.Values.Select(s => PatternUtils.WildcardToRegex(s, ignoreCase: true)).ToArray();

                            entryQuery = entryQuery.Where(e =>
                                regex.Any(r => r.IsMatch(e.Id)));
                        }

                        // Remove all excludes if given
                        if (excludeIdOption.HasValue())
                        {
                            var regex = excludeIdOption.Values.Select(s => PatternUtils.WildcardToRegex(s, ignoreCase: true)).ToArray();

                            entryQuery = entryQuery.Where(e =>
                                regex.All(r => !r.IsMatch(e.Id)));
                        }

                        // Exclude pre-release
                        if (onlyStableVersion.HasValue())
                        {
                            entryQuery = entryQuery.Where(e => !e.Version.IsPrerelease);
                        }

                        // Latest version only
                        if (onlyLatestVersion.HasValue())
                        {
                            entryQuery = entryQuery.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                                .Select(y => y.OrderByDescending(z => z.Version)
                                .First());
                        }

                        var toProcess = new Queue<CatalogEntry>(entryQuery.OrderBy(e => e.CommitTimeStamp));

                        log.LogInformation($"Catalog entries found: {toProcess.Count}");

                        var done = new List<CatalogEntry>(batchSize);
                        var complete = 0;
                        var total = toProcess.Count;
                        var totalDownloads = 0;

                        // Download files
                        var batchTimersMax = 20;
                        var batchTimers = new Queue<Tuple<Stopwatch, int>>(batchTimersMax);

                        // Download with throttling
                        while (toProcess.Count > 0)
                        {
                            // Create batches
                            var batch = new Queue<Func<Task<NupkgResult>>>(batchSize);
                            var files = new List<string>();
                            var batchTimer = new Stopwatch();
                            batchTimer.Start();

                            while (toProcess.Count > 0 && batch.Count < batchSize)
                            {
                                var entry = toProcess.Dequeue();

                                Func<CatalogEntry, Task<FileInfo>> getNupkg = (e) => persister.PersistAsync(e, token);

                                // Queue download task
                                batch.Enqueue(new Func<Task<NupkgResult>>(() => RunWithRetryAsync(entry, ignoreErrors.HasValue(), getNupkg, log, token)));
                            }

                            // Run
                            var results = await TaskUtils.RunAsync(batch, useTaskRun: true, maxThreads: maxThreads, token: token);

                            // Process results
                            foreach (var result in results)
                            {
                                var fileName = result.Nupkg?.FullName;

                                if (!string.IsNullOrEmpty(fileName))
                                {
                                    files.Add(fileName);
                                }

                                done.Add(result.Entry);
                            }

                            // Write out new files
                            if (files.Count > 0)
                            {
                                using (var newFileWriter = new StreamWriter(new FileStream(outputFilesInfo.FullName, FileMode.Append, FileAccess.Write)))
                                {
                                    foreach (var file in files)
                                    {
                                        newFileWriter.WriteLine(file);
                                    }
                                }
                            }

                            complete += done.Count;
                            totalDownloads += files.Count;
                            batchTimer.Stop();
                            batchTimers.Enqueue(new Tuple<Stopwatch, int>(batchTimer, done.Count));

                            while (batchTimers.Count > batchTimersMax)
                            {
                                batchTimers.Dequeue();
                            }

                            // Update cursor
                            var newestCommit = GetNewestCommit(done, toProcess);
                            if (newestCommit != null)
                            {
                                log.LogMinimal($"================[batch complete]================");
                                log.LogMinimal($"Processed:\t\t{complete} / {total}");
                                log.LogMinimal($"Batch downloads:\t{files.Count}");
                                log.LogMinimal($"Batch time:\t\t{batchTimer.Elapsed}");
                                log.LogMinimal($"Updating cursor.json:\t{newestCommit.Value.ToString("o")}");

                                var rate = batchTimers.Sum(e => e.Item1.Elapsed.TotalSeconds) / Math.Max(1, batchTimers.Sum(e => e.Item2));
                                var timeLeft = TimeSpan.FromSeconds(rate * (total - complete));

                                var timeLeftString = string.Empty;

                                if (timeLeft.TotalHours >= 1)
                                {
                                    timeLeftString = $"{(int)timeLeft.TotalHours} hours";
                                }
                                else if (timeLeft.TotalMinutes >= 1)
                                {
                                    timeLeftString = $"{(int)timeLeft.TotalMinutes} minutes";
                                }
                                else
                                {
                                    timeLeftString = $"{(int)timeLeft.TotalSeconds} seconds";
                                }

                                log.LogMinimal($"Estimated time left:\t{timeLeftString}");
                                log.LogMinimal($"================================================");

                                MirrorUtility.SaveCursor(outputRoot, newestCommit.Value);
                            }

                            done.Clear();

                            // Free up space
                            catalogReader.ClearCache();
                        }

                        // Set cursor to end time
                        MirrorUtility.SaveCursor(outputRoot, end);

                        timer.Stop();

                        var plural = totalDownloads == 1 ? "" : "s";
                        log.LogMinimal($"Downloaded {totalDownloads} nupkg{plural} in {timer.Elapsed.ToString()}.");
                    }
                }

                return 0;
            });
        }

        private static DateTimeOffset? GetNewestCommit(List<CatalogEntry> done, Queue<CatalogEntry> toProcess)
        {
            IEnumerable<CatalogEntry> sorted = done;

            if (toProcess.Count > 0)
            {
                sorted = sorted.Where(e => e.CommitTimeStamp < toProcess.Peek().CommitTimeStamp);
            }

            return sorted.OrderByDescending(e => e.CommitTimeStamp).FirstOrDefault()?.CommitTimeStamp;
        }

        internal static async Task<NupkgResult> RunWithRetryAsync(
            CatalogEntry entry,
            bool ignoreErrors,
            Func<CatalogEntry, Task<FileInfo>> action,
            ILogger log,
            CancellationToken token)
        {
            var success = false;
            var result = new NupkgResult()
            {
                Entry = entry
            };

            // Retry up to 10 times.
            for (var i = 0; !success && i < 10 && !token.IsCancellationRequested; i++)
            {
                try
                {
                    // Download
                    result.Nupkg = await action(entry);

                    success = true;
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("404"))
                {
                    var message = $"Unable to download {entry.Id} {entry.Version.ToFullString()}";
                    ExceptionUtils.LogException(ex, log, LogLevel.Warning, showType: true, message: message);

                    // Ignore missing packages, this is an issue with the feed.
                    success = true;
                }
                catch (Exception ex) when (i < 9)
                {
                    // Log a warning and retry
                    var message = $"Unable to download {entry.Id} {entry.Version.ToFullString()}. Retrying...";
                    ExceptionUtils.LogException(ex, log, LogLevel.Warning, showType: true, message: message);
                }
                catch (Exception ex)
                {
                    // Log an error and fail
                    var message = $"Unable to download {entry.Id} {entry.Version.ToFullString()}";
                    ExceptionUtils.LogException(ex, log, LogLevel.Error, showType: true, message: message);

                    if (!ignoreErrors)
                    {
                        throw;
                    }
                }

                if (!success && i < 9)
                {
                    await Task.Delay(TimeSpan.FromSeconds((i + 1) * 5));
                }
            }

            return result;
        }

        internal class NupkgResult
        {
            public FileInfo Nupkg { get; set; }

            public CatalogEntry Entry { get; set; }
        }
    }
}