# Sentry Crashes Deobfuscator

A standalone ingest proxy that sits between a .NET Reactor-obfuscated client app and Sentry. It resolves `.nrmap`-obfuscated type/method names in incoming crash envelopes back into their original, readable names before forwarding the envelope on to your real Sentry project.

## Why

[.NET Reactor](https://www.eziriz.com/dotnet_reactor.htm) renames types and methods on every obfuscated build. Without resolving those names back, Sentry crash reports show meaningless symbols (`a1b2c3d4.Xk9Qz2Wp(...)`) instead of the original method names, which makes triage far slower. This proxy keeps the real Sentry DSN and the `.nrmap` symbol maps out of the shipped client binary entirely: the client is built against a placeholder key, and this proxy swaps in the real key and resolves symbols server-side.

## Architecture

```
Obfuscated client app
      │  POST /api/{projectId}/envelope/  (placeholder public key, obfuscated stack frames)
      ▼
Sentry Crashes Deobfuscator (this proxy)
      │  1. Authenticate the request (API key)
      │  2. Decompress the envelope if gzipped
      │  3. Swap the placeholder Sentry public key for the real one
      │  4. Resolve obfuscated type/method names via the loaded .nrmap file(s)
      │  5. Re-compress and forward
      ▼
Your real Sentry project (ingest.sentry.io)
```

- `Controllers/SentryDeobfuscationController.cs` — the HTTP endpoint, auth, and the Sentry-forwarding logic.
- `Services/SentryDeobfuscationService.cs` — walks the envelope's exception stack frames and resolves obfuscated names.
- `Repositories/NRMapRepository.cs` — parses `.nrmap` files into an in-memory lookup table.

## Setup

```bash
git clone https://github.com/MuhammadAliAsgher/Dotnet-Reactor-Deobfuscator-for-Sentry.git
cd Dotnet-Reactor-Deobfuscator-for-Sentry
dotnet restore
dotnet build
```

Configure `appsettings.json` (or environment variables / `dotnet user-secrets` — never commit real values):

| Key | Purpose |
|---|---|
| `Sentry:RealDsn` | Your actual Sentry project DSN, e.g. `https://<key>@o<org>.ingest.us.sentry.io/<project-id>` |
| `Sentry:MappingPath` | Path to a `.nrmap` file, or a directory of `.nrmap` files (one per assembly) |
| `Sentry:PlaceholderPublicKey` | The dummy Sentry public key your obfuscated client build ships with (default: `fakekey1234567890abcdef1234567890ab`) |
| `Authentication:ApiKey` | Shared secret your client sends as `Authorization: Bearer <key>` |

Environment-variable equivalents (double underscore separates nesting): `Sentry__RealDsn`, `Sentry__MappingPath`, `Authentication__ApiKey`.

```bash
dotnet run --project "Sentry Crashes Deobfuscator"
```

The client app's Sentry DSN should point at this proxy instead of Sentry directly — same path shape (`/api/{projectId}/envelope/...`), same envelope format, just routed here first.

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

- `POST /api/{projectId}/envelope/{**catchall}` — the main proxy path, requires `Authorization: Bearer <ApiKey>`.
- `GET /api/{projectId}/mappings` — loaded mapping counts per assembly (no auth, no sensitive data).
- `GET /api/{projectId}/health` — health check + mapping status.
- `/swagger` (Development environment only) — interactive API explorer.

## Testing

```bash
dotnet test "Sentry Crashes Deobfuscator.sln"
```

Covers `NRMapRepository`'s `.nrmap` parsing and `SentryDeobfuscationService`'s frame-resolution logic (obfuscated frame resolved, normal .NET frame left untouched, malformed payload handled without throwing).

## License

MIT — see [LICENSE](LICENSE).
