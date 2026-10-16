using Prometheus;

namespace Sentry_Crashes_Deobfuscator.Observability
{
    /// <summary>
    /// Wraps the Prometheus counters/histogram for .nrmap resolution, per Project.md §3.5.
    /// Registered as a singleton so all requests share the same metric instances.
    /// </summary>
    public class SymbolResolverMetrics
    {
        private readonly Histogram _resolutionDuration = Prometheus.Metrics.CreateHistogram(
            "deobfuscator_resolution_duration_seconds",
            "Time to resolve a tenant's .nrmap mapping, including cache lookup and any disk parse.",
            new HistogramConfiguration { LabelNames = new[] { "product", "cache_result" } });

        private readonly Counter _cacheHits = Prometheus.Metrics.CreateCounter(
            "deobfuscator_cache_hit_total",
            "Count of tenant mapping lookups served from the in-process cache.",
            new CounterConfiguration { LabelNames = new[] { "product" } });

        private readonly Counter _cacheMisses = Prometheus.Metrics.CreateCounter(
            "deobfuscator_cache_miss_total",
            "Count of tenant mapping lookups not found in cache (blob store was consulted).",
            new CounterConfiguration { LabelNames = new[] { "product" } });

        private readonly Counter _unresolved = Prometheus.Metrics.CreateCounter(
            "deobfuscator_unresolved_total",
            "Count of events where no .nrmap could be found for the resolved tenant key (fallback path, §3.4).",
            new CounterConfiguration { LabelNames = new[] { "product" } });

        private readonly Counter _resolutionErrors = Prometheus.Metrics.CreateCounter(
            "deobfuscator_resolution_errors_total",
            "Count of malformed input or resolver exceptions encountered during symbol resolution.",
            new CounterConfiguration { LabelNames = new[] { "product", "error_type" } });

        public void RecordResolutionDuration(string product, string cacheResult, double seconds) =>
            _resolutionDuration.WithLabels(product, cacheResult).Observe(seconds);

        public void RecordCacheHit(string product) => _cacheHits.WithLabels(product).Inc();

        public void RecordCacheMiss(string product) => _cacheMisses.WithLabels(product).Inc();

        public void RecordUnresolved(string product) => _unresolved.WithLabels(product).Inc();

        public void RecordResolutionError(string product, string errorType) =>
            _resolutionErrors.WithLabels(product, errorType).Inc();
    }
}
