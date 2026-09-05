using Sentry_Crashes_Deobfuscator.Repositories;

namespace Deobfuscator.Tests
{
    /// <summary>In-memory IMappingRepository for service-layer tests, no .nrmap parsing involved.</summary>
    internal class FakeMappingRepository : IMappingRepository
    {
        private readonly Dictionary<string, string> _methods = new();
        private readonly Dictionary<string, string> _types = new();

        public void AddMethod(string obfuscatedKey, string original) => _methods[obfuscatedKey] = original;
        public void AddType(string obfuscatedKey, string original) => _types[obfuscatedKey] = original;

        public bool HasMappings => _methods.Count > 0 || _types.Count > 0;
        public int TotalMappings => _methods.Count + _types.Count;

        public void LoadMappings(string path) => throw new NotSupportedException("Not used by service tests");

        public bool TryDeobfuscateMethod(string obfuscatedName, out string originalName)
        {
            if (_methods.TryGetValue(obfuscatedName, out var value))
            {
                originalName = value;
                return true;
            }
            originalName = obfuscatedName;
            return false;
        }

        public bool TryDeobfuscateType(string obfuscatedType, out string originalType)
        {
            if (_types.TryGetValue(obfuscatedType, out var value))
            {
                originalType = value;
                return true;
            }
            originalType = obfuscatedType;
            return false;
        }

        public Dictionary<string, int> GetAssemblyStats() => new();
    }
}
