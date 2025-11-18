using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using System.Reflection;

namespace HdrPlus.Compute.DirectX12;

internal unsafe class DX12Pipeline : IComputePipeline
{
    private readonly D3D12 _d3d12;
    private ComPtr<ID3D12PipelineState> _pipelineState;
    private ComPtr<ID3D12RootSignature> _rootSignature;
    private bool _disposed;

    public string Name { get; }
    public string EntryPoint { get; }

    public DX12Pipeline(DX12ComputeDevice device, D3D12 d3d12, ComPtr<ID3D12Device> d3dDevice, string shaderName, string entryPoint)
    {
        _d3d12 = d3d12;
        Name = shaderName;
        EntryPoint = entryPoint;

        // Load compiled shader bytecode from embedded resources
        byte[] shaderBytecode = LoadShaderBytecode(shaderName);

        // Create root signature (describes shader resource bindings)
        CreateRootSignature(d3dDevice, shaderBytecode);

        // Create pipeline state
        CreatePipelineState(d3dDevice, shaderBytecode);
    }

    private byte[] LoadShaderBytecode(string shaderName)
    {
        // Load from embedded resources (shaders compiled offline)
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"HdrPlus.Compute.Shaders.{shaderName}.cso";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Shader '{shaderName}' not found. Make sure it's compiled and embedded as a resource.");
        }

        byte[] bytecode = new byte[stream.Length];
        stream.Read(bytecode, 0, bytecode.Length);
        return bytecode;
    }

    private void CreateRootSignature(ComPtr<ID3D12Device> device, byte[] shaderBytecode)
    {
        // Create root signature with descriptor table for UAVs and CBVs
        // This allows binding multiple resources through descriptor heaps

        // Define descriptor ranges
        var ranges = stackalloc DescriptorRange[2];

        // UAV range for textures/buffers (u0-u15)
        ranges[0] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Uav,
            NumDescriptors = 16,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0
        };

        // CBV range for constants (b0-b7)
        ranges[1] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Cbv,
            NumDescriptors = 8,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 16
        };

        // Create root parameters
        var rootParams = stackalloc RootParameter[1];
        rootParams[0].ParameterType = RootParameterType.DescriptorTable;
        rootParams[0].ShaderVisibility = ShaderVisibility.All;
        rootParams[0].Anonymous.DescriptorTable = new RootDescriptorTable
        {
            NumDescriptorRanges = 2,
            PDescriptorRanges = ranges
        };

        // Create root signature descriptor
        var rootSigDesc = new RootSignatureDesc
        {
            NumParameters = 1,
            PParameters = rootParams,
            NumStaticSamplers = 0,
            PStaticSamplers = null,
            Flags = RootSignatureFlags.None
        };

        // Serialize and create root signature
        ComPtr<ID3DBlob> signature = default;
        ComPtr<ID3DBlob> error = default;

        ID3DBlob* sigPtr = null;
        ID3DBlob* errPtr = null;

        int hr = _d3d12.SerializeRootSignature(&rootSigDesc, D3DRootSignatureVersion.V10, &sigPtr, &errPtr);

        if (hr != 0)
        {
            string errorMsg = "Failed to serialize root signature";
            if (errPtr != null)
            {
                var errorBlob = new ComPtr<ID3DBlob>(errPtr);
                // Try to get error message
                errorMsg += $" (HRESULT: 0x{hr:X8})";
                errorBlob.Dispose();
            }
            throw new Exception(errorMsg);
        }

        signature = new ComPtr<ID3DBlob>(sigPtr);

        ID3D12RootSignature* rootSigPtr;
        device.Get()->CreateRootSignature(
            0,
            signature.Get()->GetBufferPointer(),
            signature.Get()->GetBufferSize(),
            out rootSigPtr
        ).ThrowHResult("Failed to create root signature");

        _rootSignature = new ComPtr<ID3D12RootSignature>(rootSigPtr);
        signature.Dispose();
    }

    private void CreatePipelineState(ComPtr<ID3D12Device> device, byte[] shaderBytecode)
    {
        fixed (byte* bytecodePtr = shaderBytecode)
        {
            var computeDesc = new ComputePipelineStateDesc
            {
                PRootSignature = _rootSignature.Get(),
                CS = new ShaderBytecode
                {
                    PShaderBytecode = bytecodePtr,
                    BytecodeLength = (nuint)shaderBytecode.Length
                },
                NodeMask = 0,
                Flags = PipelineStateFlags.None
            };

            ID3D12PipelineState* pipelinePtr;
            device.Get()->CreateComputePipelineState(&computeDesc, out pipelinePtr)
                .ThrowHResult("Failed to create compute pipeline state");

            _pipelineState = new ComPtr<ID3D12PipelineState>(pipelinePtr);
        }
    }

    public (int X, int Y, int Z) GetThreadGroupSize()
    {
        // Default thread group size for compute shaders
        // TODO: Read from shader reflection
        return (8, 8, 1);
    }

    internal ComPtr<ID3D12PipelineState> GetPipelineState() => _pipelineState;
    internal ComPtr<ID3D12RootSignature> GetRootSignature() => _rootSignature;

    public void Dispose()
    {
        if (_disposed) return;
        _pipelineState.Dispose();
        _rootSignature.Dispose();
        _disposed = true;
    }
}
