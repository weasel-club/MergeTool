#if UNITY_EDITOR
using UnityEngine;
using System.Collections.Generic;
using UnityEditor;

public enum EditMode
{
    Face,
    Body,
    FaceEdge,
    BodyEdge,
    None,
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
    private Vector3 _hoveredVertexPosition;
    private int _hoveredVertexIndex = -1;

    private int _hoveredEdgeV1 = -1;
    private int _hoveredEdgeV2 = -1;

    private int _selectedFaceIndex = -1;
    private int _selectedPairIndex = -1;
    private Vector3 _pairHandlePosition;
    private int _pairHandleActiveIndex = -1;
    private bool _pairHandleDragging;
    private bool _showAdvancedSettings;

    private Dictionary<SkinnedMeshRenderer, Mesh> _cachedTopologyMeshes;
    private Dictionary<SkinnedMeshRenderer, List<SplitDependency>> _splitDependencies;
    private Dictionary<SkinnedMeshRenderer, MeshGraph> _meshGraphs;
    private Dictionary<SkinnedMeshRenderer, Mesh> _cachedPreviewMeshes;
    private Dictionary<SkinnedMeshRenderer, Mesh> _cachedResultMeshes;
    private List<VertexPair> _pairBuffer = new List<VertexPair>();
    private List<int> _pairLogIndices = new List<int>();
    private List<int> _triangleBuffer = new List<int>();
    private bool _pairCacheDirty = true;

    private bool _isTopologyDirty = true;
    private bool _isDeformDirty = true;

    private void OnEnable()
    {
        _target = (MergeTool)target;
        _previewMaterial = GetAsset<Material>("PreviewMaterial.mat");
        Undo.undoRedoPerformed += OnUndoRedo;
        MarkTopologyDirty();
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
        ClearAllCaches();
    }

    private void OnUndoRedo()
    {
        MarkTopologyDirty();
        SceneView.RepaintAll();
        Repaint();
    }

    private void MarkTopologyDirty()
    {
        _isTopologyDirty = true;
        _isDeformDirty = true;
        InvalidatePairCache();
    }

    private void MarkDeformDirty()
    {
        _isDeformDirty = true;
    }

    private void ClearAllCaches()
    {
        DestroyMeshDict(_cachedTopologyMeshes);
        DestroyMeshDict(_cachedPreviewMeshes);
        DestroyMeshDict(_cachedResultMeshes);
        _cachedTopologyMeshes = null;
        _cachedPreviewMeshes = null;
        _cachedResultMeshes = null;
        _splitDependencies = null;
        _meshGraphs = null;
        InvalidatePairCache();
    }

    private void DestroyMeshDict(Dictionary<SkinnedMeshRenderer, Mesh> dict)
    {
        if (dict != null)
        {
            foreach (var mesh in dict.Values) if (mesh != null) DestroyImmediate(mesh);
            dict.Clear();
        }
    }

    private void InvalidatePairCache()
    {
        _pairCacheDirty = true;
    }
}
#endif
