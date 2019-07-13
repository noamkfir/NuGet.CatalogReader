using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.CatalogReader;
using NuGet.Common;

namespace NuGetMirror.PackagePersisters
{
    public class PackageInfo
    {
        public string OutputDir { get; internal set; }
        public string NupkgPath { get; internal set; }
    }

    public class PackagePersisterOptions
    {
        public bool SkipExisting { get; set; }
    }

    public abstract class PackagePersister<T> : IPackagePersister
        where T : PackageInfo
    {
        protected readonly IEnumerable<DirectoryInfo> _storagePaths;
        protected readonly DownloadMode _mode;
        protected readonly ILogger _log;
        protected readonly ILogger _deepLog;
        protected readonly PackagePersisterOptions _options;

        public abstract string NameFormat { get; }

        protected PackagePersister(
            IEnumerable<DirectoryInfo> storagePaths,
            DownloadMode mode,
            ILogger log,
            ILogger deepLog,
            PackagePersisterOptions options
        )
        {
            _storagePaths = storagePaths;
            _mode = mode;
            _log = log;
            _deepLog = deepLog;
            _options = options;
        }

        public async Task<FileInfo> PersistAsync(
            CatalogEntry entry,
            CancellationToken token
        )
        {
            var storagePaths = _storagePaths;
            var log = _log;

            FileInfo result = null;
            DirectoryInfo rootDir = null;
            var lastCreated = DateTimeOffset.MinValue;

            // Check if the nupkg already exists on another drive.
            foreach (var storagePath in storagePaths)
            {
                var packageFilePath = GetPackageFilePath(storagePath, entry);

                if (File.Exists(packageFilePath))
                {
                    // Use the existing path
                    lastCreated = File.GetCreationTimeUtc(packageFilePath);
                    rootDir = storagePath;
                    break;
                }
            }

            // Not found, use the path with the most space
            if (rootDir == null)
            {
                rootDir = GetPathWithTheMostFreeSpace(storagePaths);
            }

            var packageInfo = CreatePackageInfo(entry, rootDir);

            var nupkgFile = new FileInfo(packageInfo.NupkgPath);

            if (_options.SkipExisting && nupkgFile.Exists)
            {
                log.LogInformation($"SKIPPED (package exists): {packageInfo.NupkgPath}");
            }
            else
            {
                // Download
                nupkgFile = await entry.DownloadNupkgAsync(packageInfo.OutputDir, _mode, token);

                if (File.Exists(packageInfo.NupkgPath))
                {
                    var currentCreated = File.GetCreationTimeUtc(packageInfo.NupkgPath);

                    if (ShouldPersistPackageFile(lastCreated, currentCreated, packageInfo))
                    {
                        result = nupkgFile;
                        log.LogInformation($"DOWNLOADED: {nupkgFile.FullName}");

                        PersistPackageFile(nupkgFile, packageInfo);
                    }
                    else
                    {
                        log.LogDebug($"SKIPPED (current file is the same or newer): {lastCreated.ToString("o")} {currentCreated.ToString("o")} {nupkgFile.FullName}");
                    }
                }
                else
                {
                    log.LogDebug($"SKIPPED (download failure?): {nupkgFile.FullName}");
                }
            }

            return result;
        }

        /// <summary>
        /// Get free space available at path.
        /// </summary>
        public static long GetFreeSpace(DirectoryInfo path)
        {
            var root = Path.GetPathRoot(path.FullName);

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && StringComparer.OrdinalIgnoreCase.Equals(root, drive.Name))
                {
                    return drive.TotalFreeSpace;
                }
            }
            return -1;
        }

        protected abstract T CreatePackageInfo(CatalogEntry entry, DirectoryInfo rootDir);

        protected abstract bool ShouldPersistPackageFile(DateTimeOffset lastCreated, DateTime currentCreated, T packageInfo);

        protected abstract void PersistPackageFile(FileInfo nupkgFile, T packageInfo);

        /// <summary>
        /// Get path with the most free space.
        /// </summary>
        public static DirectoryInfo GetPathWithTheMostFreeSpace(IEnumerable<DirectoryInfo> paths)
        {
            if (paths.Count() == 1)
            {
                return paths.First();
            }

            return paths.Select(e => new KeyValuePair<DirectoryInfo, long>(e, GetFreeSpace(e)))
                 .OrderByDescending(e => e.Value)
                 .FirstOrDefault()
                 .Key;
        }

        protected abstract string GetPackageFilePath(DirectoryInfo storagePath, CatalogEntry entry);
    }
}