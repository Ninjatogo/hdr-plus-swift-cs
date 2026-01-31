using Silk.NET.Shaderc;

namespace BurstPhoto.Rendering;

public unsafe class VulkanShaderCompiler : IDisposable
{
    private readonly Shaderc _shaderc;
    private readonly Compiler* _compiler;
    private readonly CompileOptions* _options;

    public VulkanShaderCompiler()
    {
        _shaderc = Shaderc.GetApi();
        _compiler = _shaderc.CompilerInitialize();
        _options = _shaderc.CompileOptionsInitialize();

        _shaderc.CompileOptionsSetSourceLanguage(_options, SourceLanguage.Hlsl);
        _shaderc.CompileOptionsSetTargetEnv(_options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan10);
        // Optimize for performance
        _shaderc.CompileOptionsSetOptimizationLevel(_options, OptimizationLevel.Performance);
    }

    /// <summary>
    /// Resolves #include directives in shader source code
    /// </summary>
    private string ResolveIncludes(string source, string baseDirectory)
    {
        var lines = source.Split('\n');
        var result = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#include"))
            {
                // Extract the include file name
                var firstQuote = trimmed.IndexOf('"');
                var lastQuote = trimmed.LastIndexOf('"');
                if (firstQuote != -1 && lastQuote != -1 && firstQuote < lastQuote)
                {
                    var includeFile = trimmed.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    var includePath = Path.Combine(baseDirectory, includeFile);

                    if (File.Exists(includePath))
                    {
                        var includeContent = File.ReadAllText(includePath);
                        // Recursively resolve includes in the included file
                        var includeDir = Path.GetDirectoryName(includePath);
                        includeContent = ResolveIncludes(includeContent, includeDir);
                        result.AppendLine($"// BEGIN INCLUDE: {includeFile}");
                        result.AppendLine(includeContent);
                        result.AppendLine($"// END INCLUDE: {includeFile}");
                    }
                    else
                    {
                        // Keep the original include line if file not found
                        result.AppendLine(line);
                    }
                }
                else
                {
                    result.AppendLine(line);
                }
            }
            else
            {
                result.AppendLine(line);
            }
        }

        return result.ToString();
    }

    public byte[] Compile(string source, string name, string entryPoint = "CSMain")
    {
        var result = _shaderc.CompileIntoSpv(
            _compiler,
            source,
            (nuint)source.Length,
            ShaderKind.ComputeShader,
            name,
            entryPoint,
            _options);

        if (_shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
        {
            var errorMsg = _shaderc.ResultGetErrorMessageS(result);
            _shaderc.ResultRelease(result);

            // Save failing shader source for debugging
            var debugPath = $"FailedShader_{name}_{DateTime.Now:yyyyMMdd_HHmmss}.hlsl";
            try
            {
                File.WriteAllText(debugPath, source);
                Console.WriteLine($"[SHADER ERROR] Failed shader source saved to: {debugPath}");
                Console.WriteLine($"[SHADER ERROR] Source length: {source.Length} characters, {source.Split('\n').Length} lines");
            }
            catch { /* Ignore file write errors */ }

            throw new Exception($"Shader compilation failed for {name}: {errorMsg}");
        }

        var length = _shaderc.ResultGetLength(result);
        var bytes = _shaderc.ResultGetBytes(result);

        var byteCode = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy((IntPtr)bytes, byteCode, 0, (int)length);

        _shaderc.ResultRelease(result);

        return byteCode;
    }

    /// <summary>
    /// Compiles a shader file with automatic include resolution
    /// </summary>
    public byte[] CompileFile(string filePath, string entryPoint = "CSMain")
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Shader file not found: {filePath}");
        }

        var source = File.ReadAllText(filePath);
        var directory = Path.GetDirectoryName(filePath);
        var name = Path.GetFileNameWithoutExtension(filePath);

        // Resolve all includes
        source = ResolveIncludes(source, directory);

        return Compile(source, name, entryPoint);
    }

    public void Dispose()
    {
        _shaderc.CompileOptionsRelease(_options);
        _shaderc.CompilerRelease(_compiler);
        _shaderc.Dispose();
    }
}
