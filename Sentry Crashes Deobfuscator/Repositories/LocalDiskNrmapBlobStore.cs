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
            var path = Path.Combine(_rootPath, product, $"{release}.nrmap");
            return File.Exists(path) ? path : null;
        }
    }
}
