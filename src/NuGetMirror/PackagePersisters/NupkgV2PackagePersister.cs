using System;
using System.Collections.Generic;
using System.IO;
using NuGet.CatalogReader;
using NuGet.Common;

namespace NuGetMirror.PackagePersisters
{
    public class NupkgV2PackageInfo : PackageInfo
    {
    }

    public class NupkgV2PackagePersister : PackagePersister<NupkgV2PackageInfo>
    {
        public override string NameFormat
        {
            get { return "{id}/{id}.{version}.nupkg"; }
        }

        public NupkgV2PackagePersister(
            IEnumerable<DirectoryInfo> storagePaths,
            DownloadMode mode,
            ILogger log,
            ILogger deepLog,
            PackagePersisterOptions options
        ) : base(storagePaths, mode, log, deepLog, options)
        {
        }

        protected override NupkgV2PackageInfo CreatePackageInfo(CatalogEntry entry, DirectoryInfo rootDir)
        {
            // id/id.version.nupkg
            var outputDir = Path.Combine(rootDir.FullName, entry.Id.ToLowerInvariant());
            var packageInfo = new NupkgV2PackageInfo()
            {
                OutputDir = outputDir,
                NupkgPath = Path.Combine(outputDir, $"{entry.FileBaseName}.nupkg"),
            };
            return packageInfo;
        }

        protected override bool ShouldPersistPackageFile(DateTimeOffset lastCreated, DateTime currentCreated, NupkgV2PackageInfo packageInfo)
        {
            return lastCreated < currentCreated;
        }

        protected override void PersistPackageFile(FileInfo nupkgFile, NupkgV2PackageInfo packageInfo)
        {
        }

        protected override string GetPackageFilePath(DirectoryInfo storagePath, CatalogEntry entry)
        {
            var checkOutputDir = Path.Combine(storagePath.FullName, entry.Id.ToLowerInvariant());
            var checkNupkgPath = Path.Combine(checkOutputDir, $"{entry.FileBaseName}.nupkg");
            return checkNupkgPath;
        }
    }
}