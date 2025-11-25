#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public partial class MergeToolEditor
{
    public override void OnInspectorGUI()
    {
        if (_target == null) return;
        serializedObject.Update();

        if (_target.isApplied)
        {
            DrawAppliedStateUI();
            serializedObject.ApplyModifiedProperties();
            return;
        }

        DrawConfigurationUI();
        if (!HasMeshesAssigned())
        {
            EditorGUILayout.HelpBox("Assign both Face Mesh and Body Mesh to edit pairs.", MessageType.Warning);
            serializedObject.ApplyModifiedProperties();
            return;
        }

        _pairCache.Refresh();
        DrawToolButtons();
        DrawOperationLogUI();
        DrawClearAllButton();
        DrawApplyButton();

        if (_selection.SelectedPairIndex >= 0) DrawSelectedPairInspector();
        DrawAdvancedSettings();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawAppliedStateUI()
    {
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Meshes are applied. Revert to continue editing.", MessageType.Info);
        var revertColor = GUI.backgroundColor;
        GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
        if (GUILayout.Button("Revert Applied Meshes", GUILayout.Height(30))) RevertAppliedMeshes();
        GUI.backgroundColor = revertColor;
    }

    private void DrawConfigurationUI()
    {
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
            _selection.ResetSelection();
            _editMode = EditMode.None;
        }
    }

    private void DrawToolButtons()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Editing Tools", EditorStyles.boldLabel);
        GUILayout.BeginHorizontal();
        DrawModeButton("Select Vertex Pair", EditMode.Face, new Color(1f, 0.6f, 0.6f));
        DrawModeButton("Split Face Edge", EditMode.FaceEdge, new Color(1f, 0.6f, 0.6f));
        DrawModeButton("Split Body Edge", EditMode.BodyEdge, new Color(1f, 0.6f, 0.6f));
        GUILayout.EndHorizontal();

        if (_selection.SelectedPairIndex >= _pairCache.Pairs.Count) _selection.SelectedPairIndex = -1;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"Total Pairs: {_pairCache.Pairs.Count}");
    }

    private void DrawClearAllButton()
    {
        if (GUILayout.Button("Clear All"))
        {
            if (EditorUtility.DisplayDialog("Clear All", "Remove all pairs and splits?", "Yes", "No"))
            {
                Undo.RecordObject(_target, "Clear All Data");
                _target.operationLog.Clear();
                _selection.ResetSelection();
                MarkTopologyDirty();
                MarkDeformDirty();
                _pairCache.Invalidate();
                _pairCache.Refresh();
                SceneView.RepaintAll();
            }
        }
    }

    private void DrawModeButton(string label, EditMode mode, Color activeColor)
    {
        var inMode = _editMode == mode || (_editMode == EditMode.Body && mode == EditMode.Face);
        var originalColor = GUI.color;
        GUI.color = inMode ? activeColor : Color.white;
        if (GUILayout.Button(inMode ? "Stop" : label, GUILayout.Height(30)))
        {
            if (inMode) _editMode = EditMode.None;
            else _editMode = mode;
            if (_editMode == EditMode.Face) _selection.SelectedFaceIndex = -1;
            _selection.ResetHover();
            SceneView.RepaintAll();
        }
        GUI.color = originalColor;
    }

    private void DrawApplyButton()
    {
        GUILayout.Space(10);
        var applyColor = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.7f, 1f, 0.7f);
        if (GUILayout.Button("Apply", GUILayout.Height(40)))
        {
            if (EditorUtility.DisplayDialog("Apply Changes", "Create new mesh assets and assign them to the renderers.", "Apply", "Cancel"))
            {
                ApplyChangesToScene();
            }
        }
        GUI.backgroundColor = applyColor;
        GUILayout.Space(10);
    }

    private void DrawSelectedPairInspector()
    {
        if (!TryGetCachedPair(_selection.SelectedPairIndex, out var pair, out _)) return;
        var midpoint = GetPairMidpointWorld(pair);
        var worldPosition = midpoint + pair.worldOffset;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Selected Pair", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"Indices: Face {pair.faceIndex} / Body {pair.bodyIndex}");
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.Vector3Field("Merged World Pos", worldPosition);
        }

        EditorGUI.BeginChangeCheck();
        var newOffset = EditorGUILayout.Vector3Field("Offset (World)", pair.worldOffset);
        if (EditorGUI.EndChangeCheck())
        {
            SetPairOffset(_selection.SelectedPairIndex, newOffset);
            MarkDeformDirty();
            SceneView.RepaintAll();
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Zero Offset"))
        {
            SetPairOffset(_selection.SelectedPairIndex, Vector3.zero);
            MarkDeformDirty();
            SceneView.RepaintAll();
        }

        var deleteColor = GUI.backgroundColor;
        GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
        if (GUILayout.Button("Delete Pair")) DeleteSelectedPair();
        GUI.backgroundColor = deleteColor;
        EditorGUILayout.EndHorizontal();
    }

    private void DrawAdvancedSettings()
    {
        EditorGUILayout.Space();
        _showAdvancedSettings = EditorGUILayout.Foldout(_showAdvancedSettings, "Advanced Settings", true, EditorStyles.foldoutHeader);
        if (!_showAdvancedSettings) return;
        EditorGUI.indentLevel++;
        EditorGUI.BeginChangeCheck();
        var toggleSym = EditorGUILayout.Toggle("Enable Symmetry", _target.enableSymmetry);
        var newSmooth = EditorGUILayout.IntSlider("Normal Smooth Depth", _target.normalSmoothDepth, 0, 10);
        var newStrength = EditorGUILayout.Slider("Normal Smooth Strength", _target.normalSmoothStrength, 0f, 1f);
        var newTol = _target.symmetryTolerance;
        using (new EditorGUI.DisabledScope(!toggleSym))
        {
            newTol = EditorGUILayout.Slider("Symmetry Tolerance", _target.symmetryTolerance, 0.00001f, 0.01f);
        }
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(_target, "Change Settings");
            _target.enableSymmetry = toggleSym;
            _target.normalSmoothDepth = newSmooth;
            _target.normalSmoothStrength = newStrength;
            _target.symmetryTolerance = newTol;
            MarkDeformDirty();
        }
        EditorGUI.indentLevel--;
    }
}
#endif
