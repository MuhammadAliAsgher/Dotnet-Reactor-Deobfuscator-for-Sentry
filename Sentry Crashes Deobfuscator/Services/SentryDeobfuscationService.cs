using Sentry_Crashes_Deobfuscator.Observability;
using Sentry_Crashes_Deobfuscator.Repositories;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sentry_Crashes_Deobfuscator.Services
{
    public class SentryDeobfuscationService : IDeobfuscationService
    {
        private const string UnknownProduct = "unknown";

        private readonly ITenantMappingCache _tenantMappingCache;
        private readonly SymbolResolverMetrics _metrics;
        private readonly ILogger<SentryDeobfuscationService> _logger;
        private DeobfuscationStats _lastStats = new();

        public SentryDeobfuscationService(
            ITenantMappingCache tenantMappingCache,
            SymbolResolverMetrics metrics,
            ILogger<SentryDeobfuscationService> logger)
        {
            _tenantMappingCache = tenantMappingCache;
            _metrics = metrics;
            _logger = logger;
        }

        public DeobfuscationStats GetLastStats() => _lastStats;

        public string DeobfuscateEnvelope(string envelopeText)
        {
            try
            {
                var lines = envelopeText.Split('\n');

                if (lines.Length < 3)
                {
                    return envelopeText;
                }

                _lastStats = new DeobfuscationStats();

                string envelopeHeader = lines[0];
                string itemHeader = lines[1];
                string itemPayload = lines[2];

                JsonNode? json;
                try
                {
                    json = JsonNode.Parse(itemPayload);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse JSON payload");
                    _metrics.RecordResolutionError(UnknownProduct, nameof(JsonException));
                    return envelopeText;
                }

                if (json == null)
                {
                    return envelopeText;
                }

                // Tenant key resolution (Project.md §3.1) -- never guess. A missing
                // product/release falls through to the §3.4 fallback: forward unchanged.
                if (!TenantKeyResolver.TryResolve(json, out var product, out var release, out _))
                {
                    _logger.LogWarning("tenant_key_resolution_failed: event carries no resolvable product/release");
                    return envelopeText;
                }

                if (!_tenantMappingCache.TryGet(product!, release!, out var mappingRepository) || mappingRepository == null)
                {
                    // §3.4 fallback: no .nrmap for this tenant. Forward the raw trace unchanged,
                    // never drop or fail the request.
                    return envelopeText;
                }

                DeobfuscateEventPayload(json, mappingRepository, product!);

                itemPayload = json.ToJsonString();

                var itemHeaderJson = JsonNode.Parse(itemHeader);
                if (itemHeaderJson != null && itemHeaderJson["length"] != null)
                {
                    itemHeaderJson["length"] = Encoding.UTF8.GetByteCount(itemPayload);
                    itemHeader = itemHeaderJson.ToJsonString();
                }

                return $"{envelopeHeader}\n{itemHeader}\n{itemPayload}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deobfuscate envelope");
                _metrics.RecordResolutionError(UnknownProduct, ex.GetType().Name);
                return envelopeText;
            }
        }

        private void DeobfuscateEventPayload(JsonNode eventJson, IMappingRepository mappingRepository, string product)
        {
            var exceptions = eventJson["exception"]?["values"]?.AsArray();
            if (exceptions == null)
            {
                return;
            }

            foreach (var exception in exceptions)
            {
                var stacktrace = exception?["stacktrace"];
                if (stacktrace == null) continue;

                var frames = stacktrace["frames"]?.AsArray();
                if (frames == null) continue;

                for (int i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];
                    if (frame == null) continue;

                    var result = DeobfuscateStackFrame(frame, mappingRepository);

                    switch (result)
                    {
                        case FrameResult.Deobfuscated:
                            _lastStats.FramesDeobfuscated++;
                            _metrics.RecordFrame(product, "resolved");
                            break;
                        case FrameResult.Skipped:
                            _lastStats.FramesSkipped++;
                            _metrics.RecordFrame(product, "skipped");
                            break;
                        case FrameResult.Failed:
                            _lastStats.FramesFailed++;
                            _metrics.RecordFrame(product, "unmatched");
                            break;
                    }
                }
            }
        }

        private enum FrameResult
        {
            Skipped,
            Deobfuscated,
            Failed
        }

        private FrameResult DeobfuscateStackFrame(JsonNode frame, IMappingRepository mappingRepository)
        {
            var functionStr = frame["function"]?.GetValue<string>();

            if (string.IsNullOrEmpty(functionStr))
            {
                return FrameResult.Skipped;
            }

            var (typeName, methodName) = ParseFunctionSignature(functionStr);

            if (!LooksObfuscated(methodName))
            {
                return FrameResult.Skipped;
            }

            // Extract assembly info from package
            string? assemblyName = null;
            string? assemblyVersion = null;
            var packageStr = frame["package"]?.GetValue<string>();

            if (!string.IsNullOrEmpty(packageStr))
            {
                var parts = packageStr.Split(',');
                if (parts.Length > 0)
                {
                    assemblyName = parts[0].Trim();
                }
                if (parts.Length > 1)
                {
                    assemblyVersion = parts[1].Replace("Version=", "").Replace(" ", "").Trim();
                }
            }

            // Try multiple lookup strategies using the repository
            bool found = false;
            string? originalType = null;
            string? originalMethod = null;

            // Strategy 1: Assembly-qualified lookup
            if (assemblyName != null && assemblyVersion != null && !string.IsNullOrEmpty(typeName))
            {
                var key1 = $"{assemblyName}_{assemblyVersion}:{typeName}.{methodName}";

                if (mappingRepository.TryDeobfuscateMethod(key1, out var result1))
                {
                    ParseDeobfuscatedResult(result1, out originalType, out originalMethod);
                    found = true;
                }
            }

            // Strategy 2: Type.Method
            if (!found && !string.IsNullOrEmpty(typeName))
            {
                var key2 = $"{typeName}.{methodName}";

                if (mappingRepository.TryDeobfuscateMethod(key2, out var result2))
                {
                    ParseDeobfuscatedResult(result2, out originalType, out originalMethod);
                    found = true;
                }
            }

            // Strategy 3: Just method name
            if (!found)
            {
                var key3 = methodName;

                if (mappingRepository.TryDeobfuscateMethod(key3, out var result3))
                {
                    ParseDeobfuscatedResult(result3, out originalType, out originalMethod);
                    found = true;
                }
            }

            // Strategy 4: Try type separately
            if (!found && !string.IsNullOrEmpty(typeName))
            {
                if (mappingRepository.TryDeobfuscateType(typeName, out var deobfType))
                {
                    originalType = deobfType;

                    var key4 = $"{deobfType}.{methodName}";

                    if (mappingRepository.TryDeobfuscateMethod(key4, out var result4))
                    {
                        ParseDeobfuscatedResult(result4, out originalType, out originalMethod);
                        found = true;
                    }
                }
            }

            if (!found)
            {
                _logger.LogDebug("No mapping found for: {Method}", methodName);
                return FrameResult.Failed;
            }

            ApplyDeobfuscation(frame, functionStr, typeName, methodName, originalType, originalMethod);

            return FrameResult.Deobfuscated;
        }

        private (string Type, string Method) ParseFunctionSignature(string functionStr)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                functionStr,
                @"((?:<>)?\w+)\.(\w+)\s*\(" // "<>c" = compiler-generated lambda class
            );

            if (match.Success)
            {
                var type = match.Groups[1].Value;
                var method = match.Groups[2].Value;
                return (type, method);
            }

            var simpleMatch = System.Text.RegularExpressions.Regex.Match(
                functionStr,
                @"(\w+)\s*\("
            );

            if (simpleMatch.Success)
            {
                return ("", simpleMatch.Groups[1].Value);
            }

            return ("", functionStr.Trim());
        }

        private void ParseDeobfuscatedResult(string result, out string? type, out string? method)
        {
            var parts = result.Split('.');

            if (parts.Length >= 2)
            {
                method = parts[^1];
                type = string.Join(".", parts[..^1]);
            }
            else
            {
                method = result;
                type = null;
            }
        }

        private void ApplyDeobfuscation(
            JsonNode frame,
            string originalFunctionStr,
            string obfuscatedType,
            string obfuscatedMethod,
            string? deobfuscatedType,
            string? deobfuscatedMethod)
        {
            if (deobfuscatedMethod != null)
            {
                var newFunction = originalFunctionStr.Replace(obfuscatedMethod, deobfuscatedMethod);

                if (deobfuscatedType != null && !string.IsNullOrEmpty(obfuscatedType))
                {
                    newFunction = newFunction.Replace($"{obfuscatedType}.", $"{deobfuscatedType}.");

                    var className = deobfuscatedType.Split('.').Last();
                    frame["filename"] = $"{className}.cs";
                }

                frame["function"] = newFunction;

                _logger.LogDebug("Deobfuscated: {Original} → {New}", originalFunctionStr, newFunction);
            }
        }

        private bool LooksObfuscated(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.Length <= 2)
                return true;

            if (name.Any(c => c > 127 || char.IsControl(c)))
                return true;

            if (name.Length >= 8)
            {
                int upperCount = name.Count(char.IsUpper);
                int digitCount = name.Count(char.IsDigit);

                if (upperCount > name.Length * 0.5)
                    return true;

                if (digitCount > 0 && digitCount < name.Length)
                {
                    for (int i = 0; i < name.Length - 1; i++)
                    {
                        if (char.IsDigit(name[i]) != char.IsDigit(name[i + 1]))
                        {
                            return true;
                        }
                    }
                }
            }

            var normalPatterns = new[]
            {
                "Handle", "Process", "Execute", "Initialize", "Update",
                "Create", "Delete", "Get", "Set", "Load", "Save",
                "Async", "Task", "Invoke", "Callback", "Event",
                "OnClick", "OnLoad", "OnInit", "Button", "View", "Model",
                ".ctor", "MoveNext"
            };

            foreach (var pattern in normalPatterns)
            {
                if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (name.EndsWith("ViewModel") || name.EndsWith("Service") ||
                name.EndsWith("Controller") || name.EndsWith("Manager") ||
                name.EndsWith("Helper") || name.EndsWith("Provider"))
            {
                return false;
            }

            var consecutiveUpper = 0;
            var maxConsecutiveUpper = 0;

            foreach (var c in name)
            {
                if (char.IsUpper(c))
                {
                    consecutiveUpper++;
                    maxConsecutiveUpper = Math.Max(maxConsecutiveUpper, consecutiveUpper);
                }
                else
                {
                    consecutiveUpper = 0;
                }
            }

            if (maxConsecutiveUpper >= 3 && name.Any(char.IsLower))
                return true;

            return false;
        }
    }
}
