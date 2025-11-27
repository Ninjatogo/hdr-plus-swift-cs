using Spectre.Console.Cli;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace BurstPhoto.CLI.Commands;

public class ProcessCommand : AsyncCommand<ProcessCommand.Settings>
{
    private readonly IRawImageLoader _loader;
    private readonly IComputePipeline _pipeline;
    private readonly IRawImageWriter _writer;

    public ProcessCommand(IRawImageLoader loader, IComputePipeline pipeline, IRawImageWriter writer)
    {
        _loader = loader;
        _pipeline = pipeline;
        _writer = writer;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<INPUT>")]
        [Description("Input raw file path")]
        public string InputPath { get; set; } = "";

        [CommandArgument(1, "<OUTPUT>")]
        [Description("Output PGM/PPM file path")]
        public string OutputPath { get; set; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var image = _loader.Load(settings.InputPath);
        var result = await _pipeline.ProcessAsync(image, new ProcessingProgress());
        _writer.Write(settings.OutputPath, result);
        return 0;
    }
}
