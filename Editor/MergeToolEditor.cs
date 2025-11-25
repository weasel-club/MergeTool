#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public enum EditMode
{
    Face,
    Body,
    FaceEdge,
    BodyEdge,
    None
}

[CustomEditor(typeof(MergeTool))]
public partial class MergeToolEditor : Editor
{
    private MergeTool _target;

    private const float _backgroundDim = 0.85f;
    private const float _hoverSphereScale = 0.2f;
    private const int _previewLayer = 30;
    private const float _pairHandleScale = 0.08f;
    private const float _pairSelectionScreenRadius = 18f;

    private Material _previewMaterial;
    private EditMode _editMode = EditMode.None;
    private bool _showAdvancedSettings;

    private MeshWorkspace _faceWorkspace;
    private MeshWorkspace _bodyWorkspace;
    private readonly PairCache _pairCache = new PairCache();
    private readonly SelectionState _selection = new SelectionState();
    private readonly List<int> _triangleBuffer = new List<int>();

    private bool _topologyDirty = true;
    private bool _deformDirty = true;

    private void OnEnable()
    {
        _target = (MergeTool)target;
        _pairCache.Bind(_target);
        _previewMaterial = GetAsset<Material>("PreviewMaterial.mat");
        Undo.undoRedoPerformed += OnUndoRedo;
        MarkTopologyDirty();
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
        ClearWorkspaces();
    }

    private void OnUndoRedo()
    {
        MarkTopologyDirty();
        SceneView.RepaintAll();
        Repaint();
    }

    private void MarkTopologyDirty()
    {
        _topologyDirty = true;
        _deformDirty = true;
        _pairCache.Invalidate();
    }

    private void MarkDeformDirty()
    {
        _deformDirty = true;
    }

    private void ClearWorkspaces()
    {
        _faceWorkspace = null;
        _bodyWorkspace = null;
    }

    private bool HasMeshesAssigned()
    {
        return _target != null && _target.faceMesh != null && _target.bodyMesh != null;
    }
}

internal class PairCache
{
    private MergeTool _target;
    private bool _dirty = true;
    private readonly List<VertexPair> _pairs = new List<VertexPair>();
    private readonly List<int> _logIndices = new List<int>();

    public IReadOnlyList<VertexPair> Pairs => _pairs;
    public IReadOnlyList<int> LogIndices => _logIndices;

    public void Bind(MergeTool target) { _target = target; Invalidate(); }
    public void Invalidate() { _dirty = true; }

    public void Refresh()
    {
        if (!_dirty || _target == null) return;
        _pairs.Clear();
        _logIndices.Clear();
        if (_target.operationLog != null)
        {
            for (var i = 0; i < _target.operationLog.Count; i++)
            {
                var op = _target.operationLog[i];
                if (op.kind != OperationKind.Pair) continue;
                _pairs.Add(op.pair);
                _logIndices.Add(i);
            }
        }
        _dirty = false;
    }

    public bool TryGet(int pairIndex, out VertexPair pair, out int logIndex)
    {
        pair = default;
        logIndex = -1;
        Refresh();
        if (pairIndex < 0 || pairIndex >= _pairs.Count) return false;
        pair = _pairs[pairIndex];
        logIndex = _logIndices[pairIndex];
        return true;
    }

    public int Find(int faceIndex, int bodyIndex, out int logIndex)
    {
        logIndex = -1;
        Refresh();
        for (var i = 0; i < _pairs.Count; i++)
        {
            var pair = _pairs[i];
            if (pair.faceIndex == faceIndex && pair.bodyIndex == bodyIndex)
            {
                logIndex = _logIndices[i];
                return i;
            }
        }
        return -1;
    }

    public int FindByLogIndex(int logIndex)
    {
        Refresh();
        for (var i = 0; i < _logIndices.Count; i++)
        {
            if (_logIndices[i] == logIndex) return i;
        }
        return -1;
    }
}

internal class SelectionState
{
    public int HoveredVertex = -1;
    public Vector3 HoveredVertexPosition;
    public int HoveredEdgeV1 = -1;
    public int HoveredEdgeV2 = -1;
    public int SelectedFaceIndex = -1;
    public int SelectedPairIndex = -1;
    public int ActiveHandleIndex = -1;
    public bool HandleDragging;
    public Vector3 HandlePosition;

    public void ResetHover()
    {
        HoveredVertex = -1;
        HoveredEdgeV1 = -1;
        HoveredEdgeV2 = -1;
    }

    public void ResetSelection()
    {
        SelectedPairIndex = -1;
        SelectedFaceIndex = -1;
        ActiveHandleIndex = -1;
        HandleDragging = false;
    }
}
#endif
