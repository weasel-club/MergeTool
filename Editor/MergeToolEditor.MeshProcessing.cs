
#if UNITY_EDITOR
using UnityEngine;
using System.Collections.Generic;
using UnityEditor;
using System;
using System.Linq;

public partial class MergeToolEditor : Editor
{
    private void UpdateMeshesIfDirty()
    {
        if (_target.faceMesh == null || _target.bodyMesh == null) return;

        if (!_isTopologyDirty && !_isDeformDirty && _cachedPreviewMeshes != null) return;

        DestroyMeshDict(_cachedTopologyMeshes);
        DestroyMeshDict(_cachedPreviewMeshes);
        DestroyMeshDict(_cachedResultMeshes);
        _cachedTopologyMeshes = new Dictionary<SkinnedMeshRenderer, Mesh>();
        _cachedPreviewMeshes = new Dictionary<SkinnedMeshRenderer, Mesh>();
        _cachedResultMeshes = new Dictionary<SkinnedMeshRenderer, Mesh>();
        _splitDependencies = new Dictionary<SkinnedMeshRenderer, List<SplitDependency>>();
        _meshGraphs = new Dictionary<SkinnedMeshRenderer, MeshGraph>();

        var blendCache = new Dictionary<SkinnedMeshRenderer, List<BlendShapeData>>();
        PrepareWorkingMesh(_target.faceMesh, blendCache);
        PrepareWorkingMesh(_target.bodyMesh, blendCache);

        for (var i = 0; i < _target.operationLog.Count; i++)
        {
            var op = _target.operationLog[i];
            if (op.kind == OperationKind.FaceSplit) ApplySplitOperation(_target.faceMesh, op.split);
            else if (op.kind == OperationKind.BodySplit) ApplySplitOperation(_target.bodyMesh, op.split);
        }

        ApplyVertexPairAdjustments(_cachedTopologyMeshes);

        foreach (var kvp in _cachedTopologyMeshes)
        {
            var renderer = kvp.Key;
            var mesh = kvp.Value;
            if (renderer == null || mesh == null) continue;
            if (blendCache.TryGetValue(renderer, out var data))
            {
                _splitDependencies.TryGetValue(renderer, out var deps);
                BlendShapeUtility.Apply(mesh, data, deps);
            }
            _meshGraphs[renderer] = new MeshGraph(mesh);
        }

        foreach (var kvp in _cachedTopologyMeshes)
        {
            var renderer = kvp.Key;
            var mesh = kvp.Value;
            if (renderer == null || mesh == null) continue;
            _cachedPreviewMeshes[renderer] = Instantiate(mesh);
        }

        ApplyNormalSmoothing(_cachedPreviewMeshes);

        foreach (var kvp in _cachedPreviewMeshes)
        {
            _cachedResultMeshes[kvp.Key] = Instantiate(kvp.Value);
        }
        BakePreviewMeshes();

        _isTopologyDirty = false;
        _isDeformDirty = false;
    }

    private void PrepareWorkingMesh(SkinnedMeshRenderer renderer, Dictionary<SkinnedMeshRenderer, List<BlendShapeData>> blendCache)
    {
        if (renderer == null) return;
        var source = renderer.sharedMesh;
        if (source == null) return;
        var working = Instantiate(source);
        working.name = source.name + "_Working";
        if (working.bindposes == null || working.bindposes.Length == 0) working.bindposes = source.bindposes;
        _cachedTopologyMeshes[renderer] = working;
        _splitDependencies[renderer] = new List<SplitDependency>();
        blendCache[renderer] = BlendShapeUtility.Capture(source);
    }


    private void ApplySplitOperation(SkinnedMeshRenderer renderer, EdgeSplitData split)
    {
        if (renderer == null) return;
        if (!_cachedTopologyMeshes.TryGetValue(renderer, out var mesh)) return;
        if (!_splitDependencies.TryGetValue(renderer, out var deps)) return;
        var single = new List<EdgeSplitData> { split };
        var newDeps = ApplyEdgeSplitsToMesh(mesh, single);
        if (newDeps != null && newDeps.Count > 0) deps.AddRange(newDeps);
    }

    private void BakePreviewMeshes()
    {
        if (_cachedPreviewMeshes == null) return;
        foreach (var kvp in _cachedPreviewMeshes)
        {
            var renderer = kvp.Key;
            var mesh = kvp.Value;
            if (renderer == null || mesh == null) continue;
            var baked = new Mesh();
            var originalMesh = renderer.sharedMesh;
            renderer.sharedMesh = mesh;
            renderer.BakeMesh(baked);
            renderer.sharedMesh = originalMesh;
            var verts = baked.vertices;
            var norms = baked.normals;
            var tans = baked.tangents;
            mesh.vertices = verts;
            if (norms.Length == verts.Length) mesh.normals = norms;
            if (tans.Length == verts.Length) mesh.tangents = tans;
            mesh.RecalculateBounds();
            baked.Clear();
            DestroyImmediate(baked);
        }
    }

    // Smooth normals across paired seams using neighbor propagation.
    private void ApplyNormalSmoothing(Dictionary<SkinnedMeshRenderer, Mesh> meshes)
    {
        RefreshPairCache();
        var pairs = _pairBuffer;
        if (pairs.Count == 0) return;
        if (!meshes.TryGetValue(_target.faceMesh, out var faceMesh)) return;
        if (!meshes.TryGetValue(_target.bodyMesh, out var bodyMesh)) return;

        var faceGraph = _meshGraphs[_target.faceMesh];
        var bodyGraph = _meshGraphs[_target.bodyMesh];

        var faceNormals = faceMesh.normals;
        var bodyNormals = bodyMesh.normals;
        var faceVerts = faceMesh.vertices;
        var bodyVerts = bodyMesh.vertices;

        var faceToWorld = _target.faceMesh.transform.localToWorldMatrix;
        var bodyToWorld = _target.bodyMesh.transform.localToWorldMatrix;
        var worldToFace = faceToWorld.inverse;
        var worldToBody = bodyToWorld.inverse;

        var faceCoincident = BuildCoincidentMap(faceVerts);
        var bodyCoincident = BuildCoincidentMap(bodyVerts);

        var faceNeighbors = BuildMergedNeighbors(faceGraph.vertexNeighbors, faceCoincident);
        var bodyNeighbors = BuildMergedNeighbors(bodyGraph.vertexNeighbors, bodyCoincident);

        var faceSeamNormals = new Dictionary<int, Vector3>();
        var bodySeamNormals = new Dictionary<int, Vector3>();
        var faceSeeds = new HashSet<int>();
        var bodySeeds = new HashSet<int>();

        var hasValidPair = false;
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (pair.faceIndex < 0 || pair.faceIndex >= faceVerts.Length) continue;
            if (pair.bodyIndex < 0 || pair.bodyIndex >= bodyVerts.Length) continue;

            var nFaceWorld = faceToWorld.MultiplyVector(faceNormals[pair.faceIndex]).normalized;
            var nBodyWorld = bodyToWorld.MultiplyVector(bodyNormals[pair.bodyIndex]).normalized;
            var mergedWorld = GetPairNormal(nFaceWorld, nBodyWorld);
            hasValidPair = true;

            var nFaceLocal = worldToFace.MultiplyVector(mergedWorld).normalized;
            var nBodyLocal = worldToBody.MultiplyVector(mergedWorld).normalized;

            AddSeedWithCoincident(faceSeeds, faceSeamNormals, faceCoincident, pair.faceIndex, nFaceLocal);
            AddSeedWithCoincident(bodySeeds, bodySeamNormals, bodyCoincident, pair.bodyIndex, nBodyLocal);
        }

        if (!hasValidPair) return;

        var faceWeights = BuildSmoothingWeights(faceSeeds, faceNeighbors, faceCoincident, _target.normalSmoothDepth);
        var bodyWeights = BuildSmoothingWeights(bodySeeds, bodyNeighbors, bodyCoincident, _target.normalSmoothDepth);

        ApplyNeighborSmoothing(faceNormals, faceSeamNormals, faceNeighbors, faceWeights, _target.normalSmoothStrength);
        ApplyNeighborSmoothing(bodyNormals, bodySeamNormals, bodyNeighbors, bodyWeights, _target.normalSmoothStrength);

        faceMesh.normals = faceNormals;
        bodyMesh.normals = bodyNormals;
    }
    private Dictionary<int, float> BuildSmoothingWeights(HashSet<int> seeds, Dictionary<int, HashSet<int>> neighbors, Dictionary<int, List<int>> coincident, int maxDepth)
    {
        var weights = new Dictionary<int, float>();
        var queue = new Queue<(int idx, int depth)>();
        var visited = new HashSet<int>();
        foreach (var seed in seeds)
        {
            if (visited.Add(seed)) { queue.Enqueue((seed, 0)); weights[seed] = 1.0f; }
            if (coincident != null && coincident.TryGetValue(seed, out var group))
            {
                foreach (var g in group) { if (visited.Add(g)) { queue.Enqueue((g, 0)); weights[g] = 1.0f; } }
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
                        float w = 1.0f - ((float)(depth + 1) / (maxDepth + 1));
                        weights[next] = Mathf.Max(0f, w);
                        queue.Enqueue((next, depth + 1));
                    }
                }
            }
        }
        return weights;
    }
    private void ApplyNeighborSmoothing(Vector3[] normals, Dictionary<int, Vector3> seamFixedNormals, Dictionary<int, HashSet<int>> neighbors, Dictionary<int, float> weights, float strength)
    {
        var originalNormals = (Vector3[])normals.Clone();
        foreach (var kvp in weights)
        {
            int idx = kvp.Key;
            float weight = kvp.Value;
            if (seamFixedNormals.TryGetValue(idx, out var fixedNormal)) { normals[idx] = fixedNormal; continue; }
            Vector3 avgNormal = Vector3.zero;
            int count = 0;
            avgNormal += originalNormals[idx]; count++;
            if (neighbors.TryGetValue(idx, out var adj))
            {
                foreach (var nIdx in adj)
                {
                    if (nIdx >= 0 && nIdx < originalNormals.Length) { avgNormal += originalNormals[nIdx]; count++; }
                }
            }
            if (count > 0) avgNormal = avgNormal.normalized; else avgNormal = originalNormals[idx];
            float t = weight * strength;
            normals[idx] = Vector3.Slerp(originalNormals[idx], avgNormal, t);
        }
    }
    private Vector3 GetPairNormal(Vector3 faceNormal, Vector3 bodyNormal)
    {
        var sum = faceNormal + bodyNormal;
        if (sum.sqrMagnitude > 0f) return sum.normalized;
        return faceNormal.sqrMagnitude > 0f ? faceNormal.normalized : Vector3.up;
    }
    private void AddSeedWithCoincident(HashSet<int> seeds, Dictionary<int, Vector3> targets, Dictionary<int, List<int>> coincident, int idx, Vector3 normal)
    {
        seeds.Add(idx); targets[idx] = normal;
        if (coincident != null && coincident.TryGetValue(idx, out var group))
            foreach (var g in group) { seeds.Add(g); targets[g] = normal; }
    }
    private Dictionary<int, HashSet<int>> BuildMergedNeighbors(Dictionary<int, HashSet<int>> source, Dictionary<int, List<int>> coincident)
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
                    if (source.TryGetValue(g, out var gNeigh)) foreach (var n in gNeigh) set.Add(n);
                }
            }
            merged[k] = set;
        }
        return merged;
    }
    private Dictionary<int, List<int>> BuildCoincidentMap(Vector3[] vertices)
    {
        const float scale = 10000f;
        var groups = new Dictionary<Vector3Int, List<int>>();
        var map = new Dictionary<int, List<int>>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            var key = new Vector3Int(Mathf.RoundToInt(v.x * scale), Mathf.RoundToInt(v.y * scale), Mathf.RoundToInt(v.z * scale));
            if (!groups.TryGetValue(key, out var list)) { list = new List<int>(); groups[key] = list; }
            list.Add(i);
        }
        foreach (var group in groups.Values) for (var i = 0; i < group.Count; i++) map[group[i]] = group;
        return map;
    }

    private void ApplyChangesToScene()
    {
        UpdateMeshesIfDirty();
        if (_cachedPreviewMeshes == null) return;
        var folderPath = GetSaveFolderPath();
        if (string.IsNullOrEmpty(folderPath)) return;
        var faceApplied = SaveAndApplyMesh(_target.faceMesh, "Face_Modified", out var faceOriginal);
        var bodyApplied = SaveAndApplyMesh(_target.bodyMesh, "Body_Modified", out var bodyOriginal);
        if (faceApplied == null && bodyApplied == null) return;
        Undo.RecordObject(_target, "Apply Mesh");
        _target.appliedFaceMeshBefore = faceOriginal;
        _target.appliedBodyMeshBefore = bodyOriginal;
        _target.appliedFaceMeshAfter = faceApplied;
        _target.appliedBodyMeshAfter = bodyApplied;
        _target.isApplied = true;
        EditorUtility.SetDirty(_target);
        EditorUtility.DisplayDialog("Apply Complete", $"Meshes saved to {folderPath}", "OK");
    }
    private Mesh SaveAndApplyMesh(SkinnedMeshRenderer renderer, string suffix, out Mesh originalMesh)
    {
        originalMesh = null;
        if (renderer == null || _cachedResultMeshes == null) return null;
        if (!_cachedResultMeshes.TryGetValue(renderer, out var sourceMesh)) return null;
        var newMesh = Instantiate(sourceMesh);
        newMesh.name = renderer.name + "_" + suffix;
        newMesh.RecalculateBounds();
        var fileName = $"{renderer.name}_{suffix}_{DateTime.Now.Ticks}.asset";
        SaveAsset(newMesh, fileName);
        originalMesh = renderer.sharedMesh;
        Undo.RecordObject(renderer, "Apply Mesh");
        renderer.sharedMesh = newMesh;
        EditorUtility.SetDirty(renderer);
        return newMesh;
    }

    private List<SplitDependency> ApplyEdgeSplitsToMesh(Mesh mesh, List<EdgeSplitData> splits)
    {
        var dependencies = new List<SplitDependency>();
        if (splits == null || splits.Count == 0) return dependencies;

        var vertices = new List<Vector3>(mesh.vertices);
        var normals = new List<Vector3>(mesh.normals);
        var tangents = new List<Vector4>(mesh.tangents);
        var uvs = new List<Vector2>(mesh.uv);
        var boneWeights = new List<BoneWeight>(mesh.boneWeights);
        var edgeMidpointCache = new Dictionary<ulong, int>();
        var subMeshCount = mesh.subMeshCount;
        var submeshTriangles = new List<List<int>>();
        if (subMeshCount > 0)
        {
            for (var i = 0; i < subMeshCount; i++) submeshTriangles.Add(new List<int>(mesh.GetTriangles(i)));
        }
        else
        {
            submeshTriangles.Add(new List<int>(mesh.triangles));
        }

        for (var i = 0; i < splits.Count; i++)
        {
            var s = splits[i];
            if (s.v1 < 0 || s.v2 < 0) continue;
            if (s.v1 >= vertices.Count || s.v2 >= vertices.Count) continue;
            var handled = false;
            for (var sm = 0; sm < submeshTriangles.Count; sm++)
            {
                var triangles = submeshTriangles[sm];
                if (!EdgeExists(triangles, s.v1, s.v2)) continue;
                var edgeKey = BuildEdgeKey(s.v1, s.v2);
                var mid = GetOrAddMidpoint(edgeKey, s.v1, s.v2, vertices, normals, tangents, uvs, boneWeights, edgeMidpointCache, dependencies);
                submeshTriangles[sm] = SplitTrianglesWithEdge(triangles, s.v1, s.v2, mid);
                handled = true;
            }
            if (!handled) continue;
        }

        mesh.SetVertices(vertices);
        if (normals.Count == vertices.Count) mesh.SetNormals(normals);
        if (tangents.Count == vertices.Count) mesh.SetTangents(tangents);
        if (uvs.Count == vertices.Count) mesh.SetUVs(0, uvs);
        if (boneWeights.Count == vertices.Count) mesh.boneWeights = boneWeights.ToArray();
        mesh.subMeshCount = submeshTriangles.Count;
        for (var i = 0; i < submeshTriangles.Count; i++) mesh.SetTriangles(submeshTriangles[i], i);
        mesh.RecalculateBounds();

        return dependencies;
    }

    private ulong BuildEdgeKey(int a, int b)
    {
        var i1 = (uint)Mathf.Min(a, b);
        var i2 = (uint)Mathf.Max(a, b);
        return ((ulong)i1 << 32) | i2;
    }

    private List<int> SplitTrianglesWithEdge(List<int> triangles, int v1, int v2, int mid)
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

    private bool EdgeMatches(int a, int b, int v1, int v2)
    {
        return (a == v1 && b == v2) || (a == v2 && b == v1);
    }

    private bool EdgeExists(List<int> triangles, int v1, int v2)
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

    // Create a midpoint vertex with blended attributes and dependency tracking.
    private int GetOrAddMidpoint(ulong edgeKey, int iA, int iB,
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

    // Keep split midpoints in sync after vertex movement.
    private void UpdateSplitVertices(Mesh mesh, List<SplitDependency> deps)
    {
        if (deps == null) return;
        var verts = mesh.vertices;
        var count = verts.Length;
        var norms = mesh.normals;
        var tans = mesh.tangents;
        var uvs = mesh.uv;
        var hasNorms = norms != null && norms.Length == count;
        var hasTans = tans != null && tans.Length == count;
        var hasUvs = uvs != null && uvs.Length == count;

        foreach (var d in deps)
        {
            if (d.midIndex < count && d.parent1Index < count && d.parent2Index < count)
            {
                verts[d.midIndex] = (verts[d.parent1Index] + verts[d.parent2Index]) * 0.5f;
                if (hasNorms)
                {
                    var n = norms[d.parent1Index] + norms[d.parent2Index];
                    norms[d.midIndex] = n.sqrMagnitude > 1e-12f ? n.normalized : norms[d.parent1Index];
                }
                if (hasTans) tans[d.midIndex] = AverageTangent(tans[d.parent1Index], tans[d.parent2Index]);
                if (hasUvs) uvs[d.midIndex] = AverageWrappedUv(uvs[d.parent1Index], uvs[d.parent2Index]);
            }
        }
        mesh.vertices = verts;
        if (hasNorms) mesh.normals = norms;
        if (hasTans) mesh.tangents = tans;
        if (hasUvs) mesh.uv = uvs;
    }

    private Vector2 AverageWrappedUv(Vector2 a, Vector2 b)
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

    private Vector4 AverageTangent(Vector4 t1, Vector4 t2)
    {
        var d1 = new Vector3(t1.x, t1.y, t1.z);
        var d2 = new Vector3(t2.x, t2.y, t2.z);
        var dir = d1 + d2;
        if (dir.sqrMagnitude < 1e-12f) dir = d1.sqrMagnitude >= d2.sqrMagnitude ? d1 : d2;
        dir = dir.sqrMagnitude > 1e-12f ? dir.normalized : Vector3.right;
        var w = Mathf.Abs(t1.w) >= Mathf.Abs(t2.w) ? Mathf.Sign(t1.w) : Mathf.Sign(t2.w);
        if (w == 0f) w = t1.w >= 0f ? 1f : -1f;
        return new Vector4(dir.x, dir.y, dir.z, w);
    }

    private BoneWeight BlendBoneWeights(BoneWeight a, BoneWeight b)
    {
        var map = new Dictionary<int, float>();
        AccumulateWeight(map, a.boneIndex0, a.weight0);
        AccumulateWeight(map, a.boneIndex1, a.weight1);
        AccumulateWeight(map, a.boneIndex2, a.weight2);
        AccumulateWeight(map, a.boneIndex3, a.weight3);
        AccumulateWeight(map, b.boneIndex0, b.weight0);
        AccumulateWeight(map, b.boneIndex1, b.weight1);
        AccumulateWeight(map, b.boneIndex2, b.weight2);
        AccumulateWeight(map, b.boneIndex3, b.weight3);
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
    private void AccumulateWeight(Dictionary<int, float> map, int boneIndex, float weight)
    {
        if (boneIndex < 0 || weight <= 0f) return;
        if (map.TryGetValue(boneIndex, out var existing)) map[boneIndex] = existing + weight;
        else map[boneIndex] = weight;
    }

    // Align paired vertices in working meshes using bind-pose aware transforms.
    private void ApplyVertexPairAdjustments(Dictionary<SkinnedMeshRenderer, Mesh> meshes)
    {
        if (!TryBuildPairContext(meshes, out var context)) return;
        RefreshPairCache();
        var pairs = _pairBuffer;
        if (pairs.Count == 0) return;

        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (!IsValidPair(pair, context)) continue;

            var faceWorld = ConvertLocalToWorld(context.faceVertices[pair.faceIndex], context.faceToWorld, context.faceSkinToWorld, pair.faceIndex);
            var bodyWorld = ConvertLocalToWorld(context.bodyVertices[pair.bodyIndex], context.bodyToWorld, context.bodySkinToWorld, pair.bodyIndex);
            var midpoint = (faceWorld + bodyWorld) * 0.5f + pair.worldOffset;

            ApplyMidpointToGroup(context.faceVertices, pair.faceIndex, context.faceGroups, context.worldToFace, midpoint, context.faceSkinToWorld);
            ApplyMidpointToGroup(context.bodyVertices, pair.bodyIndex, context.bodyGroups, context.worldToBody, midpoint, context.bodySkinToWorld);
        }

        context.faceMesh.vertices = context.faceVertices;
        context.bodyMesh.vertices = context.bodyVertices;
        context.faceMesh.RecalculateBounds();
        context.bodyMesh.RecalculateBounds();

        if (_splitDependencies != null)
        {
            if (_splitDependencies.TryGetValue(context.faceRenderer, out var faceDeps)) UpdateSplitVertices(context.faceMesh, faceDeps);
            if (_splitDependencies.TryGetValue(context.bodyRenderer, out var bodyDeps)) UpdateSplitVertices(context.bodyMesh, bodyDeps);
        }
    }


    private Dictionary<int, List<int>> BuildCoincidentVertexMap(Vector3[] vertices, Matrix4x4 localToWorld, Matrix4x4[] skinToWorld)
    {
        const float scale = 10000f;
        var groups = new Dictionary<Vector3Int, List<int>>();
        var map = new Dictionary<int, List<int>>();

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
            for (var i = 0; i < group.Count; i++)
                map[group[i]] = group;

        return map;
    }

    private void ApplyMidpointToGroup(Vector3[] vertices, int index, Dictionary<int, List<int>> groups, Matrix4x4 worldToLocal, Vector3 midpoint, Matrix4x4[] skinToWorld)
    {
        if (groups.TryGetValue(index, out var linkedIndices))
        {
            for (var i = 0; i < linkedIndices.Count; i++)
            {
                var tIdx = linkedIndices[i];
                if (tIdx >= 0 && tIdx < vertices.Length) vertices[tIdx] = ConvertWorldToLocal(midpoint, worldToLocal, skinToWorld, tIdx);
            }
        }
        else if (index >= 0 && index < vertices.Length)
        {
            vertices[index] = ConvertWorldToLocal(midpoint, worldToLocal, skinToWorld, index);
        }
    }
    private Matrix4x4 GetRendererToWorldMatrix(SkinnedMeshRenderer renderer) { return renderer ? renderer.transform.localToWorldMatrix : Matrix4x4.identity; }
    private Matrix4x4 GetWorldToRendererMatrix(SkinnedMeshRenderer renderer) { return renderer ? renderer.transform.worldToLocalMatrix : Matrix4x4.identity; }
    private Matrix4x4 GetRendererToWorldMatrixNoScale(SkinnedMeshRenderer renderer)
    {
        if (renderer == null) return Matrix4x4.identity;
        return Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
    }
    private Matrix4x4[] BuildSkinToWorldMatrices(SkinnedMeshRenderer renderer, Mesh mesh)
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
            if (bw.weight0 > 0f && IsValidBone(bones, bw.boneIndex0)) skin = AddMatrix(skin, ScaleMatrix(bones[bw.boneIndex0].localToWorldMatrix * bindposes[bw.boneIndex0], bw.weight0));
            if (bw.weight1 > 0f && IsValidBone(bones, bw.boneIndex1)) skin = AddMatrix(skin, ScaleMatrix(bones[bw.boneIndex1].localToWorldMatrix * bindposes[bw.boneIndex1], bw.weight1));
            if (bw.weight2 > 0f && IsValidBone(bones, bw.boneIndex2)) skin = AddMatrix(skin, ScaleMatrix(bones[bw.boneIndex2].localToWorldMatrix * bindposes[bw.boneIndex2], bw.weight2));
            if (bw.weight3 > 0f && IsValidBone(bones, bw.boneIndex3)) skin = AddMatrix(skin, ScaleMatrix(bones[bw.boneIndex3].localToWorldMatrix * bindposes[bw.boneIndex3], bw.weight3));
            matrices[i] = skin;
        }
        return matrices;
    }
    private bool IsValidBone(Transform[] bones, int index) { return index >= 0 && index < bones.Length && bones[index] != null; }
    private Matrix4x4 ScaleMatrix(Matrix4x4 m, float weight)
    {
        m.m00 *= weight; m.m01 *= weight; m.m02 *= weight; m.m03 *= weight;
        m.m10 *= weight; m.m11 *= weight; m.m12 *= weight; m.m13 *= weight;
        m.m20 *= weight; m.m21 *= weight; m.m22 *= weight; m.m23 *= weight;
        m.m30 *= weight; m.m31 *= weight; m.m32 *= weight; m.m33 *= weight;
        return m;
    }
    private Matrix4x4 AddMatrix(Matrix4x4 a, Matrix4x4 b)
    {
        a.m00 += b.m00; a.m01 += b.m01; a.m02 += b.m02; a.m03 += b.m03;
        a.m10 += b.m10; a.m11 += b.m11; a.m12 += b.m12; a.m13 += b.m13;
        a.m20 += b.m20; a.m21 += b.m21; a.m22 += b.m22; a.m23 += b.m23;
        a.m30 += b.m30; a.m31 += b.m31; a.m32 += b.m32; a.m33 += b.m33;
        return a;
    }
    private Vector3 ConvertLocalToWorld(Vector3 local, Matrix4x4 localToWorld, Matrix4x4[] skinToWorld, int index)
    {
        if (skinToWorld != null && index >= 0 && index < skinToWorld.Length) return skinToWorld[index].MultiplyPoint3x4(local);
        return localToWorld.MultiplyPoint3x4(local);
    }
    private Vector3 ConvertWorldToLocal(Vector3 world, Matrix4x4 fallbackWorldToLocal, Matrix4x4[] skinToWorld, int index)
    {
        if (skinToWorld != null && index >= 0 && index < skinToWorld.Length)
        {
            var skin = skinToWorld[index];
            if (Mathf.Abs(skin.determinant) > 1e-8f) return skin.inverse.MultiplyPoint3x4(world);
        }
        return fallbackWorldToLocal.MultiplyPoint3x4(world);
    }

    private bool TryBuildPairContext(Dictionary<SkinnedMeshRenderer, Mesh> meshes, out PairContext context)
    {
        context = default;
        if (_target == null || meshes == null) return false;
        if (_target.faceMesh == null || _target.bodyMesh == null) return false;
        if (!meshes.TryGetValue(_target.faceMesh, out var faceMesh)) return false;
        if (!meshes.TryGetValue(_target.bodyMesh, out var bodyMesh)) return false;

        var faceVertices = faceMesh.vertices;
        var bodyVertices = bodyMesh.vertices;
        context = new PairContext
        {
            faceRenderer = _target.faceMesh,
            bodyRenderer = _target.bodyMesh,
            faceMesh = faceMesh,
            bodyMesh = bodyMesh,
            faceVertices = faceVertices,
            bodyVertices = bodyVertices,
            faceToWorld = GetRendererToWorldMatrix(_target.faceMesh),
            bodyToWorld = GetRendererToWorldMatrix(_target.bodyMesh)
        };
        context.worldToFace = context.faceToWorld.inverse;
        context.worldToBody = context.bodyToWorld.inverse;
        context.faceSkinToWorld = BuildSkinToWorldMatrices(context.faceRenderer, faceMesh);
        context.bodySkinToWorld = BuildSkinToWorldMatrices(context.bodyRenderer, bodyMesh);
        context.faceGroups = BuildCoincidentVertexMap(faceVertices, context.faceToWorld, context.faceSkinToWorld);
        context.bodyGroups = BuildCoincidentVertexMap(bodyVertices, context.bodyToWorld, context.bodySkinToWorld);
        return true;
    }

    private bool IsValidPair(VertexPair pair, PairContext context)
    {
        if (pair.faceIndex < 0 || pair.bodyIndex < 0) return false;
        if (context.faceVertices == null || context.bodyVertices == null) return false;
        return pair.faceIndex < context.faceVertices.Length && pair.bodyIndex < context.bodyVertices.Length;
    }

    private struct PairContext
    {
        public SkinnedMeshRenderer faceRenderer;
        public SkinnedMeshRenderer bodyRenderer;
        public Mesh faceMesh;
        public Mesh bodyMesh;
        public Vector3[] faceVertices;
        public Vector3[] bodyVertices;
        public Matrix4x4 faceToWorld;
        public Matrix4x4 bodyToWorld;
        public Matrix4x4 worldToFace;
        public Matrix4x4 worldToBody;
        public Matrix4x4[] faceSkinToWorld;
        public Matrix4x4[] bodySkinToWorld;
        public Dictionary<int, List<int>> faceGroups;
        public Dictionary<int, List<int>> bodyGroups;
    }
}
#endif
