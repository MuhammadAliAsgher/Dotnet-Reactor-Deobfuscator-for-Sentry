# Issues and TODO

Last updated: 2026-11-03. Found while testing the proxy against a test app and a local load test. Status: OPEN unless marked DONE.

## Known issues

1. **Cold-cache stampede (OPEN, medium).** When several requests for the same release arrive while its map is not cached, each request loads and parses the map. In the load test (1000 concurrent requests, cold cache) the 4014-type map was loaded 9 times for one release. Fix: load each tenant's map once, with a per-key lock or `Lazy`, and let the other requests wait for it. File: `Repositories/TenantMappingCache.cs`.
2. **Shared per-request stats (OPEN, low).** `SentryDeobfuscationService` keeps `_lastStats` in a field on a singleton. Under concurrency the log line "Deobfuscation: N deobfuscated, N skipped, N failed" mixes counts between requests. The Prometheus counters are not affected. Fix: return stats from the call instead of storing them. Files: `Services/SentryDeobfuscationService.cs`, `Services/IDeobfuscationService.cs`, the controller.
3. **Failed forwards lose the event (OPEN, high).** If forwarding to Sentry fails (for example a connect timeout, seen as SocketException 10060), the proxy returns HTTP 500 and keeps nothing. In one test run 5 events were lost this way. Fix: a short retry with backoff and a lower timeout (default is 100 s), then a small disk queue for envelopes that still fail. File: `Controllers/SentryDeobfuscationController.cs` (`ForwardToSentry`).
4. **No rate limit or body-size cap (OPEN, medium).** Anyone holding the ClientKey (it ships inside the app DSN) can post events. Add request size limits and a per-key or per-IP rate limit before production.
5. **Header `dsn` is not rewritten (OPEN, low).** The proxy swaps the placeholder key in the trace context `public_key` only. An envelope header `dsn` containing the placeholder or client key goes to Sentry unchanged and Sentry rejects it with 400 "invalid envelope header". The .NET SDK does not appear to send it. Confirm, or rewrite it.
6. **Cache eviction at capacity has no test (OPEN, low).** `Cache:NrmapMaxEntries` is wired up, but there is no automated test of eviction.
7. **Short-name collisions in merged maps (OPEN, low).** About 80 short type names (such as `Header`, `Status`) exist in several assemblies with different original names. Only types that were not renamed are affected, but one could be rewritten to the wrong namespace. First entry wins.
8. **Log has no timestamps and gzip decoding is not logged (OPEN, low).** This made it hard to match events to a time.
9. **Single shared ClientKey for all apps (OPEN, medium).** `Authentication:ClientKey` is one key for every app that uses the proxy. It is visible in each shipped DSN, so a leak means rotating it for all apps at once, and the proxy cannot tell which app a request came from. Fix: one key per product (for example a `product -> key` map in config), checked against the event's product, with a way to rotate one key without touching the others. Files: `Controllers/SentryDeobfuscationController.cs` (`IsAuthorized`), config.
10. **Security advisories (DONE).** `System.Text.Json` 8.0.0 and `Microsoft.Extensions.Caching.Memory` 8.0.0 had NU1903 advisories. Bumped to 8.0.5 and 8.0.1.
11. **Stale cached map after a map file was replaced (DONE).** A cached tenant kept an old map, so frames stayed obfuscated after the map was rebuilt. The cache now compares the file's last-write time on each hit and reloads when it changes (`nrmap_changed` warning).
12. **Lambda frames were mangled (DONE).** `void <>c.NTr7cETsf9()` became `<>Acme.App.Program/<>c...`. The function regex now accepts the `<>` prefix. A test covers it.
13. **Duplicate obfuscated names resolve silently to the first entry (OPEN, medium).** Reactor can reuse one obfuscated name for several methods of the same signature inside one type. In a real merged map, `F78CpkZSgR` is the obfuscated name of both `<RunSentryTestKind>b__40_4` and `<RunSentryTestKind>b__40_5` in the same `<>c` type. `NRMapRepository` keeps the first entry for the plain method, `Type.Method` and `Class.Method` keys (the assembly-qualified keys keep the last), so a crash in `b__40_5` is reported as `b__40_4` and nothing signals the guess. The name alone cannot tell them apart. Fix: record every candidate per key. When a frame matches more than one, leave it raw or show all candidates, and count it as a new `ambiguous` result in `deobfuscator_frames_total`. Add a test. Files: `Repositories/NRMapRepository.cs`, `Services/SentryDeobfuscationService.cs`, `Observability/SymbolResolverMetrics.cs`. Related to issue 7.
14. **Map file name must equal the release string exactly (OPEN, medium).** The proxy reads `{MappingPath}/{product}/{release}.nrmap`, with the release taken from the event. A name that differs by even a suffix or case, for example `1.2.3.nrmap` against a release of `1.2.3-PRODUCTION(1)`, is never found, and the events are forwarded raw (counted as `unresolved`). Recommended fix, with no proxy change: the build or release script copies the merged map to `{product}/{release}.nrmap` using the same release string that the app sends to Sentry, and fails the build if the two disagree. Do not add fuzzy matching in the proxy, because it could pick the wrong map. Related TODO: automate the map copy.
15. **Stale map risk when a version is re-obfuscated (OPEN, low).** If the obfuscation project does not overwrite its previous output, a second obfuscation of the same version can leave an old map next to a new binary. Confirm that the map was written after the binary that is being tested. The proxy only notices a map file that changes on disk.

## Multi-version and multi-tenant test results (2026-11-02)

- Synthetic: two products and three releases with maps that map the same obfuscated frame to different names. Each product and release resolved with its own map in the test Sentry project. A release with no map was forwarded raw and returned 200.
- Real second build (release string with a new version): its frames came out readable in the test Sentry project. One lambda frame is ambiguous, see issue 13.
- Some events under the earlier release came from a different, unobfuscated build of the same version, so they say nothing about the map. Record which binary sends which test event.

## Load test findings (2026-11-02)

- Setup: the proxy forwarded to a local stub (50 ms delay) instead of Sentry, using a temporary `Sentry:UpstreamBaseUrl` override that has since been removed. Up to 5000 requests, 900 distinct systems, concurrency 100 to 1000, plain and gzip.
- Correctness: every request returned 200, none returned 5xx, and the stub received every forwarded envelope.
- Throughput: about 55 requests per second through the proxy. This number is **not valid**. The host was running at 78 to 89 percent CPU from other processes, and a request straight to the stub with no proxy reached only about 120 per second. Repeat the test on a quiet machine before quoting any capacity number.
- Memory: the proxy process grew to about 640 MB after roughly 12,000 requests. Check whether it settles, and whether Server GC or the 50-map cache explains it.

## TODO

- Fix issues 1 to 3 first (stampede, shared stats, failed-forward retry or queue).
- Add per-product ClientKeys (issue 9) before a second app uses the proxy.
- Repeat the load test on a quiet machine with the tool in `tools/LoadTest` (see its README), and record requests per second, p50/p95/p99 latency, status counts and memory. Try a stub with no artificial delay as well.
- Make the proxy's forward URL configurable for tests. `tools/LoadTest` needs a temporary local change to point the proxy at its stub, because the earlier `Sentry:UpstreamBaseUrl` override was removed. Decide whether to bring back a test-only override.
- Fix issue 13 (ambiguous duplicate names) before trusting lambda frames from a merged map.
- Check the one remaining raw lambda frame in the second real build against its map (issue 13) and note which binary produced each test event.
- Clean up the local repo: remove the pre-rewrite backup branch (it still holds real maps) and fast-forward local `main` to `origin/main`.
- Ask GitHub support to purge cached views of the old commits that held real maps. Treat maps that were ever pushed as exposed.
- Automate copying each release's merged `.nrmap` into `{MappingPath}/{product}/{release}.nrmap` at build or release time. Never commit a real `.nrmap`.
- Add a Sentry or log alert on `deobfuscator_frames_total{result="unmatched"}` rising after a release.
- Decide where to host production, and whether a single instance is enough (each instance keeps its own in-memory cache, see ADR-001). S3 blob store is documented but not built.
- Add the missing eviction test and a DSN-header test.

## Config added during testing

- `Authentication:ClientKey`: accepts the DSN public key (`X-Sentry-Auth` `sentry_key` or `?sentry_key=`) as authentication, for SDKs that cannot send a Bearer header. The key is visible in the shipped app, so treat it as weak authentication.
- `Cache` entries now record the map file's last-write time.
- Metric `deobfuscator_frames_total{product,result}` with `result` of `resolved`, `unmatched` or `skipped`.
