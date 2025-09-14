// This file is no longer being used

#ifndef CLIPMAP_HEIGHTMAP_INCLUDE
#define CLIPMAP_HEIGHTMAP_INCLUDE
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

Texture2DArray _ClipmapStack;
Texture2D _ClipmapPyramid;

int _ClipSize;
int _ClipHalfSize;
int _InvalidBorder;
int _ClipmapStackLevelCount;
int _MaxTextureLOD; // Used to temporarily hide clipmap levels that are not ready

#define CLIPMAP_MAX_SIZE 6
float2 _ClipCenter[CLIPMAP_MAX_SIZE];
float _MipSize[CLIPMAP_MAX_SIZE];
float _MipHalfSize[CLIPMAP_MAX_SIZE];
float _ClipScaleToMip[CLIPMAP_MAX_SIZE]; // worldSize / clipSize
float _MipScaleToWorld[CLIPMAP_MAX_SIZE]; // MipSize * MipScaleToWorld = TerrainSize : 1, 2, 4, 8, ...

SamplerState clipmap_point_clamp_sampler;
SamplerState clipmap_bilinear_clamp_sampler;


// Convert BatchMeshUV to the clipmapUV, with toroidal addressing adjustment
float2 GetClipmapUV(in float2 uv, in int clipmapStackLevel)
{
    return frac(uv * _ClipScaleToMip[clipmapStackLevel]);
}


// use the original uv (uv in mip0)
UNITY_BRANCH
float4 SampleClipmapLevel(float2 uv, int depth)
{
    
    if (depth >= _ClipmapStackLevelCount)
    {
        return _ClipmapPyramid.SampleLevel(clipmap_point_clamp_sampler, uv, 0);
    }
    else
    {
        int mip = max(depth, _MaxTextureLOD);
        float2 toroidalUV = GetClipmapUV(uv, mip);
        return _ClipmapStack.SampleLevel(clipmap_point_clamp_sampler, float3(toroidalUV, mip), 0); //+ 2 * (1 - depth / 5.0);
    }
}


// 
void GetClipmapStackLevels(in float2 uv, out int coarseLevelIndex, out int fineLevelIndex,
                           out float fraction)
{
    // mip calculation by world space
    float2 homogeneousCoord = (uv - 0.5) * _MipSize[0];
    int clipmapLevelincludeCount = 0;
    for (int levelIndex = _MaxTextureLOD; levelIndex < _ClipmapStackLevelCount; ++levelIndex)
    {
        float2 diff = homogeneousCoord - _ClipCenter[levelIndex] * _MipScaleToWorld[levelIndex];
        float2 sqrDiff = diff * diff;

        float2 sqrHalfSize = pow((_ClipHalfSize - 2) * _MipScaleToWorld[levelIndex], 2);    // zb -4 is invalidborder, should change to uniform
        float2 containXY = step(sqrDiff, sqrHalfSize);
        
        // x+y in [0, 1, 2], 2 means the coordinates in both axis are within the current clipmap level
        float contain = step(1.5, containXY.x + containXY.y);
        clipmapLevelincludeCount += contain;
    }
    fineLevelIndex = _ClipmapStackLevelCount - clipmapLevelincludeCount;
    fineLevelIndex = min(fineLevelIndex, _ClipmapStackLevelCount);
    coarseLevelIndex = fineLevelIndex + 1;

    // Blending algorithm from: https://hhoppe.com/proj/geomclipmap/
    float w = 0.1;
    float2 diff = homogeneousCoord - (_ClipCenter[fineLevelIndex]) * _MipScaleToWorld[fineLevelIndex];
    float2 halfSize = _ClipHalfSize * _MipScaleToWorld[fineLevelIndex];
    float2 proportion = (abs(diff) + 1) / halfSize;
    proportion = (proportion - (1 - w)) / w;
    fraction = max(proportion.x, proportion.y);

    fraction = clamp(fraction, 0, 1);
    fineLevelIndex = clamp(fineLevelIndex, 0, _ClipmapStackLevelCount);
    coarseLevelIndex = clamp(coarseLevelIndex, 0, _ClipmapStackLevelCount);
}

#endif
