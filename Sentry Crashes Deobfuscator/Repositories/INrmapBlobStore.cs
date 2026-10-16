namespace Sentry_Crashes_Deobfuscator.Repositories
{
    /// <summary>
    /// Resolves the storage path for a tenant's .nrmap file, per Project.md §3.2.
    /// Local disk (<see cref="LocalDiskNrmapBlobStore"/>) is the default and the only
    /// implementation shipped so far; S3 is a documented production-scale option behind
    /// this same interface, not built until instance count and parse cost justify it
    /// (ADR-001 applies the same "ship the smaller provable increment" reasoning here).
    /// </summary>
    public interface INrmapBlobStore
    {
        /// <summary>Returns the resolved path if the tenant's .nrmap exists, else null.</summary>
        string? TryResolve(string product, string release);
    }
}
