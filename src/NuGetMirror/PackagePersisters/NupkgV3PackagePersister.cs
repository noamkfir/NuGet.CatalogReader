using System;
using System.Collections.Generic;
using System.IO;
using NuGet.CatalogReader;
using NuGet.Common;
using NuGet.Packaging;

namespace NuGetMirror.PackagePersisters
{
    public class NupkgV3PackageInfo : PackageInfo
    {
        public string HashPath { get; internal set; }
        public string NuspecPath { get; internal set; }
    }

    public class NupkgV3PackagePersister : PackagePersister<NupkgV3PackageInfo>
    {
        public override string NameFormat
        {
            get { return "{id}/{version}/{id}.{version}.nupkg"; }
        }

        public NupkgV3PackagePersister(
            IEnumerable<DirectoryInfo> storagePaths,
            DownloadMode mode,
            ILogger log,
            ILogger deepLog
        ) : base(storagePaths, mode, log, deepLog)
        {
        }

        protected override NupkgV3PackageInfo CreatePackageInfo(CatalogEntry entry, DirectoryInfo rootDir)
        {
            // id/version/id.version.nupkg
            var versionFolderResolver = new VersionFolderPathResolver(rootDir.FullName);
            var packageInfo = new NupkgV3PackageInfo()
            {
                OutputDir = versionFolderResolver.GetInstallPath(entry.Id, entry.Version),
                NupkgPath = versionFolderResolver.GetPackageFilePath(entry.Id, entry.Version),
                HashPath = versionFolderResolver.GetHashPath(entry.Id, entry.Version),
                NuspecPath = versionFolderResolver.GetManifestFilePath(entry.Id, entry.Version),
            };
            return packageInfo;
        }

        protected override bool ShouldPersistPackageFile(DateTimeOffset lastCreated, DateTime currentCreated, NupkgV3PackageInfo packageInfo)
        {
            // Clean up nuspec and hash if the file changed
            return lastCreated < currentCreated || !File.Exists(packageInfo.HashPath) || !File.Exists(packageInfo.NuspecPath);
        }

        protected override void PersistPackageFile(FileInfo nupkgFile, NupkgV3PackageInfo packageInfo)
        {
            var hashPath = packageInfo.HashPath;
            var nuspecPath = packageInfo.NuspecPath;

            FileUtility.Delete(hashPath);
            FileUtility.Delete(nuspecPath);

            using (var fileStream = File.OpenRead(nupkgFile.FullName))
            {
                var packageHash = Convert.ToBase64String(new CryptoHashProvider("SHA512").CalculateHash(fileStream));
                fileStream.Seek(0, SeekOrigin.Begin);

                // Write nuspec
                using (var reader = new PackageArchiveReader(fileStream))
                {
                    var nuspecFile = reader.GetNuspecFile();
                    reader.ExtractFile(nuspecFile, nuspecPath, _deepLog);
                }

                // Write package hash
                File.WriteAllText(hashPath, packageHash);
            }
        }

        protected override string GetPackageFilePath(DirectoryInfo storagePath, CatalogEntry entry) {
            var currentResolver = new VersionFolderPathResolver(storagePath.FullName);
            var checkNupkgPath = currentResolver.GetPackageFilePath(entry.Id, entry.Version);
            return checkNupkgPath;
        }
    }
}