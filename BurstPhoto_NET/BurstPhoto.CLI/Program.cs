using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using BurstPhoto.CLI.Infrastructure;
using BurstPhoto.CLI.Commands;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Implementations;
using BurstPhoto.Rendering;
using BurstPhoto.Rendering.Implementations;
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Sdcb.LibRaw; // For Debug Command

namespace BurstPhoto.CLI;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<IRawImageLoader, LibRawLoader>();
        services.AddSingleton<IRawImageWriter, DngSdkWriter>();
        
        // Rendering/Compute services - try Vulkan, fall back to passthrough
        services.AddSingleton<IComputePipeline>(sp =>
        {
            try
            {
                // Check for GPU preference from environment variable
                int? gpuIndex = null;
                string? gpuEnv = Environment.GetEnvironmentVariable("BURSTPHOTO_GPU");
                if (!string.IsNullOrEmpty(gpuEnv) && int.TryParse(gpuEnv, out int envGpuIndex))
                {
                    gpuIndex = envGpuIndex;
                    Console.WriteLine($"[INFO] Using GPU index {gpuIndex} from BURSTPHOTO_GPU environment variable");
                }

                var ctx = new VulkanContext(gpuIndex);
                return new VulkanComputePipeline(ctx);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Vulkan initialization failed: {ex.Message}");
                Console.WriteLine("[INFO] Using passthrough compute pipeline (no GPU acceleration)");
                return new PassthroughComputePipeline();
            }
        });
        
        // Pipeline
        services.AddSingleton<IDenoisePipeline, DenoisePipeline>();

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);

        app.Configure(config =>
        {
            config.AddCommand<ProcessCommand>("process")
                .WithDescription("Process a burst of raw images to produce a denoised output.");
            config.AddCommand<DebugLibRawCommand>("debug-libraw");
        });

        return await app.RunAsync(args);
    }
}

public class DebugLibRawSettings : CommandSettings
{
    [CommandArgument(0, "[FILE]")]
    public string? FilePath { get; set; }
}

public class DebugLibRawCommand : AsyncCommand<DebugLibRawSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, DebugLibRawSettings settings, CancellationToken cancellationToken)
    {
        Console.WriteLine("RawContext Properties:");
        var properties = typeof(RawContext).GetProperties();
        foreach (var p in properties)
        {
            Console.WriteLine($"- {p.Name} ({p.PropertyType.Name})");
        }

        if (!string.IsNullOrEmpty(settings.FilePath) && File.Exists(settings.FilePath))
        {
            Console.WriteLine($"\n--- Reading: {settings.FilePath} ---\n");
            using var ctx = RawContext.OpenFile(settings.FilePath);
            ctx.Unpack();
            
            // Image Other Params - contains ISO, shutter, aperture
            var other = ctx.ImageOtherParams;
            Console.WriteLine("ImageOtherParams:");
            Console.WriteLine($"  IsoSpeed: {other.IsoSpeed}");
            Console.WriteLine($"  Shutter: {other.Shutter}");
            Console.WriteLine($"  Aperture: {other.Aperture}");
            Console.WriteLine($"  FocalLength: {other.FocalLength}");
            Console.WriteLine($"  Timestamp: {other.Timestamp}");
            Console.WriteLine($"  ShotOrder: {other.ShotOrder}");
            Console.WriteLine($"  Artist: {other.Artist}");
            
            // Color data
            Console.WriteLine($"\nColorMaximum (WhiteLevel): {ctx.ColorMaximum}");
            Console.WriteLine($"CameraMultiplier: [{ctx.CameraMultipler[0]}, {ctx.CameraMultipler[1]}, {ctx.CameraMultipler[2]}, {ctx.CameraMultipler[3]}]");
            
            // Calculated IsoExposureTime
            float isoExposureTime = other.IsoSpeed * other.Shutter;
            Console.WriteLine($"\nCalculated IsoExposureTime (ISO * Shutter): {isoExposureTime}");
        }
        else if (!string.IsNullOrEmpty(settings.FilePath))
        {
            Console.WriteLine($"\nFile not found: {settings.FilePath}");
        }
        
        return Task.FromResult(0);
    }
}
