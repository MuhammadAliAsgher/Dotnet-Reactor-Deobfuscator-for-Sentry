using System.Text.Json.Nodes;

namespace Sentry_Crashes_Deobfuscator.Repositories
{
    /// <summary>
    /// Resolves a tenant key ("{product}:{release}") from a Sentry event payload, per
    /// Project.md §3.1. Never guesses -- a missing product or release fails resolution
    /// and the caller falls through to the §3.4 fallback path.
    /// </summary>
    public static class TenantKeyResolver
    {
        public static bool TryResolve(JsonNode eventJson, out string? product, out string? release, out string? tenantKey)
        {
            product = eventJson["project"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(product))
            {
                product = GetProductTag(eventJson);
            }

            release = eventJson["release"]?.GetValue<string>();

            if (!IsSafeSegment(product) || !IsSafeSegment(release))
            {
                product = null;
                release = null;
                tenantKey = null;
                return false;
            }

            tenantKey = $"{product}:{release}";
            return true;
        }

        /// <summary>
        /// Product and release become path segments in the blob store, so anything that
        /// could escape the store directory (separators, "..", leading dot, odd characters) is rejected.
        /// </summary>
        public static bool IsSafeSegment(string? segment) =>
            segment != null && SafeSegment.IsMatch(segment) && !segment.Contains("..");

        private static readonly System.Text.RegularExpressions.Regex SafeSegment =
            new(@"^[A-Za-z0-9][A-Za-z0-9._+@-]{0,127}$");

        /// <summary>
        /// Sentry's "tags" field has shipped as both an object map and an array of
        /// [key, value] pairs depending on SDK/relay version -- support both rather
        /// than assume one.
        /// </summary>
        private static string? GetProductTag(JsonNode eventJson)
        {
            var tagsNode = eventJson["tags"];
            if (tagsNode == null) return null;

            if (tagsNode is JsonObject tagsObject)
            {
                return tagsObject["product"]?.GetValue<string>();
            }

            if (tagsNode is JsonArray tagsArray)
            {
                foreach (var pair in tagsArray)
                {
                    if (pair is JsonArray pairArray && pairArray.Count == 2 &&
                        string.Equals(pairArray[0]?.GetValue<string>(), "product", StringComparison.OrdinalIgnoreCase))
                    {
                        return pairArray[1]?.GetValue<string>();
                    }
                }
            }

            return null;
        }
    }
}
