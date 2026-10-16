using Microsoft.Extensions.Logging.Abstractions;
using Sentry_Crashes_Deobfuscator.Observability;
using Sentry_Crashes_Deobfuscator.Services;
using Xunit;

namespace Deobfuscator.Tests
{
    public class SentryDeobfuscationServiceTests
    {
        private static string BuildEnvelope(string itemPayloadJson) =>
            "{\"event_id\":\"abc123\"}\n{\"type\":\"event\",\"length\":1}\n" + itemPayloadJson;

        private static SentryDeobfuscationService BuildService(FakeMappingRepository? repo = null) =>
            new(
                new FakeTenantMappingCache(repo),
                new SymbolResolverMetrics(),
                NullLogger<SentryDeobfuscationService>.Instance);

        [Fact]
        public void DeobfuscateEnvelope_ObfuscatedFrame_ReplacesFunctionAndFilename()
        {
            var repo = new FakeMappingRepository();
            repo.AddMethod("Xk9Qz2Wp.a1b2c3d4", "Deobfuscator.Core.SymbolResolver.ResolveSymbol");
            var service = BuildService(repo);

            var payload = "{\"project\":\"demo-app\",\"release\":\"1.4.2\",\"exception\":{\"values\":[{\"stacktrace\":{\"frames\":[" +
                "{\"function\":\"Xk9Qz2Wp.a1b2c3d4(object sender)\",\"package\":\"MyAssembly, Version=1.0.0.0\"}" +
                "]}}]}}";

            var result = service.DeobfuscateEnvelope(BuildEnvelope(payload));
            var stats = service.GetLastStats();

            Assert.Contains("\"function\":\"Deobfuscator.Core.SymbolResolver.ResolveSymbol(object sender)\"", result);
            Assert.Contains("\"filename\":\"SymbolResolver.cs\"", result);
            Assert.Equal(1, stats.FramesDeobfuscated);
            Assert.Equal(0, stats.FramesFailed);
        }

        [Fact]
        public void DeobfuscateEnvelope_NormalDotNetFrame_LeftUnchanged()
        {
            var repo = new FakeMappingRepository();
            var service = BuildService(repo);

            var payload = "{\"project\":\"demo-app\",\"release\":\"1.4.2\",\"exception\":{\"values\":[{\"stacktrace\":{\"frames\":[" +
                "{\"function\":\"MyViewModel.HandleClick(object sender, EventArgs e)\",\"package\":\"MyApp\"}" +
                "]}}]}}";

            var result = service.DeobfuscateEnvelope(BuildEnvelope(payload));
            var stats = service.GetLastStats();

            Assert.Contains("\"function\":\"MyViewModel.HandleClick(object sender, EventArgs e)\"", result);
            Assert.Equal(0, stats.FramesDeobfuscated);
            Assert.Equal(1, stats.FramesSkipped);
        }

        [Fact]
        public void DeobfuscateEnvelope_MalformedPayload_ReturnsOriginalTextUnchanged()
        {
            var service = BuildService();

            var envelope = BuildEnvelope("{not valid json");

            var result = service.DeobfuscateEnvelope(envelope);

            Assert.Equal(envelope, result);
        }

        [Fact]
        public void DeobfuscateEnvelope_FewerThanThreeLines_ReturnsOriginalTextUnchanged()
        {
            var service = BuildService();

            var envelope = "{\"event_id\":\"abc123\"}\n{\"type\":\"event\"}";

            var result = service.DeobfuscateEnvelope(envelope);

            Assert.Equal(envelope, result);
        }

        [Fact]
        public void DeobfuscateEnvelope_MissingProjectAndRelease_FallsThroughUnchanged()
        {
            // Project.md §3.1: never guess a tenant key -- a missing product/release
            // must fall through to the §3.4 fallback (forward the raw trace), even
            // when a mapping repository would otherwise be available.
            var repo = new FakeMappingRepository();
            repo.AddMethod("Xk9Qz2Wp.a1b2c3d4", "Deobfuscator.Core.SymbolResolver.ResolveSymbol");
            var service = BuildService(repo);

            var payload = "{\"exception\":{\"values\":[{\"stacktrace\":{\"frames\":[" +
                "{\"function\":\"Xk9Qz2Wp.a1b2c3d4(object sender)\",\"package\":\"MyAssembly, Version=1.0.0.0\"}" +
                "]}}]}}";

            var result = service.DeobfuscateEnvelope(BuildEnvelope(payload));

            Assert.Contains("\"function\":\"Xk9Qz2Wp.a1b2c3d4(object sender)\"", result);
        }

        [Fact]
        public void DeobfuscateEnvelope_TenantResolvesButNoMappingFound_ForwardsRawTraceUnchanged()
        {
            // Project.md §3.4 fallback: tenant key resolves fine, but no .nrmap exists for it.
            var service = BuildService(repo: null);

            var payload = "{\"project\":\"demo-app\",\"release\":\"1.4.2\",\"exception\":{\"values\":[{\"stacktrace\":{\"frames\":[" +
                "{\"function\":\"Xk9Qz2Wp.a1b2c3d4(object sender)\",\"package\":\"MyAssembly, Version=1.0.0.0\"}" +
                "]}}]}}";

            var result = service.DeobfuscateEnvelope(BuildEnvelope(payload));

            Assert.Contains("\"function\":\"Xk9Qz2Wp.a1b2c3d4(object sender)\"", result);
        }
    }
}
