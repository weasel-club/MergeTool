#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class SymmetryUtility
{
    public static Dictionary<int, int> BuildSymmetryLookup(SkinnedMeshRenderer renderer, float tolerance)
    {
        var map = new Dictionary<int, int>();
        if (renderer == null || renderer.sharedMesh == null) return map;
        var vertices = renderer.sharedMesh.vertices;
        var scale = 1f / Mathf.Max(0.000001f, tolerance);
        var keyToIndex = new Dictionary<Vector3Int, int>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            var key = new Vector3Int(Mathf.RoundToInt(v.x * scale), Mathf.RoundToInt(v.y * scale), Mathf.RoundToInt(v.z * scale));
            var mirrorKey = new Vector3Int(Mathf.RoundToInt(-v.x * scale), Mathf.RoundToInt(v.y * scale), Mathf.RoundToInt(v.z * scale));
            if (keyToIndex.TryGetValue(mirrorKey, out var other))
            {
                map[i] = other;
                map[other] = i;
            }
            if (!keyToIndex.ContainsKey(key)) keyToIndex[key] = i;
        }
        return map;
    }

    public static Vector3 MirrorOffset(Vector3 worldOffset, Transform reference)
    {
        if (reference == null) return worldOffset;
        var local = reference.worldToLocalMatrix.MultiplyVector(worldOffset);
        local.x = -local.x;
        return reference.localToWorldMatrix.MultiplyVector(local);
    }
}

public static class PairAligner
{
    public static void ApplyPairs(PairContext context, IReadOnlyList<VertexPair> pairs)
    {
        if (!context.IsValid() || pairs == null) return;
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (!context.IsValidPair(pair)) continue;
            var faceWorld = MeshSpace.ConvertLocalToWorld(context.FaceVertices[pair.faceIndex], context.FaceLocalToWorld, context.FaceSkinToWorld, pair.faceIndex);
            var bodyWorld = MeshSpace.ConvertLocalToWorld(context.BodyVertices[pair.bodyIndex], context.BodyLocalToWorld, context.BodySkinToWorld, pair.bodyIndex);
            var midpoint = (faceWorld + bodyWorld) * 0.5f + pair.worldOffset;
            ApplyMidpoint(context.FaceVertices, pair.faceIndex, context.FaceGroups, context.WorldToFace, context.FaceSkinToWorld, midpoint);
            ApplyMidpoint(context.BodyVertices, pair.bodyIndex, context.BodyGroups, context.WorldToBody, context.BodySkinToWorld, midpoint);
        }

        context.FaceWorkspace.SetVertices(context.FaceVertices);
        context.BodyWorkspace.SetVertices(context.BodyVertices);
    }

    private static void ApplyMidpoint(Vector3[] vertices, int index, Dictionary<int, List<int>> groups, Matrix4x4 worldToLocal, Matrix4x4[] skinToWorld, Vector3 midpoint)
    {
        if (groups.TryGetValue(index, out var linked))
        {
            for (var i = 0; i < linked.Count; i++)
            {
                var tIdx = linked[i];
                if (tIdx >= 0 && tIdx < vertices.Length) vertices[tIdx] = MeshSpace.ConvertWorldToLocal(midpoint, worldToLocal, skinToWorld, tIdx);
            }
        }
        else if (index >= 0 && index < vertices.Length)
        {
            vertices[index] = MeshSpace.ConvertWorldToLocal(midpoint, worldToLocal, skinToWorld, index);
        }
    }
}

public struct PairContext
{
    public MeshWorkspace FaceWorkspace;
    public MeshWorkspace BodyWorkspace;
    public Vector3[] FaceVertices;
    public Vector3[] BodyVertices;
    public Matrix4x4 FaceLocalToWorld;
    public Matrix4x4 BodyLocalToWorld;
    public Matrix4x4 WorldToFace;
    public Matrix4x4 WorldToBody;
    public Matrix4x4[] FaceSkinToWorld;
    public Matrix4x4[] BodySkinToWorld;
    public Dictionary<int, List<int>> FaceGroups;
    public Dictionary<int, List<int>> BodyGroups;

    public bool IsValid()
    {
        return FaceWorkspace?.WorkingMesh != null && BodyWorkspace?.WorkingMesh != null && FaceVertices != null && BodyVertices != null;
    }

    public bool IsValidPair(VertexPair pair)
    {
        if (!IsValid()) return false;
        return pair.faceIndex >= 0 && pair.bodyIndex >= 0 && pair.faceIndex < FaceVertices.Length && pair.bodyIndex < BodyVertices.Length;
    }
}

public static class NormalSmoother
{
    public static void Smooth(IMeshWorkspace face, IMeshWorkspace body, IReadOnlyList<VertexPair> pairs, int depth, float strength)
    {
        if (face?.Graph == null || body?.Graph == null) return;
        if (face.PreviewMesh == null || body.PreviewMesh == null) return;
        if (pairs == null || pairs.Count == 0) return;

        var faceNormals = face.PreviewMesh.normals;
        var bodyNormals = body.PreviewMesh.normals;
        var faceVerts = face.PreviewMesh.vertices;
        var bodyVerts = body.PreviewMesh.vertices;

        var faceCoincident = MergeNeighbors.BuildCoincident(faceVerts);
        var bodyCoincident = MergeNeighbors.BuildCoincident(bodyVerts);

        var faceNeighbors = MergeNeighbors.Merge(face.Graph.vertexNeighbors, faceCoincident);
        var bodyNeighbors = MergeNeighbors.Merge(body.Graph.vertexNeighbors, bodyCoincident);

        var faceSeam = new Dictionary<int, Vector3>();
        var bodySeam = new Dictionary<int, Vector3>();
        var faceSeeds = new HashSet<int>();
        var bodySeeds = new HashSet<int>();

        var faceToWorld = face.LocalToWorld;
        var bodyToWorld = body.LocalToWorld;
        var worldToFace = face.WorldToLocal;
        var worldToBody = body.WorldToLocal;

        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (pair.faceIndex >= faceNormals.Length || pair.bodyIndex >= bodyNormals.Length) continue;
            var nFaceWorld = faceToWorld.MultiplyVector(faceNormals[pair.faceIndex]).normalized;
            var nBodyWorld = bodyToWorld.MultiplyVector(bodyNormals[pair.bodyIndex]).normalized;
            var merged = (nFaceWorld + nBodyWorld).sqrMagnitude > 0f ? (nFaceWorld + nBodyWorld).normalized : nFaceWorld;
            var faceLocal = worldToFace.MultiplyVector(merged).normalized;
            var bodyLocal = worldToBody.MultiplyVector(merged).normalized;
            MergeNeighbors.AddSeed(faceSeeds, faceSeam, faceCoincident, pair.faceIndex, faceLocal);
            MergeNeighbors.AddSeed(bodySeeds, bodySeam, bodyCoincident, pair.bodyIndex, bodyLocal);
        }

        var faceWeights = MergeNeighbors.BuildWeights(faceSeeds, faceNeighbors, faceCoincident, depth);
        var bodyWeights = MergeNeighbors.BuildWeights(bodySeeds, bodyNeighbors, bodyCoincident, depth);

        MergeNeighbors.BlendNormals(faceNormals, faceSeam, faceNeighbors, faceWeights, strength);
        MergeNeighbors.BlendNormals(bodyNormals, bodySeam, bodyNeighbors, bodyWeights, strength);

        face.PreviewMesh.normals = faceNormals;
        body.PreviewMesh.normals = bodyNormals;
    }
}

public static class MergeNeighbors
{
    public static Dictionary<int, List<int>> BuildCoincident(Vector3[] vertices)
    {
        const float scale = 10000f;
        var groups = new Dictionary<Vector3Int, List<int>>();
        var map = new Dictionary<int, List<int>>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            var key = new Vector3Int(Mathf.RoundToInt(v.x * scale), Mathf.RoundToInt(v.y * scale), Mathf.RoundToInt(v.z * scale));
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

    public static Dictionary<int, HashSet<int>> Merge(Dictionary<int, HashSet<int>> source, Dictionary<int, List<int>> coincident)
    {
        var merged = new Dictionary<int, HashSet<int>>();
        var keys = new HashSet<int>(source.Keys);
        if (coincident != null) foreach (var k in coincident.Keys) keys.Add(k);
        foreach (var k in keys)
        {
            var set = new HashSet<int>();
            if (source.TryGetValue(k, out var src)) foreach (var n in src) set.Add(n);
            if (coincident != null && coincident.TryGetValue(k, out var group))
            {
                foreach (var g in group)
                {
                    if (g != k) set.Add(g);
                    if (source.TryGetValue(g, out var gNeighbors)) foreach (var n in gNeighbors) set.Add(n);
                }
            }
            merged[k] = set;
        }
        return merged;
    }

    public static void AddSeed(HashSet<int> seeds, Dictionary<int, Vector3> targets, Dictionary<int, List<int>> coincident, int idx, Vector3 normal)
    {
        seeds.Add(idx);
        targets[idx] = normal;
        if (coincident != null && coincident.TryGetValue(idx, out var group))
        {
            foreach (var g in group)
            {
                seeds.Add(g);
                targets[g] = normal;
            }
        }
    }

    public static Dictionary<int, float> BuildWeights(HashSet<int> seeds, Dictionary<int, HashSet<int>> neighbors, Dictionary<int, List<int>> coincident, int maxDepth)
    {
        var weights = new Dictionary<int, float>();
        var queue = new Queue<(int idx, int depth)>();
        var visited = new HashSet<int>();
        foreach (var seed in seeds)
        {
            if (visited.Add(seed)) { queue.Enqueue((seed, 0)); weights[seed] = 1.0f; }
            if (coincident != null && coincident.TryGetValue(seed, out var group))
            {
                foreach (var g in group) if (visited.Add(g)) { queue.Enqueue((g, 0)); weights[g] = 1.0f; }
            }
        }
        while (queue.Count > 0)
        {
            var (curr, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;
            if (neighbors.TryGetValue(curr, out var adj))
            {
                foreach (var next in adj)
                {
                    if (visited.Add(next))
                    {
                        var w = 1.0f - ((float)(depth + 1) / (maxDepth + 1));
                        weights[next] = Mathf.Max(0f, w);
                        queue.Enqueue((next, depth + 1));
                    }
                }
            }
        }
        return weights;
    }

    public static void BlendNormals(Vector3[] normals, Dictionary<int, Vector3> seamFixed, Dictionary<int, HashSet<int>> neighbors, Dictionary<int, float> weights, float strength)
    {
        var original = (Vector3[])normals.Clone();
        foreach (var kvp in weights)
        {
            var idx = kvp.Key;
            var weight = kvp.Value;
            if (seamFixed.TryGetValue(idx, out var fixedNormal))
            {
                normals[idx] = fixedNormal;
                continue;
            }
            var avg = original[idx];
            var count = 1;
            if (neighbors.TryGetValue(idx, out var adj))
            {
                foreach (var nIdx in adj)
                {
                    if (nIdx < 0 || nIdx >= original.Length) continue;
                    avg += original[nIdx];
                    count++;
                }
            }
            avg = count > 0 ? avg.normalized : original[idx];
            var t = weight * strength;
            normals[idx] = Vector3.Slerp(original[idx], avg, t);
        }
    }
}

public static class SceneRaycaster
{
    public static bool TryHitVertex(Mesh mesh, Ray ray, Matrix4x4 noScaleMatrix, List<int> triangleBuffer, out Vector3 worldPos, out int vertexIndex)
    {
        worldPos = Vector3.zero;
        vertexIndex = -1;
        if (mesh == null) return false;
        var vertices = mesh.vertices;
        CollectTriangles(mesh, triangleBuffer);

        var hasHit = false;
        var hitDistance = float.MaxValue;
        var hitPoint = Vector3.zero;

        for (var i = 0; i < triangleBuffer.Count; i += 3)
        {
            var v0 = noScaleMatrix.MultiplyPoint3x4(vertices[triangleBuffer[i]]);
            var v1 = noScaleMatrix.MultiplyPoint3x4(vertices[triangleBuffer[i + 1]]);
            var v2 = noScaleMatrix.MultiplyPoint3x4(vertices[triangleBuffer[i + 2]]);
            if (RayTriangle(ray, v0, v1, v2, out var distance, out var point) && distance < hitDistance)
            {
                hitDistance = distance;
                hitPoint = point;
                hasHit = true;
            }
        }

        if (!hasHit) return false;

        var bestDistance = float.MaxValue;
        for (var i = 0; i < vertices.Length; i++)
        {
            var world = noScaleMatrix.MultiplyPoint3x4(vertices[i]);
            var d = (world - hitPoint).sqrMagnitude;
            if (d < bestDistance)
            {
                bestDistance = d;
                worldPos = world;
                vertexIndex = i;
            }
        }
        return vertexIndex >= 0;
    }

    public static bool TryHitEdge(Mesh mesh, Ray ray, Matrix4x4 noScaleMatrix, List<int> triangleBuffer, out int v1, out int v2)
    {
        v1 = -1;
        v2 = -1;
        if (mesh == null) return false;

        var vertices = mesh.vertices;
        CollectTriangles(mesh, triangleBuffer);
        var hasHit = false;
        var hitDistance = float.MaxValue;
        var hitPoint = Vector3.zero;
        var hitTriIdx = -1;

        for (var i = 0; i < triangleBuffer.Count; i += 3)
        {
            var tv0 = noScaleMatrix.MultiplyPoint3x4(vertices[triangleBuffer[i]]);
            var tv1 = noScaleMatrix.MultiplyPoint3x4(vertices[triangleBuffer[i + 1]]);
            var tv2 = noScaleMatrix.MultiplyPoint3x4(vertices[triangleBuffer[i + 2]]);
            if (RayTriangle(ray, tv0, tv1, tv2, out var distance, out var point) && distance < hitDistance)
            {
                hitDistance = distance;
                hitPoint = point;
                hasHit = true;
                hitTriIdx = i;
            }
        }

        if (!hasHit) return false;

        var idx0 = triangleBuffer[hitTriIdx];
        var idx1 = triangleBuffer[hitTriIdx + 1];
        var idx2 = triangleBuffer[hitTriIdx + 2];
        var p0 = noScaleMatrix.MultiplyPoint3x4(vertices[idx0]);
        var p1 = noScaleMatrix.MultiplyPoint3x4(vertices[idx1]);
        var p2 = noScaleMatrix.MultiplyPoint3x4(vertices[idx2]);
        var d0 = HandleUtility.DistancePointLine(hitPoint, p0, p1);
        var d1 = HandleUtility.DistancePointLine(hitPoint, p1, p2);
        var d2 = HandleUtility.DistancePointLine(hitPoint, p2, p0);
        if (d0 <= d1 && d0 <= d2) { v1 = idx0; v2 = idx1; }
        else if (d1 <= d0 && d1 <= d2) { v1 = idx1; v2 = idx2; }
        else { v1 = idx2; v2 = idx0; }
        return true;
    }

    public static void CollectTriangles(Mesh mesh, List<int> buffer)
    {
        buffer.Clear();
        if (mesh == null) return;
        var subMeshCount = mesh.subMeshCount;
        if (subMeshCount <= 1)
        {
            buffer.AddRange(mesh.triangles);
            return;
        }
        for (var i = 0; i < subMeshCount; i++) buffer.AddRange(mesh.GetTriangles(i));
    }

    private static bool RayTriangle(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float distance, out Vector3 point)
    {
        distance = 0f;
        point = Vector3.zero;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = Vector3.Cross(ray.direction, e2);
        var det = Vector3.Dot(e1, p);
        if (Mathf.Abs(det) < 1e-6f) return false;
        var invDet = 1f / det;
        var t = ray.origin - v0;
        var u = Vector3.Dot(t, p) * invDet;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(t, e1);
        var v = Vector3.Dot(ray.direction, q) * invDet;
        if (v < 0f || u + v > 1f) return false;
        distance = Vector3.Dot(e2, q) * invDet;
        if (distance < 0f) return false;
        point = ray.origin + ray.direction * distance;
        return true;
    }
}
#endif
