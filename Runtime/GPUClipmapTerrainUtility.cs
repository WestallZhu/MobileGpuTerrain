using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

internal static unsafe class GPUClipmapTerrainUtility
{
    public static Mesh CreatePatchMesh(int size, float unitMeter)
    {
        var mesh = new Mesh();
        int quadCount = size * size;
        int triCount = quadCount * 2;
        float centerOffset = -size * unitMeter * 0.5f;
        int vertCount = (size + 1) * (size + 1);
        NativeArray<Vector3> vertices = new NativeArray<Vector3>(vertCount, Allocator.Temp);
        NativeArray<Vector4> uvs = new NativeArray<Vector4>(vertCount, Allocator.Temp);
        for (int i = 0; i < vertCount; i++)
        {
            int x = i / (size + 1);
            int z = i % (size + 1);
            vertices[i] = new Vector3(centerOffset + x * unitMeter, 0, centerOffset + z * unitMeter);
            Vector4 edge = new Vector4(0, 0, 0, 0);

            if (z % 2 != 0)
            {
                if(x == 0 && z != 0 && z != size )
                {
                    edge.x = 1; // left
                }
                if(x == size && z != 0 && z != size)
                {
                    edge.y = 1; // right
                }
            }
            if (x % 2 != 0)
            {
                if(z == size && x !=0 && x != size)
                {
                    edge.w = 1; // up
                }
                if(z == 0 && x != 0 && x != size)
                {
                    edge.z = 1; // bottom
                }
            }
            
            uvs[i] = edge;
        }
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);

        int[] indices = new int[triCount * 3];  // 6*quad


        for (int i = 0; i < quadCount; i++)
        {
            int offset = i * 6;
            int index = (i / size) * (size + 1) + (i % size);

            indices[offset] = index;
            indices[offset + 1] = index + size + 2;
            indices[offset + 2] = index + size + 1;
            indices[offset + 3] = index + size + 2;
            indices[offset + 4] = index;
            indices[offset + 5] = index + 1;
        }

        mesh.SetIndices(indices, MeshTopology.Triangles, 0);
        mesh.UploadMeshData(false);

        return mesh;
    }

}
