namespace Sentry_Crashes_Deobfuscator.Repositories
{
    public class NRMapRepository : IMappingRepository
    {
        private readonly Dictionary<string, MethodMapping> _methodMappings = new();
        private readonly Dictionary<string, string> _typeMappings = new();
        private readonly ILogger<NRMapRepository> _logger;

        public class MethodMapping
        {
            public string ObfuscatedName { get; set; } = "";
            public string OriginalName { get; set; } = "";
            public string ObfuscatedTypeName { get; set; } = "";
            public string OriginalTypeName { get; set; } = "";
            public string SourceAssembly { get; set; } = "";
        }

        public int TotalMappings => _methodMappings.Count + _typeMappings.Count;
        public bool HasMappings => TotalMappings > 0;

        public NRMapRepository(ILogger<NRMapRepository> logger)
        {
            _logger = logger;
        }

        public void LoadMappings(string path)
        {
            if (Directory.Exists(path))
            {
                LoadMappingDirectory(path);
            }
            else if (File.Exists(path))
            {
                LoadMappingFile(path);
            }
            else
            {
                _logger.LogWarning("Mapping path not found: {Path}", path);
            }
        }

        public bool TryDeobfuscateMethod(string obfuscatedName, out string originalName)
        {
            if (_methodMappings.TryGetValue(obfuscatedName, out var mapping))
            {
                originalName = $"{mapping.OriginalTypeName}.{mapping.OriginalName}";
                return true;
            }

            originalName = obfuscatedName;
            return false;
        }

        public bool TryDeobfuscateType(string obfuscatedType, out string originalType)
        {
            if (_typeMappings.TryGetValue(obfuscatedType, out var original))
            {
                originalType = original;
                return true;
            }

            originalType = obfuscatedType;
            return false;
        }

        public Dictionary<string, int> GetAssemblyStats()
        {
            var stats = new Dictionary<string, int>();
            foreach (var mapping in _methodMappings.Values.Distinct())
            {
                if (!string.IsNullOrEmpty(mapping.SourceAssembly))
                {
                    stats.TryGetValue(mapping.SourceAssembly, out var count);
                    stats[mapping.SourceAssembly] = count + 1;
                }
            }
            return stats;
        }

        private void LoadMappingDirectory(string directoryPath)
        {
            var nrmapFiles = Directory.GetFiles(directoryPath, "*.nrmap");

            if (nrmapFiles.Length == 0)
            {
                _logger.LogWarning("No .nrmap files found in directory: {DirectoryPath}", directoryPath);
                return;
            }

            _logger.LogInformation("Found {Count} .nrmap file(s)", nrmapFiles.Length);

            foreach (var filePath in nrmapFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                LoadMappingFile(filePath, fileName);
            }

            _logger.LogInformation("Loaded {TypeCount} types, {MethodCount} methods from {FileCount} file(s)",
                _typeMappings.Count, _methodMappings.Count, nrmapFiles.Length);
        }

        private void LoadMappingFile(string filePath, string? assemblyName = null)
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Mapping file not found: {FilePath}", filePath);
                return;
            }

            try
            {
                var lines = File.ReadAllLines(filePath);
                assemblyName ??= Path.GetFileNameWithoutExtension(filePath);

                string? currentObfuscatedType = null;
                string? currentOriginalType = null;
                int typeCount = 0;
                int methodCount = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();

                    if (string.IsNullOrEmpty(line))
                        continue;

                    if (line == "<<type>>")
                    {
                        if (i + 4 < lines.Length)
                        {
                            var obfType = lines[i + 1].Trim();
                            var origType = lines[i + 2].Trim();

                            if (!string.IsNullOrEmpty(obfType) && !string.IsNullOrEmpty(origType))
                            {
                                // Store the fully qualified type
                                _typeMappings[obfType] = origType;

                                // Also store the short type name
                                if (i + 3 < lines.Length)
                                {
                                    var shortObfType = lines[i + 3].Trim();
                                    if (!string.IsNullOrEmpty(shortObfType) && shortObfType != obfType)
                                    {
                                        _typeMappings[shortObfType] = origType;
                                    }
                                }

                                currentObfuscatedType = obfType;
                                currentOriginalType = origType;
                                typeCount++;
                            }

                            i += 4;

                            // Process methods in this type
                            for (int j = i + 1; j < lines.Length; j++)
                            {
                                var methodLine = lines[j].Trim();

                                if (methodLine == "<<type>>")
                                {
                                    i = j - 1;
                                    break;
                                }

                                if (string.IsNullOrEmpty(methodLine))
                                    continue;

                                if (j + 1 < lines.Length)
                                {
                                    var obfMethod = methodLine;
                                    var origMethod = lines[j + 1].Trim();

                                    if (!string.IsNullOrEmpty(obfMethod) && !string.IsNullOrEmpty(origMethod))
                                    {
                                        var mapping = new MethodMapping
                                        {
                                            ObfuscatedName = obfMethod,
                                            OriginalName = origMethod,
                                            ObfuscatedTypeName = currentObfuscatedType ?? "",
                                            OriginalTypeName = currentOriginalType ?? "",
                                            SourceAssembly = assemblyName ?? ""
                                        };

                                        // Store with multiple keys for easier lookup
                                        var baseKey = $"{assemblyName}:{obfMethod}";
                                        _methodMappings[baseKey] = mapping;

                                        if (!_methodMappings.ContainsKey(obfMethod))
                                        {
                                            _methodMappings[obfMethod] = mapping;
                                        }

                                        if (!string.IsNullOrEmpty(currentObfuscatedType))
                                        {
                                            var fullKey = $"{currentObfuscatedType}.{obfMethod}";
                                            if (!_methodMappings.ContainsKey(fullKey))
                                            {
                                                _methodMappings[fullKey] = mapping;
                                            }
                                            _methodMappings[$"{assemblyName}:{fullKey}"] = mapping;

                                            var obfClassName = currentObfuscatedType.Contains('.')
                                                ? currentObfuscatedType.Substring(currentObfuscatedType.LastIndexOf('.') + 1)
                                                : currentObfuscatedType;
                                            var classKey = $"{obfClassName}.{obfMethod}";
                                            if (!_methodMappings.ContainsKey(classKey))
                                            {
                                                _methodMappings[classKey] = mapping;
                                            }
                                        }

                                        methodCount++;
                                    }

                                    j++;
                                }
                            }
                        }
                    }
                }

                _logger.LogInformation("Loaded {Assembly}: {TypeCount} types, {MethodCount} methods",
                    assemblyName, typeCount, methodCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse mapping file: {FilePath}", filePath);
            }
        }
    }
}