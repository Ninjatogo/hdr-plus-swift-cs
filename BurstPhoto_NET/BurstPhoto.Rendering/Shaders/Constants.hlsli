
// Constants.hlsli
// Shared constants for HLSL shaders, mirroring constants.h from Metal

static const uint UINT16_MAX_VAL = 65535;
static const float PI = 3.14159265358979323846f;

// Half-precision filtering sentinels (using float for HLSL implementation simplicity)
static const float FLOAT16_ZERO_VAL = 0.0f;
static const float FLOAT16_MIN_VAL = -65504.0f;
static const float FLOAT16_MAX_VAL = 65504.0f;
static const float FLOAT16_05_VAL = 0.5f;

// Common sampler states can be defined here if needed in future
