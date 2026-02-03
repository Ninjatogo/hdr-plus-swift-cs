using BurstPhoto.Core.Models;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace BurstPhoto.Rendering.Utilities;

/// <summary>
/// Helper class for texture format conversions (Bayer <-> RGBA) and preparation.
/// Extracted from VulkanComputePipeline for better code organization.
/// </summary>
public unsafe class TextureConversionHelper
{
    private readonly VulkanContext _ctx;
    private readonly VulkanDescriptorManager _descriptors;
    private readonly TextureUtilities _textureUtils;

    /// <summary>
    /// Gets or sets whether verbose validation logging is enabled.
    /// When true, performs expensive GPU->CPU transfers for texture validation.
    /// </summary>
    public bool Verbose { get; set; }

    public TextureConversionHelper(VulkanContext ctx, VulkanDescriptorManager descriptors, TextureUtilities textureUtils)
    {
        _ctx = ctx;
        _descriptors = descriptors;
        _textureUtils = textureUtils;
    }

    /// <summary>
    /// Converts Bayer (R32Sfloat) texture to RGBA (R32G32B32A32Sfloat) superpixels for FFT processing.
    /// Output dimensions are half of input (2x2 Bayer -> 1 RGBA pixel).
    /// </summary>
    public void ConvertToRgba(
        VulkanImage bayerInput,
        VulkanImage rgbaOutput,
        int[] cfaPattern,
        ComputeKernel kernel,
        DescriptorSetLayout layout,
        int cropX = 0,
        int cropY = 0)
    {
        // === PRE-SHADER DATA VALIDATION (only when verbose enabled) ===
        if (Verbose)
        {
            var inputData = bayerInput.GetData<float>();
            var sampleCount = Math.Min(inputData.Length, 1000);
            double inputSum = 0;
            for (var i = 0; i < sampleCount; i++)
            {
                inputSum += Math.Abs(inputData[i]);
            }

            var dataStartIdx = cropY * (int)bayerInput.Width + cropX;
            double dataRegionSum = 0;
            var dataRegionSamples = Math.Min(1000, inputData.Length - dataStartIdx);
            if (dataStartIdx >= 0 && dataStartIdx < inputData.Length)
            {
                for (var i = 0; i < dataRegionSamples; i++)
                {
                    dataRegionSum += Math.Abs(inputData[dataStartIdx + i]);
                }
            }

            Console.WriteLine($"[CONVERT_RGBA] === PRE-SHADER VALIDATION ===");
            Console.WriteLine($"[CONVERT_RGBA] Input: {bayerInput.Width}x{bayerInput.Height}, Output: {rgbaOutput.Width}x{rgbaOutput.Height}");
            Console.WriteLine($"[CONVERT_RGBA] CropX={cropX}, CropY={cropY}");
            Console.WriteLine($"[CONVERT_RGBA] Input data (first {sampleCount}): sum={inputSum:F2}, mean={inputSum / sampleCount:F4}");
            Console.WriteLine($"[CONVERT_RGBA] Input data (at offset cropY*W+cropX): sum={dataRegionSum:F2}, mean={dataRegionSum / dataRegionSamples:F4}");
            Console.WriteLine($"[CONVERT_RGBA] Input layout before transition: {bayerInput.CurrentLayout}");

            if (inputSum < 0.01 && dataRegionSum < 0.01)
            {
                Console.WriteLine($"[CONVERT_RGBA] ERROR: Input texture is EMPTY before shader execution!");
            }
        }

        // Determine CFA pattern index
        var cfaIndex = DetermineCfaIndex(cfaPattern);

        var texParams = new TextureParams { CfaPattern = cfaIndex, PadLeft = cropX, PadTop = cropY };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData([texParams]);

        if (Verbose)
            Console.WriteLine($"[CONVERT_RGBA] TextureParams: CfaPattern={cfaIndex}, PadLeft={cropX}, PadTop={cropY}");

        // Dummy textures for unused bindings
        using var dummyRgba = new VulkanImage(_ctx, 1, 1, Format.R32G32B32A32Sfloat, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
        using var dummyFloat = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.StorageBit);

        var cmd = _ctx.BeginSingleTimeCommands();
        bayerInput.TransitionLayout(ImageLayout.General, cmd);
        rgbaOutput.TransitionLayout(ImageLayout.General, cmd);
        dummyRgba.TransitionLayout(ImageLayout.General, cmd);
        dummyFloat.TransitionLayout(ImageLayout.General, cmd);

        // Memory barrier to ensure input writes are visible
        var memoryBarrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        _ctx.Vk.CmdPipelineBarrier(cmd,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit,
            0, 1, &memoryBarrier, 0, null, 0, null);

        var set = _descriptors.Allocate(layout);
        _descriptors.UpdateBuffer(set, ShaderBindings.Conversion.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.BayerInput, bayerInput.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.UnusedSampled, dummyRgba.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.UnusedStorage, dummyFloat.View, ImageLayout.General, DescriptorType.StorageImage);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.RgbaOutput, rgbaOutput.View, ImageLayout.General, DescriptorType.StorageImage);

        kernel.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);

        if (Verbose)
            Console.WriteLine($"[DEBUG] ConvertToRgba: Input={bayerInput.Width}x{bayerInput.Height}, Output={rgbaOutput.Width}x{rgbaOutput.Height}, CropX={cropX}, CropY={cropY}");
        kernel.Dispatch(cmd, rgbaOutput.Width, rgbaOutput.Height, 1);

        _ctx.EndSingleTimeCommands(cmd);

        // POST-SHADER VALIDATION (only when verbose enabled)
        if (Verbose)
            ValidateConversionOutput(rgbaOutput);
    }

    /// <summary>
    /// Converts RGBA (R32G32B32A32Sfloat) superpixels back to Bayer (R32Sfloat) pattern.
    /// Output dimensions are double the input (1 RGBA pixel -> 2x2 Bayer).
    /// </summary>
    public void ConvertToBayer(
        VulkanImage rgbaInput,
        VulkanImage bayerOutput,
        int[] cfaPattern,
        ComputeKernel kernel,
        DescriptorSetLayout layout)
    {
        var cfaIndex = DetermineCfaIndex(cfaPattern);

        var texParams = new TextureParams { CfaPattern = cfaIndex };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData([texParams]);

        // Dummy textures for unused bindings
        using var dummyFloat = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        using var dummyRgba = new VulkanImage(_ctx, 1, 1, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit);

        var cmd = _ctx.BeginSingleTimeCommands();
        rgbaInput.TransitionLayout(ImageLayout.General, cmd);
        bayerOutput.TransitionLayout(ImageLayout.General, cmd);
        dummyFloat.TransitionLayout(ImageLayout.General, cmd);
        dummyRgba.TransitionLayout(ImageLayout.General, cmd);

        var set = _descriptors.Allocate(layout);
        _descriptors.UpdateBuffer(set, ShaderBindings.Conversion.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.BayerInput, dummyFloat.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.RgbaInput, rgbaInput.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.BayerOutput, bayerOutput.View, ImageLayout.General, DescriptorType.StorageImage);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.UnusedStorage2, dummyRgba.View, ImageLayout.General, DescriptorType.StorageImage);

        kernel.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);

        kernel.Dispatch(cmd, bayerOutput.Width, bayerOutput.Height, 1);

        _ctx.EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Prepares raw input texture (converts to float, applies scaling, handles padding).
    /// </summary>
    public void Prepare(
        VulkanImage input,
        VulkanImage output,
        RawImage rawInfo,
        int padLeft,
        int padTop,
        ComputeKernel kernel,
        DescriptorSetLayout layout,
        int exposureDiff = 0)
    {
        // Fill output with zeros first (important for padding region)
        _textureUtils.FillWithZeros(output);

        var texParams = new TextureParams
        {
            WhiteLevel = rawInfo.WhiteLevel,
            BlackLevel = 0,
            BlackLevelMean = 0.0f,
            ScaleFactor = 1.0f,
            CfaPattern = 0,
            Width = (int)output.Width,
            Height = (int)output.Height,
            InputWidth = (int)input.Width,
            InputHeight = (int)input.Height,
            PadLeft = padLeft,
            PadTop = padTop,
            ExposureDiff = exposureDiff,
            HotPixelThreshold = 1000.0f,
            HotPixelMultiplicator = 1.0f,
            CorrectionStrength = 0.0f
        };

        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([texParams]);

        var blackLevels = new float[4];
        switch (rawInfo.BlackLevels.Length)
        {
            case >= 4:
                for (var i = 0; i < 4; i++)
                    blackLevels[i] = rawInfo.BlackLevels[i];
                break;
            case > 0:
                for (var i = 0; i < 4; i++)
                    blackLevels[i] = rawInfo.BlackLevels[0];
                break;
        }

        using var blParams = new VulkanBuffer(_ctx, 4 * sizeof(float),
            BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        blParams.SetData(blackLevels);

        using var meanBuffer = new VulkanBuffer(_ctx, sizeof(float),
            BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        meanBuffer.SetData([0.0f]);

        using var dummyWeight = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
        dummyWeight.SetData([0.0f]);

        using var dummyRgba = new VulkanImage(_ctx, 1, 1, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
        dummyRgba.SetData(new byte[] { 0, 0, 0, 0 });

        using var dummyUint = new VulkanImage(_ctx, 1, 1, Format.R16Uint, ImageUsageFlags.StorageBit);

        // Command buffer
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = _ctx.CommandPool,
            CommandBufferCount = 1
        };

        _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmdBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        _ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);

        // Transitions
        input.TransitionLayout(ImageLayout.General, cmdBuffer);
        output.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyWeight.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyRgba.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyUint.TransitionLayout(ImageLayout.General, cmdBuffer);

        var set = _descriptors.Allocate(layout);

        _descriptors.UpdateBuffer(set, ShaderBindings.Prepare.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.UnusedFloat, dummyWeight.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.InputUint, input.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.UnusedRgba, dummyRgba.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.HotPixelWeight, dummyWeight.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateBuffer(set, ShaderBindings.Prepare.MeanBuffer, meanBuffer.Handle, sizeof(float), DescriptorType.StorageBuffer);
        _descriptors.UpdateBuffer(set, ShaderBindings.Prepare.BlackLevelsBuffer, blParams.Handle, 4 * sizeof(float), DescriptorType.StorageBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.OutputFloat, output.View, ImageLayout.General, DescriptorType.StorageImage);
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.UnusedOutputUint, dummyUint.View, ImageLayout.General, DescriptorType.StorageImage);
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.UnusedOutputRgba, dummyRgba.View, ImageLayout.General, DescriptorType.StorageImage);

        kernel.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, in set, 0, null);

        // Dispatch using INPUT dimensions
        var dispatchW = input.Width;
        var dispatchH = input.Height;
        var groupX = (dispatchW + 15) / 16;
        var groupY = (dispatchH + 15) / 16;

        _ctx.Vk.CmdDispatch(cmdBuffer, groupX, groupY, 1);

        _ctx.Vk.EndCommandBuffer(cmdBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer
        };

        _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in submitInfo, default);
        _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);

        _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);
    }

    private static int DetermineCfaIndex(int[] cfaPattern)
    {
        if (cfaPattern.Length < 4) return 0;

        // RGGB=0, GRBG=1, GBRG=2, BGGR=3
        if (cfaPattern[0] == 0) return 0;
        if (cfaPattern[0] == 1 && cfaPattern[1] == 0) return 1;
        if (cfaPattern[0] == 1 && cfaPattern[2] == 0) return 2;
        if (cfaPattern[0] == 2) return 3;

        return 0;
    }

    private void ValidateConversionOutput(VulkanImage rgbaOutput)
    {
        var outData = rgbaOutput.GetData<float>();
        double sumFirst = 0, sumMid = 0;
        var samples = Math.Min(1000, outData.Length);
        var midStart = outData.Length / 2;

        for (var i = 0; i < samples; i++)
        {
            sumFirst += Math.Abs(outData[i]);
        }

        for (var i = 0; i < samples && midStart + i < outData.Length; i++)
        {
            sumMid += Math.Abs(outData[midStart + i]);
        }

        Console.WriteLine($"[CONVERT_RGBA] POST-SHADER: first1000={sumFirst:F2}, mid1000={sumMid:F2}, total={outData.Length}");

        var texWidth = (int)rgbaOutput.Width;
        var texHeight = (int)rgbaOutput.Height;

        for (var row = 0; row < texHeight; row += texHeight / 8)
        {
            var rowStartFloat = row * texWidth * 4;
            double rowSum = 0;
            var rowSamples = Math.Min(texWidth * 4, outData.Length - rowStartFloat);

            if (rowStartFloat < 0 || rowStartFloat >= outData.Length || rowSamples <= 0)
                continue;

            for (var i = 0; i < Math.Min(400, rowSamples); i++)
            {
                rowSum += Math.Abs(outData[rowStartFloat + i]);
            }

            Console.WriteLine($"[CONVERT_RGBA] Row {row}: sum={rowSum:F2} (first 100 pixels)");
        }

        if (sumFirst < 0.01 && sumMid < 0.01)
        {
            Console.WriteLine($"[CONVERT_RGBA] ❌ SHADER PRODUCED ALL ZEROS!");
        }
    }
}
