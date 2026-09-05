using Microsoft.Extensions.Logging.Abstractions;
using Sentry_Crashes_Deobfuscator.Repositories;
using Xunit;

namespace Deobfuscator.Tests
{
    public class NRMapRepositoryTests
    {
        private static NRMapRepository LoadFixture()
        {
            var repo = new NRMapRepository(NullLogger<NRMapRepository>.Instance);
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.nrmap");
            repo.LoadMappings(path);
            return repo;
        }

        [Fact]
        public void LoadMappings_ValidFile_PopulatesMappings()
        {
            var repo = LoadFixture();

            Assert.True(repo.HasMappings);
            Assert.True(repo.TotalMappings > 0);
        }

        [Fact]
        public void TryDeobfuscateMethod_KnownAssemblyQualifiedKey_ReturnsOriginalTypeAndMethod()
        {
            var repo = LoadFixture();

            var found = repo.TryDeobfuscateMethod("Xk9Qz2Wp.a1b2c3d4", out var original);

            Assert.True(found);
            Assert.Equal("Deobfuscator.Core.SymbolResolver.ResolveSymbol", original);
        }

        [Fact]
        public void TryDeobfuscateMethod_BareObfuscatedName_ReturnsOriginalTypeAndMethod()
        {
            var repo = LoadFixture();

            var found = repo.TryDeobfuscateMethod("a1b2c3d4", out var original);

            Assert.True(found);
            Assert.Equal("Deobfuscator.Core.SymbolResolver.ResolveSymbol", original);
        }

        [Fact]
        public void TryDeobfuscateType_FullAndShortObfuscatedName_ReturnsOriginalType()
        {
            var repo = LoadFixture();

            Assert.True(repo.TryDeobfuscateType("Xk9Qz2Wp", out var full));
            Assert.Equal("Deobfuscator.Core.SymbolResolver", full);

            Assert.True(repo.TryDeobfuscateType("Wp", out var shortForm));
            Assert.Equal("Deobfuscator.Core.SymbolResolver", shortForm);
        }

        [Fact]
        public void TryDeobfuscateMethod_UnknownSymbol_ReturnsInputUnchanged()
        {
            var repo = LoadFixture();

            var found = repo.TryDeobfuscateMethod("totally_unknown_symbol", out var original);

            Assert.False(found);
            Assert.Equal("totally_unknown_symbol", original);
        }

        [Fact]
        public void LoadMappings_MissingPath_LeavesRepositoryEmptyWithoutThrowing()
        {
            var repo = new NRMapRepository(NullLogger<NRMapRepository>.Instance);

            repo.LoadMappings(Path.Combine(AppContext.BaseDirectory, "does-not-exist.nrmap"));

            Assert.False(repo.HasMappings);
        }
    }
}
