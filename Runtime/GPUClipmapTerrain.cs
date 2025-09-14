using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine.Experimental.Rendering;
using static Unity.Mathematics.math;

using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

public unsafe class GPUClipmapTerrain : MonoBehaviour
{
    // attributes
    private const int MAX_LOD = 6;  // 4096 to 16
    private readonly int baseLevelNodeCounts = 16;

    // kernels
    private const int ID_CSCullPassResultArgReset = 0; 
    private const int ID_CSTerrainNextLevelCandidateReset = 1;
    private const int ID_CSTerrainNextLevelCandidate = 2;
    
    // intermediate vars
    private Plane[] frustumPlanes = new Plane[6];
    private Vector4[] frustumPlanesVecs = new Vector4[6];
    [SerializeField] private ComputeShader terrainCompute;
    private CommandBuffer cmdBuffer;
    private Mesh patchMesh;
    private Material terrainLitMaterial;

    private ComputeBuffer patchMeshIndirectArgs;
    private ComputeBuffer[] terrainLevelCandidateBuffers; // 2 buffers for current level and next level
    private ComputeBuffer terrainCullResultDataBuffer;
    private ComputeBuffer terrainNodeAABBBuffer;
    private ComputeBuffer terrainNodeDataBuffer;

    private Texture2DArray AlbedoAtlas;
    private Texture2DArray NormalAtlas;
    
    private bool initializedCS = false;
    
    [SerializeField] private Clipmap heightClipmap;
    [SerializeField] private Clipmap splatIDWeightClipmap;
    [SerializeField] private Texture2D terrainNormalmap;
    
    [SerializeField] private Texture[] AlbedoTex;
    [SerializeField] private Texture[] NormalTex;

    [SerializeField] private TerrainNodeAABBBuffer terrainNodeAABBBufferStorage;
    [SerializeField] private TerrainNodeDataBuffer terrainNodeDataBufferStorage;  
    
    [SerializeField] private Transform playerPawn;
    
    // TODO: ? merged into one coordinate system (0, worldSize)
    [SerializeField] private Transform worldCenterPawn; // clipmap camPos calculation
    [SerializeField] private Transform worldOriginPawn; // quad tree camPos calculation
    

    #region Initialize Helper Functions
    private int GetIndexBaseAtLOD(int depth)    // TODO: to return list 
    {
        int reverseDepth = MAX_LOD - depth;
        if (reverseDepth <= 0)
        {
            return 0;
        }
        
        // sum_{n=1}^{reverseDepth} 4^{n+1}
        int indexBase = (16 * ((int)Math.Pow(4, reverseDepth) - 1)) / 3;
        return indexBase;
    }

    private int GetIndirectArgsOffsetAtLOD(int depth)   // TODO: to return list 
    {
        // IndirectArgs: index count per instance | instance count | start index location | base vertex location | start instance location
        int reverseDepth = MAX_LOD - depth;
        int argsOffset = 5 * reverseDepth;
        return argsOffset;
    }
    #endregion
    
    
    private void OnEnable()
    {
        if (!SystemInfo.supportsComputeShaders)
        {
            Debug.LogError("System does not support compute shaders.");
            return;
        }
        if (heightClipmap == null)
        {
            Debug.LogError("No height clipmap found.");
            return;
        }

        if (splatIDWeightClipmap == null)
        {
            Debug.LogError("No splatIDWeight clipmap found.");
        }
        
        initializedCS = false;
        
        // Initialize clipmap (this is done automatically)
        terrainLitMaterial = new Material(Shader.Find("Terrain/ClipmapTerrainLit"));
        terrainLitMaterial.enableInstancing = true;
        heightClipmap.Initialize();
        splatIDWeightClipmap.Initialize();
        
        // Generate the patch mesh
        patchMesh = GPUClipmapTerrainUtility.CreatePatchMesh(16, 1);
       
        // Generate Atlas textures for splatting
        int albedoAtlasSize = AlbedoTex[0].width;
        int albedoAtlasLength = AlbedoTex.Length;
        GraphicsFormat albedoAtlasFormat = AlbedoTex[0].graphicsFormat;
        AlbedoAtlas = new Texture2DArray(albedoAtlasSize, albedoAtlasSize, albedoAtlasLength, albedoAtlasFormat, TextureCreationFlags.MipChain);
        for (int id = 0; id < albedoAtlasLength; id++)
        {
            Graphics.CopyTexture(AlbedoTex[id], 0, AlbedoAtlas, id);
            Resources.UnloadAsset(AlbedoTex[id]);
        }
        
        int normalAtlasSize = NormalTex[0].width;
        int normalAtlasLength = NormalTex.Length;
        GraphicsFormat normalAtlasFormat = NormalTex[0].graphicsFormat;
        NormalAtlas = new Texture2DArray(normalAtlasSize, normalAtlasSize, normalAtlasLength, normalAtlasFormat, TextureCreationFlags.MipChain);
        for (int id = 0; id < normalAtlasLength; id++)
        {
            Graphics.CopyTexture(NormalTex[id], 0, NormalAtlas, id);
            Resources.UnloadAsset(NormalTex[id]);
        }
        
        // Generate buffers
        cmdBuffer = new CommandBuffer();
        cmdBuffer.name = "GPUTerrainClipmap";
        
        patchMeshIndirectArgs = new ComputeBuffer(5, sizeof(int), ComputeBufferType.IndirectArguments);
        patchMeshIndirectArgs.name = "MeshIndirectArgs";
        uint[] meshIndirectArgs = {patchMesh.GetIndexCount(0), 0, 0, 0, 0};
        patchMeshIndirectArgs.SetData(meshIndirectArgs, 0, 0, 5); 
        
        terrainLevelCandidateBuffers = new ComputeBuffer[2];
        terrainLevelCandidateBuffers[0] = new ComputeBuffer(128, sizeof(uint), ComputeBufferType.IndirectArguments);
        terrainLevelCandidateBuffers[0].name = "terrainLevelCandidate_0";
        terrainLevelCandidateBuffers[1] = new ComputeBuffer(128, sizeof(uint), ComputeBufferType.IndirectArguments);
        terrainLevelCandidateBuffers[1].name = "terrainLevelCandidate_1";
        
        terrainCullResultDataBuffer = new ComputeBuffer(512, Marshal.SizeOf(typeof(GPUClipmapTerrainCommon.TerrainCullResultData)), ComputeBufferType.Structured);
        terrainCullResultDataBuffer.name = "terrainCullResultDataBuffer";
        
        terrainNodeAABBBuffer = new ComputeBuffer(terrainNodeAABBBufferStorage.size, Marshal.SizeOf(typeof(GPUClipmapTerrainCommon.TerrainNodeAABB)), ComputeBufferType.Structured);
        terrainNodeAABBBuffer.name = "terrainNodeAABBBuffer";
        terrainNodeAABBBuffer.SetData(terrainNodeAABBBufferStorage.data);
        
        terrainNodeDataBuffer = new ComputeBuffer(terrainNodeDataBufferStorage.size, Marshal.SizeOf(typeof(GPUClipmapTerrainCommon.TerrainNodeData)), ComputeBufferType.Structured);
        terrainNodeDataBuffer.name = "terrainNodeDataBuffer";
        terrainNodeDataBuffer.SetData(terrainNodeDataBufferStorage.data);
        
        GPUClipmapTerrainPass.s_ExecuteAction += Render;
    }

    
    private void OnDisable()
    {
        patchMeshIndirectArgs.Release();
        terrainLevelCandidateBuffers[0].Release();
        terrainLevelCandidateBuffers[1].Release();
        terrainCullResultDataBuffer.Release();
        terrainNodeAABBBuffer.Release();
        terrainNodeDataBuffer.Release();
        
        GPUClipmapTerrainPass.s_ExecuteAction -= Render;
    }
    
    
    private readonly uint[] firstLevelNodeCandidates = {16, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15};
    // private readonly float[] lodDistances = {0, 64, 96, 224, 480, 992, 2016, 65535};    // lodDistance = 2 levelSize - 32
    private readonly float[] lodDistances = {0, 64, 128-32, 256-32, 512-32, 1024-32, 2048-32, 65535};   

    private int lastRenderFrame = 0;
    void Render(ScriptableRenderContext context, CameraData cameraData, int pass)
    {
        cmdBuffer.Clear();
        if (cameraData.cameraType != CameraType.Game || lastRenderFrame == Time.frameCount)
        {
            cmdBuffer.DrawMeshInstancedIndirect(patchMesh, 0, terrainLitMaterial, pass, patchMeshIndirectArgs, 0, null);  
            
            context.ExecuteCommandBuffer(cmdBuffer);
            cmdBuffer.Clear();
            return;
        }
        
        if (!initializedCS)
        {
            BindTerrainComputeBuffer();
            BindTerrainShadingBuffer();
            initializedCS = true;
        }

        // per frame update
        cmdBuffer.DispatchCompute(terrainCompute, ID_CSCullPassResultArgReset, 1, 1, 1);
        UpdateCameraRelated(cameraData.camera);

        // per dispatch update
        float currTerrainLODLevelSize = 1024;
        terrainLevelCandidateBuffers[0].SetData(firstLevelNodeCandidates, 0, 0, firstLevelNodeCandidates.Length);  // first level contains all root nodes
        terrainLevelCandidateBuffers[1].SetData(firstLevelNodeCandidates, 0, 0, firstLevelNodeCandidates.Length);  // first level contains all root nodes
        for (int lodLevel = MAX_LOD; lodLevel >= 0; lodLevel--)
        {
            int currBufferIndex = lodLevel % 2;
            int nextBufferIndex = (lodLevel + 1) % 2; 
            float currTerrainLODDistance = lodDistances[lodLevel];
            float lastTerrainLODDistance = lodDistances[lodLevel+1];
            
            // reset next level candidates before each quad tree level calculation
            cmdBuffer.SetComputeBufferParam(terrainCompute, ID_CSTerrainNextLevelCandidateReset, ShaderConstants.TerrainNextLevelCandidate, terrainLevelCandidateBuffers[nextBufferIndex]);
            cmdBuffer.DispatchCompute(terrainCompute, ID_CSTerrainNextLevelCandidateReset, 1, 1, 1);
            
            cmdBuffer.SetComputeBufferParam(terrainCompute, ID_CSTerrainNextLevelCandidate, ShaderConstants.TerrainCurrLevelCandidate, terrainLevelCandidateBuffers[currBufferIndex]);
            cmdBuffer.SetComputeBufferParam(terrainCompute, ID_CSTerrainNextLevelCandidate, ShaderConstants.TerrainNextLevelCandidate, terrainLevelCandidateBuffers[nextBufferIndex]);
            cmdBuffer.SetComputeFloatParam(terrainCompute, ShaderConstants.CurrTerrainLODLevelSize, currTerrainLODLevelSize);
            
            cmdBuffer.SetComputeFloatParam(terrainCompute, ShaderConstants.CurrTerrainLODDistance, currTerrainLODDistance);
            cmdBuffer.SetComputeFloatParam(terrainCompute, ShaderConstants.LastTerrainLODDistance, lastTerrainLODDistance);
            cmdBuffer.SetComputeIntParam(terrainCompute, ShaderConstants.CurrLODArgOffset, GetIndirectArgsOffsetAtLOD(MAX_LOD));  // Our demo has only one lod
            cmdBuffer.SetComputeIntParam(terrainCompute,ShaderConstants.CurrLODIndexBase, GetIndexBaseAtLOD(lodLevel));
            
            int nextLodLevel = lodLevel - 1;
            cmdBuffer.SetComputeIntParam(terrainCompute,ShaderConstants.NextLODIndexBase, GetIndexBaseAtLOD(nextLodLevel));
            
            cmdBuffer.DispatchCompute(terrainCompute, ID_CSTerrainNextLevelCandidate, 8, 1, 1);
            
            currTerrainLODLevelSize /= 2;
        }
        
        // Tell the shader some clipmap levels are not up to date, the shader will skip sampling those levels
        terrainLitMaterial.SetInteger(ShaderConstants.HeightClipmapFinestLevelUptoDate, heightClipmap.FinestClipStackLevelUptoDate);  
        terrainLitMaterial.SetInteger(ShaderConstants.SplatIDWClipmapFinestLevelUptoDate, splatIDWeightClipmap.FinestClipStackLevelUptoDate);    

        cmdBuffer.DrawMeshInstancedIndirect(patchMesh, 0, terrainLitMaterial, pass, patchMeshIndirectArgs, 0, null);
        
        context.ExecuteCommandBuffer(cmdBuffer);
        lastRenderFrame = Time.frameCount;
    }

    
    private void BindTerrainComputeBuffer()
    {
        cmdBuffer.SetComputeIntParam(terrainCompute, ShaderConstants.TotalRenderBatchCount, 1);
        cmdBuffer.SetComputeBufferParam(terrainCompute, ID_CSCullPassResultArgReset,
            ShaderConstants.PatchMeshIndirectArgs, patchMeshIndirectArgs);

        cmdBuffer.SetComputeVectorParam(terrainCompute, ShaderConstants.TerrainOffset, float4((float3)worldOriginPawn.position, 4096)); // w is not being used here, just to be consistent with nshm
        cmdBuffer.SetComputeBufferParam(terrainCompute, ID_CSTerrainNextLevelCandidate, ShaderConstants.PatchMeshIndirectArgs, patchMeshIndirectArgs);
        cmdBuffer.SetComputeBufferParam(terrainCompute, ID_CSTerrainNextLevelCandidate, ShaderConstants.TerrainNodeAABBBuffer, terrainNodeAABBBuffer);
        cmdBuffer.SetComputeBufferParam(terrainCompute, ID_CSTerrainNextLevelCandidate, ShaderConstants.TerrainCullResultDataBuffer, terrainCullResultDataBuffer);
    }

    
    private void BindTerrainShadingBuffer()
    {
        Shader.SetGlobalBuffer(ShaderConstants.TerrainCullResultDataBufferShading, terrainCullResultDataBuffer);
        Shader.SetGlobalBuffer(ShaderConstants.TerrainNodeDataBuffer, terrainNodeDataBuffer);
        Shader.SetGlobalVector(ShaderConstants.TerrainOffset, float4((float3)worldOriginPawn.position, 0));
        Shader.SetGlobalVector(ShaderConstants.WorldSize, float4(4096.0f, 1/4096.0f, 0, 0));

        
        terrainLitMaterial.SetTexture(ShaderConstants.TerrainNormalTex, terrainNormalmap);
        terrainLitMaterial.SetTexture(ShaderConstants.AlbedoAtlas, AlbedoAtlas);
        terrainLitMaterial.SetTexture(ShaderConstants.NormalAtlas, NormalAtlas);
        
        // Heightmap
        terrainLitMaterial.SetTexture(ShaderConstants.HeightClipmapStack, heightClipmap.ClipmapStack);
        terrainLitMaterial.SetTexture(ShaderConstants.HeightClipmapPyramid, heightClipmap.ClipmapPyramid);
        terrainLitMaterial.SetFloat(ShaderConstants.HeightClipmapClipSize, heightClipmap.ClipSize);
        terrainLitMaterial.SetFloat(ShaderConstants.HeightClipmapTexSize, heightClipmap.TextureSize);
        terrainLitMaterial.SetInteger(ShaderConstants.HeightClipmapStackLen, heightClipmap.ClipmapStackLevelCount);
        // SplatID & weight map 
        terrainLitMaterial.SetTexture(ShaderConstants.SplatIDWClipmapStack, splatIDWeightClipmap.ClipmapStack);
        terrainLitMaterial.SetTexture(ShaderConstants.SplatIDWClipmapPyramid, splatIDWeightClipmap.ClipmapPyramid);
        terrainLitMaterial.SetFloat(ShaderConstants.SplatIDWClipmapClipSize, splatIDWeightClipmap.ClipSize);
        terrainLitMaterial.SetFloat(ShaderConstants.SplatIDWClipmapTexSize, splatIDWeightClipmap.TextureSize);
        terrainLitMaterial.SetInteger(ShaderConstants.SplatIDWClipmapStackLen, splatIDWeightClipmap.ClipmapStackLevelCount);
    }
    
    
    private void UpdateCameraRelated(Camera camera)
    {
        Vector3 camPos = camera.transform.position;
        Vector2 camPosSnapped = ClipmapUtility.SnapToGrid(new Vector2(camPos.x, camPos.z), 16);
        cmdBuffer.SetComputeVectorParam(terrainCompute, ShaderConstants.CamPosition, new Vector4(camPos.x, camPos.z));
        cmdBuffer.SetComputeVectorParam(terrainCompute, ShaderConstants.CamPositionSnapped, new Vector4(camPosSnapped.x, camPosSnapped.y));
        
        GeometryUtility.CalculateFrustumPlanes(camera, frustumPlanes);
        for( int i = 0; i < frustumPlanes.Length; i++)
        {
            Plane p = frustumPlanes[i];
            frustumPlanesVecs[i] = p.normal;
            frustumPlanesVecs[i].w = p.distance; 
        }

        (float3 frustumPlanesMin, float3 frustumPlanesMax) = GPUTerrainUtility.GetFrustumMinMaxPoint(camera);
        cmdBuffer.SetComputeVectorArrayParam(terrainCompute, ShaderConstants.FrustumPlanes, frustumPlanesVecs);
        cmdBuffer.SetComputeVectorParam(terrainCompute, ShaderConstants.FrustumMinPoint, float4(frustumPlanesMin,1.0f));
        cmdBuffer.SetComputeVectorParam(terrainCompute, ShaderConstants.FrustumMaxPoint, float4(frustumPlanesMax, 1.0f));
        Shader.SetGlobalVector(ShaderConstants.CamPosSnapped, new Vector4(camPosSnapped.x, camPos.y, camPosSnapped.y, 1.0f));
    }

    
    private class  ShaderConstants
    {
        // QuadTree Compute
        public static readonly int PatchMeshIndirectArgs = Shader.PropertyToID("PatchMeshIndirectArgs"); // DrawPatchMeshInstancedIndirectArgs
        public static readonly int TotalRenderBatchCount = Shader.PropertyToID("TotalRenderBatchCount"); // max lod
        public static readonly int TerrainCullResultDataBuffer = Shader.PropertyToID("TerrainCullResultDataBuffer");
        public static readonly int TerrainCurrLevelCandidate = Shader.PropertyToID("TerrainCurrLevelCandidate");
        public static readonly int TerrainNextLevelCandidate = Shader.PropertyToID("TerrainNextLevelCandidate");
        public static readonly int CurrTerrainLODLevelSize = Shader.PropertyToID("_CurrTerrainLODLevelSize");
        public static readonly int LastTerrainLODDistance = Shader.PropertyToID("_LastTerrainLODDistance");
        public static readonly int CurrTerrainLODDistance = Shader.PropertyToID("_CurrTerrainLODDistance");
        public static readonly int CurrLODArgOffset = Shader.PropertyToID("_CurrLODArgOffset");
        public static readonly int CurrLODIndexBase = Shader.PropertyToID("_CurrLODIndexBase");
        public static readonly int NextLODIndexBase = Shader.PropertyToID("_NextLODIndexBase");
        public static readonly int TerrainNodeAABBBuffer = Shader.PropertyToID("TerrainNodeAABBBuffer");
        public static readonly int TerrainOffset = Shader.PropertyToID("_TerrainOffset");
        public static readonly int CamPosition = Shader.PropertyToID("_CamPosition");
        public static readonly int CamPositionSnapped = Shader.PropertyToID("_CamPositionSnapped");
        public static readonly int FrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
        public static readonly int FrustumMinPoint = Shader.PropertyToID("_FrustumMinPoint");
        public static readonly int FrustumMaxPoint = Shader.PropertyToID("_FrustumMaxPoint");
        
        // Heightmap clipmap
        public static readonly int HeightClipmapStack = Shader.PropertyToID("_HeightClipmap_Stack");
        public static readonly int HeightClipmapPyramid = Shader.PropertyToID("_HeightClipmap_Pyramid");
        public static readonly int HeightClipmapClipSize = Shader.PropertyToID("_HeightClipmap_ClipSize");
        public static readonly int HeightClipmapTexSize = Shader.PropertyToID("_HeightClipmap_TexSize");
        public static readonly int HeightClipmapStackLen = Shader.PropertyToID("_HeightClipmap_StackLen");
        public static readonly int HeightClipmapFinestLevelUptoDate = Shader.PropertyToID("_HeightClipmapFinestLevelUptoDate");
        
        public static readonly int TerrainNormalTex = Shader.PropertyToID("_TerrainNormalTex");
        
        // Shading
        public static readonly int WorldSize = Shader.PropertyToID("_WorldSize");

        public static readonly int TerrainCullResultDataBufferShading = Shader.PropertyToID("terrainCullResultData_buf");
        public static readonly int TerrainNodeDataBuffer = Shader.PropertyToID("terrainNodeData_buf");
        public static readonly int CamPosSnapped = Shader.PropertyToID("_CamPosSnapped");
        public static readonly int AlbedoAtlas = Shader.PropertyToID("_AlbedoAtlas");
        public static readonly int NormalAtlas = Shader.PropertyToID("_NormalAtlas");
        
        public static readonly int SplatIDWClipmapStack = Shader.PropertyToID("_SplatIDWClipmap_Stack");
        public static readonly int SplatIDWClipmapPyramid = Shader.PropertyToID("_SplatIDWClipmap_Pyramid");
        public static readonly int SplatIDWClipmapClipSize = Shader.PropertyToID("_SplatIDWClipmap_ClipSize");
        public static readonly int SplatIDWClipmapTexSize = Shader.PropertyToID("_SplatIDWClipmap_TexSize");
        public static readonly int SplatIDWClipmapStackLen = Shader.PropertyToID("_SplatIDWClipmap_StackLen");
        public static readonly int SplatIDWClipmapFinestLevelUptoDate = Shader.PropertyToID("_SplatIDWClipmapFinestLevelUptoDate");
    }
    
    
    private void Update()
    {
        var worldCoordinate = playerPawn.position - worldCenterPawn.position;
        heightClipmap.UpdateCamera(worldCoordinate);
        splatIDWeightClipmap.UpdateCamera(worldCoordinate);
    }
}
