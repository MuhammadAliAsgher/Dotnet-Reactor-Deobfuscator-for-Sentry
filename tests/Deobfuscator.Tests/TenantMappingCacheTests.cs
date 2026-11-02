using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sentry_Crashes_Deobfuscator.Observability;
using Sentry_Crashes_Deobfuscator.Repositories;
using Xunit;

namespace Deobfuscator.Tests
{
    /// <summary>Returns a fixed path for whichever tenants are registered -- avoids touching
    /// real disk layout for the cache-layer tests.</summary>
    internal class FakeBlobStore : INrmapBlobStore
    {
        private readonly Dictionary<string, string> _paths = new();

        public void SetPath(string product, string release, string path) => _paths[$"{product}:{release}"] = path;

        public string? TryResolve(string product, string release) =>
            _paths.TryGetValue($"{product}:{release}", out var path) ? path : null;
    }

    public class TenantMappingCacheTests
    {
        private static IConfiguration BuildConfig(int? maxEntries = null) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(maxEntries.HasValue
                    ? new Dictionary<string, string?> { ["Cache:NrmapMaxEntries"] = maxEntries.Value.ToString() }
                    : new Dictionary<string, string?>())
                .Build();

        private static TenantMappingCache BuildCache(INrmapBlobStore blobStore, IConfiguration? config = null) =>
            new(
                blobStore,
                NullLoggerFactory.Instance,
                NullLogger<TenantMappingCache>.Instance,
                new SymbolResolverMetrics(),
                config ?? BuildConfig());

        [Fact]
        public void TryGet_NoFileForTenant_ReturnsFalse()
        {
            var cache = BuildCache(new FakeBlobStore());

            var found = cache.TryGet("demo-app", "1.4.2", out var repository);

            Assert.False(found);
            Assert.Null(repository);
        }

        [Fact]
        public void TryGet_FileExists_LoadsAndCachesSameInstanceOnSecondCall()
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.nrmap");
            var blobStore = new FakeBlobStore();
            blobStore.SetPath("demo-app", "1.4.2", fixturePath);

            var cache = BuildCache(blobStore);

            var firstFound = cache.TryGet("demo-app", "1.4.2", out var first);
            var secondFound = cache.TryGet("demo-app", "1.4.2", out var second);

            Assert.True(firstFound);
            Assert.True(secondFound);
            Assert.NotNull(first);
            Assert.Same(first, second); // second call is a cache hit, not a re-parse
            Assert.Equal(1, cache.CachedTenantCount);
        }

        [Fact]
        public void TryGet_DifferentTenants_ResolveIndependently()
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.nrmap");
            var blobStore = new FakeBlobStore();
            blobStore.SetPath("demo-app", "1.0.0", fixturePath);
            blobStore.SetPath("other-app", "2.0.0", fixturePath);

            var cache = BuildCache(blobStore);

            var firstFound = cache.TryGet("demo-app", "1.0.0", out var first);
            var secondFound = cache.TryGet("other-app", "2.0.0", out var second);

            Assert.True(firstFound);
            Assert.True(secondFound);
            Assert.NotSame(first, second); // distinct tenant keys get distinct repository instances
            Assert.Equal(2, cache.CachedTenantCount);
        }

        [Fact]
        public void TryGet_FileOverwrittenAfterCaching_ReloadsNewContent()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nrmap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "1.0.nrmap");
                var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.nrmap");
                File.WriteAllLines(path, new[] { "<<type>>", "Aaaa", "Old.Type", "Aa", "PAD", "m1m1m1m1", "OldMethod" });
                var blobStore = new FakeBlobStore();
                blobStore.SetPath("demo-app", "1.0", path);
                var cache = BuildCache(blobStore);

                cache.TryGet("demo-app", "1.0", out var first);
                Assert.True(first!.TryDeobfuscateMethod("m1m1m1m1", out _));

                File.Copy(fixture, path, overwrite: true);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

                cache.TryGet("demo-app", "1.0", out var second);
                Assert.NotSame(first, second);
                Assert.True(second!.TryDeobfuscateMethod("Xk9Qz2Wp.a1b2c3d4", out _));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
