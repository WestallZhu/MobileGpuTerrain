using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class GPUClipmapTerrainCommon
{
    [Serializable]
    public struct TerrainNodeAABB
    {
        public float3 extent; 
        public float3 position;
    }

    [Serializable]
    public struct TerrainNodeData
    {
        public float positionX;
        public float positionZ;
        public float scale;
    }
    
    public struct TerrainCullResultData
    {
        uint id;
        uint lodTransition;
    };

    public struct ClipmapInfo
    {
        public int BaseTextureSize;
        public int ClipSize;
        public int StackLevelCount;
    }
}
