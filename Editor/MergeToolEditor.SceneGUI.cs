#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public partial class MergeToolEditor
{
    private void OnSceneGUI()
    {
        if (_target == null || _target.isApplied) return;
        if (!HasMeshesAssigned()) return;

        UpdateMeshesIfDirty();
        _pairCache.Refresh();
        if (_selection.SelectedPairIndex >= _pairCache.Pairs.Count) _selection.SelectedPairIndex = -1;

        var e = Event.current;
        var needsControl = _editMode != EditMode.None;
        if (e.type == EventType.Layout && needsControl) HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            if (TrySelectPairByClick(e))
            {
                _selection.ResetHover();
                _selection.SelectedFaceIndex = -1;
                e.Use();
            }
        }

        if (_editMode != EditMode.None)
        {
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                if (_editMode == EditMode.FaceEdge || _editMode == EditMode.BodyEdge) UpdateHoverEdge();
                else UpdateHoverVertex();
                SceneView.RepaintAll();
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (_editMode == EditMode.FaceEdge || _editMode == EditMode.BodyEdge) HandleEdgeClick();
                else HandleVertexClick();
                e.Use();
            }
        }

        if (Event.current.type == EventType.Repaint)
        {
            DrawDarkOverlay(_backgroundDim);
            DrawMeshPreview();
            DrawHoverMarkers();
        }

        DrawPairMarkers();
        DrawSelectedPairHandle();
    }

    private void HandleVertexClick()
    {
        if (_selection.HoveredVertex < 0) return;

        if (_editMode == EditMode.Face)
        {
            _selection.SelectedFaceIndex = _selection.HoveredVertex;
            _editMode = EditMode.Body;
        }
        else if (_editMode == EditMode.Body && _selection.SelectedFaceIndex >= 0)
        {
            var pair = new VertexPair { faceIndex = _selection.SelectedFaceIndex, bodyIndex = _selection.HoveredVertex };
            AddPairWithSymmetry(pair);
            _selection.SelectedFaceIndex = -1;
            _editMode = EditMode.Face;
        }
        _selection.HoveredVertex = -1;
        MarkDeformDirty();
        SceneView.RepaintAll();
    }

    private void HandleEdgeClick()
    {
        if (_selection.HoveredEdgeV1 < 0 || _selection.HoveredEdgeV2 < 0) return;
        var isFace = _editMode == EditMode.FaceEdge;
        var split = new EdgeSplitData { v1 = _selection.HoveredEdgeV1, v2 = _selection.HoveredEdgeV2 };
        AddSplitWithSymmetry(split, isFace);
        _selection.HoveredEdgeV1 = -1;
        _selection.HoveredEdgeV2 = -1;
        SceneView.RepaintAll();
    }

    private void UpdateHoverVertex()
    {
        var workspace = GetActiveWorkspace();
        if (workspace == null)
        {
            _selection.HoveredVertex = -1;
            return;
        }
        var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        var matrix = MeshSpace.GetRendererToWorldNoScale(workspace.Renderer);
        if (SceneRaycaster.TryHitVertex(workspace.ResultMesh, ray, matrix, _triangleBuffer, out var position, out var index))
        {
            if (!IsVertexUsed(_editMode, index))
            {
                _selection.HoveredVertexPosition = position;
                _selection.HoveredVertex = index;
            }
            else _selection.HoveredVertex = -1;
        }
        else _selection.HoveredVertex = -1;
    }

    private void UpdateHoverEdge()
    {
        var workspace = GetActiveWorkspace();
        if (workspace == null)
        {
            _selection.HoveredEdgeV1 = -1;
            _selection.HoveredEdgeV2 = -1;
            return;
        }
        var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        var matrix = MeshSpace.GetRendererToWorldNoScale(workspace.Renderer);
        if (SceneRaycaster.TryHitEdge(workspace.ResultMesh, ray, matrix, _triangleBuffer, out var v1, out var v2))
        {
            _selection.HoveredEdgeV1 = v1;
            _selection.HoveredEdgeV2 = v2;
        }
        else
        {
            _selection.HoveredEdgeV1 = -1;
            _selection.HoveredEdgeV2 = -1;
        }
    }

    private MeshWorkspace GetActiveWorkspace()
    {
        if (_editMode == EditMode.Face || _editMode == EditMode.FaceEdge) return _faceWorkspace;
        if (_editMode == EditMode.Body || _editMode == EditMode.BodyEdge) return _bodyWorkspace;
        return null;
    }

    private void DrawMeshPreview()
    {
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null || _previewMaterial == null) return;

        var entries = new List<(SkinnedMeshRenderer, Mesh, float, bool)>();
        switch (_editMode)
        {
            case EditMode.None:
                entries.Add((_target.bodyMesh, _bodyWorkspace?.ResultMesh, 1f, true));
                entries.Add((_target.faceMesh, _faceWorkspace?.ResultMesh, 1f, true));
                break;
            case EditMode.Face:
                entries.Add((_target.bodyMesh, _bodyWorkspace?.ResultMesh, 0.5f, false));
                entries.Add((_target.faceMesh, _faceWorkspace?.ResultMesh, 1f, true));
                break;
            case EditMode.Body:
                entries.Add((_target.bodyMesh, _bodyWorkspace?.ResultMesh, 1f, true));
                entries.Add((_target.faceMesh, _faceWorkspace?.ResultMesh, 0.5f, false));
                break;
            case EditMode.FaceEdge:
                entries.Add((_target.bodyMesh, _bodyWorkspace?.ResultMesh, 0.5f, false));
                entries.Add((_target.faceMesh, _faceWorkspace?.ResultMesh, 1f, true));
                break;
            case EditMode.BodyEdge:
                entries.Add((_target.bodyMesh, _bodyWorkspace?.ResultMesh, 1f, true));
                entries.Add((_target.faceMesh, _faceWorkspace?.ResultMesh, 0.5f, false));
                break;
        }

        using var preview = new PreviewRenderer(_previewLayer, _previewMaterial);
        preview.Draw(sceneView, entries.ToArray());
    }

    private void DrawHoverMarkers()
    {
        if (_selection.HoveredVertex >= 0 && (_editMode == EditMode.Face || _editMode == EditMode.Body))
        {
            Handles.color = Color.yellow;
            Handles.SphereHandleCap(0, _selection.HoveredVertexPosition, Quaternion.identity, HandleUtility.GetHandleSize(_selection.HoveredVertexPosition) * _hoverSphereScale, EventType.Repaint);
        }

        if (_selection.HoveredEdgeV1 >= 0 && (_editMode == EditMode.FaceEdge || _editMode == EditMode.BodyEdge))
        {
            var workspace = GetActiveWorkspace();
            if (workspace?.ResultMesh != null)
            {
                var verts = workspace.ResultMesh.vertices;
                var mtx = MeshSpace.GetRendererToWorldNoScale(workspace.Renderer);
                if (_selection.HoveredEdgeV1 < verts.Length && _selection.HoveredEdgeV2 < verts.Length)
                {
                    var p1 = mtx.MultiplyPoint3x4(verts[_selection.HoveredEdgeV1]);
                    var p2 = mtx.MultiplyPoint3x4(verts[_selection.HoveredEdgeV2]);
                    Handles.color = Color.cyan;
                    Handles.DrawLine(p1, p2, 4.0f);
                    Handles.SphereHandleCap(0, (p1 + p2) * 0.5f, Quaternion.identity, HandleUtility.GetHandleSize(p1) * 0.1f, EventType.Repaint);
                }
            }
        }
    }

    private void DrawPairMarkers()
    {
        if (Event.current.type != EventType.Repaint) return;
        if (_target == null) return;

        for (var i = 0; i < _pairCache.Pairs.Count; i++)
        {
            var pair = _pairCache.Pairs[i];
            if (!TryGetPairWorldPosition(pair, out var world)) continue;
            var size = HandleUtility.GetHandleSize(world) * _pairHandleScale;
            Handles.color = i == _selection.SelectedPairIndex ? Color.magenta : Color.red;
            Handles.SphereHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);
        }
    }

    private void DrawSelectedPairHandle()
    {
        if (_selection.SelectedPairIndex < 0 || _selection.SelectedPairIndex >= _pairCache.Pairs.Count)
        {
            _selection.HandleDragging = false;
            _selection.ActiveHandleIndex = -1;
            return;
        }

        var pair = _pairCache.Pairs[_selection.SelectedPairIndex];
        if (!TryGetPairWorldPosition(pair, out var world))
        {
            _selection.HandleDragging = false;
            _selection.ActiveHandleIndex = -1;
            return;
        }

        if (_selection.ActiveHandleIndex != _selection.SelectedPairIndex)
        {
            _selection.HandleDragging = false;
            _selection.ActiveHandleIndex = _selection.SelectedPairIndex;
        }

        if (!_selection.HandleDragging) _selection.HandlePosition = world;

        EditorGUI.BeginChangeCheck();
        var newPosition = Handles.PositionHandle(_selection.HandlePosition, Quaternion.identity);
        if (EditorGUI.EndChangeCheck())
        {
            _selection.HandlePosition = newPosition;
            _selection.HandleDragging = true;
        }

        if (_selection.HandleDragging && GUIUtility.hotControl == 0)
        {
            SetPairOffsetFromWorld(_selection.SelectedPairIndex, _selection.HandlePosition);
            _selection.HandleDragging = false;
            _selection.HandlePosition = world;
            MarkDeformDirty();
            SceneView.RepaintAll();
        }
    }

    private bool TrySelectPairByClick(Event e)
    {
        var bestIndex = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < _pairCache.Pairs.Count; i++)
        {
            var pair = _pairCache.Pairs[i];
            if (!TryGetPairWorldPosition(pair, out var world)) continue;
            var distance = Vector2.Distance(HandleUtility.WorldToGUIPoint(world), e.mousePosition);
            if (distance < _pairSelectionScreenRadius && distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            _selection.SelectedPairIndex = bestIndex;
            _editMode = EditMode.None;
            SceneView.RepaintAll();
            return true;
        }

        return false;
    }

    private void DrawDarkOverlay(float alpha)
    {
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null) return;

        Handles.BeginGUI();
        EditorGUI.DrawRect(new Rect(0, 0, sceneView.position.width, sceneView.position.height), new Color(0, 0, 0, alpha));
        Handles.EndGUI();
    }

    private bool TryGetPairWorldPosition(VertexPair pair, out Vector3 world)
    {
        world = Vector3.zero;
        if (_faceWorkspace?.ResultMesh == null || _bodyWorkspace?.ResultMesh == null) return false;
        var fv = _faceWorkspace.ResultMesh.vertices;
        var bv = _bodyWorkspace.ResultMesh.vertices;
        if (pair.faceIndex < 0 || pair.faceIndex >= fv.Length) return false;
        if (pair.bodyIndex < 0 || pair.bodyIndex >= bv.Length) return false;
        var faceWorld = MeshSpace.GetRendererToWorldNoScale(_target.faceMesh).MultiplyPoint3x4(fv[pair.faceIndex]);
        var bodyWorld = MeshSpace.GetRendererToWorldNoScale(_target.bodyMesh).MultiplyPoint3x4(bv[pair.bodyIndex]);
        world = (faceWorld + bodyWorld) * 0.5f;
        return true;
    }

    private Vector3 GetPairMidpointWorld(VertexPair pair)
    {
        if (!TryGetPairWorldPosition(pair, out var world)) return Vector3.zero;
        return world;
    }
}
#endif
