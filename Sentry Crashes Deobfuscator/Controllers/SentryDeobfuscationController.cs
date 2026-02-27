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
        private readonly IMappingRepository _mappingRepository;
        private readonly IDeobfuscationService _deobfuscationService;
        private readonly string _realDsn;
        private readonly string _realPublicKey;
        private readonly string _realProjectId;
        private readonly string _loginSalt;
        private readonly IConfiguration _configuration;

        public SentryDeobfuscationController(
            IHttpClientFactory clientFactory,
            IConfiguration config,
            ILogger<SentryDeobfuscationController> logger,
            IMappingRepository mappingRepository,
            IDeobfuscationService deobfuscationService)
        {
            _clientFactory = clientFactory;
            _logger = logger;
            _mappingRepository = mappingRepository;
            _deobfuscationService = deobfuscationService;
            _configuration = config;

            _realDsn = config["Sentry:RealDsn"] ?? throw new InvalidOperationException("Sentry:RealDsn missing");
            
            // Get LoginSalt from config or use default (same as Dark Web Monitoring)
            var saltConfig = config["Authentication:LoginSalt"];
            if (!string.IsNullOrEmpty(saltConfig))
            {
                _loginSalt = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(saltConfig)).Split(',')[0];
            }
            else
            {
                _loginSalt = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String("ROTATED-SECRET-REMOVED-FROM-HISTORY")).Split(',')[0];
            }

            var uri = new Uri(_realDsn);
            _realProjectId = uri.AbsolutePath.TrimStart('/');
            _realPublicKey = uri.UserInfo;

            LogConfiguration();
        }

        private void LogConfiguration()
        {
            _logger.LogInformation("Sentry Deobfuscation Proxy Ready");
            _logger.LogInformation("Real Project ID: {ProjectId}, Mappings: {Count}", 
                _realProjectId, _mappingRepository.TotalMappings);

            if (!_mappingRepository.HasMappings)
            {
                _logger.LogError("WARNING: ZERO MAPPINGS LOADED! Deobfuscation will NOT work!");
            }
        }

        /// <summary>
        /// Validate MD5 hash authentication
        /// </summary>
        private bool ValidateAuthentication(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return false;
            }

            if (!Request.Headers.TryGetValue("Token", out var tokenHeader))
            {
                _logger.LogWarning("Missing Token header");
                return false;
            }

            var expectedHash = ComputeMD5Hash(_loginSalt + deviceId);
            var providedHash = tokenHeader.ToString();

            var isValid = string.Equals(expectedHash, providedHash, StringComparison.OrdinalIgnoreCase);
            
            if (!isValid)
            {
                _logger.LogWarning("Authentication failed. Expected: {Expected}, Provided: {Provided}", 
                    expectedHash, providedHash);
            }

            return isValid;
        }

        /// <summary>
        /// Compute MD5 hash
        /// </summary>
        private string ComputeMD5Hash(string input)
        {
            using (var md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Handle Sentry envelope POST requests
        /// </summary>
        [HttpPost("envelope/{**catchall}")]
        public async Task<IActionResult> HandleEnvelope(string projectId, string? catchall = null)
        {
            // Extract deviceId from query string or headers
            var deviceId = Request.Query["device_id"].ToString();
            if (string.IsNullOrEmpty(deviceId))
            {
                deviceId = Request.Headers["X-Device-Id"].ToString();
            }

            if (string.IsNullOrEmpty(deviceId))
            {
                _logger.LogWarning("Missing device_id in request");
                return Unauthorized(new { message = "Missing device_id" });
            }

            // Validate MD5 hash authentication
            if (!ValidateAuthentication(deviceId))
            {
                _logger.LogWarning("Authentication failed for device_id: {DeviceId}", deviceId);
                return Unauthorized(new { message = "Invalid authentication token" });
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

            // Deobfuscate stack traces
            if (_mappingRepository.HasMappings)
            {
                envelopeText = _deobfuscationService.DeobfuscateEnvelope(envelopeText);

                var stats = _deobfuscationService.GetLastStats();
                _logger.LogInformation("Deobfuscation: {Deobf} deobfuscated, {Skip} skipped, {Fail} failed",
                    stats.FramesDeobfuscated, stats.FramesSkipped, stats.FramesFailed);
            }
            else
            {
                _logger.LogWarning("Skipping deobfuscation - No mappings loaded");
            }

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
            var stats = _mappingRepository.GetAssemblyStats();

            return Ok(new
            {
                totalMappings = _mappingRepository.TotalMappings,
                assemblies = stats.Select(kvp => new
                {
                    assembly = kvp.Key,
                    methodCount = kvp.Value
                }).ToList(),
                status = _mappingRepository.HasMappings ? "OK" : "ERROR - No mappings loaded"
            });
        }

        [HttpGet("health")]
        [AllowAnonymous]
        public IActionResult Health()
        {
            var stats = _mappingRepository.GetAssemblyStats();
            var isHealthy = _mappingRepository.HasMappings;

            return Ok(new
            {
                status = isHealthy ? "healthy" : "unhealthy",
                proxyFor = _realProjectId,
                mappingsLoaded = _mappingRepository.TotalMappings,
                assemblies = stats,
                warnings = !isHealthy ? new[] { "No mapping files loaded - deobfuscation will not work!" } : Array.Empty<string>()
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
                "\"public_key\":\"fakekey1234567890abcdef1234567890ab\"",
                $"\"public_key\":\"{_realPublicKey}\"",
                StringComparison.OrdinalIgnoreCase
            );

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

            var realUrl = $"https://o4510792744239104.ingest.us.sentry.io/api/{_realProjectId}/envelope/";

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

    public class TestDeobfuscateRequest
    {
        public string ObfuscatedName { get; set; } = "";
    }
}
