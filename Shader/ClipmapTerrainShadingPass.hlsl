#ifndef _ClipmapTerrainShadingPass_
#define _ClipmapTerrainShadingPass_

/* ---------------- FLAGS --------------------- */
// #pragma enable_d3d11_debug_symbols
#define SAMPLE_TERRAIN_NORMAL_IN_PIXEL_SHADER 1
/* ------------------------------------------- */

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "ClipmapTerrainCommon.hlsl"

struct Attributes
{
    float4 Vertex: POSITION;
    float4 Stitching : TEXCOORD0; 
};

struct Varyings
{
    float4 Vertex_CS : SV_POSITION;
    float2 VertexUV : TEXCOORD0;
    float3 Vertex_WS : TEXCOORD1;
    half3 ViewDirection_WS : TEXCOORD2;
    half3 Normal_WS  : TEXCOORD3;
    half3 Tangent_WS : TEXCOORD4;
    half3 Bitangent_WS : TEXCOORD5;
    half FogFactor : TEXCOORD6;
};

#define TERRAIN_HEIGHT_RANGE 800

SamplerState clipmap_point_clamp_sampler;
SamplerState clipmap_point_repeat_sampler;
SamplerState clipmap_trilinear_repeat_sampler;

StructuredBuffer<TerrainCullResultData> terrainCullResultData_buf;
StructuredBuffer<TerrainNode> terrainNodeData_buf;

cbuffer CGlobals
{
    float4 _WorldSize; // (baseTextureSize, 1/baseTextureSize, 0, 0);
    float4 _CamPosSnapped;
    float3 _TerrainOffset;   // The origin (bottom left corner of the terrain) in world space.
};

cbuffer UnityPerMaterial
{
    // Heightmap
    Texture2DArray  _HeightClipmap_Stack;
    Texture2D       _HeightClipmap_Pyramid;
    float           _HeightClipmap_ClipSize;
    float           _HeightClipmap_TexSize;
    int             _HeightClipmap_StackLen;
    float _HeightClipmapFinestLevelUptoDate;

    // SplatID & Weight map
    Texture2DArray  _SplatIDWClipmap_Stack;
    Texture2D       _SplatIDWClipmap_Pyramid;
    float           _SplatIDWClipmap_ClipSize;
    float           _SplatIDWClipmap_TexSize;
    int             _SplatIDWClipmap_StackLen;
    float _SplatIDWClipmapFinestLevelUptoDate;

    Texture2D _TerrainNormalTex;
    Texture2DArray _AlbedoAtlas;
    Texture2DArray _NormalAtlas;
}


Varyings ShadingVert(Attributes In, uint InstanceId : SV_InstanceID)
{
    Varyings Out;
    TerrainCullResultData cullResultData = terrainCullResultData_buf[InstanceId];
    int nodeID = cullResultData.id;
    int lodTransitionInfo = cullResultData.lodTransition;
    TerrainNode terrainNodeData = terrainNodeData_buf[nodeID];
    
    int4 shouldStitch = (lodTransitionInfo.xxxx & int4(leftEncoding, rightEncoding, bottomEncoding, topEncoding)) > 0;

    float4 stitching = shouldStitch * float4(-In.Stitching.x, In.Stitching.y, In.Stitching.z, -In.Stitching.w);
    float2 vertexDisplacement = float2(stitching.z + stitching.w, stitching.x + stitching.y);
    float3 vertexPos;
    vertexPos.xz = ((In.Vertex.xz + vertexDisplacement) * terrainNodeData.scale) + float2(terrainNodeData.positionX, terrainNodeData.positionZ);
    vertexPos.y = 0;

    float2 vertexUV = (vertexPos.xz + 0.5) * _WorldSize.y;  // _WorldSize.y = (1 / TerrainSize)
    vertexUV = clamp(vertexUV, 2.0 * _WorldSize.y, 1 - 2.0 * _WorldSize.y);
    Out.VertexUV = vertexUV;
    
    // By our quadtree terrain, mip0 will be a quad of size in the range of (128, 164).
    // Sometimes there will be one more node of lower resolution (larger in scale) is subdivided to current lod level, 
    // so conservatively speaking the lod distance should be 128/2 + 32 = 96    (32 is our clipmap minimum update unit)
    // (applying vertex distance in the larger of x and z) 
    static const float LOD_DISTANCE = 96.0f;

    float2 vertDistToCam = abs((vertexUV * _WorldSize.x + _TerrainOffset.xz) - _CamPosSnapped.xz); 
    int depth = floor(log2(max(vertDistToCam.x, vertDistToCam.y) / LOD_DISTANCE) + 1);
    // nshm: depth = max(0, floor(log2(sqrt(dot(vertDistToCam, vertDistToCam)) / 96.0f) + 1)); // nshm:
    
    depth = max(0, depth);

    // TODO: for clipmap as heightmap, add combine the clipmap stack and clipmap pyramid into one TextureArray, then we can remove the branch 
    float4 rawHeightmapVal;
    [branch]
    if (depth >= _HeightClipmap_StackLen)
    {
        rawHeightmapVal = _HeightClipmap_Pyramid.SampleLevel(clipmap_point_clamp_sampler, vertexUV, 0);
    }
    else
    {
        depth = max(depth, _HeightClipmapFinestLevelUptoDate);
        float toroidalFix = _WorldSize.x / (_HeightClipmap_ClipSize * pow(2, depth));
        float2 toroidalUV  = frac(vertexUV * toroidalFix);
        rawHeightmapVal = _HeightClipmap_Stack.SampleLevel(clipmap_point_clamp_sampler, float3(toroidalUV, depth), 0);
    }
    
    // Unity's terrain heightmap encodes signed height value into unsigned texture, so we need to do a re-map
    // See https://docs.unity3d.com/Packages/com.unity.terrain-tools@5.0/manual/create-use-custom-shaders.html
    float unpackedHeight = UnpackHeightmap(rawHeightmapVal);
    unpackedHeight /= kMaxHeight;
    
    // vertex pos
    float3 vertexWorldPos;
    vertexWorldPos.xz = vertexPos.xz + _TerrainOffset.xz ;
    vertexWorldPos.y =  unpackedHeight * TERRAIN_HEIGHT_RANGE ;
    Out.Vertex_WS = vertexWorldPos;
    Out.Vertex_CS = mul(UNITY_MATRIX_VP, float4(vertexWorldPos, 1.0));
    
    Out.ViewDirection_WS = GetWorldSpaceViewDir(vertexWorldPos);
    Out.FogFactor = ComputeFogFactor(Out.Vertex_CS.z);
    
    #if !SAMPLE_TERRAIN_NORMAL_IN_PIXEL_SHADER
    // vertex normal and tangent space 
    float3 normal; float3 tangent; float3 bitangent;
    float4 rawNormalData = _TerrainNormalTex.SampleLevel(clipmap_point_clamp_sampler, float2(vertexUV.x, 1 - vertexUV.y), 0);   // TODO: align coordinate system
    normal = rawNormalData * 2.0f - 1.0f;
    normal.z *= -1;  // Convert to Unity's WorldSpace

    tangent = half3(1, 0, 0);
    bitangent = normalize(cross(normal, tangent));
    tangent = cross(bitangent, normal);
    Out.Normal_WS = normal;
    Out.Tangent_WS = tangent;
    Out.Bitangent_WS = bitangent;
    #endif
    
    return Out;
}


void UnpackIDW(in float4 IDW, out int4 ids, out float4 weights)
{
    float4 scaleToIntIDW = floor(IDW * 255);
    // To perfectly unpack the original id value, it should be round(IDW * 255/16),
    // but floor(IDW * 16) will suffice without causing overflow
    ids = floor(IDW * 16);  
    weights = (scaleToIntIDW - ids * 16) / 15.0;
}


// TODO: need splat clipmapSize for toroidal uv
void SplatmapMix(float2 vertexUV, out half3 mixedAlbedo, out half4 mixedNormal, out int4 ids_debug, out float4 weights_debug)
{
    // Compute mip level
    float2 vertDistToCam = abs((vertexUV * _WorldSize.x + _TerrainOffset.xz) - _CamPosSnapped.xz); 
    int stackDepth = max(0,floor(log2(max(vertDistToCam.x, vertDistToCam.y) / 96.0f) + 1));

    // SS
    float2 dx, dy;
    dx = ddx(vertexUV);
    dy = ddy(vertexUV);
    float maxSqrPixelDiff = max(dot(dx, dx), dot(dy, dy)) * _SplatIDWClipmap_TexSize;
    float mipLevelScreenSpace = 0.5 * log2(maxSqrPixelDiff);
    int mipLevelScreenSpaceFine = floor(mipLevelScreenSpace);

    // WS + SS
    stackDepth = max(stackDepth, mipLevelScreenSpaceFine);
    stackDepth = max(stackDepth, _SplatIDWClipmapFinestLevelUptoDate);
    
    stackDepth = min(stackDepth, _SplatIDWClipmap_StackLen);
    stackDepth = max(stackDepth, _SplatIDWClipmapFinestLevelUptoDate);
    float currDepthCoverage = _SplatIDWClipmap_ClipSize * pow(2, stackDepth);
    float currDepthResolution = _SplatIDWClipmap_ClipSize * pow(2, _SplatIDWClipmap_StackLen - stackDepth);

    float2 blendingOffsetFix = - float2(0.5, 0.5) / currDepthResolution;
    float2 blendingUV = vertexUV + blendingOffsetFix;
    float2 uv_frac = frac(blendingUV * currDepthResolution);

    float4 sharedIDW;
    float4 IDW_01;
    float4 IDW_10;
    float4 IDW_11;
    [branch]
    if (stackDepth >= _SplatIDWClipmap_StackLen)
    {
        sharedIDW   = _SplatIDWClipmap_Pyramid.Sample(clipmap_point_repeat_sampler, blendingUV);
        IDW_01      = _SplatIDWClipmap_Pyramid.Sample(clipmap_point_repeat_sampler, blendingUV + (float2(1, 0) / _SplatIDWClipmap_ClipSize));
        IDW_10      = _SplatIDWClipmap_Pyramid.Sample(clipmap_point_repeat_sampler, blendingUV + (float2(0, 1) / _SplatIDWClipmap_ClipSize));
        IDW_11      = _SplatIDWClipmap_Pyramid.Sample(clipmap_point_repeat_sampler, blendingUV + (float2(1, 1) / _SplatIDWClipmap_ClipSize));
    }
    else
    {
        float toroidalFix = _SplatIDWClipmap_TexSize / currDepthCoverage;
        
        float2 toridalUV_00 = float2(blendingUV * toroidalFix);
        float2 toridalUV_01 = float2((blendingUV + (float2(1, 0) / currDepthResolution)) * toroidalFix);
        float2 toridalUV_10 = float2((blendingUV + (float2(0, 1) / currDepthResolution)) * toroidalFix);
        float2 toridalUV_11 = float2((blendingUV + (float2(1, 1) / currDepthResolution)) * toroidalFix);
        
        sharedIDW   = _SplatIDWClipmap_Stack.SampleLevel(clipmap_point_repeat_sampler, float3(toridalUV_00, stackDepth), 0);
        IDW_01      = _SplatIDWClipmap_Stack.SampleLevel(clipmap_point_repeat_sampler, float3(toridalUV_01, stackDepth), 0);
        IDW_10      = _SplatIDWClipmap_Stack.SampleLevel(clipmap_point_repeat_sampler, float3(toridalUV_10, stackDepth), 0);
        IDW_11      = _SplatIDWClipmap_Stack.SampleLevel(clipmap_point_repeat_sampler, float3(toridalUV_11, stackDepth), 0);
    }

    int4 sharedIDs, IDs_01, IDs_10, IDs_11;
    float4 w_00, w_01, w_10, w_11;

    UnpackIDW(sharedIDW, sharedIDs, w_00);
    UnpackIDW(IDW_01, IDs_01, w_01);
    UnpackIDW(IDW_10, IDs_10, w_10);
    UnpackIDW(IDW_11, IDs_11, w_11);
    
    // Calculate blending weights
    float4  wSorted_01 = 0, wSorted_10 = 0, wSorted_11 = 0;

    [unroll]
    for (int p = 0; p < 4; p++)
    {
        wSorted_01[p] = sum4((sharedIDs[p].rrrr == IDs_01.rgba ? 1 : 0) * w_01);
        wSorted_10[p] = sum4((sharedIDs[p].rrrr == IDs_10.rgba ? 1 : 0) * w_10);
        wSorted_11[p] = sum4((sharedIDs[p].rrrr == IDs_11.rgba ? 1 : 0) * w_11);
    }
    
    float4 mixedWeight = lerp(lerp(w_00, wSorted_01, uv_frac.x), lerp(wSorted_10, wSorted_11, uv_frac.x), uv_frac.y);
    mixedWeight /= sum4(mixedWeight) + 1e-3f; 
    
    // Get base blending colors and normals
    // Different clipmap levels have different blendingUV offsets,
    // so to ensure uv transition consistency across different clipmap regions, we must sample using vertexUV instead of blendingUV 
    float2 splatTexUV = vertexUV * 256; // Unity uses 256 
    dx = ddx(splatTexUV);
    dy = ddy(splatTexUV);
    
    half3 col0 = _AlbedoAtlas.SampleGrad(clipmap_trilinear_repeat_sampler, float3(splatTexUV, sharedIDs.r), dx, dy);
    half3 col1 = _AlbedoAtlas.SampleGrad(clipmap_trilinear_repeat_sampler, float3(splatTexUV, sharedIDs.g), dx, dy);
    half3 col2 = _AlbedoAtlas.SampleGrad(clipmap_trilinear_repeat_sampler, float3(splatTexUV, sharedIDs.b), dx, dy);
    half3 col3 = _AlbedoAtlas.SampleGrad(clipmap_trilinear_repeat_sampler, float3(splatTexUV, sharedIDs.a), dx, dy);
    
    half4 normal0TS = _NormalAtlas.SampleGrad(clipmap_trilinear_repeat_sampler, float3(splatTexUV, sharedIDs.r), dx, dy);
    half4 normal1TS = _NormalAtlas.SampleGrad(clipmap_trilinear_repeat_sampler, float3(splatTexUV, sharedIDs.g), dx, dy);
    half4 normal2TS = _NormalAtlas.SampleGrad(clipmap_trilinear_repeat_sampler, float3(splatTexUV, sharedIDs.b), dx, dy);
    half4 normal3TS = _NormalAtlas.SampleGrad(clipmap_trilinear_repeat_sampler, float3(splatTexUV, sharedIDs.a), dx, dy);
    
    mixedAlbedo = col0 * mixedWeight.r + col1 * mixedWeight.g + col2 * mixedWeight.b + col3 * mixedWeight.a;
    mixedNormal = normal0TS * mixedWeight.r + normal1TS * mixedWeight.g + normal2TS * mixedWeight.b + normal3TS * mixedWeight.a;

    ids_debug = sharedIDs;
    weights_debug = mixedWeight;
}


half4 ShadingPixel(const Varyings In) : SV_Target 
{
    float2 vertexUV         = In.VertexUV.xy;
    
    float3 vertexNormal     ;
    float3 vertexTangent    ;
    float3 vertexBitangent  ;
    #if SAMPLE_TERRAIN_NORMAL_IN_PIXEL_SHADER
    float4 rawNormalData = _TerrainNormalTex.Sample(clipmap_trilinear_repeat_sampler, float2(vertexUV.x, 1 - vertexUV.y));   // TODO: align coordinate system
    vertexNormal = rawNormalData * 2.0f - 1.0f;
    vertexNormal.z *= -1;  // Convert to Unity's WorldSpace
    
    vertexTangent = half3(1, 0, 0);
    vertexBitangent = normalize(cross(vertexNormal, vertexTangent));
    vertexTangent = cross(vertexBitangent, vertexNormal);
    #else
    vertexNormal     = normalize(In.Normal_WS);
    vertexTangent    = normalize(In.Tangent_WS);
    vertexBitangent  = normalize(In.Bitangent_WS);
    #endif
    
    half3 albedo;
    half4 packedNormal;
    float4 sharedIDs_debug, mixedWeight_debug;
    SplatmapMix(vertexUV, albedo, packedNormal, sharedIDs_debug, mixedWeight_debug);

    half3 pixelNormalTS = UnpackNormal(packedNormal);
    half3 pixelNormalWS = TransformTangentToWorld(pixelNormalTS, half3x3(vertexTangent, vertexBitangent, vertexNormal));

    // shading
    #define PBR_TERRAIN_SHADING 1
    #if !PBR_TERRAIN_SHADING
        Light mainLight = GetMainLight();
        half diffuseTerm = saturate(dot(pixelNormalWS, mainLight.direction));
        half ambientTerm = 0.1; 
        half4 retCol =  half4((diffuseTerm * (1-ambientTerm) + ambientTerm) * albedo * mainLight.color, 1);
    #else
        half3 SH = 0;
        InputData inputData;
        inputData.positionWS = In.Vertex_WS;

        inputData.normalWS = pixelNormalWS.xyz * half3(1,1,-1);
        inputData.viewDirectionWS = normalize(In.ViewDirection_WS);
        inputData.bakedGI = SampleSH(pixelNormalWS); //SAMPLE_GI(input.uvMainAndLM.zw, SH, normalWS);

        inputData.shadowCoord = TransformWorldToShadowCoord(In.Vertex_WS);
        inputData.fogCoord = In.FogFactor;
        inputData.vertexLighting = 0;
        inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(In.Vertex_WS);
        inputData.shadowMask = SAMPLE_SHADOWMASK(In.VertexUV);

        half metallic = 0;
        half alpha = 1;
        half smoothness = 0;
        half occlusion = 1;
        
        half4 retCol = UniversalFragmentPBR(inputData, albedo, metallic, /* specular */ half3(0.0h, 0.0h, 0.0h), smoothness, occlusion, /* emission */ half3(0, 0, 0), alpha);
    #endif
    
    // retCol = half4((sharedIDs_debug.rg)/6,0, 1);                         // ID debug
    // retCol = half4(diffuseTerm, diffuseTerm, diffuseTerm, 0);        // normal debug
    // retCol = half4(saturate(vertexNormal), 1);                       // world space vertex normal debug
    // retCol = half4(saturate(pixelNormalWS), 1);                      // world space pixel normal debug
    // retCol = half4(mixedWeight_debug.xyzw);                                // mixedWeight debug
    // retCol = half4(albedo, 1);                     // mixedAlbedo debug
    retCol.xyz = max(retCol.xyz, albedo * 0.2);
    
    return retCol;
}


#endif