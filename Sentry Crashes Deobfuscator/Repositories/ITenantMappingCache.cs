namespace Sentry_Crashes_Deobfuscator.Repositories
{
    /// <summary>
    /// Per-instance LRU+TTL cache of tenant mapping repositories, per Project.md §3.3
    /// (ADR-001, Option A: in-process MemoryCache, not shared Redis). A cache miss
    /// triggers a load from the blob store; a blob-store miss too means the §3.4
    /// fallback path applies -- the caller forwards the raw obfuscated trace unchanged.
    /// </summary>
    public interface ITenantMappingCache
    {
        bool TryGet(string product, string release, out IMappingRepository? repository);

        /// <summary>Tenants currently cached -- exposed for the /mappings and /health endpoints.</summary>
        int CachedTenantCount { get; }
    }
}
