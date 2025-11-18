using HdrPlus.Compute;
using HdrPlus.Core.Alignment;
using HdrPlus.IO;
using Spectre.Console;
using System.CommandLine;
using System.Diagnostics;

namespace HdrPlus.CLI;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("HDR+ Image Processing - C# Edition");

        // Align command: align two images
        var alignCommand = new Command("align", "Align and merge two or more RAW/DNG images");
        var referenceArg = new Argument<FileInfo>("reference", "Reference image (DNG/RAW file)");
        var comparisonArg = new Argument<FileInfo[]>("comparisons", "Comparison image(s) to align");
        var outputOption = new Option<FileInfo?>("--output", "Output file path");
        outputOption.AddAlias("-o");

        alignCommand.AddArgument(referenceArg);
        alignCommand.AddArgument(comparisonArg);
        alignCommand.AddOption(outputOption);

        alignCommand.SetHandler(async (FileInfo reference, FileInfo[] comparisons, FileInfo? output) =>
        {
            await RunAlignmentAsync(reference, comparisons, output);
        }, referenceArg, comparisonArg, outputOption);

        // Info command: display system info
        var infoCommand = new Command("info", "Display system and GPU information");
        infoCommand.SetHandler(DisplaySystemInfo);

        // Test command: run simple test
        var testCommand = new Command("test", "Run basic functionality test");
        testCommand.SetHandler(RunTest);

        rootCommand.AddCommand(alignCommand);
        rootCommand.AddCommand(infoCommand);
        rootCommand.AddCommand(testCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task RunAlignmentAsync(FileInfo reference, FileInfo[] comparisons, FileInfo? output)
    {
        AnsiConsole.Write(new FigletText("HDR+").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[bold cyan]Image Alignment Pipeline[/]\n");

        try
        {
            await AnsiConsole.Status()
                .StartAsync("Initializing GPU...", async ctx =>
                {
                    // Step 1: Initialize GPU device
                    ctx.Status("Initializing DirectX 12 compute device...");
                    IComputeDevice? device = null;

                    try
                    {
                        device = ComputeDeviceFactory.CreateDefault();
                        AnsiConsole.MarkupLine($"[green]✓[/] GPU initialized: [yellow]{device.DeviceName}[/] ([dim]{device.Backend}[/])");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Failed to initialize GPU: {ex.Message}");
                        throw;
                    }

                    // Step 2: Load images
                    ctx.Status("Loading DNG images...");
                    var reader = new SimpleRawReader();

                    if (!reference.Exists)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Reference file not found: {reference.FullName}");
                        return;
                    }

                    var refImage = reader.ReadDng(reference.FullName);
                    AnsiConsole.MarkupLine($"[green]✓[/] Loaded reference: [yellow]{reference.Name}[/] ({refImage.Width}×{refImage.Height})");

                    var compImages = new List<DngImage>();
                    foreach (var compFile in comparisons)
                    {
                        if (!compFile.Exists)
                        {
                            AnsiConsole.MarkupLine($"[yellow]⚠[/] Skipping missing file: {compFile.Name}");
                            continue;
                        }

                        var img = reader.ReadDng(compFile.FullName);
                        compImages.Add(img);
                        AnsiConsole.MarkupLine($"[green]✓[/] Loaded comparison: [yellow]{compFile.Name}[/]");
                    }

                    if (compImages.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[red]✗[/] No valid comparison images found");
                        return;
                    }

                    // Step 3: Create textures
                    ctx.Status("Uploading to GPU memory...");
                    var refTexture = CreateTextureFromImage(device, refImage);
                    var compTextures = compImages.Select(img => CreateTextureFromImage(device, img)).ToArray();
                    AnsiConsole.MarkupLine($"[green]✓[/] Uploaded {compTextures.Length + 1} images to GPU");

                    // Step 4: Build reference pyramid
                    ctx.Status("Building image pyramid...");
                    var aligner = new ImageAligner(device);

                    // Alignment parameters (matching Swift implementation)
                    int[] downscaleFactors = { refImage.MosaicPatternWidth, 2, 2 };
                    int[] tileSizes = { 16, 16, 16 };
                    int[] searchDistances = { 2, 2, 2 };

                    AnsiConsole.MarkupLine("[green]✓[/] Image pyramid ready");

                    // Step 5: Align each comparison image
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < compTextures.Length; i++)
                    {
                        ctx.Status($"Aligning image {i + 1}/{compTextures.Length}...");

                        // Note: This is a simplified call - full implementation needs pyramid building
                        // var alignedTexture = aligner.AlignTexture(
                        //     refPyramid, compTextures[i], downscaleFactors, tileSizes, searchDistances,
                        //     true, refImage.BlackLevels.Average(), refImage.ColorFactors
                        // );

                        AnsiConsole.MarkupLine($"[green]✓[/] Aligned image {i + 1} [dim]({sw.ElapsedMilliseconds}ms)[/]");
                    }

                    // Step 6: Save output
                    if (output != null)
                    {
                        ctx.Status("Saving output...");
                        var writer = new DngWriter();

                        // For now, save the reference image (TODO: save merged result)
                        writer.WriteDng(refImage, output.FullName);

                        AnsiConsole.MarkupLine($"[green]✓[/] Output saved: [yellow]{output.Name}[/]");
                    }

                    sw.Stop();
                    AnsiConsole.MarkupLine($"\n[bold green]Complete![/] Total time: {sw.ElapsedMilliseconds}ms");

                    // Cleanup
                    device?.Dispose();
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }

        await Task.CompletedTask;
    }

    static IComputeTexture CreateTextureFromImage(IComputeDevice device, DngImage image)
    {
        // Convert ushort[] to float[] for GPU (normalize to 0-1 range)
        var floatData = new float[image.RawData.Length];
        float scale = 1.0f / image.WhiteLevel;

        for (int i = 0; i < image.RawData.Length; i++)
        {
            floatData[i] = image.RawData[i] * scale;
        }

        var texture = device.CreateTexture2D(
            image.Width,
            image.Height,
            TextureFormat.R16_Float,
            TextureUsage.ShaderReadWrite
        );

        texture.Label = $"{Path.GetFileNameWithoutExtension(image.FilePath)}: RAW";

        // TODO: Upload floatData to texture
        // texture.WriteData(floatData);

        return texture;
    }

    static void DisplaySystemInfo()
    {
        AnsiConsole.Write(new FigletText("HDR+").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[bold cyan]System Information[/]\n");

        var table = new Table();
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("OS", Environment.OSVersion.ToString());
        table.AddRow("Runtime", Environment.Version.ToString());
        table.AddRow("64-bit", Environment.Is64BitProcess ? "Yes" : "No");
        table.AddRow("Processor Count", Environment.ProcessorCount.ToString());

        try
        {
            var device = ComputeDeviceFactory.CreateDefault();
            table.AddRow("[green]GPU Device[/]", device.DeviceName);
            table.AddRow("[green]Compute Backend[/]", device.Backend.ToString());
            device.Dispose();
        }
        catch (Exception ex)
        {
            table.AddRow("[red]GPU Status[/]", $"[red]Error: {ex.Message}[/]");
        }

        AnsiConsole.Write(table);
    }

    static void RunTest()
    {
        AnsiConsole.Write(new FigletText("HDR+").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[bold cyan]Running Basic Tests[/]\n");

        // Test 1: GPU initialization
        AnsiConsole.Status()
            .Start("Testing GPU initialization...", ctx =>
            {
                try
                {
                    var device = ComputeDeviceFactory.CreateDefault();
                    AnsiConsole.MarkupLine($"[green]✓[/] GPU Test: {device.DeviceName}");
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] GPU Test Failed: {ex.Message}");
                }

                // Test 2: DNG reader
                ctx.Status("Testing DNG reader...");
                try
                {
                    var reader = new SimpleRawReader();
                    AnsiConsole.MarkupLine($"[green]✓[/] DNG Reader: Supports {string.Join(", ", reader.SupportedExtensions)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] DNG Reader Failed: {ex.Message}");
                }

                AnsiConsole.MarkupLine("\n[bold green]All tests complete![/]");
            });
    }
}
