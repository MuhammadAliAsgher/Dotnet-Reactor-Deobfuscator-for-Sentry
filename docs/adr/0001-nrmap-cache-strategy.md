# ADR-001: In-process LRU cache vs. shared Redis cache for `.nrmap` lookups

**Status:** Accepted
**Date:** 2026-10-16

## Context

The proxy may run as N horizontally-scaled instances behind a load balancer. Each
`.nrmap` file is tied to one `{product}:{release}` tenant key, and parsing one is
expensive (large file, non-trivial deserialization). Naive per-request disk reads
would dominate resolution latency, so some form of caching is required once the
proxy is multi-tenant (see the architecture in `README.md`'s "How it's built"
section).

## Options considered

**Option A — Per-instance in-memory LRU cache** (`Microsoft.Extensions.Caching.Memory.MemoryCache`)
- Pros: zero network hop, zero serialization cost, simplest to implement.
- Cons: N instances redundantly parse the same map; cache is cold on every
  instance restart/deploy.

**Option B — Shared Redis cache**
- Pros: parse cost paid once cluster-wide; cache survives individual instance
  restarts.
- Cons: network round-trip per lookup, serialization cost for a potentially
  large map object, adds Redis as an operational dependency.

## Decision

**Option A (per-instance LRU)**, implemented in `TenantMappingCache`. Promote to
Option B only if instance count and parse cost both justify it — i.e. if
`deobfuscator_cache_miss_total` × average parse time shows redundant-parse cost
across instances exceeding what Redis's network/serialization overhead would
cost instead. Ship the smaller, provable increment first; measure before adding
a Redis dependency.

### Eviction policy (applies to either option): TTL + LRU

- **TTL: 30 days from last access**, not from insertion (`Cache:NrmapTtlDays`,
  implemented as `MemoryCacheEntryOptions.SlidingExpiration`). Rationale: users
  linger on old app builds for weeks after a new release ships, and their crash
  reports still need the old release's map. An eviction policy keyed to "new
  release deployed" is explicitly rejected — it would break resolution for
  exactly the in-the-wild old-build crashes that most need it.
- **LRU as the secondary bound**, via `MemoryCacheOptions.SizeLimit`
  (`Cache:NrmapMaxEntries`, default 50): caps memory by tenant-entry count,
  evicting the least-recently-used tenant key first once the cap is hit,
  independent of TTL.

### Consequences

Memory is bounded by `Cache:NrmapMaxEntries`, not by tenant-key cardinality
directly. A sudden spike of many concurrent releases (e.g. a staged rollout
across many product lines) can evict a legitimately-still-needed map before its
TTL — this is acceptable: it falls through to the fallback path (the proxy
re-reads the tenant's `.nrmap` from the blob store on the next request for it,
same as any other cache miss), not data loss. If no `.nrmap` exists at all for
the resolved tenant key, the envelope's raw obfuscated trace is forwarded
unchanged rather than dropped or failed.
