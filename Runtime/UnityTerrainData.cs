using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Unity.Mathematics;
using UnityEditor;
using static Unity.Mathematics.math;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;


public class UnityTerrainData : MonoBehaviour
{
    private float unitMeter = 1.0f;
    public Vector3 sceneOffset = Vector3.zero;

    private RenderTexture HeightmapRenderTexture;
    private RenderTexture NormalmapRenderTexture;
    private RenderTexture[] SplatmapRenderTextureArray;
    
    private TerrainLayer[] TextureLayer;
    private Terrain[] terrainArray;
    private static Dictionary<TerrainLayer, int> TextureLayerMap;

    private float[] splatOffsetArray;
    private float[] splatCountArray;
    private float[] tileData;
    private int splatTotalCount;
    private int heightResolution = 1025;
    private int splatResolution = 512;
    private int albedoResolution = 512;
    private int minMaxHeightResolution;
    private int terrainGridCount; /* zb: Number of Terrains on each side */

    private Vector4 heightmapScale;
    
    const float kMaxHeight = 32766.0f / 65535.0f;

    
    void CollectionUnityTerrain()
    {
        Terrain[] terrains = this.GetComponentsInChildren<Terrain>();
        int terrainCount = terrains.Length;
        System.Array.Sort<Terrain>(terrains, (a, b) =>
        {
            Vector3 apos = a.transform.position;
            Vector3 bpos = b.transform.position;
            int zc = apos.z.CompareTo(bpos.z);
            return zc != 0 ? zc : apos.x.CompareTo(bpos.x);
        });

        terrainArray = terrains;
        terrainGridCount = (int)Mathf.Sqrt(terrainArray.Length);

        TerrainData terrainData0 = terrains[0].terrainData;
        Vector3 size = terrainData0.size;

        // zb: (65535.0f / kMaxHeight) * hmScale.y ??
        // zb: _TerrainTilesScaleOffsetY = hmScale.y * (65535.0f / 32766.0f)
        heightmapScale = new Vector3(terrainData0.heightmapScale.x, terrainData0.heightmapScale.y / kMaxHeight,
            terrainData0.heightmapScale.z);
        heightResolution = terrainData0.heightmapResolution;
        splatResolution = terrainData0.alphamapResolution;
        this.unitMeter = size.x / (heightResolution - 1);
        this.sceneOffset = terrains[0].transform.position;

        TextureLayerMap = new Dictionary<TerrainLayer, int>();

        // zb: collect all layers in the terrain, different terrain chunks may use the same layers (same instance ID)
        List<TerrainLayer> layerList = new List<TerrainLayer>();
        for (int i = 0; i < terrainCount; i++)
        {
            foreach (var layer in terrains[i].terrainData.terrainLayers)
            {
                if (!TextureLayerMap.ContainsKey(layer))
                {
                    layerList.Add(layer);
                    TextureLayerMap.Add(layer, layerList.Count - 1);
                }
            }
        }

        TextureLayer = layerList.ToArray();

        tileData = new float[TextureLayer.Length];
        for (int i = 0; i < tileData.Length; i++)
        {
            tileData[i] = size.x / TextureLayer[i].tileSize.x;
        }

        albedoResolution = TextureLayer[0].diffuseTexture.width;

        splatTotalCount = 0;
        splatOffsetArray = new float[terrainArray.Length];
        splatCountArray = new float[terrainArray.Length];
        for (int i = 0; i < terrainCount; i++)
        {
            splatCountArray[i] = terrainArray[0].terrainData.alphamapTextureCount;
            splatOffsetArray[i] = splatTotalCount;
            splatTotalCount += (int)splatCountArray[i];
        }
    }

    
    void CreateTexture()
    {
        int heightmapResolution = (heightResolution - 1) * terrainGridCount;
        RenderTextureDescriptor HeightRTDesc = new RenderTextureDescriptor
        {
            width = heightmapResolution, height = heightmapResolution, volumeDepth = 1,
            dimension = TextureDimension.Tex2D, graphicsFormat = GraphicsFormat.R16_UNorm, depthBufferBits = 0,
            mipCount = -1, useMipMap = true, autoGenerateMips = false, bindMS = false, msaaSamples = 1
        };
        HeightmapRenderTexture = new RenderTexture(HeightRTDesc);
       
        
        RenderTextureDescriptor NormalRTDesc = new RenderTextureDescriptor
        {
            width = heightmapResolution, height = heightmapResolution, volumeDepth = 1,
            dimension = TextureDimension.Tex2D, graphicsFormat = GraphicsFormat.A2B10G10R10_UNormPack32,
            depthBufferBits = 0, mipCount = -1, useMipMap = true, autoGenerateMips = false, bindMS = false,
            msaaSamples = 1
        };
        NormalmapRenderTexture = new RenderTexture(NormalRTDesc);
        

        int splatmapResolution = splatResolution * terrainGridCount;
        SplatmapRenderTextureArray = new RenderTexture[TextureLayer.Length];
        RenderTextureDescriptor SplatRTDesc = new RenderTextureDescriptor
        {
            width = splatmapResolution, height = splatmapResolution, volumeDepth = 1,
            dimension = TextureDimension.Tex2D, graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
            depthBufferBits = 0, mipCount = -1, useMipMap = true, autoGenerateMips = false, bindMS = false,
            msaaSamples = 1
        };
        for (int layer = 0; layer < TextureLayer.Length; layer++)
        {
            SplatmapRenderTextureArray[layer] = new RenderTexture(SplatRTDesc);
        }
    }

    
    void InitializeTexture()
    {
        int heightmapChunkResolution = (heightResolution - 1);
        for (int terrainChunkID = 0; terrainChunkID < terrainArray.Length; terrainChunkID++)
        {
            var srcTerrain = terrainArray[terrainChunkID];
            AssembleTexture(srcTerrain.terrainData.heightmapTexture, terrainChunkID, heightmapChunkResolution,
                HeightmapRenderTexture);
            AssembleTexture(srcTerrain.normalmapTexture, terrainChunkID, heightmapChunkResolution,
                NormalmapRenderTexture);

            for (int terrainLayer = 0; terrainLayer < TextureLayer.Length; terrainLayer++)
            {
                if (terrainLayer >= srcTerrain.terrainData.alphamapTextures.Length)
                {
                    continue;
                }

                var srcSplat = srcTerrain.terrainData.alphamapTextures[terrainLayer];
                var dstSplat = SplatmapRenderTextureArray[terrainLayer];
                AssembleTexture(srcSplat, terrainChunkID, splatResolution, dstSplat);
            }
        }

        int heightmapResolution = heightmapChunkResolution * terrainGridCount;
        HeightmapRenderTexture.GenerateMips();
        NormalmapRenderTexture.GenerateMips();
        for (int terrainLayer = 0; terrainLayer < TextureLayer.Length; terrainLayer++)
        {
                SplatmapRenderTextureArray[terrainLayer].GenerateMips();
        }
        
        // Copy data from render texture to texture and write to disk
        AsyncGPUReadbackRequest readbackRequest;
        NativeArray<byte> pixelData;
        byte[] pngData;
        
        // Heightmap
        readbackRequest = AsyncGPUReadback.Request(HeightmapRenderTexture);
        readbackRequest.WaitForCompletion();
        pixelData = readbackRequest.GetData<byte>();
        
        Texture2D heightTex = new Texture2D(heightmapResolution, heightmapResolution, TextureFormat.R16, true, true);
        heightTex.SetPixelData(pixelData, 0, 0);
        pixelData.Dispose();
        heightTex.Apply(true);
        AssetDatabase.CreateAsset(heightTex, "Assets/Resources/TerrainHeightmap.asset");

        pngData = heightTex.EncodeToPNG();
        string heightTexPath = Application.dataPath + "/Resources/TerrainHeightmap.png";
        System.IO.File.WriteAllBytes(heightTexPath, pngData);
        
        // Normalmap
        // AsyncGPUReadBack does not support GraphicsFormat.A2B10G10R10_UNormPack32, so we have to blit to RGBA16 first
        RenderTextureDescriptor NormalRTDesc = new RenderTextureDescriptor
        {
            width = heightmapResolution, height = heightmapResolution, volumeDepth = 1,
            dimension = TextureDimension.Tex2D, graphicsFormat = GraphicsFormat.R16G16B16A16_UNorm, 
            depthBufferBits = 0, mipCount = -1, useMipMap = true, autoGenerateMips = false, bindMS = false,
            msaaSamples = 1
        };
        RenderTexture Intermediate; 
        Intermediate = new RenderTexture(NormalRTDesc);
        Graphics.Blit(NormalmapRenderTexture, Intermediate);
        Intermediate.GenerateMips();
        
        readbackRequest = AsyncGPUReadback.Request(Intermediate);
        readbackRequest.WaitForCompletion();
        pixelData = readbackRequest.GetData<byte>();
        
        Texture2D normalTex = new Texture2D(heightmapResolution, heightmapResolution, TextureFormat.RGBA64, true, true);
        normalTex.SetPixelData(pixelData, 0, 0);
        pixelData.Dispose();
        normalTex.Apply(true);
        AssetDatabase.CreateAsset(normalTex, "Assets/Resources/TerrainNormalmap.asset");

        pngData = normalTex.EncodeToPNG();
        string normalTexPath = Application.dataPath + "/Resources/TerrainNormalmap.png";
        System.IO.File.WriteAllBytes(normalTexPath, pngData);
        
        // Splatmap
        int splatmapResolution = splatResolution * terrainGridCount;
        for (int layer = 0; layer < TextureLayer.Length; layer++)
        {
            readbackRequest = AsyncGPUReadback.Request(SplatmapRenderTextureArray[layer]);
            readbackRequest.WaitForCompletion();
            pixelData = readbackRequest.GetData<byte>();
        
            Texture2D splatTex = new Texture2D(splatmapResolution, splatmapResolution, TextureFormat.RGBA32, true, true);
            splatTex.SetPixelData(pixelData, 0, 0);
            pixelData.Dispose();
            splatTex.Apply(true);
            AssetDatabase.CreateAsset(splatTex, "Assets/Resources/TerrainWeightmap_" + layer + ".asset");

            pngData = splatTex.EncodeToPNG();
            string splatTexPath = Application.dataPath + "/Resources/TerrainWeightmap_" + layer + ".png";
            System.IO.File.WriteAllBytes(splatTexPath, pngData);
        }
    }


    private GraphicsFormat GetAlbedoFormat()
    {
        return TextureLayer[0].diffuseTexture.graphicsFormat;
    }


    private GraphicsFormat GetNormalFormat()
    {
        return TextureLayer[0].normalMapTexture.graphicsFormat;
    }


    private void AssembleTexture(Texture srcTexture, int id, int srcResolution, Texture destTexture)
    {
        int x = id % terrainGridCount;
        int z = id / terrainGridCount;
        int destX = x * srcResolution;
        int destY = z * srcResolution;

        Graphics.CopyTexture(srcTexture, 0, 0, 0, 0, srcResolution, srcResolution, destTexture, 0, 0, destX, destY);
    }
}