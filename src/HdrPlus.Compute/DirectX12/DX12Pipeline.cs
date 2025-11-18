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
        // Create a simple root signature with UAV descriptors for compute
        // For simplicity, using a default root signature for now
        // TODO: Parse shader reflection and create optimal root signature

        var ranges = stackalloc DescriptorRange1[2];

        // UAV range for textures/buffers (u0-u15)
        ranges[0] = new DescriptorRange1
        {
            RangeType = DescriptorRangeType.Uav,
            NumDescriptors = 16,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            Flags = DescriptorRangeFlags.None,
            OffsetInDescriptorsFromTableStart = 0
        };

        // CBV range for constants (b0-b7)
        ranges[1] = new DescriptorRange1
        {
            RangeType = DescriptorRangeType.Cbv,
            NumDescriptors = 8,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            Flags = DescriptorRangeFlags.None,
            OffsetInDescriptorsFromTableStart = 16
        };

        var rootParams = stackalloc RootParameter1[1];
        rootParams[0] = new RootParameter1
        {
            ParameterType = RootParameterType.DescriptorTable,
            ShaderVisibility = ShaderVisibility.All
        };
        rootParams[0].Anonymous.DescriptorTable = new RootDescriptorTable1
        {
            NumDescriptorRanges = 2,
            PDescriptorRanges = ranges
        };

        var rootSigDesc = new VersionedRootSignatureDesc
        {
            Version = D3DRootSignatureVersion.V11
        };

        var desc11 = new RootSignatureDesc1
        {
            NumParameters = 1,
            PParameters = rootParams,
            NumStaticSamplers = 0,
            PStaticSamplers = null,
            Flags = RootSignatureFlags.None
        };
        rootSigDesc.Anonymous.Desc11 = desc11;

        ComPtr<ID3DBlob> signature = default;
        ComPtr<ID3DBlob> error = default;

        fixed (byte* bytecodePtr = shaderBytecode)
        {
            // For now, create a simple default root signature
            // TODO: Use D3D12SerializeVersionedRootSignature

            // Simplified: create empty root signature
            var simpleDesc = new RootSignatureDesc
            {
                NumParameters = 0,
                PParameters = null,
                NumStaticSamplers = 0,
                PStaticSamplers = null,
                Flags = RootSignatureFlags.None
            };

            ID3DBlob* sigPtr, errPtr;
            _d3d12.SerializeRootSignature(&simpleDesc, D3DRootSignatureVersion.V10, &sigPtr, &errPtr)
                .ThrowHResult("Failed to serialize root signature");

            signature = new ComPtr<ID3DBlob>(sigPtr);

            ID3D12RootSignature* rootSigPtr;
            device.Get()->CreateRootSignature(
                0,
                signature.Get()->GetBufferPointer(),
                signature.Get()->GetBufferSize(),
                out rootSigPtr
            ).ThrowHResult("Failed to create root signature");

            _rootSignature = new ComPtr<ID3D12RootSignature>(rootSigPtr);
        }

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
