# GPU Selection Guide

This document explains how to select which GPU to use on multi-GPU systems (e.g., laptops with both integrated and discrete graphics).

## Quick Start

### List Available GPUs

```bash
burstphoto process --list-gpus
```

This will display all Vulkan-capable devices, for example:

```
=== Available Vulkan Devices (2) ===
  [0] AMD Radeon(TM) Graphics
      Type: Integrated GPU
      Vendor ID: 0x1002 (Device ID: 0x1638)
      API Version: 1.3.0
      Driver Version: 8388638

  [1] NVIDIA GeForce RTX 3060
      Type: Discrete GPU
      Vendor ID: 0x10DE (Device ID: 0x2503)
      API Version: 1.3.0
      Driver Version: 31457280

✓ Auto-selected device [1] (discrete GPU preferred)
   Selected: NVIDIA GeForce RTX 3060
```

## Method 1: Command-Line Option

Use the `--gpu` option to specify the device index:

```bash
burstphoto process --gpu 0 input1.dng input2.dng -o output/
```

This will use device [0] (the integrated AMD GPU in the example above).

```bash
burstphoto process --gpu 1 input1.dng input2.dng -o output/
```

This will use device [1] (the discrete NVIDIA GPU in the example above).

## Method 2: Environment Variable

Set the `BURSTPHOTO_GPU` environment variable:

### Windows (PowerShell)
```powershell
$env:BURSTPHOTO_GPU = "1"
burstphoto process input1.dng input2.dng -o output/
```

### Windows (Command Prompt)
```cmd
set BURSTPHOTO_GPU=1
burstphoto process input1.dng input2.dng -o output/
```

### Linux/macOS
```bash
export BURSTPHOTO_GPU=1
burstphoto process input1.dng input2.dng -o output/
```

## Automatic Selection

If no GPU is specified (no `--gpu` option and no `BURSTPHOTO_GPU` environment variable), BurstPhoto will automatically select the **first discrete GPU** it finds. If no discrete GPU is available, it will use the first available device.

**Priority order:**
1. `--gpu` command-line option (highest priority)
2. `BURSTPHOTO_GPU` environment variable
3. Auto-select discrete GPU
4. Auto-select first available device

## Vendor IDs

Common GPU vendor IDs for reference:
- **0x1002**: AMD
- **0x10DE**: NVIDIA
- **0x8086**: Intel
- **0x1414**: Microsoft (software rendering)

## Troubleshooting

### Feature Support Check

When initializing, BurstPhoto will check if your selected GPU supports the `ShaderStorageImageWriteWithoutFormat` feature, which is critical for the frequency domain pipeline:

```
✓ ShaderStorageImageWriteWithoutFormat feature is supported and enabled
```

If you see a warning instead:

```
WARNING: ShaderStorageImageWriteWithoutFormat is NOT supported by this device!
```

This means your GPU/driver may not fully support the required Vulkan features. Try:
1. Updating your GPU drivers to the latest version
2. Selecting a different GPU using `--gpu <index>`
3. Using a different device that supports the required features

### Common Issues

**Issue**: "Invalid device index X"
- **Solution**: Run `--list-gpus` to see valid device indices (0 to N-1)

**Issue**: Integrated GPU is selected instead of discrete GPU
- **Solution**: Explicitly specify the discrete GPU with `--gpu 1` (or appropriate index)

**Issue**: "No Vulkan devices found"
- **Solution**:
  - Ensure your GPU drivers are installed and up to date
  - Check that your GPU supports Vulkan 1.2 or higher
  - On Linux, ensure Vulkan runtime libraries are installed (e.g., `vulkan-tools`, `vulkan-loader`)

## Example Session

```bash
# Step 1: List available GPUs
$ burstphoto process --list-gpus

=== Available Vulkan Devices (2) ===
  [0] Intel(R) UHD Graphics 630
      Type: Integrated GPU
      ...
  [1] NVIDIA GeForce RTX 2060
      Type: Discrete GPU
      ...

# Step 2: Process images using the NVIDIA GPU
$ burstphoto process --gpu 1 *.dng --algorithm HigherQuality -o merged/

Initializing Vulkan...
✓ Auto-selected device [1] (discrete GPU preferred)
   Selected: NVIDIA GeForce RTX 2060

✓ ShaderStorageImageWriteWithoutFormat feature is supported and enabled
Vulkan Initialized.
...
```

## Implementation Details

The GPU selection happens during VulkanContext initialization in `VulkanContext.cs`. The selection logic:

1. Enumerates all physical devices
2. Displays device properties (name, type, vendor, API version)
3. Selects based on preference (command-line > environment > auto-discrete > first)
4. Validates required Vulkan features are supported

This information is logged to the console during startup, making it easy to verify which GPU is being used.
