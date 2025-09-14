using System;
using System.Net;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Windows;

// This class contains helper functions for generating on-disk precalculated data for terrain quad tree traversal 
public class TerrainQuadTreeUtility : MonoBehaviour
{
    // compute shaders
    public ComputeShader TerrainQuadTreeUtilityCS;

    private static readonly int ID_CSGenerateHeightRangeMip0 = 0;
    private static readonly int ID_CSGenerateHeightRangeMipDown = 1;
    private static readonly int ID_CSGenerateTerrainNodeAABBBuffer = 2;
    private static readonly int ID_CSGenerateTerrainNodeDataBuffer = 3;
    
    public Texture2D heightmap;
    
    [ContextMenu("Generate Height Range Texture")]
    public void GenerateHeightRangeTexture()
    {
        int mip0Size = heightmap.width;
        int mipCount = (int)Math.Log(mip0Size, 2) + 1;

        RenderTexture heightRangeTex = new RenderTexture(mip0Size, mip0Size, 0, RenderTextureFormat.RG32, mipCount)
        {
            enableRandomWrite = true,
            useMipMap = true
        };
        heightRangeTex.Create();

        
        TerrainQuadTreeUtilityCS.SetTexture(ID_CSGenerateHeightRangeMip0, "Heightmap", heightmap);
        TerrainQuadTreeUtilityCS.SetTexture(ID_CSGenerateHeightRangeMip0, "HeightRangeTex", heightRangeTex);
        int groupCount = heightmap.width / 8;
        TerrainQuadTreeUtilityCS.Dispatch(ID_CSGenerateHeightRangeMip0, groupCount, groupCount, 1);
        
        
        // Height Range Down
        for (int mip = 0; mip < mipCount - 1; mip++)
        {
            TerrainQuadTreeUtilityCS.SetTexture(ID_CSGenerateHeightRangeMipDown, "SrcHeightRangeTexMip", heightRangeTex, mip);
            TerrainQuadTreeUtilityCS.SetTexture(ID_CSGenerateHeightRangeMipDown, "DestHeightRangeTexMip",
                heightRangeTex, mip + 1);

            int mipSize = heightmap.width / (int)Math.Pow(2, mip);
            groupCount = Math.Max(mipSize / 8, 1);
            TerrainQuadTreeUtilityCS.Dispatch(ID_CSGenerateHeightRangeMipDown, groupCount, groupCount, 1);
        }

        // Create Asset
        Texture2D tex = new Texture2D(mip0Size, mip0Size, TextureFormat.RG32, mipCount, true);
        tex.Apply(true, false);
        for (int mip = 0; mip <= mipCount - 1; mip++)
        {
            var req = AsyncGPUReadback.Request(heightRangeTex, mip);
            req.WaitForCompletion();
            var data = req.GetData<byte>();
            tex.SetPixelData(data, mip);
        }

        AssetDatabase.DeleteAsset("Assets/GPUTerrainClipmap/Temp/HeightRangeTex.asset");
        AssetDatabase.CreateAsset(tex, "Assets/GPUTerrainClipmap/Temp/HeightRangeTex.asset");
    }

    
    private const float heightScale = 800;
    private const float kMaxHeight = (32766.0f / 65535.0f); // rescale unsigned (0, 0.5) to signed (0, 1) 
    public Texture2D heightRangeTex;
    
    [ContextMenu("Generate Terrain Node AABB Buffer")]
    public void GenerateTerrainNodeAABBBuffer()
    {
        
        int quadTreeLevels = 7;

        int rootLevelNodeSize = 1024;
        float rootLevelnodeHalfSize = rootLevelNodeSize / 2.0f;
        int rootLevelSideNodes = 4;
        int heightRangestartMip = 10;
        
        int[] bufferLevelLen = new int[quadTreeLevels];
        int[] bufferStartIndex = new int[quadTreeLevels];
        
        bufferStartIndex[0] = 0;
        bufferLevelLen[0] = rootLevelSideNodes * rootLevelSideNodes;

        int bufferTotalLen = 0;
        for (int i = 0; i < bufferLevelLen.Length; i++)
        {
            bufferLevelLen[i] = rootLevelSideNodes * rootLevelSideNodes * (int)Mathf.Pow(4, i);
            bufferTotalLen += bufferLevelLen[i];
        }
        
        ComputeBuffer terrainNodeAABBBuffer = new ComputeBuffer(bufferTotalLen, Marshal.SizeOf(typeof(GPUClipmapTerrainCommon.TerrainNodeAABB)), ComputeBufferType.Structured);
        
        for (int i = 1; i < bufferStartIndex.Length; i++)
        {
            bufferStartIndex[i] = bufferStartIndex[i - 1] + bufferLevelLen[i-1];
        }
        
        // Dispatch compute
        TerrainQuadTreeUtilityCS.SetBuffer(ID_CSGenerateTerrainNodeAABBBuffer, "TerrainNodeAABBBuffer", terrainNodeAABBBuffer);
        TerrainQuadTreeUtilityCS.SetTexture(ID_CSGenerateTerrainNodeAABBBuffer, "AABBHeightRangeLUT", heightRangeTex);
        TerrainQuadTreeUtilityCS.SetFloat("HeightScale", heightScale / kMaxHeight);
        TerrainQuadTreeUtilityCS.SetInt("RootLevelMip", heightRangestartMip);
        TerrainQuadTreeUtilityCS.SetInt("RootLevelSideNodes", rootLevelSideNodes);
        
        int currLevelSideNodes = rootLevelSideNodes;
        float currLevelNodeHalfSize = rootLevelnodeHalfSize;
        for (int currLevel = 0; currLevel < quadTreeLevels; currLevel++)
        {
            TerrainQuadTreeUtilityCS.SetInt("BufferCurrLevelStartIndex", bufferStartIndex[currLevel]);
            TerrainQuadTreeUtilityCS.SetFloat("CurrLevelNodeHalfSize", currLevelNodeHalfSize);
            TerrainQuadTreeUtilityCS.SetInt("CurrLevel", currLevel);
            TerrainQuadTreeUtilityCS.SetInt("CurrLevelSideNodes", currLevelSideNodes);
            
            int groupSize = Math.Max(1, currLevelSideNodes / 8);    // 8 is the dimension in the compute shader
            TerrainQuadTreeUtilityCS.Dispatch(ID_CSGenerateTerrainNodeAABBBuffer, groupSize, 1, groupSize);
            currLevelNodeHalfSize /= 2;
            currLevelSideNodes *= 2;
        }
        
        // Transfer data to disk
        var buf = new GPUClipmapTerrainCommon.TerrainNodeAABB[bufferTotalLen];
        terrainNodeAABBBuffer.GetData(buf);

        TerrainNodeAABBBuffer buffer = ScriptableObject.CreateInstance<TerrainNodeAABBBuffer>();
        buffer.size = bufferTotalLen;
        buffer.data = buf;
        AssetDatabase.CreateAsset(buffer, "Assets/GPUTerrainClipmap/Temp/TerrainNodeAABBBuffer.asset");
        terrainNodeAABBBuffer.Release();
    }
    
    [ContextMenu("Generate Terrain Node Data Buffer")]
    public void GenerateNodeDataBuffer()
    {
        TerrainNodeAABBBuffer aabbBuffer = AssetDatabase.LoadAssetAtPath<TerrainNodeAABBBuffer>("Assets/GPUTerrainClipmap/Temp/TerrainNodeAABBBuffer.asset");
        int bufferSize = aabbBuffer.size;
        
        ComputeBuffer terrainNodeAABBBuffer = new ComputeBuffer(bufferSize, Marshal.SizeOf(typeof(GPUClipmapTerrainCommon.TerrainNodeAABB)), ComputeBufferType.Structured);
        ComputeBuffer terrainNodeDataBuffer = new ComputeBuffer(bufferSize, Marshal.SizeOf(typeof(GPUClipmapTerrainCommon.TerrainNodeData)), ComputeBufferType.Structured);
        terrainNodeAABBBuffer.SetData(aabbBuffer.data);
        TerrainQuadTreeUtilityCS.SetBuffer(ID_CSGenerateTerrainNodeDataBuffer, "TerrainNodeAABBBufferRead", terrainNodeAABBBuffer);
        TerrainQuadTreeUtilityCS.SetBuffer(ID_CSGenerateTerrainNodeDataBuffer, "TerrainNodeDataBuffer", terrainNodeDataBuffer);
        TerrainQuadTreeUtilityCS.SetInt("bufferSize", bufferSize);
        
        int groupSize = (int)Math.Ceiling((float)bufferSize / 64);
        TerrainQuadTreeUtilityCS.Dispatch(ID_CSGenerateTerrainNodeDataBuffer, groupSize, 1, 1);
        
        // Transfer data to disk
        TerrainNodeDataBuffer dataBuffer = ScriptableObject.CreateInstance<TerrainNodeDataBuffer>();
        dataBuffer.size = bufferSize;
        dataBuffer.data = new GPUClipmapTerrainCommon.TerrainNodeData[terrainNodeDataBuffer.count];
        terrainNodeDataBuffer.GetData(dataBuffer.data);
        
        AssetDatabase.CreateAsset(dataBuffer, "Assets/GPUTerrainClipmap/Temp/TerrainNodeDataBuffer.asset");
    }
}