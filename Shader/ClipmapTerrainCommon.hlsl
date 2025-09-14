#ifndef _ClipmapTerrainCommon_
#define _ClipmapTerrainCommon_

#define kMaxHeight (32766.0f/65535.0f)


struct TerrainCullResultData
{
    uint id;
    uint lodTransition;
};


struct TerrainNode 
{
    float positionX;
    float positionZ;
    float scale;
};


struct TerrainNodeAABB
{
    float3 extent;
    float3 position;
};


struct ClipmapInfo
{
    int BaseTextureSize;
    int ClipSize;
    int StackLevelCount;
};


// encoding for mesh stitching
static const uint leftEncoding     = 1;
static const uint topEncoding      = 1 << 1;
static const uint rightEncoding    = 1 << 2;
static const uint bottomEncoding   = 1 << 3;


float sum4(float4 v)
{
    #if 1
    return dot(v, float4(1,1,1,1));
    # else 
    return v.x + v.y + v.z + v.w;
    #endif
}

#endif