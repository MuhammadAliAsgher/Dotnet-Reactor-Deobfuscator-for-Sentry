using Sentry_Crashes_Deobfuscator.Repositories;

namespace Deobfuscator.Tests
{
    /// <summary>In-memory ITenantMappingCache for service-layer tests -- no blob store or real caching involved.</summary>
    internal class FakeTenantMappingCache : ITenantMappingCache
    {
        private readonly IMappingRepository? _repository;

        public FakeTenantMappingCache(IMappingRepository? repository = null)
        {
            _repository = repository;
        }

        public int CachedTenantCount => _repository != null ? 1 : 0;

        public bool TryGet(string product, string release, out IMappingRepository? repository)
        {
            repository = _repository;
            return _repository != null;
        }
    }
}
