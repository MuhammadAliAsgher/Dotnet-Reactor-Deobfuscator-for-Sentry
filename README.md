# Sentry Crashes Deobfuscator

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![CI](https://img.shields.io/github/actions/workflow/status/MuhammadAliAsgher/Dotnet-Reactor-Deobfuscator-for-Sentry/ci.yml?branch=main&logo=github-actions&label=CI)](https://github.com/MuhammadAliAsgher/Dotnet-Reactor-Deobfuscator-for-Sentry/actions/workflows/ci.yml)

A standalone ingest proxy that sits between a .NET Reactor-obfuscated client app and Sentry. It resolves `.nrmap`-obfuscated type/method names in incoming crash envelopes back into their original, readable names before forwarding the envelope on to your real Sentry project.

## Why

[.NET Reactor](https://www.eziriz.com/dotnet_reactor.htm) renames types and methods on every obfuscated build. Without resolving those names back, Sentry crash reports show meaningless symbols (`a1b2c3d4.Xk9Qz2Wp(...)`) instead of the original method names, which makes triage far slower. This proxy keeps the real Sentry DSN and the `.nrmap` symbol maps out of the shipped client binary entirely: the client is built against a placeholder key, and this proxy swaps in the real key and resolves symbols server-side.

## Architecture

```
Obfuscated client app
      │  POST /api/{projectId}/envelope/  (placeholder public key, obfuscated stack frames)
      ▼
Sentry Crashes Deobfuscator (this proxy)
      │  1. Authenticate the request (Bearer API key, or the DSN public key as a client key)
      │  2. Decompress the envelope if gzipped
      │  3. Swap the placeholder Sentry public key for the real one
      │  4. Resolve the event's tenant key ({product}:{release}) and look up its .nrmap
      │     (per-instance LRU+TTL cache; falls through to the blob store on a miss)
      │  5. Resolve obfuscated type/method names via that tenant's mapping
      │  6. Re-compress and forward
      ▼
Your real Sentry project (ingest.sentry.io)
```

Multi-tenant by design: one proxy instance can deobfuscate crash reports from many different app builds/releases at once, each with its own `.nrmap`. A tenant with no stored `.nrmap`, or an event with no resolvable `{product}:{release}`, never fails the request — it just forwards the raw obfuscated trace unchanged. See [`docs/adr/0001-nrmap-cache-strategy.md`](docs/adr/0001-nrmap-cache-strategy.md) for the caching tradeoff (in-process LRU vs. shared Redis) and why.

- `Controllers/SentryDeobfuscationController.cs` — the HTTP endpoint, auth, and the Sentry-forwarding logic.
- `Services/SentryDeobfuscationService.cs` — resolves the event's tenant key, then walks its exception stack frames resolving obfuscated names.
- `Repositories/TenantKeyResolver.cs` — extracts `{product}:{release}` from a Sentry event (`project`/a `product` tag, and `release`); never guesses.
- `Repositories/TenantMappingCache.cs` — the per-instance LRU+TTL cache of tenant mappings (ADR-001).
- `Repositories/INrmapBlobStore.cs` / `LocalDiskNrmapBlobStore.cs` — resolves where a tenant's `.nrmap` lives; local disk today, S3 is a documented future implementation behind the same interface.
- `Repositories/NRMapRepository.cs` — parses one tenant's `.nrmap` file into an in-memory lookup table.
- `Observability/SymbolResolverMetrics.cs` — the Prometheus counters/histogram exposed at `/metrics`.

## Setup

```bash
git clone https://github.com/MuhammadAliAsgher/Dotnet-Reactor-Deobfuscator-for-Sentry.git
cd Dotnet-Reactor-Deobfuscator-for-Sentry
dotnet restore
dotnet build
```

Configure with environment variables or `dotnet user-secrets`. Keep real values out of `appsettings.json` and never commit them:

| Key | Purpose |
|---|---|
| `Sentry:RealDsn` | Your actual Sentry project DSN, e.g. `https://<key>@o<org>.ingest.us.sentry.io/<project-id>` |
| `Sentry:MappingPath` | Root directory of the `.nrmap` blob store, laid out as `{root}/{product}/{release}.nrmap` — see **Multi-tenant mapping layout** below |
| `Sentry:PlaceholderPublicKey` | The dummy Sentry public key your obfuscated client build ships with (default: `fakekey1234567890abcdef1234567890ab`) |
| `Authentication:ApiKey` | Shared secret your client sends as `Authorization: Bearer <key>` |
| `Authentication:ClientKey` | Optional. Lets an SDK that cannot send a Bearer header authenticate with the public key of its DSN (`X-Sentry-Auth` `sentry_key=` or `?sentry_key=`). The key ships inside the app, so this is weak authentication. One key is shared by every app that uses the proxy |
| `Cache:NrmapTtlDays` | How long a tenant's mapping stays cached after its *last* access, not insertion (default: `30`) |
| `Cache:NrmapMaxEntries` | Max tenants held in the cache at once; least-recently-used is evicted first past this (default: `50`) |

Environment-variable equivalents (double underscore separates nesting): `Sentry__RealDsn`, `Sentry__MappingPath`, `Authentication__ApiKey`, `Authentication__ClientKey`, `Cache__NrmapTtlDays`, `Cache__NrmapMaxEntries`.

```bash
dotnet run --project "Sentry Crashes Deobfuscator"
```

The client app's Sentry DSN should point at this proxy instead of Sentry directly — same path shape (`/api/{projectId}/envelope/...`), same envelope format, just routed here first.

## Multi-tenant mapping layout

Each event's tenant key is `{product}:{release}` — `product` from the event's `project` field (or a `product` tag if the event doesn't carry one natively), `release` from the event's own `release` field. Resolution happens before any symbol work starts; a missing product or release is never guessed, it just means the event passes through unresolved.

`.nrmap` files live at `{Sentry:MappingPath}/{product}/{release}.nrmap`, one per tenant. A release's obfuscation map should not change after that build ships. Never delete one automatically: a missing map for an old release is a support incident (users linger on old builds for weeks after a new release), not a disk-space optimization.

The file name must equal the event's release string exactly, including case and any suffix. For a release of `1.2.3-PRODUCTION(1)` the file is `1.2.3-PRODUCTION(1).nrmap`; `1.2.3.nrmap` is never found and the events are forwarded raw. Allowed characters are letters, digits and `. _ + @ ( ) -`, up to 128. Have the build or release script copy the merged map using the same release string the app sends to Sentry.

If a map file is replaced on disk, the proxy notices the changed last-write time on the next event for that release and reloads it, so no restart is needed.

Two methods of one type can share an obfuscated name in a merged map. The proxy then resolves to the first entry without any warning, so such a frame can be mislabeled. See issue 13 in [`docs/ISSUES-AND-TODO.md`](docs/ISSUES-AND-TODO.md).

## `.nrmap` format

A synthetic sample lives at `tests/Deobfuscator.Tests/Fixtures/sample.nrmap`. Each obfuscated type block is:

```
<<type>>
{obfuscated fully-qualified type name}
{original fully-qualified type name}
{obfuscated short type name}
{reserved line}
{obfuscated method name}
{original method name}
{obfuscated method name}
{original method name}
...
```

Never commit a real `.nrmap` file — it maps your actual obfuscated build back to source, which is exactly the information the proxy exists to keep server-side.

## Endpoints

- `POST /api/{projectId}/envelope/{**catchall}` — the main proxy path, requires `Authorization: Bearer <ApiKey>`, or the DSN public key (`sentry_key`) when `Authentication:ClientKey` is set.
- `GET /api/{projectId}/mappings` — cache occupancy (how many tenants are currently cached); per-tenant hit/miss/unresolved counts live at `/metrics` instead (no auth, no sensitive data).
- `GET /api/{projectId}/health` — health check + cache occupancy.
- `GET /metrics` — Prometheus exposition format: `deobfuscator_resolution_duration_seconds`, `deobfuscator_cache_hit_total`, `deobfuscator_cache_miss_total`, `deobfuscator_unresolved_total`, `deobfuscator_resolution_errors_total`, and `deobfuscator_frames_total{product,result}` where `result` is `resolved`, `unmatched` or `skipped` (see Project.md §3.5). A rising `unmatched` count after a release usually means a missing or stale map.
- `/swagger` (Development environment only) — interactive API explorer.

## Testing

```bash
dotnet test "Sentry Crashes Deobfuscator.sln"
```

Covers `.nrmap` parsing (`NRMapRepository`), tenant-key resolution (`TenantKeyResolver` — missing product/release, both tag shapes Sentry has shipped), the blob store (`LocalDiskNrmapBlobStore`), the LRU+TTL cache (`TenantMappingCache` — cache hits return the same instance, distinct tenants resolve independently, a changed map file is reloaded), the client-key authentication, and `SentryDeobfuscationService`'s frame-resolution logic including lambda frames, per-outcome frame counts and both fallback paths (no resolvable tenant key, and a resolved tenant with no stored mapping).

Not covered by an automated test: the cache's eviction-at-capacity behavior (`Cache:NrmapMaxEntries`). The cap is wired per ADR-001; only its precise eviction timing is unverified.

Known issues and the TODO list are in [`docs/ISSUES-AND-TODO.md`](docs/ISSUES-AND-TODO.md).

## License

MIT — see [LICENSE](LICENSE).
