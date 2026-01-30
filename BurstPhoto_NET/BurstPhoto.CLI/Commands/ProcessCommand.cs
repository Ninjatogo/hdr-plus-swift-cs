using Spectre.Console.Cli;
using Spectre.Console;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BurstPhoto.CLI.Commands;

public class ProcessCommand : AsyncCommand<ProcessCommand.Settings>
{
    private readonly IDenoisePipeline _pipeline;

    public ProcessCommand(IDenoisePipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<INPUT...>")]
        [Description("Input raw file paths (minimum 2 required)")]
        public string[] InputPaths { get; set; } = Array.Empty<string>();

        [CommandOption("-o|--output")]
        [Description("Output directory (default: current directory)")]
        public string OutputDirectory { get; set; } = ".";

        [CommandOption("--algorithm")]
        [Description("Merging algorithm: Fast or HigherQuality (default: Fast)")]
        public string Algorithm { get; set; } = "Fast";

        [CommandOption("--tile-size")]
        [Description("Tile size: Small, Medium, or Large (default: Medium)")]
        public string TileSize { get; set; } = "Medium";

        [CommandOption("--search-distance")]
        [Description("Search distance: Small, Medium, or Large (default: Medium)")]
        public string SearchDistance { get; set; } = "Medium";

        [CommandOption("--noise-reduction")]
        [Description("Noise reduction value (default: 13.0)")]
        public double NoiseReduction { get; set; } = 13.0;

        [CommandOption("--exposure-control")]
        [Description("Exposure control: Off, LinearFullRange, Linear1EV, Curve0EV, Curve1EV (default: LinearFullRange)")]
        public string ExposureControl { get; set; } = "LinearFullRange";

        [CommandOption("--bit-depth")]
        [Description("Output bit depth: Native or 16Bit (default: Native)")]
        public string BitDepth { get; set; } = "Native";
        
        [CommandOption("--debug-dump")]
        [Description("Enable debug output: saves intermediate DNGs to DebugOutput folder")]
        public bool DebugDump { get; set; } = false;
        
        [CommandOption("-v|--validate-fft")]
        [Description("Run FFT validation tests (Parseval's theorem, round-trip, DC component). Stops early if validation fails.")]
        public bool ValidateFft { get; set; } = false;
        
        [CommandOption("--log")]
        [Description("Save console output to a log file (default: logs/process_YYYYMMDD_HHMMSS.log)")]
        public bool Log { get; set; } = false;
        
        [CommandOption("--log-file")]
        [Description("Custom log file path (implies --log)")]
        public string? LogFile { get; set; } = null;

        [CommandOption("--gpu")]
        [Description("GPU device index to use (0, 1, etc.). Use --list-gpus to see available devices. Can also be set via BURSTPHOTO_GPU environment variable.")]
        public int? GpuIndex { get; set; } = null;

        [CommandOption("--list-gpus")]
        [Description("List available GPU devices and exit")]
        public bool ListGpus { get; set; } = false;

        [CommandOption("--skip-reduce-artifacts")]
        [Description("Debug: Skip the reduce_artifacts_tile_border pass to test if it causes the 8x8 grid pattern")]
        public bool SkipReduceArtifacts { get; set; } = false;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Handle --list-gpus command
        if (settings.ListGpus)
        {
            ListAvailableGpus();
            return 0;
        }

        // Handle --gpu option by setting environment variable for this process
        if (settings.GpuIndex.HasValue)
        {
            Environment.SetEnvironmentVariable("BURSTPHOTO_GPU", settings.GpuIndex.Value.ToString());
            Console.WriteLine($"[INFO] GPU device index set to {settings.GpuIndex.Value} for this session");
        }

        TextWriter? logWriter = null;
        TextWriter originalOut = Console.Out;

        try
        {
            // Setup logging if requested
            if (settings.Log || !string.IsNullOrEmpty(settings.LogFile))
            {
                string logPath = settings.LogFile ?? GenerateLogPath();
                string? logDir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
                Console.SetOut(new TeeTextWriter(originalOut, logWriter));
                Console.WriteLine($"[LOG] Output being saved to: {logPath}");
            }
            // Parse options
            Console.WriteLine($"[CLI DEBUG] settings.ValidateFft = {settings.ValidateFft}");
            Console.WriteLine($"[CLI DEBUG] settings.DebugDump = {settings.DebugDump}");
            var options = new ProcessingOptions
            {
                Merging = ParseEnum<MergingAlgorithm>(settings.Algorithm, "algorithm"),
                TileSize = ParseEnum<TileSizeOption>(settings.TileSize, "tile-size"),
                SearchDistance = ParseEnum<SearchDistanceOption>(settings.SearchDistance, "search-distance"),
                NoiseReduction = settings.NoiseReduction,
                ExposureControl = ParseExposureControl(settings.ExposureControl),
                OutputBitDepth = settings.BitDepth.Equals("16Bit", StringComparison.OrdinalIgnoreCase) 
                    ? OutputBitDepthOption.Bit16 
                    : OutputBitDepthOption.Native,
                EnableDebugDump = settings.DebugDump,
                EnableFftValidation = settings.ValidateFft,
                SkipReduceArtifacts = settings.SkipReduceArtifacts
            };

            var progress = new ProcessingProgress();

            // Ensure output directory exists
            Directory.CreateDirectory(settings.OutputDirectory);

            AnsiConsole.MarkupLine($"[blue]Processing {settings.InputPaths.Length} images...[/]");

            string outputPath = await _pipeline.ProcessAsync(
                settings.InputPaths,
                options,
                progress,
                settings.OutputDirectory,
                cancellationToken);

            AnsiConsole.MarkupLine($"[green]Output saved to: {outputPath}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.ToString())}[/]");
            return 1;
        }
        finally
        {
            // Restore original console output and close log file
            if (logWriter != null)
            {
                Console.SetOut(originalOut);
                logWriter.Dispose();
            }
        }
    }
    
    private static string GenerateLogPath()
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine("logs", $"process_{timestamp}.log");
    }

    private static T ParseEnum<T>(string value, string optionName) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, ignoreCase: true, out var result))
        {
            return result;
        }
        throw new ArgumentException($"Invalid value '{value}' for --{optionName}. Valid values: {string.Join(", ", Enum.GetNames<T>())}");
    }

    private static ExposureControlOption ParseExposureControl(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "off" => ExposureControlOption.Off,
            "linearfullrange" => ExposureControlOption.LinearFullRange,
            "linear1ev" => ExposureControlOption.Linear1EV,
            "curve0ev" => ExposureControlOption.Curve0EV,
            "curve1ev" => ExposureControlOption.Curve1EV,
            _ => throw new ArgumentException($"Invalid exposure control: {value}")
        };
    }

    private static void ListAvailableGpus()
    {
        try
        {
            // Temporarily create a VulkanContext just to enumerate devices
            // The constructor will print all available GPUs
            Console.WriteLine("Enumerating available Vulkan devices...\n");
            using var ctx = new BurstPhoto.Rendering.VulkanContext();
            Console.WriteLine("\nTo use a specific GPU, use one of these methods:");
            Console.WriteLine("  1. Command-line: --gpu <index>");
            Console.WriteLine("  2. Environment variable: set BURSTPHOTO_GPU=<index>");
            Console.WriteLine("\nExample: burstphoto process --gpu 0 input1.dng input2.dng");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error enumerating GPUs: {ex.Message}");
        }
    }
}

/// <summary>
/// TextWriter that writes to two outputs simultaneously (console and file).
/// </summary>
internal class TeeTextWriter : TextWriter
{
    private readonly TextWriter _primary;
    private readonly TextWriter _secondary;

    public TeeTextWriter(TextWriter primary, TextWriter secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    public override Encoding Encoding => _primary.Encoding;

    public override void Write(char value)
    {
        _primary.Write(value);
        _secondary.Write(value);
    }

    public override void Write(string? value)
    {
        _primary.Write(value);
        _secondary.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _primary.WriteLine(value);
        _secondary.WriteLine(value);
    }

    public override void Flush()
    {
        _primary.Flush();
        _secondary.Flush();
    }
}
