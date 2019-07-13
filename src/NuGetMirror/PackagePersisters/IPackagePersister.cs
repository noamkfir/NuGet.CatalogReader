using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NuGet.CatalogReader;

namespace NuGetMirror.PackagePersisters
{
    public interface IPackagePersister
    {
        string NameFormat { get; }

        Task<FileInfo> PersistAsync(CatalogEntry entry, CancellationToken token);
    }
}