Shader "Terrain/ClipmapTerrainLit"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
    }
        SubShader
    {
       Tags { "Queue" = "Geometry" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "UniversalMaterialType" = "Lit" "IgnoreProjector" = "False" "TerrainCompatible" = "True"}
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

        #pragma target 3.5

        #include "ClipmapTerrainShadingPass.hlsl"

        #pragma enable_d3d11_debug_symbols
        #pragma vertex ShadingVert
        #pragma fragment ShadingPixel

        ENDHLSL
    }

    }
}
