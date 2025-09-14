using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public class TerrainSplatUtil : MonoBehaviour
{
    public Texture2D[] RawTerrainControl;   // Unity Terrain's control alpha map
    
    public Texture2D SplatID;   
    public Texture2D SplatWeights;  

    [Range(0, 12)] 
    public int weightMipmap;

     struct SplatData
    {
        public int id;
        public float weight;
    }


    [ContextMenu("MakeSplat")]
    void MakeSplat()
    {
        int scale = 1 << weightMipmap;

        int wid = RawTerrainControl[0].width / scale;
        int hei = RawTerrainControl[0].height / scale;
    
        List<Texture2D> originSplatTexs = new List<Texture2D>();

        for (int i = 0; i < RawTerrainControl.Length; i++)
        {
            var tex= new Texture2D(wid, hei, TextureFormat.ARGB32, false, true);
            var controlTexPixels = RawTerrainControl[i].GetPixels(weightMipmap);
            tex.SetPixels(controlTexPixels);
            originSplatTexs.Add(tex);
        }

        SplatID = new Texture2D(wid, hei, TextureFormat.RGBA32, false, true);

        SplatID.filterMode = FilterMode.Point;

        var splatIDColors = SplatID.GetPixels();
        SplatWeights = new Texture2D(wid, hei, TextureFormat.RGBA32, false, true);
        SplatWeights.filterMode = FilterMode.Point;
        var splatWeightsColors = new Color[4][];
        
        // recalculate splatWeights
        for (int i = 0; i < 4; i++) splatWeightsColors[i] = new Color[wid*hei];
        for (int i = 0; i < hei; i++)
        {
            for (int j = 0; j < wid; j++)
            {
                List<SplatData> splatDatas = new List<SplatData>();
                int index = i * wid + j;

                // collect splatData: the summed weight of the nearby 4 texels
                for (int unityWeightTexID = 0; unityWeightTexID < originSplatTexs.Count; unityWeightTexID++)
                {
                    Color corner00 = originSplatTexs[unityWeightTexID].GetPixel(j, i);
                    Color corner10 = originSplatTexs[unityWeightTexID].GetPixel(j + 1, i);
                    Color corner01 = originSplatTexs[unityWeightTexID].GetPixel(j, i + 1);
                    Color corner11 = originSplatTexs[unityWeightTexID].GetPixel(j + 1, i + 1);
                    
                    SplatData sd;   // id: splatWeightID
                    
                    sd.id = unityWeightTexID * 4;
                    sd.weight = corner00.r + corner10.r + corner01.r + corner11.r;
                    splatDatas.Add(sd);
                    
                    sd.id++;
                    sd.weight = corner00.g + corner10.g + corner01.g + corner11.g;
                    splatDatas.Add(sd);
                    
                    sd.id++;
                    sd.weight = corner00.b + corner10.b + corner01.b + corner11.b;
                    splatDatas.Add(sd);
                    
                    sd.id++;
                    sd.weight = corner00.a + corner10.a + corner01.a + corner11.a;
                    splatDatas.Add(sd);
                }

                // sort to get top 4 weights
                splatDatas.Sort((x, y) => -(x.weight).CompareTo(y.weight)); 
                
                //权重只记录前4张 所以需要统计丢弃部分的权重 并平均加到前4张上
                // also remove trivial (not the top 4 most significant) weights from weight tex?
                Vector4 lostWeightSum = Vector4.zero;
                for (int k = 4; k < originSplatTexs.Count * 4; k++)
                {
                    int layer;  // zb: layer is unity weightmap layer (4 id weightmap)
                    int channel;    // zb: channel is unity weightmap channel (splat texture id [0,3])
                    getWeightLayerAndChannel(splatDatas[k].id, out layer, out channel);

                    Color corner00 = originSplatTexs[layer].GetPixel(j, i);
                    Color corner10 = originSplatTexs[layer].GetPixel(j + 1, i);
                    Color corner01 = originSplatTexs[layer].GetPixel(j, i + 1);
                    Color corner11 = originSplatTexs[layer].GetPixel(j + 1, i + 1);
                    lostWeightSum += new Vector4(corner00[channel], corner10[channel], corner01[channel], corner11[channel]);
                    corner00[channel] = 0;
                    originSplatTexs[layer].SetPixel(j, i, corner00);

                    corner10[channel] = 0;
                    originSplatTexs[layer].SetPixel(j + 1, i, corner10);

                    corner01[channel] = 0;
                    originSplatTexs[layer].SetPixel(j, i + 1, corner01);

                    corner11[channel] = 0;
                    originSplatTexs[layer].SetPixel(j + 1, i + 1, corner11);
                }
                
                // calculate the top 4 most significant weights
                Vector4 top4WeightSum = Vector4.zero;  // sum of the top weights for the 4 nearby texels
                for (int k = 0; k < 4; k++)
                {
                    int layer;
                    int channel;
                    getWeightLayerAndChannel(splatDatas[k].id, out layer, out channel);
                    
                    Color corner00 = originSplatTexs[layer].GetPixel(j, i);
                    Color corner10 = originSplatTexs[layer].GetPixel(j + 1, i);
                    Color corner01 = originSplatTexs[layer].GetPixel(j, i + 1);
                    Color corner11 = originSplatTexs[layer].GetPixel(j + 1, i + 1);
                    top4WeightSum += new Vector4(corner00[channel], corner10[channel], corner01[channel], corner11[channel]);
                }
                
                // recalculate weights in the splatTexture 
                for (int k = 0; k < 4; k++)
                {
                    int layer;
                    int channel;

                    getWeightLayerAndChannel(splatDatas[k].id, out layer, out channel);

                    Color corner00 = originSplatTexs[layer].GetPixel(j, i);
                    Color corner10 = originSplatTexs[layer].GetPixel(j + 1, i);
                    Color corner01 = originSplatTexs[layer].GetPixel(j, i + 1);
                    Color corner11 = originSplatTexs[layer].GetPixel(j + 1, i + 1);
                    corner00[channel] += lostWeightSum.x * corner00[channel] / top4WeightSum.x;
                    originSplatTexs[layer].SetPixel(j, i, corner00);

                    corner10[channel] += lostWeightSum.y * corner10[channel] / top4WeightSum.y;
                    originSplatTexs[layer].SetPixel(j + 1, i, corner10);

                    corner01[channel] += lostWeightSum.z * corner01[channel] / top4WeightSum.z;
                    originSplatTexs[layer].SetPixel(j, i + 1, corner01);
                    corner11[channel] += lostWeightSum.w * corner11[channel] / top4WeightSum.w;
                    originSplatTexs[layer].SetPixel(j + 1, i + 1, corner11);
                }
            }
        }
        
        
        for (int i = 0; i < hei; i++)
        {
            for (int j = 0; j < wid; j++)
            {
                List<SplatData> splatDatas = new List<SplatData>();
                int index = i * wid + j;
                
                for (int k = 0; k < originSplatTexs.Count; k++)
                {
                    SplatData sd;
                    sd.id = k * 4;
                    Color corner00 = originSplatTexs[k].GetPixel(j, i);
                    Color corner10 = originSplatTexs[k].GetPixel(j + 1, i);
                    Color corner01 = originSplatTexs[k].GetPixel(j, i + 1);
                    Color corner11 = originSplatTexs[k].GetPixel(j + 1, i + 1);
                    sd.weight = corner00.r + corner10.r + corner01.r + corner11.r;


                    splatDatas.Add(sd);
                    sd.id++;
                    
                    sd.weight = corner00.g + corner10.g + corner01.g + corner11.g;
                    splatDatas.Add(sd);
                    sd.id++;
                  
                    sd.weight = corner00.b + corner10.b + corner01.b + corner11.b;
                    splatDatas.Add(sd);
                    sd.id++;
                   
                    sd.weight = corner00.a + corner10.a + corner01.a + corner11.a;
                    splatDatas.Add(sd);
                }
                
                //按权排序选出相邻4个点最权重最大的ID 作为4个点都采样的公用id
                splatDatas.Sort((x, y) => -(x.weight).CompareTo(y.weight));
                splatIDColors[index].r = splatDatas[0].id / 15f; 
                splatIDColors[index].g = splatDatas[1].id / 15f; 
                splatIDColors[index].b = splatDatas[2].id / 15f; 
                splatIDColors[index].a = splatDatas[3].id / 15f; 
                
                for (int k = 0; k < 4; k++)
                {
                    int layer;
                    int channel;

                    getWeightLayerAndChannel(splatDatas[k].id, out layer, out channel);

                    Color corner00 = originSplatTexs[layer].GetPixel(j, i);
                    // zb: redundant
                    Color corner10 = originSplatTexs[layer].GetPixel(j + 1, i);
                    Color corner01 = originSplatTexs[layer].GetPixel(j, i + 1);
                    Color corner11 = originSplatTexs[layer].GetPixel(j + 1, i + 1);

                    splatWeightsColors[0][index][k] = corner00[channel] ;

                }
            }
        }
        
        SplatID.SetPixels(splatIDColors);
        SplatID.Apply();
        SplatWeights.SetPixels(splatWeightsColors[0]);
        SplatWeights.Apply();
    }
     
    private void getWeightLayerAndChannel(int id, out int layer, out int channel)
    {
        layer = id / 4;
        channel = id % 4;
    }
    
    [ContextMenu("savePngs")]
    void savePngs()
    {
        System.IO.File.WriteAllBytes(Application.dataPath + @$"/Resources/splatID_{weightMipmap}.png", SplatID.EncodeToPNG());
      
        System.IO.File.WriteAllBytes(Application.dataPath + @$"/Resources/splatWeights_{weightMipmap}.png", SplatWeights.EncodeToPNG());
        Debug.Log("Pngs saved");
    }
    
}
