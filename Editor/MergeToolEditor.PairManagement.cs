#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public partial class MergeToolEditor
{
    private void DrawOperationLogUI()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Operation Log", EditorStyles.boldLabel);
        if (_target.operationLog == null || _target.operationLog.Count == 0)
        {
            EditorGUILayout.LabelField("No operations recorded.");
            return;
        }
        for (var i = 0; i < _target.operationLog.Count; i++)
        {
            var op = _target.operationLog[i];
            EditorGUILayout.BeginHorizontal();
            var revertContent = EditorGUIUtility.IconContent("d_Animation.PrevKey");
            if (GUILayout.Button(revertContent, GUILayout.Width(26), GUILayout.Height(20)))
            {
                RevertToOperationIndex(i);
                EditorGUILayout.EndHorizontal();
                break;
            }
            EditorGUILayout.LabelField($"{i + 1}. {GetOperationLabel(op)}");
            if (op.kind == OperationKind.Pair)
            {
                var selectContent = EditorGUIUtility.IconContent("d_ViewToolOrbit");
                if (GUILayout.Button(selectContent, GUILayout.Width(26), GUILayout.Height(20)))
                {
                    SelectPairByLogIndex(i);
                }
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    private string GetOperationLabel(OperationRecord op)
    {
        return op.kind switch
        {
            OperationKind.FaceSplit => $"Face Split {op.split.v1} - {op.split.v2}",
            OperationKind.BodySplit => $"Body Split {op.split.v1} - {op.split.v2}",
            OperationKind.Pair => $"Pair Face {op.pair.faceIndex} / Body {op.pair.bodyIndex}",
            _ => "Unknown"
        };
    }

    private void RevertToOperationIndex(int logIndex)
    {
        if (_target.operationLog == null) return;
        if (logIndex < 0 || logIndex >= _target.operationLog.Count) return;
        Undo.RecordObject(_target, "Revert Operation Log");
        var removeCount = _target.operationLog.Count - logIndex - 1;
        if (removeCount > 0) _target.operationLog.RemoveRange(logIndex + 1, removeCount);
        _selection.ResetSelection();
        _selection.ResetHover();
        _editMode = EditMode.None;
        MarkTopologyDirty();
        MarkDeformDirty();
        _pairCache.Invalidate();
        _pairCache.Refresh();
        SceneView.RepaintAll();
        Repaint();
    }

    private void SelectPairByLogIndex(int logIndex)
    {
        var pairIndex = _pairCache.FindByLogIndex(logIndex);
        if (pairIndex < 0) return;
        _selection.SelectedPairIndex = pairIndex;
        _editMode = EditMode.None;
        _selection.ResetHover();
        SceneView.RepaintAll();
        Repaint();
    }

    private void AddSplitWithSymmetry(EdgeSplitData split, bool isFace)
    {
        Undo.RecordObject(_target, "Add Edge Split");
        _target.operationLog.Add(new OperationRecord
        {
            kind = isFace ? OperationKind.FaceSplit : OperationKind.BodySplit,
            split = split
        });
        MarkTopologyDirty();
        _pairCache.Invalidate();
    }

    private void AddPairWithSymmetry(VertexPair pair)
    {
        var baseIndex = AddOrUpdatePair(pair);
        _selection.SelectedPairIndex = baseIndex;
        if (!_target.enableSymmetry || !HasMeshesAssigned()) return;

        var faceMap = SymmetryUtility.BuildSymmetryLookup(_target.faceMesh, _target.symmetryTolerance);
        var bodyMap = SymmetryUtility.BuildSymmetryLookup(_target.bodyMesh, _target.symmetryTolerance);
        var symmetricFace = FindSymmetricIndex(pair.faceIndex, faceMap);
        var symmetricBody = FindSymmetricIndex(pair.bodyIndex, bodyMap);
        if (symmetricFace < 0 || symmetricBody < 0) return;
        if (PairExists(symmetricFace, symmetricBody)) return;
        var symmetricPair = pair;
        symmetricPair.faceIndex = symmetricFace;
        symmetricPair.bodyIndex = symmetricBody;
        symmetricPair.worldOffset = SymmetryUtility.MirrorOffset(pair.worldOffset, _target.faceMesh.transform);
        AddOrUpdatePair(symmetricPair);
        _pairCache.Refresh();
    }

    private int AddOrUpdatePair(VertexPair pair)
    {
        var existing = _pairCache.Find(pair.faceIndex, pair.bodyIndex, out var logIndex);
        if (existing >= 0)
        {
            Undo.RecordObject(_target, "Update Vertex Pair");
            UpdatePairAtLogIndex(logIndex, pair);
            MarkDeformDirty();
            _pairCache.Invalidate();
            _pairCache.Refresh();
            return _pairCache.Find(pair.faceIndex, pair.bodyIndex, out _);
        }

        Undo.RecordObject(_target, "Add Vertex Pair");
        _target.operationLog.Add(new OperationRecord { kind = OperationKind.Pair, pair = pair });
        MarkDeformDirty();
        _pairCache.Invalidate();
        _pairCache.Refresh();
        return _pairCache.Pairs.Count - 1;
    }

    private void DeleteSelectedPair()
    {
        _pairCache.Refresh();
        if (_selection.SelectedPairIndex < 0 || _selection.SelectedPairIndex >= _pairCache.Pairs.Count) return;

        Undo.RecordObject(_target, "Delete Pair");
        var removeLogIndices = new List<int>();
        if (_pairCache.LogIndices.Count > _selection.SelectedPairIndex) removeLogIndices.Add(_pairCache.LogIndices[_selection.SelectedPairIndex]);
        var symmetricIndex = _target.enableSymmetry ? FindSymmetricPairIndex(_selection.SelectedPairIndex) : -1;
        if (symmetricIndex == _selection.SelectedPairIndex) symmetricIndex = -1;
        if (symmetricIndex >= 0 && symmetricIndex < _pairCache.LogIndices.Count) removeLogIndices.Add(_pairCache.LogIndices[symmetricIndex]);
        removeLogIndices.Sort();
        for (var i = removeLogIndices.Count - 1; i >= 0; i--)
        {
            var logIndex = removeLogIndices[i];
            if (logIndex >= 0 && logIndex < _target.operationLog.Count) _target.operationLog.RemoveAt(logIndex);
        }

        _selection.SelectedPairIndex = -1;
        _pairCache.Invalidate();
        _pairCache.Refresh();
        MarkDeformDirty();
        SceneView.RepaintAll();
    }

    private void SetPairOffset(int index, Vector3 offset)
    {
        if (!TryGetCachedPair(index, out var pair, out var logIndex)) return;
        Undo.RecordObject(_target, "Move Pair");
        pair.worldOffset = offset;
        UpdatePairAtLogIndex(logIndex, pair);
        SyncSymmetricOffset(index, offset);
        _pairCache.Invalidate();
        _pairCache.Refresh();
    }

    private void SetPairOffsetFromWorld(int index, Vector3 worldPosition)
    {
        if (!TryGetCachedPair(index, out var pair, out _)) return;
        if (_faceWorkspace?.ResultMesh == null || _bodyWorkspace?.ResultMesh == null) return;

        var fv = _faceWorkspace.ResultMesh.vertices;
        var bv = _bodyWorkspace.ResultMesh.vertices;
        var fp = MeshSpace.GetRendererToWorldNoScale(_target.faceMesh).MultiplyPoint3x4(fv[pair.faceIndex]);
        var bp = MeshSpace.GetRendererToWorldNoScale(_target.bodyMesh).MultiplyPoint3x4(bv[pair.bodyIndex]);

        var currentPos = (fp + bp) * 0.5f;
        var basePos = currentPos - pair.worldOffset;
        var newOffset = worldPosition - basePos;

        SetPairOffset(index, newOffset);
        _pairCache.Refresh();
    }

    private void SyncSymmetricOffset(int sourceIndex, Vector3 offset)
    {
        if (!_target.enableSymmetry || !HasMeshesAssigned()) return;
        var symmetricIndex = FindSymmetricPairIndex(sourceIndex);
        if (symmetricIndex < 0 || symmetricIndex == sourceIndex) return;
        if (!TryGetCachedPair(symmetricIndex, out var pair, out var logIndex)) return;
        var mirrored = SymmetryUtility.MirrorOffset(offset, _target.faceMesh.transform);
        pair.worldOffset = mirrored;
        UpdatePairAtLogIndex(logIndex, pair);
        _pairCache.Invalidate();
        _pairCache.Refresh();
    }

    private int FindSymmetricPairIndex(int sourceIndex)
    {
        if (!HasMeshesAssigned()) return -1;
        var faceMap = SymmetryUtility.BuildSymmetryLookup(_target.faceMesh, _target.symmetryTolerance);
        var bodyMap = SymmetryUtility.BuildSymmetryLookup(_target.bodyMesh, _target.symmetryTolerance);
        _pairCache.Refresh();
        if (sourceIndex < 0 || sourceIndex >= _pairCache.Pairs.Count) return -1;
        var sourcePair = _pairCache.Pairs[sourceIndex];
        var sFace = FindSymmetricIndex(sourcePair.faceIndex, faceMap);
        var sBody = FindSymmetricIndex(sourcePair.bodyIndex, bodyMap);
        if (sFace < 0 || sBody < 0) return -1;
        for (var i = 0; i < _pairCache.Pairs.Count; i++)
        {
            if (i == sourceIndex) continue;
            var pair = _pairCache.Pairs[i];
            if (pair.faceIndex == sFace && pair.bodyIndex == sBody) return i;
        }
        return -1;
    }

    private int FindSymmetricIndex(int index, Dictionary<int, int> map)
    {
        if (map != null && map.TryGetValue(index, out var symmetric)) return symmetric;
        return -1;
    }

    private bool PairExists(int faceIndex, int bodyIndex)
    {
        return _pairCache.Find(faceIndex, bodyIndex, out _) >= 0;
    }

    private bool IsVertexUsed(EditMode mode, int index)
    {
        if (index < 0) return true;
        _pairCache.Refresh();
        for (var i = 0; i < _pairCache.Pairs.Count; i++)
        {
            var pair = _pairCache.Pairs[i];
            if (mode == EditMode.Face && pair.faceIndex == index) return true;
            if (mode == EditMode.Body && pair.bodyIndex == index) return true;
        }
        return false;
    }

    private bool TryGetCachedPair(int pairIndex, out VertexPair pair, out int logIndex)
    {
        return _pairCache.TryGet(pairIndex, out pair, out logIndex);
    }

    private void UpdatePairAtLogIndex(int logIndex, VertexPair pair)
    {
        if (logIndex < 0 || logIndex >= _target.operationLog.Count) return;
        var op = _target.operationLog[logIndex];
        op.pair = pair;
        _target.operationLog[logIndex] = op;
    }
}
#endif
