#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public interface IMeshWorkspace
{
    SkinnedMeshRenderer Renderer { get; }
    Mesh WorkingMesh { get; }
    Mesh PreviewMesh { get; }
    Mesh ResultMesh { get; }
    MeshGraph Graph { get; }
    List<SplitDependency> Dependencies { get; }
    Matrix4x4 LocalToWorld { get; }
    Matrix4x4 WorldToLocal { get; }
    Matrix4x4[] SkinToWorld { get; }
    Dictionary<int, List<int>> CoincidentGroups { get; }
    void PrepareSource();
    void ApplySplits(IEnumerable<EdgeSplitData> splits);
    void ApplyBlendShapes(List<BlendShapeData> shapes);
    void RebuildGraph();
    void BakeSkinnedMesh();
    void ApplyNormals(Vector3[] normals);
    Vector3[] GetNormals();
    Vector3[] GetVertices();
    void SetVertices(Vector3[] vertices);
}

public class MeshWorkspace : IMeshWorkspace
{
    public SkinnedMeshRenderer Renderer { get; }
    public Mesh WorkingMesh { get; private set; }
    public Mesh PreviewMesh { get; private set; }
    public Mesh ResultMesh { get; private set; }
    public MeshGraph Graph { get; private set; }
    public List<SplitDependency> Dependencies { get; } = new List<SplitDependency>();
    public Matrix4x4 LocalToWorld { get; private set; }
    public Matrix4x4 WorldToLocal { get; private set; }
    public Matrix4x4[] SkinToWorld { get; private set; }
    public Dictionary<int, List<int>> CoincidentGroups { get; private set; } = new Dictionary<int, List<int>>();
    public bool EnableBlendShapes { get; set; } = true;

    private List<BlendShapeData> _capturedBlendShapes;

    public MeshWorkspace(SkinnedMeshRenderer renderer)
    {
        Renderer = renderer;
        RefreshTransforms();
    }

    public void PrepareSource()
    {
        if (WorkingMesh != null) UnityEngine.Object.DestroyImmediate(WorkingMesh);
        if (PreviewMesh != null) UnityEngine.Object.DestroyImmediate(PreviewMesh);
        if (ResultMesh != null) UnityEngine.Object.DestroyImmediate(ResultMesh);
        RefreshTransforms();
        WorkingMesh = CloneMesh(Renderer?.sharedMesh, "_Working");
        Dependencies.Clear();
        _capturedBlendShapes = EnableBlendShapes && Renderer != null ? BlendShapeUtility.Capture(Renderer.sharedMesh) : new List<BlendShapeData>();
        SkinToWorld = MeshSpace.BuildSkinToWorld(Renderer, WorkingMesh);
        CoincidentGroups = MeshSpace.BuildCoincidentMap(WorkingMesh?.vertices, LocalToWorld, SkinToWorld);
    }

    public void ApplySplits(IEnumerable<EdgeSplitData> splits)
    {
        if (WorkingMesh == null) return;
        if (splits == null) return;
        var newDeps = MeshSplitter.ApplyEdgeSplits(WorkingMesh, splits);
        if (newDeps.Count > 0) Dependencies.AddRange(newDeps);
        SkinToWorld = MeshSpace.BuildSkinToWorld(Renderer, WorkingMesh);
        CoincidentGroups = MeshSpace.BuildCoincidentMap(WorkingMesh.vertices, LocalToWorld, SkinToWorld);
    }

    public void ApplyBlendShapes(List<BlendShapeData> shapes)
    {
        if (!EnableBlendShapes) return;
        if (WorkingMesh == null) return;
        var data = shapes ?? _capturedBlendShapes ?? new List<BlendShapeData>();
        if (data.Count > 0) BlendShapeUtility.Apply(WorkingMesh, data, Dependencies);
    }

    public void RebuildGraph()
    {
        Graph = WorkingMesh != null ? new MeshGraph(WorkingMesh) : null;
    }

    public void BakeSkinnedMesh()
    {
        if (Renderer == null || WorkingMesh == null) return;
        PreviewMesh = CloneMesh(WorkingMesh, "_Preview");
        MeshSpace.Bake(renderer: Renderer, source: PreviewMesh);
        ResultMesh = CloneMesh(PreviewMesh, "_Result");
    }

    public void ApplyNormals(Vector3[] normals)
    {
        if (ResultMesh == null || normals == null) return;
        ResultMesh.normals = normals;
    }

    public Vector3[] GetNormals()
    {
        return ResultMesh != null ? ResultMesh.normals : Array.Empty<Vector3>();
    }

    public Vector3[] GetVertices()
    {
        return WorkingMesh != null ? WorkingMesh.vertices : Array.Empty<Vector3>();
    }

    public void SetVertices(Vector3[] vertices)
    {
        if (WorkingMesh == null || vertices == null) return;
        WorkingMesh.vertices = vertices;
        WorkingMesh.RecalculateBounds();
        MeshSplitter.UpdateSplitVertices(WorkingMesh, Dependencies);
    }

    private Mesh CloneMesh(Mesh mesh, string suffix)
    {
        if (mesh == null) return null;
        var clone = UnityEngine.Object.Instantiate(mesh);
        clone.name = mesh.name + suffix;
        if (clone.bindposes == null || clone.bindposes.Length == 0) clone.bindposes = mesh.bindposes;
        return clone;
    }

    private void RefreshTransforms()
    {
        LocalToWorld = MeshSpace.GetRendererToWorld(Renderer);
        WorldToLocal = MeshSpace.GetWorldToRenderer(Renderer);
    }
}

public static class MeshSpace
{
    public static Matrix4x4 GetRendererToWorld(SkinnedMeshRenderer renderer)
    {
        return renderer ? renderer.transform.localToWorldMatrix : Matrix4x4.identity;
    }

    public static Matrix4x4 GetWorldToRenderer(SkinnedMeshRenderer renderer)
    {
        return renderer ? renderer.transform.worldToLocalMatrix : Matrix4x4.identity;
    }

    public static Matrix4x4 GetRendererToWorldNoScale(SkinnedMeshRenderer renderer)
    {
        if (renderer == null) return Matrix4x4.identity;
        return Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
    }

    public static Matrix4x4[] BuildSkinToWorld(SkinnedMeshRenderer renderer, Mesh mesh)
    {
        if (renderer == null || mesh == null) return null;
        var bones = renderer.bones;
        var bindposes = mesh.bindposes;
        if (bones == null || bindposes == null || bindposes.Length == 0) return null;
        var weights = mesh.boneWeights;
        if (weights == null || weights.Length == 0 || weights.Length != mesh.vertexCount) return null;

        var matrices = new Matrix4x4[mesh.vertexCount];
        for (var i = 0; i < weights.Length; i++)
        {
            var bw = weights[i];
            var skin = Matrix4x4.zero;
            Accumulate(ref skin, bones, bindposes, bw.boneIndex0, bw.weight0);
            Accumulate(ref skin, bones, bindposes, bw.boneIndex1, bw.weight1);
            Accumulate(ref skin, bones, bindposes, bw.boneIndex2, bw.weight2);
            Accumulate(ref skin, bones, bindposes, bw.boneIndex3, bw.weight3);
            matrices[i] = skin;
        }
        return matrices;
    }

    public static Dictionary<int, List<int>> BuildCoincidentMap(Vector3[] vertices, Matrix4x4 localToWorld, Matrix4x4[] skinToWorld)
    {
        var map = new Dictionary<int, List<int>>();
        if (vertices == null) return map;
        const float scale = 10000f;
        var groups = new Dictionary<Vector3Int, List<int>>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var world = ConvertLocalToWorld(vertices[i], localToWorld, skinToWorld, i);
            var key = new Vector3Int(Mathf.RoundToInt(world.x * scale), Mathf.RoundToInt(world.y * scale), Mathf.RoundToInt(world.z * scale));
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<int>();
                groups[key] = list;
            }
            list.Add(i);
        }
        foreach (var group in groups.Values)
        {
            foreach (var idx in group) map[idx] = group;
        }
        return map;
    }

    public static Vector3 ConvertLocalToWorld(Vector3 local, Matrix4x4 localToWorld, Matrix4x4[] skinToWorld, int index)
    {
        if (skinToWorld != null && index >= 0 && index < skinToWorld.Length) return skinToWorld[index].MultiplyPoint3x4(local);
        return localToWorld.MultiplyPoint3x4(local);
    }

    public static Vector3 ConvertWorldToLocal(Vector3 world, Matrix4x4 fallbackWorldToLocal, Matrix4x4[] skinToWorld, int index)
    {
        if (skinToWorld != null && index >= 0 && index < skinToWorld.Length)
        {
            var skin = skinToWorld[index];
            if (Mathf.Abs(skin.determinant) > 1e-8f) return skin.inverse.MultiplyPoint3x4(world);
        }
        return fallbackWorldToLocal.MultiplyPoint3x4(world);
    }

    public static void Bake(SkinnedMeshRenderer renderer, Mesh source)
    {
        if (renderer == null || source == null) return;
        var baked = new Mesh();
        var original = renderer.sharedMesh;
        renderer.sharedMesh = source;
        renderer.BakeMesh(baked);
        renderer.sharedMesh = original;
        source.vertices = baked.vertices;
        if (baked.normals.Length == baked.vertexCount) source.normals = baked.normals;
        if (baked.tangents.Length == baked.vertexCount) source.tangents = baked.tangents;
        source.RecalculateBounds();
        UnityEngine.Object.DestroyImmediate(baked);
    }

    private static void Accumulate(ref Matrix4x4 target, Transform[] bones, Matrix4x4[] bindposes, int boneIndex, float weight)
    {
        if (weight <= 0f) return;
        if (boneIndex < 0 || boneIndex >= bones.Length) return;
        if (bones[boneIndex] == null) return;
        var m = bones[boneIndex].localToWorldMatrix * bindposes[boneIndex];
        for (var i = 0; i < 16; i++)
        {
            target[i] += m[i] * weight;
        }
    }
}

public static class MeshSplitter
{
    public static List<SplitDependency> ApplyEdgeSplits(Mesh mesh, IEnumerable<EdgeSplitData> splits)
    {
        var dependencies = new List<SplitDependency>();
        if (mesh == null || splits == null) return dependencies;

        var vertices = new List<Vector3>(mesh.vertices);
        var normals = new List<Vector3>(mesh.normals);
        var tangents = new List<Vector4>(mesh.tangents);
        var uvs = new List<Vector2>(mesh.uv);
        var boneWeights = new List<BoneWeight>(mesh.boneWeights);
        var edgeMidpointCache = new Dictionary<ulong, int>();
        var submeshTriangles = CollectSubMeshes(mesh);

        foreach (var split in splits)
        {
            if (split.v1 < 0 || split.v2 < 0) continue;
            if (split.v1 >= vertices.Count || split.v2 >= vertices.Count) continue;
            for (var sm = 0; sm < submeshTriangles.Count; sm++)
            {
                var triangles = submeshTriangles[sm];
                if (!EdgeExists(triangles, split.v1, split.v2)) continue;
                var edgeKey = BuildEdgeKey(split.v1, split.v2);
                var mid = GetOrAddMidpoint(edgeKey, split.v1, split.v2, vertices, normals, tangents, uvs, boneWeights, edgeMidpointCache, dependencies);
                submeshTriangles[sm] = SplitTrianglesWithEdge(triangles, split.v1, split.v2, mid);
            }
        }

        WriteMesh(mesh, vertices, normals, tangents, uvs, boneWeights, submeshTriangles);
        return dependencies;
    }

    public static void UpdateSplitVertices(Mesh mesh, List<SplitDependency> deps)
    {
        if (mesh == null || deps == null) return;
        var verts = mesh.vertices;
        var norms = mesh.normals;
        var tans = mesh.tangents;
        var uvs = mesh.uv;
        var hasNorms = norms != null && norms.Length == verts.Length;
        var hasTans = tans != null && tans.Length == verts.Length;
        var hasUvs = uvs != null && uvs.Length == verts.Length;

        foreach (var d in deps)
        {
            if (d.midIndex >= verts.Length || d.parent1Index >= verts.Length || d.parent2Index >= verts.Length) continue;
            verts[d.midIndex] = (verts[d.parent1Index] + verts[d.parent2Index]) * 0.5f;
            if (hasNorms)
            {
                var n = norms[d.parent1Index] + norms[d.parent2Index];
                norms[d.midIndex] = n.sqrMagnitude > 1e-12f ? n.normalized : norms[d.parent1Index];
            }
            if (hasTans) tans[d.midIndex] = AverageTangent(tans[d.parent1Index], tans[d.parent2Index]);
            if (hasUvs) uvs[d.midIndex] = AverageWrappedUv(uvs[d.parent1Index], uvs[d.parent2Index]);
        }

        mesh.vertices = verts;
        if (hasNorms) mesh.normals = norms;
        if (hasTans) mesh.tangents = tans;
        if (hasUvs) mesh.uv = uvs;
    }

    private static List<List<int>> CollectSubMeshes(Mesh mesh)
    {
        var result = new List<List<int>>();
        if (mesh.subMeshCount > 0)
        {
            for (var i = 0; i < mesh.subMeshCount; i++) result.Add(new List<int>(mesh.GetTriangles(i)));
        }
        else
        {
            result.Add(new List<int>(mesh.triangles));
        }
        return result;
    }

    private static void WriteMesh(Mesh mesh, List<Vector3> verts, List<Vector3> norms, List<Vector4> tans, List<Vector2> uvs, List<BoneWeight> bws, List<List<int>> triangles)
    {
        mesh.SetVertices(verts);
        if (norms.Count == verts.Count) mesh.SetNormals(norms);
        if (tans.Count == verts.Count) mesh.SetTangents(tans);
        if (uvs.Count == verts.Count) mesh.SetUVs(0, uvs);
        if (bws.Count == verts.Count) mesh.boneWeights = bws.ToArray();
        mesh.subMeshCount = triangles.Count;
        for (var i = 0; i < triangles.Count; i++) mesh.SetTriangles(triangles[i], i);
        mesh.RecalculateBounds();
    }

    private static ulong BuildEdgeKey(int a, int b)
    {
        var i1 = (uint)Mathf.Min(a, b);
        var i2 = (uint)Mathf.Max(a, b);
        return ((ulong)i1 << 32) | i2;
    }

    private static bool EdgeExists(List<int> triangles, int v1, int v2)
    {
        for (var i = 0; i < triangles.Count; i += 3)
        {
            var i0 = triangles[i];
            var i1 = triangles[i + 1];
            var i2 = triangles[i + 2];
            if (EdgeMatches(i0, i1, v1, v2) || EdgeMatches(i1, i2, v1, v2) || EdgeMatches(i2, i0, v1, v2)) return true;
        }
        return false;
    }

    private static bool EdgeMatches(int a, int b, int v1, int v2)
    {
        return (a == v1 && b == v2) || (a == v2 && b == v1);
    }

    private static List<int> SplitTrianglesWithEdge(List<int> triangles, int v1, int v2, int mid)
    {
        var result = new List<int>(triangles.Count + 3);
        for (var i = 0; i < triangles.Count; i += 3)
        {
            var i0 = triangles[i];
            var i1 = triangles[i + 1];
            var i2 = triangles[i + 2];
            var handled = false;
            if (EdgeMatches(i0, i1, v1, v2))
            {
                result.Add(i0); result.Add(mid); result.Add(i2);
                result.Add(mid); result.Add(i1); result.Add(i2);
                handled = true;
            }
            else if (EdgeMatches(i1, i2, v1, v2))
            {
                result.Add(i0); result.Add(i1); result.Add(mid);
                result.Add(i0); result.Add(mid); result.Add(i2);
                handled = true;
            }
            else if (EdgeMatches(i2, i0, v1, v2))
            {
                result.Add(i0); result.Add(i1); result.Add(mid);
                result.Add(mid); result.Add(i1); result.Add(i2);
                handled = true;
            }
            if (!handled)
            {
                result.Add(i0); result.Add(i1); result.Add(i2);
            }
        }
        return result;
    }

    private static int GetOrAddMidpoint(ulong edgeKey, int iA, int iB,
        List<Vector3> verts, List<Vector3> norms, List<Vector4> tans, List<Vector2> uvs, List<BoneWeight> bws,
        Dictionary<ulong, int> cache, List<SplitDependency> deps)
    {
        if (cache.TryGetValue(edgeKey, out var idx)) return idx;

        var newPos = (verts[iA] + verts[iB]) * 0.5f;
        verts.Add(newPos);

        if (norms.Count > 0)
        {
            var n = norms[iA] + norms[iB];
            var normalized = n.sqrMagnitude > 1e-12f ? n.normalized : norms[iA];
            norms.Add(normalized);
        }

        if (tans.Count > 0) tans.Add(AverageTangent(tans[iA], tans[iB]));

        if (uvs.Count > 0) uvs.Add(AverageWrappedUv(uvs[iA], uvs[iB]));

        if (bws.Count > 0) bws.Add(BlendBoneWeights(bws[iA], bws[iB]));

        idx = verts.Count - 1;
        cache[edgeKey] = idx;

        deps.Add(new SplitDependency { midIndex = idx, parent1Index = iA, parent2Index = iB });
        return idx;
    }

    private static Vector2 AverageWrappedUv(Vector2 a, Vector2 b)
    {
        var ax = a.x;
        var bx = b.x;
        if (Mathf.Abs(ax - bx) > 0.5f)
        {
            if (ax > bx) bx += 1f;
            else ax += 1f;
        }
        var ay = a.y;
        var by = b.y;
        if (Mathf.Abs(ay - by) > 0.5f)
        {
            if (ay > by) by += 1f;
            else ay += 1f;
        }
        var mid = new Vector2((ax + bx) * 0.5f, (ay + by) * 0.5f);
        mid.x = Mathf.Repeat(mid.x, 1f);
        mid.y = Mathf.Repeat(mid.y, 1f);
        return mid;
    }

    private static Vector4 AverageTangent(Vector4 t1, Vector4 t2)
    {
        var d1 = new Vector3(t1.x, t1.y, t1.z);
        var d2 = new Vector3(t2.x, t2.y, t2.z);
        var dir = d1 + d2;
        if (dir.sqrMagnitude < 1e-12f) dir = d1.sqrMagnitude >= d2.sqrMagnitude ? d1 : d2;
        dir = dir.sqrMagnitude > 1e-12f ? dir.normalized : Vector3.right;
        var w = Mathf.Abs(t1.w) >= Mathf.Abs(t2.w) ? Mathf.Sign(t1.w) : Mathf.Sign(t2.w);
        if (Mathf.Abs(w) < 1e-6f) w = t1.w >= 0f ? 1f : -1f;
        return new Vector4(dir.x, dir.y, dir.z, w);
    }

    private static BoneWeight BlendBoneWeights(BoneWeight a, BoneWeight b)
    {
        var map = new Dictionary<int, float>();
        Accumulate(map, a.boneIndex0, a.weight0);
        Accumulate(map, a.boneIndex1, a.weight1);
        Accumulate(map, a.boneIndex2, a.weight2);
        Accumulate(map, a.boneIndex3, a.weight3);
        Accumulate(map, b.boneIndex0, b.weight0);
        Accumulate(map, b.boneIndex1, b.weight1);
        Accumulate(map, b.boneIndex2, b.weight2);
        Accumulate(map, b.boneIndex3, b.weight3);
        var ordered = map.OrderByDescending(x => x.Value).Take(4).ToList();
        var total = ordered.Sum(x => x.Value);
        var blended = new BoneWeight();
        if (total <= 0f) return blended;
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var normalized = entry.Value / total;
            if (i == 0) { blended.boneIndex0 = entry.Key; blended.weight0 = normalized; }
            else if (i == 1) { blended.boneIndex1 = entry.Key; blended.weight1 = normalized; }
            else if (i == 2) { blended.boneIndex2 = entry.Key; blended.weight2 = normalized; }
            else if (i == 3) { blended.boneIndex3 = entry.Key; blended.weight3 = normalized; }
        }
        return blended;
    }

    private static void Accumulate(Dictionary<int, float> map, int boneIndex, float weight)
    {
        if (boneIndex < 0 || weight <= 0f) return;
        if (map.TryGetValue(boneIndex, out var existing)) map[boneIndex] = existing + weight;
        else map[boneIndex] = weight;
    }
}
#endif
