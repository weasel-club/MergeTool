#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public partial class MergeToolEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (_target == null) return;

        serializedObject.Update();

        if (_target.isApplied)
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Meshes are applied. Revert to continue editing.", MessageType.Info);
            var revertColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
            if (GUILayout.Button("Revert Applied Meshes", GUILayout.Height(30)))
            {
                RevertAppliedMeshes();
            }
            GUI.backgroundColor = revertColor;
            serializedObject.ApplyModifiedProperties();
            return;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        var newFace = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Face Mesh", _target.faceMesh, typeof(SkinnedMeshRenderer), true);
        var newBody = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Body Mesh", _target.bodyMesh, typeof(SkinnedMeshRenderer), true);

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(_target, "Assign Meshes");
            _target.faceMesh = newFace;
            _target.bodyMesh = newBody;
            MarkTopologyDirty();
        }

        if (_target.faceMesh == null || _target.bodyMesh == null)
        {
            EditorGUILayout.HelpBox("Assign both Face Mesh and Body Mesh to edit pairs.", MessageType.Warning);
            return;
        }

        RefreshPairCache();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Editing Tools", EditorStyles.boldLabel);

        GUILayout.BeginHorizontal();
        var originalColor = GUI.color;

        var inEditVertexPair = _editMode == EditMode.Face || _editMode == EditMode.Body;
        GUI.color = inEditVertexPair ? new Color(1f, 0.6f, 0.6f) : Color.white;
        if (GUILayout.Button(inEditVertexPair ? "Stop" : "Start Select Vertex Pair", GUILayout.Height(30)))
        {
            if (inEditVertexPair)
                _editMode = EditMode.None;
            else
                _editMode = EditMode.Face;

            _hoveredVertexIndex = -1;
            _selectedFaceIndex = -1;
            _hoveredEdgeV1 = -1;
            SceneView.RepaintAll();
        }
        GUI.color = originalColor;

        var inEditSplitFaceEdge = _editMode == EditMode.FaceEdge;
        GUI.color = inEditSplitFaceEdge ? new Color(1f, 0.6f, 0.6f) : Color.white;
        if (GUILayout.Button(inEditSplitFaceEdge ? "Stop" : "Split Face Edge", GUILayout.Height(30)))
        {
            _editMode = inEditSplitFaceEdge ? EditMode.None : EditMode.FaceEdge;
            _hoveredVertexIndex = -1;
            _hoveredEdgeV1 = -1;
            SceneView.RepaintAll();
        }
        GUI.color = originalColor;

        var inEditSplitBodyEdge = _editMode == EditMode.BodyEdge;
        GUI.color = inEditSplitBodyEdge ? new Color(1f, 0.6f, 0.6f) : Color.white;
        if (GUILayout.Button(inEditSplitBodyEdge ? "Stop" : "Split Body Edge", GUILayout.Height(30)))
        {
            _editMode = inEditSplitBodyEdge ? EditMode.None : EditMode.BodyEdge;
            _hoveredVertexIndex = -1;
            _hoveredEdgeV1 = -1;
            SceneView.RepaintAll();
        }
        GUI.color = originalColor;

        GUILayout.EndHorizontal();

        if (_selectedPairIndex >= _pairBuffer.Count) _selectedPairIndex = -1;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"Total Pairs: {_pairBuffer.Count}");

        DrawOperationLogUI();

        if (GUILayout.Button("Clear All"))
        {
            if (EditorUtility.DisplayDialog("Clear All", "Are you sure you want to clear all data?", "Yes", "No"))
            {
                Undo.RecordObject(_target, "Clear All Data");
                _target.operationLog.Clear();
                _selectedPairIndex = -1;
                MarkTopologyDirty();
                MarkDeformDirty();
                InvalidatePairCache();
                RefreshPairCache();
                SceneView.RepaintAll();
            }
        }

        GUILayout.Space(10);
        var applyColor = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.7f, 1f, 0.7f);
        if (GUILayout.Button("Apply", GUILayout.Height(40)))
        {
            if (EditorUtility.DisplayDialog("Apply Changes", "This will create new mesh assets and assign them to the renderers. Continue?", "Yes", "Cancel"))
            {
                ApplyChangesToScene();
            }
        }
        GUI.backgroundColor = applyColor;
        GUILayout.Space(10);

        if (_selectedPairIndex >= 0 && _selectedPairIndex < _pairBuffer.Count)
        {
            DrawSelectedPairInspector();
        }

        EditorGUILayout.Space();
        _showAdvancedSettings = EditorGUILayout.Foldout(_showAdvancedSettings, "Advanced Settings", true, EditorStyles.foldoutHeader);
        if (_showAdvancedSettings)
        {
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            var toggleSym = EditorGUILayout.Toggle("Enable Symmetry", _target.enableSymmetry);
            var newTol = _target.symmetryTolerance;
            var newSmooth = EditorGUILayout.IntSlider("Normal Smooth Depth", _target.normalSmoothDepth, 0, 10);
            var newStrength = EditorGUILayout.Slider("Normal Smooth Strength", _target.normalSmoothStrength, 0f, 1f);

            using (new EditorGUI.DisabledScope(!toggleSym))
            {
                newTol = EditorGUILayout.Slider("Symmetry Tolerance", _target.symmetryTolerance, 0.00001f, 0.01f);
            }

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_target, "Change Settings");
                _target.enableSymmetry = toggleSym;
                _target.symmetryTolerance = newTol;
                _target.normalSmoothDepth = newSmooth;
                _target.normalSmoothStrength = newStrength;
                MarkDeformDirty();
            }
            EditorGUI.indentLevel--;
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawSelectedPairInspector()
    {
        EditorGUILayout.Space();
        if (!TryGetCachedPair(_selectedPairIndex, out var pair, out _)) return;
        var midpoint = GetPairMidpointWorld(pair);
        var worldPosition = midpoint + pair.worldOffset;

        EditorGUILayout.LabelField("Selected Pair Details", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"Indices: Face {pair.faceIndex} / Body {pair.bodyIndex}");

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.Vector3Field("Merged World Pos", worldPosition);
        }

        EditorGUI.BeginChangeCheck();
        var newOffset = EditorGUILayout.Vector3Field("Offset (World)", pair.worldOffset);
        if (EditorGUI.EndChangeCheck())
        {
            SetPairOffset(_selectedPairIndex, newOffset);
            MarkDeformDirty();
            SceneView.RepaintAll();
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Zero Offset"))
        {
            SetPairOffset(_selectedPairIndex, Vector3.zero);
            MarkDeformDirty();
            SceneView.RepaintAll();
        }

        var deleteColor = GUI.backgroundColor;
        GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
        if (GUILayout.Button("Delete Pair"))
        {
            DeleteSelectedPair();
        }
        GUI.backgroundColor = deleteColor;
        EditorGUILayout.EndHorizontal();
    }

    private void DeleteSelectedPair()
    {
        RefreshPairCache();
        if (_selectedPairIndex < 0 || _selectedPairIndex >= _pairBuffer.Count) return;

        Undo.RecordObject(_target, "Delete Pair");

        var removeLogIndices = new List<int>();
        if (_pairLogIndices.Count > _selectedPairIndex) removeLogIndices.Add(_pairLogIndices[_selectedPairIndex]);
        var symmetricIndex = _target.enableSymmetry ? FindSymmetricPairIndex(_selectedPairIndex) : -1;
        if (symmetricIndex == _selectedPairIndex) symmetricIndex = -1;
        if (symmetricIndex >= 0 && symmetricIndex < _pairLogIndices.Count) removeLogIndices.Add(_pairLogIndices[symmetricIndex]);
        removeLogIndices.Sort();
        for (var i = removeLogIndices.Count - 1; i >= 0; i--)
        {
            var logIndex = removeLogIndices[i];
            if (logIndex >= 0 && logIndex < _target.operationLog.Count) _target.operationLog.RemoveAt(logIndex);
        }

        _selectedPairIndex = -1;
        InvalidatePairCache();
        RefreshPairCache();
        MarkDeformDirty();
        SceneView.RepaintAll();
    }

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
            var label = GetOperationLabel(op);
            EditorGUILayout.LabelField($"{i + 1}. {label}");
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
        if (op.kind == OperationKind.FaceSplit) return $"Face Split {op.split.v1} - {op.split.v2}";
        if (op.kind == OperationKind.BodySplit) return $"Body Split {op.split.v1} - {op.split.v2}";
        if (op.kind == OperationKind.Pair) return $"Pair Face {op.pair.faceIndex} / Body {op.pair.bodyIndex}";
        return "Unknown";
    }

    private void RevertToOperationIndex(int logIndex)
    {
        if (_target == null || _target.operationLog == null) return;
        if (logIndex < 0 || logIndex >= _target.operationLog.Count) return;
        Undo.RecordObject(_target, "Revert Operation Log");
        var removeCount = _target.operationLog.Count - logIndex - 1;
        if (removeCount > 0) _target.operationLog.RemoveRange(logIndex + 1, removeCount);
        _selectedPairIndex = -1;
        _editMode = EditMode.None;
        _hoveredVertexIndex = -1;
        _selectedFaceIndex = -1;
        _hoveredEdgeV1 = -1;
        _hoveredEdgeV2 = -1;
        MarkTopologyDirty();
        MarkDeformDirty();
        InvalidatePairCache();
        RefreshPairCache();
        SceneView.RepaintAll();
        Repaint();
    }

    private void SelectPairByLogIndex(int logIndex)
    {
        var pairIndex = GetPairIndexByLogIndex(logIndex);
        if (pairIndex < 0) return;
        _selectedPairIndex = pairIndex;
        _editMode = EditMode.None;
        _hoveredVertexIndex = -1;
        _selectedFaceIndex = -1;
        _hoveredEdgeV1 = -1;
        _hoveredEdgeV2 = -1;
        SceneView.RepaintAll();
        Repaint();
    }

    private void RevertAppliedMeshes()
    {
        if (_target == null) return;
        var faceRenderer = _target.faceMesh;
        var bodyRenderer = _target.bodyMesh;
        var hasStored = _target.appliedFaceMeshBefore != null || _target.appliedBodyMeshBefore != null;
        if (!hasStored)
        {
            EditorUtility.DisplayDialog("Revert Failed", "No stored meshes to revert.", "OK");
            return;
        }
        var changed = false;
        if (faceRenderer != null && _target.appliedFaceMeshAfter != null && faceRenderer.sharedMesh != _target.appliedFaceMeshAfter) changed = true;
        if (bodyRenderer != null && _target.appliedBodyMeshAfter != null && bodyRenderer.sharedMesh != _target.appliedBodyMeshAfter) changed = true;
        if (changed)
        {
            var proceed = EditorUtility.DisplayDialog("Mesh Changed", "Current mesh differs from applied mesh. Continue revert?", "Continue", "Cancel");
            if (!proceed) return;
        }
        Undo.RecordObject(_target, "Revert Applied Meshes");
        if (faceRenderer != null && _target.appliedFaceMeshBefore != null)
        {
            Undo.RecordObject(faceRenderer, "Revert Applied Meshes");
            faceRenderer.sharedMesh = _target.appliedFaceMeshBefore;
            EditorUtility.SetDirty(faceRenderer);
        }
        if (bodyRenderer != null && _target.appliedBodyMeshBefore != null)
        {
            Undo.RecordObject(bodyRenderer, "Revert Applied Meshes");
            bodyRenderer.sharedMesh = _target.appliedBodyMeshBefore;
            EditorUtility.SetDirty(bodyRenderer);
        }
        _target.appliedFaceMeshAfter = null;
        _target.appliedBodyMeshAfter = null;
        _target.appliedFaceMeshBefore = null;
        _target.appliedBodyMeshBefore = null;
        _target.isApplied = false;
        EditorUtility.SetDirty(_target);
        MarkTopologyDirty();
        SceneView.RepaintAll();
    }
}
#endif
