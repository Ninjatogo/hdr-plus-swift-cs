using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using BurstPhoto.CLI.Infrastructure;
using BurstPhoto.CLI.Commands;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Implementations;
using BurstPhoto.Rendering;
using BurstPhoto.Rendering.Implementations;

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
                var gpuEnv = Environment.GetEnvironmentVariable("BURSTPHOTO_GPU");
                if (!string.IsNullOrEmpty(gpuEnv) && int.TryParse(gpuEnv, out var envGpuIndex))
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
            config.PropagateExceptions();
        });

        return await app.RunAsync(args);
    }
}
