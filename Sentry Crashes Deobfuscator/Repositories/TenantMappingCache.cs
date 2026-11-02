using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Sentry_Crashes_Deobfuscator.Observability;

namespace Sentry_Crashes_Deobfuscator.Repositories
{
    public class TenantMappingCache : ITenantMappingCache, IDisposable
    {
        private readonly MemoryCache _cache;
        private readonly INrmapBlobStore _blobStore;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<TenantMappingCache> _logger;
        private readonly SymbolResolverMetrics _metrics;
        private readonly TimeSpan _ttl;

        public TenantMappingCache(
            INrmapBlobStore blobStore,
            ILoggerFactory loggerFactory,
            ILogger<TenantMappingCache> logger,
            SymbolResolverMetrics metrics,
            IConfiguration config)
        {
            _blobStore = blobStore;
            _loggerFactory = loggerFactory;
            _logger = logger;
            _metrics = metrics;

            var ttlDays = config.GetValue<double?>("Cache:NrmapTtlDays") ?? 30;
            _ttl = TimeSpan.FromDays(ttlDays);

            var maxEntries = config.GetValue<long?>("Cache:NrmapMaxEntries") ?? 50;
            _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxEntries });
        }

        public int CachedTenantCount => _cache.Count;

        public bool TryGet(string product, string release, out IMappingRepository? repository)
        {
            var tenantKey = $"{product}:{release}";
            var stopwatch = Stopwatch.StartNew();

            if (_cache.TryGetValue(tenantKey, out CacheEntry? cached) && cached != null)
            {
                // A rebuilt/overwritten .nrmap must not be served stale: reload when the file changed.
                if (File.GetLastWriteTimeUtc(cached.Path) == cached.LastWriteUtc)
                {
                    repository = cached.Repository;
                    _metrics.RecordCacheHit(product);
                    _metrics.RecordResolutionDuration(product, "hit", stopwatch.Elapsed.TotalSeconds);
                    return true;
                }

                _logger.LogWarning("nrmap_changed: {TenantKey} file modified since it was cached, reloading", tenantKey);
                _cache.Remove(tenantKey);
            }

            _metrics.RecordCacheMiss(product);

            var path = _blobStore.TryResolve(product, release);
            if (path == null)
            {
                _logger.LogWarning("nrmap_not_found: tenant {TenantKey}", tenantKey);
                _metrics.RecordUnresolved(product);
                _metrics.RecordResolutionDuration(product, "miss", stopwatch.Elapsed.TotalSeconds);
                repository = null;
                return false;
            }

            var loaded = new NRMapRepository(_loggerFactory.CreateLogger<NRMapRepository>());
            loaded.LoadMappings(path);

            _cache.Set(tenantKey, new CacheEntry(loaded, path, File.GetLastWriteTimeUtc(path)), new MemoryCacheEntryOptions
            {
                SlidingExpiration = _ttl,
                Size = 1
            });

            repository = loaded;
            _metrics.RecordResolutionDuration(product, "miss", stopwatch.Elapsed.TotalSeconds);
            return true;
        }

        private sealed record CacheEntry(IMappingRepository Repository, string Path, DateTime LastWriteUtc);

        public void Dispose() => _cache.Dispose();
    }
}
