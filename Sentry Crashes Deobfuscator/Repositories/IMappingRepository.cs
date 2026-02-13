namespace Sentry_Crashes_Deobfuscator.Repositories
{

    public interface IMappingRepository
    {
        void LoadMappings(string path);

        bool TryDeobfuscateMethod(string obfuscatedName, out string originalName);

        bool TryDeobfuscateType(string obfuscatedType, out string originalType);

        Dictionary<string, int> GetAssemblyStats();

        int TotalMappings { get; }

        bool HasMappings { get; }
    }
}