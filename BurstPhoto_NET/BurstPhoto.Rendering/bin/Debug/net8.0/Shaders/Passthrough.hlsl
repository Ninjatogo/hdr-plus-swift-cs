RWByteAddressBuffer BufferOut : register(u0);
ByteAddressBuffer BufferIn : register(t0);

[numthreads(64, 1, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    // Each thread copies 4 bytes (2 pixels of 16-bit)
    uint addr = DTid.x * 4;
    // We assume the buffer size is handled by dispatch size
    // In a real shader, we check bounds

    uint val = BufferIn.Load(addr);
    BufferOut.Store(addr, val);
}
