using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentry_Crashes_Deobfuscator.Repositories;
using Sentry_Crashes_Deobfuscator.Services;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;

namespace Sentry_Crashes_Deobfuscator.Controllers
{
    [ApiController]
    [Route("api/{projectId}")]
    public class SentryDeobfuscationController : ControllerBase
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly ILogger<SentryDeobfuscationController> _logger;
        private readonly ITenantMappingCache _tenantMappingCache;
        private readonly IDeobfuscationService _deobfuscationService;
        private readonly string _realDsn;
        private readonly string _realPublicKey;
        private readonly string _realProjectId;
        private readonly string _realIngestHost;
        private readonly string _apiKey;
        private readonly string _placeholderPublicKey;
        private readonly string? _clientKey;

        public SentryDeobfuscationController(
            IHttpClientFactory clientFactory,
            IConfiguration config,
            ILogger<SentryDeobfuscationController> logger,
            ITenantMappingCache tenantMappingCache,
            IDeobfuscationService deobfuscationService)
        {
            _clientFactory = clientFactory;
            _logger = logger;
            _tenantMappingCache = tenantMappingCache;
            _deobfuscationService = deobfuscationService;

            var realDsn = config["Sentry:RealDsn"];
            if (string.IsNullOrEmpty(realDsn)) throw new InvalidOperationException("Sentry:RealDsn missing");
            _realDsn = realDsn;

            var apiKey = config["Authentication:ApiKey"];
            if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("Authentication:ApiKey missing");
            _apiKey = apiKey;

            _placeholderPublicKey = config["Sentry:PlaceholderPublicKey"] ?? "fakekey1234567890abcdef1234567890ab";

            _clientKey = config["Authentication:ClientKey"];
            if (string.IsNullOrEmpty(_clientKey)) _clientKey = null;

            var uri = new Uri(_realDsn);
            _realProjectId = uri.AbsolutePath.TrimStart('/');
            _realPublicKey = uri.UserInfo;
            _realIngestHost = uri.Host;

            LogConfiguration();
        }

        private void LogConfiguration()
        {
            _logger.LogInformation("Sentry Deobfuscation Proxy Ready (multi-tenant, per-request .nrmap resolution)");
            _logger.LogInformation("Real Project ID: {ProjectId}", _realProjectId);
        }

        /// <summary>
        /// Accepts EITHER "Authorization: Bearer {ApiKey}" OR a Sentry DSN public key equal to
        /// Authentication:ClientKey (X-Sentry-Auth "sentry_key=" or ?sentry_key=), for SDKs that
        /// cannot add custom headers. Both compares are constant-time.
        /// </summary>
        private bool ValidateAuthentication() => IsAuthorized(
            Request.Headers["Authorization"].ToString(),
            Request.Headers["X-Sentry-Auth"].ToString(),
            Request.Query["sentry_key"].ToString(),
            _apiKey, _clientKey);

        public static bool IsAuthorized(string? authorization, string? xSentryAuth, string? querySentryKey,
            string apiKey, string? clientKey)
        {
            const string prefix = "Bearer ";
            if (!string.IsNullOrEmpty(authorization)
                && authorization.StartsWith(prefix, StringComparison.Ordinal)
                && FixedEquals(apiKey, authorization[prefix.Length..]))
                return true;

            if (string.IsNullOrEmpty(clientKey)) return false;

            var provided = querySentryKey;
            if (string.IsNullOrEmpty(provided) && !string.IsNullOrEmpty(xSentryAuth))
            {
                foreach (var part in xSentryAuth.Split(',', StringSplitOptions.TrimEntries))
                {
                    var kv = part.Replace("Sentry ", "", StringComparison.Ordinal).Trim();
                    if (kv.StartsWith("sentry_key=", StringComparison.Ordinal))
                        provided = kv["sentry_key=".Length..];
                }
            }
            return !string.IsNullOrEmpty(provided) && FixedEquals(clientKey, provided);
        }

        private static bool FixedEquals(string expected, string provided)
        {
            var e = Encoding.UTF8.GetBytes(expected);
            var p = Encoding.UTF8.GetBytes(provided);
            return e.Length == p.Length && CryptographicOperations.FixedTimeEquals(e, p);
        }

        /// <summary>
        /// Handle Sentry envelope POST requests
        /// </summary>
        [HttpPost("envelope/{**catchall}")]
        public async Task<IActionResult> HandleEnvelope(string projectId, string? catchall = null)
        {
            if (!ValidateAuthentication())
            {
                _logger.LogWarning("Authentication failed");
                return Unauthorized(new { message = "Invalid or missing API key" });
            }

            _logger.LogInformation("Received Sentry envelope: {Size} bytes", Request.ContentLength ?? 0);

            // Read raw body
            using var memoryStream = new MemoryStream();
            await Request.Body.CopyToAsync(memoryStream);
            var rawBytes = memoryStream.ToArray();

            // Check compression
            bool isGzipped = rawBytes.Length >= 2 && rawBytes[0] == 0x1F && rawBytes[1] == 0x8B;

            // Decompress if needed
            string envelopeText = isGzipped
                ? await DecompressGzip(rawBytes)
                : Encoding.UTF8.GetString(rawBytes);

            // Replace fake public key with real one
            envelopeText = ReplaceFakePublicKey(envelopeText);

            // Deobfuscate stack traces. Mapping resolution is now per-tenant and per-request
            // (Project.md §3) -- a tenant with no cached/stored .nrmap falls through to the
            // §3.4 fallback inside the service and the envelope passes through unchanged.
            envelopeText = _deobfuscationService.DeobfuscateEnvelope(envelopeText);

            var stats = _deobfuscationService.GetLastStats();
            _logger.LogInformation("Deobfuscation: {Deobf} deobfuscated, {Skip} skipped, {Fail} failed",
                stats.FramesDeobfuscated, stats.FramesSkipped, stats.FramesFailed);

            // Re-compress if needed
            byte[] finalBytes = isGzipped
                ? await CompressGzip(envelopeText)
                : Encoding.UTF8.GetBytes(envelopeText);

            // Forward to real Sentry
            var result = await ForwardToSentry(finalBytes, isGzipped);

            return result;
        }

        [HttpGet("mappings")]
        [AllowAnonymous]
        public IActionResult GetMappings()
        {
            return Ok(new
            {
                architecture = "multi-tenant: .nrmap resolved per {product}:{release} tenant key, LRU+TTL cached (Project.md §3)",
                cachedTenants = _tenantMappingCache.CachedTenantCount,
                note = "Per-tenant hit/miss/unresolved counts are exposed via Prometheus at /metrics " +
                       "(deobfuscator_cache_hit_total, deobfuscator_cache_miss_total, deobfuscator_unresolved_total), not here."
            });
        }

        [HttpGet("health")]
        [AllowAnonymous]
        public IActionResult Health()
        {
            return Ok(new
            {
                status = "healthy",
                proxyFor = _realProjectId,
                cachedTenants = _tenantMappingCache.CachedTenantCount
            });
        }

        #region Private Helper Methods

        private async Task<string> DecompressGzip(byte[] compressedBytes)
        {
            using var compressedStream = new MemoryStream(compressedBytes);
            using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream();
            await gzipStream.CopyToAsync(decompressedStream);
            return Encoding.UTF8.GetString(decompressedStream.ToArray());
        }

        private async Task<byte[]> CompressGzip(string text)
        {
            using var outputStream = new MemoryStream();
            using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Optimal))
            {
                var textBytes = Encoding.UTF8.GetBytes(text);
                await gzipStream.WriteAsync(textBytes, 0, textBytes.Length);
            }
            return outputStream.ToArray();
        }

        private string ReplaceFakePublicKey(string envelopeText)
        {
            var updated = envelopeText.Replace(
                $"\"public_key\":\"{_placeholderPublicKey}\"",
                $"\"public_key\":\"{_realPublicKey}\"",
                StringComparison.OrdinalIgnoreCase
            );

            if (_clientKey != null)
                updated = updated.Replace(
                    $"\"public_key\":\"{_clientKey}\"",
                    $"\"public_key\":\"{_realPublicKey}\"",
                    StringComparison.OrdinalIgnoreCase);

            return updated;
        }

        private async Task<IActionResult> ForwardToSentry(byte[] finalBytes, bool isGzipped)
        {
            var content = new ByteArrayContent(finalBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-sentry-envelope");

            if (isGzipped)
            {
                content.Headers.ContentEncoding.Add("gzip");
            }

            var realUrl = $"https://{_realIngestHost}/api/{_realProjectId}/envelope/";

            try
            {
                var client = _clientFactory.CreateClient();

                var authHeader = $"Sentry sentry_version=7, sentry_key={_realPublicKey}, sentry_client=sentry.dotnet/6.0.0";
                content.Headers.TryAddWithoutValidation("X-Sentry-Auth", authHeader);

                var response = await client.PostAsync(realUrl, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully forwarded to Sentry: {StatusCode}", response.StatusCode);
                }
                else
                {
                    _logger.LogError("Sentry rejected envelope: {StatusCode} - {Body}", 
                        response.StatusCode, responseBody);
                }

                return StatusCode((int)response.StatusCode, responseBody);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while forwarding to Sentry");
                return StatusCode(500, ex.Message);
            }
        }

        #endregion
    }
}
