#if UNITY_EDITOR
using UnityEngine;
using System.Collections.Generic;
using UnityEditor;

public partial class MergeToolEditor : Editor
{
    private void HandleVertexClick()
    {
        if (_hoveredVertexIndex < 0) return;

        if (_editMode == EditMode.Face)
        {
            _selectedFaceIndex = _hoveredVertexIndex;
            _editMode = EditMode.Body;
        }
        else if (_editMode == EditMode.Body && _selectedFaceIndex >= 0)
        {
            var pair = new VertexPair
            {
                faceIndex = _selectedFaceIndex,
                bodyIndex = _hoveredVertexIndex
            };
            AddPairWithSymmetry(pair);
            _selectedFaceIndex = -1;
            _editMode = EditMode.Face;
        }
        _hoveredVertexIndex = -1;
        MarkDeformDirty();
        SceneView.RepaintAll();
    }

    private void HandleEdgeClick()
    {
        if (_hoveredEdgeV1 < 0 || _hoveredEdgeV2 < 0) return;

        var isFace = _editMode == EditMode.FaceEdge;
        var split = new EdgeSplitData { v1 = _hoveredEdgeV1, v2 = _hoveredEdgeV2 };
        AddSplitWithSymmetry(split, isFace);

        _hoveredEdgeV1 = -1;
        _hoveredEdgeV2 = -1;
        MarkTopologyDirty();
        SceneView.RepaintAll();
    }

    private void AddSplitWithSymmetry(EdgeSplitData split, bool isFace)
    {
        Undo.RecordObject(_target, "Add Edge Split");
        var entry = new OperationRecord
        {
            kind = isFace ? OperationKind.FaceSplit : OperationKind.BodySplit,
            split = split,
            pair = default
        };
        _target.operationLog.Add(entry);
        InvalidatePairCache();
    }
    private void AddPairWithSymmetry(VertexPair pair)
    {
        var baseIndex = AddPairIfNew(pair);
        _selectedPairIndex = baseIndex;
        if (!_target.enableSymmetry || _target.faceMesh == null || _target.bodyMesh == null) return;

        var faceMap = BuildSymmetryLookup(_target.faceMesh);
        var bodyMap = BuildSymmetryLookup(_target.bodyMesh);
        var symmetricFace = FindSymmetricIndex(pair.faceIndex, faceMap);
        var symmetricBody = FindSymmetricIndex(pair.bodyIndex, bodyMap);
        if (symmetricFace < 0 || symmetricBody < 0) return;
        if (IsVertexUsed(EditMode.Face, symmetricFace)) return;
        if (IsVertexUsed(EditMode.Body, symmetricBody)) return;
        if (PairExists(symmetricFace, symmetricBody)) return;

        var symmetricPair = pair;
        symmetricPair.faceIndex = symmetricFace;
        symmetricPair.bodyIndex = symmetricBody;
        symmetricPair.worldOffset = MirrorWorldOffset(pair.worldOffset, _target.faceMesh.transform);
        AddPairIfNew(symmetricPair);
        RefreshPairCache();
    }

    private int AddPairIfNew(VertexPair pair)
    {
        var existingIndex = FindPairIndex(pair.faceIndex, pair.bodyIndex, out var logIndex);
        if (existingIndex >= 0)
        {
            Undo.RecordObject(_target, "Update Vertex Pair");
            UpdatePairAtLogIndex(logIndex, pair);
            MarkDeformDirty();
            InvalidatePairCache();
            RefreshPairCache();
            return FindPairIndex(pair.faceIndex, pair.bodyIndex, out _);
        }
        Undo.RecordObject(_target, "Add Vertex Pair");
        var entry = new OperationRecord
        {
            kind = OperationKind.Pair,
            split = default,
            pair = pair
        };
        _target.operationLog.Add(entry);
        MarkDeformDirty();
        InvalidatePairCache();
        RefreshPairCache();
        return GetPairCount() - 1;
    }

    private bool PairExists(int faceIndex, int bodyIndex)
    {
        return FindPairIndex(faceIndex, bodyIndex, out _) >= 0;
    }

    private bool IsVertexUsed(EditMode mode, int index)
    {
        if (index < 0) return true;
        RefreshPairCache();
        for (var i = 0; i < _pairBuffer.Count; i++)
        {
            var pair = _pairBuffer[i];
            if (mode == EditMode.Face && pair.faceIndex == index) return true;
            if (mode == EditMode.Body && pair.bodyIndex == index) return true;
        }
        return false;
    }

    private Dictionary<int, int> BuildSymmetryLookup(SkinnedMeshRenderer renderer)
    {
        var map = new Dictionary<int, int>();
        if (renderer == null || renderer.sharedMesh == null) return map;
        var vertices = renderer.sharedMesh.vertices;
        var scale = 1f / Mathf.Max(0.000001f, _target != null ? _target.symmetryTolerance : 0.0001f);
        var keyToIndex = new Dictionary<Vector3Int, int>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            var key = new Vector3Int(Mathf.RoundToInt(v.x * scale), Mathf.RoundToInt(v.y * scale), Mathf.RoundToInt(v.z * scale));
            var mKey = new Vector3Int(Mathf.RoundToInt(-v.x * scale), Mathf.RoundToInt(v.y * scale), Mathf.RoundToInt(v.z * scale));
            if (keyToIndex.TryGetValue(mKey, out var other))
            {
                map[i] = other;
                map[other] = i;
            }
            if (!keyToIndex.ContainsKey(key)) keyToIndex[key] = i;
        }
        return map;
    }

    private int FindSymmetricIndex(int index, Dictionary<int, int> map)
    {
        if (map != null && map.TryGetValue(index, out var symmetric)) return symmetric;
        return -1;
    }

    private Vector3 MirrorWorldOffset(Vector3 worldOffset, Transform reference)
    {
        if (reference == null) return worldOffset;
        var local = reference.worldToLocalMatrix.MultiplyVector(worldOffset);
        local.x = -local.x;
        return reference.localToWorldMatrix.MultiplyVector(local);
    }

    private Vector3 GetPairMidpointWorld(VertexPair pair)
    {
        if (_target == null || _target.faceMesh == null || _target.bodyMesh == null) return Vector3.zero;
        if (_cachedPreviewMeshes == null) return Vector3.zero;

        if (_cachedPreviewMeshes.TryGetValue(_target.faceMesh, out var fm) && _cachedPreviewMeshes.TryGetValue(_target.bodyMesh, out var bm))
        {
            var fv = fm.vertices;
            var bv = bm.vertices;
            if (pair.faceIndex < fv.Length && pair.bodyIndex < bv.Length)
            {
                var fp = GetRendererToWorldMatrixNoScale(_target.faceMesh).MultiplyPoint3x4(fv[pair.faceIndex]);
                var bp = GetRendererToWorldMatrixNoScale(_target.bodyMesh).MultiplyPoint3x4(bv[pair.bodyIndex]);
                return (fp + bp) * 0.5f;
            }
        }
        return Vector3.zero;
    }

    private bool TryGetPairWorldPosition(VertexPair pair, Dictionary<SkinnedMeshRenderer, Mesh> bakedMeshes, out Vector3 world)
    {
        world = Vector3.zero;
        if (_target == null || _target.faceMesh == null || _target.bodyMesh == null) return false;
        if (bakedMeshes == null) return false;

        if (bakedMeshes.TryGetValue(_target.faceMesh, out var fm) && bakedMeshes.TryGetValue(_target.bodyMesh, out var bm))
        {
            var fv = fm.vertices;
            var bv = bm.vertices;
            if (pair.faceIndex >= 0 && pair.faceIndex < fv.Length && pair.bodyIndex >= 0 && pair.bodyIndex < bv.Length)
            {
                var faceWorld = GetRendererToWorldMatrixNoScale(_target.faceMesh).MultiplyPoint3x4(fv[pair.faceIndex]);
                var bodyWorld = GetRendererToWorldMatrixNoScale(_target.bodyMesh).MultiplyPoint3x4(bv[pair.bodyIndex]);
                world = (faceWorld + bodyWorld) * 0.5f;
                return true;
            }
        }
        return false;
    }

    private void SetPairOffset(int index, Vector3 offset)
    {
        if (_target == null) return;
        if (!TryGetCachedPair(index, out var pair, out var logIndex)) return;
        Undo.RecordObject(_target, "Move Pair");
        pair.worldOffset = offset;
        UpdatePairAtLogIndex(logIndex, pair);
        SyncSymmetricOffset(index, offset);
        InvalidatePairCache();
        RefreshPairCache();
    }

    private void SetPairOffsetFromWorld(int index, Vector3 worldPosition)
    {
        if (_target == null) return;
        if (!TryGetCachedPair(index, out var pair, out _)) return;
        if (_cachedPreviewMeshes == null) return;

        if (_cachedPreviewMeshes.TryGetValue(_target.faceMesh, out var fm) && _cachedPreviewMeshes.TryGetValue(_target.bodyMesh, out var bm))
        {
            var fv = fm.vertices;
            var bv = bm.vertices;
            var fp = GetRendererToWorldMatrixNoScale(_target.faceMesh).MultiplyPoint3x4(fv[pair.faceIndex]);
            var bp = GetRendererToWorldMatrixNoScale(_target.bodyMesh).MultiplyPoint3x4(bv[pair.bodyIndex]);

            var currentPos = (fp + bp) * 0.5f;
            var basePos = currentPos - pair.worldOffset;
            var newOffset = worldPosition - basePos;

            SetPairOffset(index, newOffset);
            RefreshPairCache();
        }
    }

    private void SyncSymmetricOffset(int sourceIndex, Vector3 offset)
    {
        if (_target == null || !_target.enableSymmetry || _target.faceMesh == null || _target.bodyMesh == null) return;
        var symmetricIndex = FindSymmetricPairIndex(sourceIndex);
        if (symmetricIndex < 0 || symmetricIndex == sourceIndex) return;
        if (!TryGetCachedPair(symmetricIndex, out var pair, out var logIndex)) return;
        var mirroredOffset = MirrorWorldOffset(offset, _target.faceMesh.transform);
        pair.worldOffset = mirroredOffset;
        UpdatePairAtLogIndex(logIndex, pair);
        InvalidatePairCache();
        RefreshPairCache();
    }

    private int FindSymmetricPairIndex(int sourceIndex)
    {
        if (_target == null || _target.faceMesh == null || _target.bodyMesh == null) return -1;
        var faceMap = BuildSymmetryLookup(_target.faceMesh);
        var bodyMap = BuildSymmetryLookup(_target.bodyMesh);
        RefreshPairCache();
        if (sourceIndex < 0 || sourceIndex >= _pairBuffer.Count) return -1;
        var sourcePair = _pairBuffer[sourceIndex];
        var sFace = FindSymmetricIndex(sourcePair.faceIndex, faceMap);
        var sBody = FindSymmetricIndex(sourcePair.bodyIndex, bodyMap);
        if (sFace < 0 || sBody < 0) return -1;
        for (var i = 0; i < _pairBuffer.Count; i++)
        {
            if (i == sourceIndex) continue;
            var pair = _pairBuffer[i];
            if (pair.faceIndex == sFace && pair.bodyIndex == sBody) return i;
        }
        return -1;
    }

    private void RefreshPairCache()
    {
        if (!_pairCacheDirty) return;
        _pairBuffer.Clear();
        _pairLogIndices.Clear();
        if (_target == null || _target.operationLog == null) return;
        for (var i = 0; i < _target.operationLog.Count; i++)
        {
            var op = _target.operationLog[i];
            if (op.kind != OperationKind.Pair) continue;
            _pairBuffer.Add(op.pair);
            _pairLogIndices.Add(i);
        }
        _pairCacheDirty = false;
    }

    private int GetPairCount()
    {
        RefreshPairCache();
        return _pairBuffer.Count;
    }

    private bool TryGetCachedPair(int pairIndex, out VertexPair pair, out int logIndex)
    {
        pair = default;
        logIndex = -1;
        RefreshPairCache();
        if (pairIndex < 0 || pairIndex >= _pairBuffer.Count) return false;
        pair = _pairBuffer[pairIndex];
        logIndex = _pairLogIndices[pairIndex];
        return logIndex >= 0;
    }

    private int FindPairIndex(int faceIndex, int bodyIndex, out int logIndex)
    {
        logIndex = -1;
        RefreshPairCache();
        for (var i = 0; i < _pairBuffer.Count; i++)
        {
            var pair = _pairBuffer[i];
            if (pair.faceIndex == faceIndex && pair.bodyIndex == bodyIndex)
            {
                logIndex = _pairLogIndices[i];
                return i;
            }
        }
        return -1;
    }

    private int GetPairIndexByLogIndex(int logIndex)
    {
        RefreshPairCache();
        for (var i = 0; i < _pairLogIndices.Count; i++)
        {
            if (_pairLogIndices[i] == logIndex) return i;
        }
        return -1;
    }

    private void UpdatePairAtLogIndex(int logIndex, VertexPair pair)
    {
        if (_target == null || _target.operationLog == null) return;
        if (logIndex < 0 || logIndex >= _target.operationLog.Count) return;
        var op = _target.operationLog[logIndex];
        op.pair = pair;
        _target.operationLog[logIndex] = op;
    }
}
#endif
