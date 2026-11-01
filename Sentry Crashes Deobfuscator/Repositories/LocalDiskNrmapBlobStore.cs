namespace Sentry_Crashes_Deobfuscator.Repositories
{
    /// <summary>Layout: {rootPath}/{product}/{release}.nrmap, per Project.md §3.2.</summary>
    public class LocalDiskNrmapBlobStore : INrmapBlobStore
    {
        private readonly string _rootPath;

        public LocalDiskNrmapBlobStore(string rootPath)
        {
            _rootPath = rootPath;
        }

        public string? TryResolve(string product, string release)
        {
            if (!TenantKeyResolver.IsSafeSegment(product) || !TenantKeyResolver.IsSafeSegment(release))
            {
                return null;
            }

            var root = Path.GetFullPath(_rootPath) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, product, $"{release}.nrmap"));

            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return File.Exists(path) ? path : null;
        }
    }
}
