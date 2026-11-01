using System.Text.Json.Nodes;
using Sentry_Crashes_Deobfuscator.Repositories;
using Xunit;

namespace Deobfuscator.Tests
{
    public class TenantKeyResolverTests
    {
        [Fact]
        public void TryResolve_ProjectAndReleasePresent_ReturnsTenantKey()
        {
            var json = JsonNode.Parse("{\"project\":\"demo-app\",\"release\":\"1.4.2\"}")!;

            var resolved = TenantKeyResolver.TryResolve(json, out var product, out var release, out var tenantKey);

            Assert.True(resolved);
            Assert.Equal("demo-app", product);
            Assert.Equal("1.4.2", release);
            Assert.Equal("demo-app:1.4.2", tenantKey);
        }

        [Theory]
        [InlineData("..", "1.4.2")]
        [InlineData("demo-app", "../../secret")]
        [InlineData("demo\\..\\x", "1.4.2")]
        [InlineData("/etc", "1.4.2")]
        [InlineData(".hidden", "1.4.2")]
        public void TryResolve_PathTraversalInSegments_Rejected(string product, string release)
        {
            var json = new JsonObject { ["project"] = product, ["release"] = release };

            var resolved = TenantKeyResolver.TryResolve(json, out _, out _, out var tenantKey);

            Assert.False(resolved);
            Assert.Null(tenantKey);
        }

        [Fact]
        public void TryResolve_ProjectMissing_FallsBackToProductTagObjectForm()
        {
            var json = JsonNode.Parse("{\"release\":\"1.4.2\",\"tags\":{\"product\":\"demo-app\",\"environment\":\"prod\"}}")!;

            var resolved = TenantKeyResolver.TryResolve(json, out var product, out _, out var tenantKey);

            Assert.True(resolved);
            Assert.Equal("demo-app", product);
            Assert.Equal("demo-app:1.4.2", tenantKey);
        }

        [Fact]
        public void TryResolve_ProjectMissing_FallsBackToProductTagArrayForm()
        {
            var json = JsonNode.Parse("{\"release\":\"1.4.2\",\"tags\":[[\"environment\",\"prod\"],[\"product\",\"demo-app\"]]}")!;

            var resolved = TenantKeyResolver.TryResolve(json, out var product, out _, out var tenantKey);

            Assert.True(resolved);
            Assert.Equal("demo-app", product);
            Assert.Equal("demo-app:1.4.2", tenantKey);
        }

        [Fact]
        public void TryResolve_ReleaseMissing_FailsAndNeverGuesses()
        {
            var json = JsonNode.Parse("{\"project\":\"demo-app\"}")!;

            var resolved = TenantKeyResolver.TryResolve(json, out var product, out var release, out var tenantKey);

            Assert.False(resolved);
            Assert.Null(product);
            Assert.Null(release);
            Assert.Null(tenantKey);
        }

        [Fact]
        public void TryResolve_NeitherProjectNorTagPresent_Fails()
        {
            var json = JsonNode.Parse("{\"release\":\"1.4.2\"}")!;

            var resolved = TenantKeyResolver.TryResolve(json, out _, out _, out var tenantKey);

            Assert.False(resolved);
            Assert.Null(tenantKey);
        }
    }
}
