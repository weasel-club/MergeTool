#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

public class MeshGraph
{
    public Dictionary<int, List<int>> vertexToTriangles = new Dictionary<int, List<int>>();
    public Dictionary<int, HashSet<int>> vertexNeighbors = new Dictionary<int, HashSet<int>>();

    public MeshGraph(Mesh mesh)
    {
        var tris = mesh.triangles;
        for (var i = 0; i < tris.Length; i += 3)
        {
            var t0 = tris[i];
            var t1 = tris[i + 1];
            var t2 = tris[i + 2];
            AddTri(t0, i);
            AddTri(t1, i);
            AddTri(t2, i);
            AddNeighbor(t0, t1);
            AddNeighbor(t0, t2);
            AddNeighbor(t1, t0);
            AddNeighbor(t1, t2);
            AddNeighbor(t2, t0);
            AddNeighbor(t2, t1);
        }
    }

    private void AddTri(int v, int triIdx)
    {
        if (!vertexToTriangles.TryGetValue(v, out var list))
        {
            list = new List<int>();
            vertexToTriangles[v] = list;
        }
        list.Add(triIdx);
    }

    private void AddNeighbor(int v, int n)
    {
        if (!vertexNeighbors.TryGetValue(v, out var set))
        {
            set = new HashSet<int>();
            vertexNeighbors[v] = set;
        }
        set.Add(n);
    }
}
#endif
