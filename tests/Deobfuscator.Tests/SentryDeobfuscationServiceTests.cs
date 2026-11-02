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

        [Fact]
        public void DeobfuscateEnvelope_LambdaFrameInCompilerGeneratedClass_ResolvesWithoutMangling()
        {
            // Real Reactor output: lambda class "<>c" is not renamed, its method is.
            var repo = new FakeMappingRepository();
            repo.AddMethod("NTr7cETsf9", "Acme.App.Program/<>c.<RunSentryTestKind>b__40_4");
            var service = BuildService(repo);

            var payload = "{\"project\":\"acme\",\"release\":\"1.2.3.4\",\"exception\":{\"values\":[{\"stacktrace\":{\"frames\":[" +
                "{\"function\":\"void <>c.NTr7cETsf9()\",\"package\":\"Acme, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null\"}" +
                "]}}]}}";

            var result = service.DeobfuscateEnvelope(BuildEnvelope(payload));

            // System.Text.Json escapes < and > as \u003C / \u003E in its output.
            var decoded = result.Replace("\\u003C", "<").Replace("\\u003E", ">");
            Assert.Contains("\"function\":\"void Acme.App.Program/<>c.<RunSentryTestKind>b__40_4()\"", decoded);
        }

        [Fact]
        public async Task DeobfuscateEnvelope_FramesCounted_ByOutcome()
        {
            var repo = new FakeMappingRepository();
            repo.AddMethod("Xk9Qz2Wp.a1b2c3d4", "Deobfuscator.Core.SymbolResolver.ResolveSymbol");
            var service = BuildService(repo);

            var payload = "{\"project\":\"FrameProbe\",\"release\":\"1.0\",\"exception\":{\"values\":[{\"stacktrace\":{\"frames\":[" +
                "{\"function\":\"Xk9Qz2Wp.a1b2c3d4(object sender)\",\"package\":\"A, Version=1.0.0.0\"}," +
                "{\"function\":\"Zq8Rt3Yu.b2c3d4e5(object sender)\",\"package\":\"A, Version=1.0.0.0\"}," +
                "{\"function\":\"MyViewModel.HandleClick(object sender)\",\"package\":\"A\"}" +
                "]}}]}}";

            service.DeobfuscateEnvelope(BuildEnvelope(payload));

            using var ms = new MemoryStream();
            await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
            var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            Assert.Contains("deobfuscator_frames_total{product=\"FrameProbe\",result=\"resolved\"} 1", text);
            Assert.Contains("deobfuscator_frames_total{product=\"FrameProbe\",result=\"unmatched\"} 1", text);
            Assert.Contains("deobfuscator_frames_total{product=\"FrameProbe\",result=\"skipped\"} 1", text);
        }
    }
}
