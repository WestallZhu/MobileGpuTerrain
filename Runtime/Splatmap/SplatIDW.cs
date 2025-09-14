using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Serialization;
using Debug = UnityEngine.Debug;

// This class contains helper functions for compressing splat ID map and weight map into a single texture 
public class SplatIDW : MonoBehaviour
{
    public Texture2D splatID;
    public Texture2D splatWeight;

    public Texture2D CombinedIDW;

    public bool EnableTextureValidation = true;

    [ContextMenu("Generate IDW")]
    void GenerateIDW()
    {
        int texSize = splatID.width;
        /*
        CombinedIDW = new Texture2D(texSize, texSize, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None);
        */
        CombinedIDW = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false, true);
        for (int y = 0; y < texSize; y++)
        {
            for (int x = 0; x < texSize; x++)
            {
                if (y==45 && x==119) {Debugger.Break();}
                var splatControlData = splatID.GetPixel(x, y, 0); // splatControlData = ID / 15f
                var splatWeightData = splatWeight.GetPixel(x, y, 0);
                
                int idr = Mathf.FloorToInt(splatControlData.r * 15f + 0.5f);
                int idg = Mathf.FloorToInt(splatControlData.g * 15f + 0.5f);
                int idb = Mathf.FloorToInt(splatControlData.b * 15f + 0.5f);
                int ida = Mathf.FloorToInt(splatControlData.a * 15f + 0.5f);
                
                Color IDW = new Color();
                
                // IDs are encoded to the 4 higher bits, and weights are encoded to the 4 lower bits,
                // IDs are in range of [0, 15], so we can simply apply a 4-bit shift
                IDW.r = idr * (1 << 4);
                IDW.g = idg * (1 << 4);
                IDW.b = idb * (1 << 4);
                IDW.a = ida * (1 << 4);
                
                // we rescale the weight from [0, 1] to [0, 15]
                IDW.r += splatWeightData.r * 15;
                IDW.g += splatWeightData.g * 15;
                IDW.b += splatWeightData.b * 15;
                IDW.a += splatWeightData.a * 15;
                
                IDW /= 255; 
                CombinedIDW.SetPixel(x, y, IDW);
            }
        }
        
        CombinedIDW.Apply();
        Debug.Log("IDWTex Generated");

        if (!EnableTextureValidation)
        {
            return;
        }
        
        Debug.Log("Validating...");
        List<int2> unmatchedCoords = new List<int2>();
        for (int t = 0; t < texSize; t++)
        {
            for (int s = 0; s < texSize; s++)
            {
                var splatControlData = splatID.GetPixel(s, t, 0); // splatControlData = ID / 15f
                var splatWeightData = splatWeight.GetPixel(s, t, 0);

                int id = Mathf.FloorToInt(splatControlData.r * 15f + 0.5f);
                
                Color c = CombinedIDW.GetPixel(pixelX, pixelY);
                float4 raw = new float4(c.r, c.g, c.b, c.a);
                float4 scaleToInt = math.floor(raw * 255f);
                float4 ids = math.floor(raw * 16f) ;
                float4 weights = (scaleToInt - ids * 16) / 15;
                
                if (Math.Abs(id - ids.x) > 0.01)
                {
                    /*Debug.Log("");
                    Debug.Log($"---- Coordinate: ({s}, {t}) ----------");
                    Debug.Log("Raw: " + raw);
                    Debug.Log("To Int (raw * 255): " + scaleToInt);
                    Debug.Log("IDs: " + ids);
                    Debug.Log("Weights: " + weights);*/
                    Debugger.Break();
                    // Assert.AreEqual(id, ids.x);
                    unmatchedCoords.Add(new int2(s, t));
                }
            }
        }
        Debug.Log(unmatchedCoords.Count);
    }

    [ContextMenu("savePngs")]
    void savePngs()
    {
        System.IO.File.WriteAllBytes(Application.dataPath + @$"/Resources/CombinedIDW.png", CombinedIDW.EncodeToPNG());
        Debug.Log("Exporting CombinedIDW to disk");
    }

    [Range(0, 2047)]
    public int pixelX;
    [Range(0, 2047)]
    public int pixelY;
    [ContextMenu("Validate")]
    void validate()
    {
        Color c = CombinedIDW.GetPixel(pixelX, pixelY);
        float4 raw = new float4(c.r, c.g, c.b, c.a);
        float4 scaleToInt = math.floor(raw * 255f);
        float4 ids = math.floor(raw * 16f) ;
        float4 weights = (scaleToInt - ids * 16) / 15;
        Debug.Log("Raw: " + raw);
        Debug.Log("To Int (raw * 255): " + scaleToInt);
        Debug.Log("IDs: " + ids);
        Debug.Log("Weights: " + weights);
    }
}
