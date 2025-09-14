using System;
using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

internal class GPUClipmapTerrainRendererFeature : ScriptableRendererFeature
{
    GPUClipmapTerrainPass m_GPUClipmapTerrainPass;
    public override void Create()
    {
        if (m_GPUClipmapTerrainPass != null) return;
        
        m_GPUClipmapTerrainPass = new GPUClipmapTerrainPass();
        m_GPUClipmapTerrainPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // if (renderingData.cameraData.cameraType == CameraType.Game)
        {
            renderer.EnqueuePass(m_GPUClipmapTerrainPass);
        }
    }
}

public class GPUClipmapTerrainPass : ScriptableRenderPass
{
    public static Action<ScriptableRenderContext, CameraData, int> s_ExecuteAction;
    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        s_ExecuteAction?.Invoke(context, renderingData.cameraData, 0);
    }
}

