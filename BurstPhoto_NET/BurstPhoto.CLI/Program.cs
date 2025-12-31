using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using BurstPhoto.CLI.Infrastructure;
using BurstPhoto.CLI.Commands;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Implementations;
using BurstPhoto.Rendering;
using BurstPhoto.Rendering.Implementations;
using System;
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

        services.AddSingleton<IRawImageLoader, LibRawLoader>();
        services.AddSingleton<IRawImageWriter, SimpleRawWriter>();
        services.AddSingleton<VulkanContext>();
        services.AddSingleton<IComputePipeline, VulkanComputePipeline>();

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);

        app.Configure(config =>
        {
            config.AddCommand<ProcessCommand>("process");
            config.AddCommand<DebugLibRawCommand>("debug-libraw");
        });

        return await app.RunAsync(args);
    }
}

public class DebugLibRawSettings : CommandSettings
{
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
        return Task.FromResult(0);
    }
}
