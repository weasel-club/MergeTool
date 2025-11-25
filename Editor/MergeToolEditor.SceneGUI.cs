#if UNITY_EDITOR
using UnityEngine;
using System.Collections.Generic;
using UnityEditor;

public partial class MergeToolEditor : Editor
{
    private void OnSceneGUI()
    {
        if (_target == null) return;
        if (_target.bodyMesh == null || _target.faceMesh == null) return;
        if (_target.isApplied) return;

        UpdateMeshesIfDirty();
        RefreshPairCache();
        if (_selectedPairIndex >= _pairBuffer.Count) _selectedPairIndex = -1;

        var e = Event.current;
        var needsControl = _editMode != EditMode.None;
        if (e.type == EventType.Layout && needsControl)
        {
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
        }

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            if (TrySelectPairByClick(e, _cachedPreviewMeshes))
            {
                _hoveredVertexIndex = -1;
                _selectedFaceIndex = -1;
                e.Use();
            }
        }

        if (_editMode != EditMode.None)
        {
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                if (_editMode == EditMode.FaceEdge || _editMode == EditMode.BodyEdge)
                {
                    UpdateHoverEdge(_cachedPreviewMeshes);
                }
                else
                {
                    UpdateHoverVertex(_cachedPreviewMeshes);
                }
                SceneView.RepaintAll();
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (_editMode == EditMode.FaceEdge || _editMode == EditMode.BodyEdge)
                {
                    HandleEdgeClick();
                }
                else
                {
                    HandleVertexClick();
                }
                e.Use();
            }
        }

        if (Event.current.type == EventType.Repaint)
        {
            DrawDarkOverlay(_backgroundDim);

            switch (_editMode)
            {
                case EditMode.None:
                    DrawMeshPreview(_cachedPreviewMeshes, (_target.bodyMesh, 1.0f, true), (_target.faceMesh, 1.0f, true));
                    break;
                case EditMode.Face:
                    DrawMeshPreview(_cachedPreviewMeshes, (_target.bodyMesh, 0.5f, false), (_target.faceMesh, 1.0f, true));
                    break;
                case EditMode.Body:
                    DrawMeshPreview(_cachedPreviewMeshes, (_target.bodyMesh, 1.0f, true), (_target.faceMesh, 0.5f, false));
                    break;
                case EditMode.FaceEdge:
                    DrawMeshPreview(_cachedPreviewMeshes, (_target.bodyMesh, 0.5f, false), (_target.faceMesh, 1.0f, true));
                    break;
                case EditMode.BodyEdge:
                    DrawMeshPreview(_cachedPreviewMeshes, (_target.bodyMesh, 1.0f, true), (_target.faceMesh, 0.5f, false));
                    break;
            }

            if (_hoveredVertexIndex >= 0 && (_editMode == EditMode.Face || _editMode == EditMode.Body))
            {
                Handles.color = Color.yellow;
                Handles.SphereHandleCap(0, _hoveredVertexPosition, Quaternion.identity, HandleUtility.GetHandleSize(_hoveredVertexPosition) * _hoverSphereScale, EventType.Repaint);
            }

            if (_hoveredEdgeV1 >= 0 && (_editMode == EditMode.FaceEdge || _editMode == EditMode.BodyEdge))
            {
                var activeRenderer = GetActiveRenderer();
                if (activeRenderer != null && _cachedPreviewMeshes.TryGetValue(activeRenderer, out var mesh))
                {
                    var verts = mesh.vertices;
                    var mtx = GetRendererToWorldMatrixNoScale(activeRenderer);
                    if (_hoveredEdgeV1 < verts.Length && _hoveredEdgeV2 < verts.Length)
                    {
                        var p1 = mtx.MultiplyPoint3x4(verts[_hoveredEdgeV1]);
                        var p2 = mtx.MultiplyPoint3x4(verts[_hoveredEdgeV2]);
                        Handles.color = Color.cyan;
                        Handles.DrawLine(p1, p2, 4.0f);
                        Handles.SphereHandleCap(0, (p1 + p2) * 0.5f, Quaternion.identity, HandleUtility.GetHandleSize(p1) * 0.1f, EventType.Repaint);
                    }
                }
            }
        }

        DrawPairMarkers(_cachedPreviewMeshes);
        DrawSelectedPairHandle();
    }

    private void DrawPairMarkers(Dictionary<SkinnedMeshRenderer, Mesh> bakedMeshes)
    {
        if (Event.current.type != EventType.Repaint) return;
        if (_target == null) return;

        for (var i = 0; i < _pairBuffer.Count; i++)
        {
            var pair = _pairBuffer[i];
            if (!TryGetPairWorldPosition(pair, bakedMeshes, out var world)) continue;
            var size = HandleUtility.GetHandleSize(world) * _pairHandleScale;
            Handles.color = i == _selectedPairIndex ? Color.magenta : Color.red;
            Handles.SphereHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);
        }
    }
    private void DrawSelectedPairHandle()
    {
        if (!HasValidSelectedPair())
        {
            ResetPairHandleState();
            return;
        }

        if (_cachedPreviewMeshes == null) return;

        var pair = _pairBuffer[_selectedPairIndex];
        if (!TryGetPairWorldPosition(pair, _cachedPreviewMeshes, out var world))
        {
            ResetPairHandleState();
            return;
        }

        if (_pairHandleActiveIndex != _selectedPairIndex)
        {
            _pairHandleDragging = false;
            _pairHandleActiveIndex = _selectedPairIndex;
        }

        if (!_pairHandleDragging)
        {
            _pairHandlePosition = world;
        }

        EditorGUI.BeginChangeCheck();
        var newPosition = Handles.PositionHandle(_pairHandlePosition, Quaternion.identity);
        if (EditorGUI.EndChangeCheck())
        {
            _pairHandlePosition = newPosition;
            _pairHandleDragging = true;
        }

        if (_pairHandleDragging && GUIUtility.hotControl == 0)
        {
            SetPairOffsetFromWorld(_selectedPairIndex, _pairHandlePosition);

            _pairHandleDragging = false;
            _pairHandlePosition = world;

            MarkDeformDirty();
            SceneView.RepaintAll();
        }
    }

    private bool HasValidSelectedPair()
    {
        if (_target == null) return false;
        return _selectedPairIndex >= 0 && _selectedPairIndex < _pairBuffer.Count;
    }

    private void ResetPairHandleState()
    {
        _pairHandleDragging = false;
        _pairHandleActiveIndex = -1;
    }

    private bool TrySelectPairByClick(Event e, Dictionary<SkinnedMeshRenderer, Mesh> bakedMeshes)
    {
        if (_target == null) return false;

        var bestIndex = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < _pairBuffer.Count; i++)
        {
            var pair = _pairBuffer[i];
            if (!TryGetPairWorldPosition(pair, bakedMeshes, out var world)) continue;
            var distance = Vector2.Distance(HandleUtility.WorldToGUIPoint(world), e.mousePosition);
            if (distance < _pairSelectionScreenRadius && distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            _selectedPairIndex = bestIndex;
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

    private void DrawMeshPreview(Dictionary<SkinnedMeshRenderer, Mesh> bakedMeshes, params (SkinnedMeshRenderer renderer, float alpha, bool wireframe)[] renderers)
    {
        if (renderers == null || renderers.Length == 0) return;
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null || sceneView.camera == null) return;
        if (bakedMeshes == null) return;

        var cloneGOs = new List<GameObject>();
        var previewMaterials = new List<Material>();
        var tempCamGO = default(GameObject);
        var rt = default(RenderTexture);

        try
        {
            tempCamGO = new GameObject("TempPreviewCamera");
            var tempCam = tempCamGO.AddComponent<Camera>();
            tempCam.CopyFrom(sceneView.camera);
            tempCam.enabled = false;
            tempCam.clearFlags = CameraClearFlags.SolidColor;
            tempCam.backgroundColor = Color.clear;
            tempCam.cullingMask = 1 << _previewLayer;

            var pixelWidth = sceneView.camera.pixelWidth;
            var pixelHeight = sceneView.camera.pixelHeight;

            if (pixelWidth <= 0 || pixelHeight <= 0) return;

            rt = RenderTexture.GetTemporary(pixelWidth, pixelHeight, 24, RenderTextureFormat.ARGB32);
            rt.name = "TempPreviewRT";
            tempCam.targetTexture = rt;

            var light = tempCamGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            light.transform.rotation = sceneView.camera.transform.rotation;

            foreach (var tuple in renderers)
            {
                var renderer = tuple.renderer;
                if (renderer == null || !bakedMeshes.TryGetValue(renderer, out var bakedMesh)) continue;

                var cloneGO = new GameObject("TempPreviewGameObject_" + renderer.name);
                cloneGOs.Add(cloneGO);
                cloneGO.transform.SetPositionAndRotation(renderer.transform.position, renderer.transform.rotation);
                cloneGO.transform.localScale = Vector3.one;

                SetLayerRecursively(cloneGO, _previewLayer);

                var mf = cloneGO.AddComponent<MeshFilter>();
                mf.sharedMesh = bakedMesh;
                var mr = cloneGO.AddComponent<MeshRenderer>();

                var previewMatInstance = new Material(_previewMaterial);
                mr.materials = new Material[] { previewMatInstance };
                previewMaterials.Add(previewMatInstance);

                ConfigurePreviewMaterial(previewMatInstance, tuple.alpha, tuple.wireframe);
            }

            tempCam.Render();

            var prevMask = sceneView.camera.cullingMask;
            sceneView.camera.cullingMask = 1 << _previewLayer;
            try
            {
                Handles.BeginGUI();

                float ppp = EditorGUIUtility.pixelsPerPoint;
                float logicalWidth = pixelWidth / ppp;
                float logicalHeight = pixelHeight / ppp;

                GUI.DrawTexture(
                    new Rect(0, 0, logicalWidth, logicalHeight),
                    rt,
                    ScaleMode.StretchToFill,
                    true
                );

                Handles.EndGUI();
            }
            finally
            {
                sceneView.camera.cullingMask = prevMask;
            }
        }
        finally
        {
            if (tempCamGO != null) DestroyImmediate(tempCamGO);
            foreach (var go in cloneGOs) if (go != null) DestroyImmediate(go);
            foreach (var mat in previewMaterials) if (mat != null) DestroyImmediate(mat);
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }
    }

    private void ConfigurePreviewMaterial(Material material, float alpha, bool showWireframe)
    {
        if (material == null) return;
        var isTransparent = alpha < 1.0f;
        material.SetOverrideTag("RenderType", isTransparent ? "Transparent" : "Opaque");
        material.renderQueue = isTransparent ? 3000 : 2001;
        material.SetInt("_SrcBlend", isTransparent ? (int)UnityEngine.Rendering.BlendMode.SrcAlpha : (int)UnityEngine.Rendering.BlendMode.One);
        material.SetInt("_DstBlend", isTransparent ? (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : (int)UnityEngine.Rendering.BlendMode.Zero);
        material.SetInt("_ZWrite", 1);
        var baseColor = material.GetColor("_BaseColor");
        baseColor.a = alpha;
        material.SetColor("_BaseColor", baseColor);
        var lineColor = material.GetColor("_LineColor");
        lineColor.a = 1f;
        material.SetColor("_LineColor", lineColor);
        var lineWidth = _previewMaterial != null ? _previewMaterial.GetFloat("_LineWidth") : 1f;
        if (lineWidth < 0.01f) lineWidth = 0.01f;
        material.SetFloat("_LineWidth", lineWidth);
        material.SetInt("_ShowWireframe", showWireframe ? 1 : 0);
    }

    private void SetLayerRecursively(GameObject obj, int newLayer)
    {
        obj.layer = newLayer;
        foreach (Transform child in obj.transform) SetLayerRecursively(child.gameObject, newLayer);
    }

    private void UpdateHoverVertex(Dictionary<SkinnedMeshRenderer, Mesh> bakedMeshes)
    {
        var renderer = GetActiveRenderer();
        if (renderer == null)
        {
            _hoveredVertexIndex = -1;
            return;
        }
        var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        if (TryRaycastVertex(renderer, bakedMeshes, ray, out var position, out var index))
        {
            if (!IsVertexUsed(_editMode, index))
            {
                _hoveredVertexPosition = position;
                _hoveredVertexIndex = index;
            }
            else _hoveredVertexIndex = -1;
        }
        else _hoveredVertexIndex = -1;
    }

    private void UpdateHoverEdge(Dictionary<SkinnedMeshRenderer, Mesh> bakedMeshes)
    {
        var renderer = GetActiveRenderer();
        if (renderer == null)
        {
            _hoveredEdgeV1 = -1;
            return;
        }
        var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        if (TryRaycastEdge(renderer, bakedMeshes, ray, out var v1, out var v2))
        {
            _hoveredEdgeV1 = v1;
            _hoveredEdgeV2 = v2;
        }
        else
        {
            _hoveredEdgeV1 = -1;
            _hoveredEdgeV2 = -1;
        }
    }

    private SkinnedMeshRenderer GetActiveRenderer()
    {
        if (_editMode == EditMode.Face || _editMode == EditMode.FaceEdge) return _target.faceMesh;
        if (_editMode == EditMode.Body || _editMode == EditMode.BodyEdge) return _target.bodyMesh;
        return null;
    }

    private bool TryRaycastVertex(SkinnedMeshRenderer renderer, Dictionary<SkinnedMeshRenderer, Mesh> bakedMeshes, Ray ray, out Vector3 vertexWorld, out int vertexIndex)
    {
        vertexWorld = Vector3.zero;
        vertexIndex = -1;
        if (bakedMeshes == null || !bakedMeshes.TryGetValue(renderer, out var bakedMesh)) return false;

        var vertices = bakedMesh.vertices;
        CollectTriangles(bakedMesh, _triangleBuffer);
        var matrix = GetRendererToWorldMatrixNoScale(renderer);
        var hasHit = false;
        var hitDistance = float.MaxValue;
        var hitPoint = Vector3.zero;

        for (var i = 0; i < _triangleBuffer.Count; i += 3)
        {
            var v0 = matrix.MultiplyPoint3x4(vertices[_triangleBuffer[i]]);
            var v1 = matrix.MultiplyPoint3x4(vertices[_triangleBuffer[i + 1]]);
            var v2 = matrix.MultiplyPoint3x4(vertices[_triangleBuffer[i + 2]]);
            if (RayTriangle(ray, v0, v1, v2, out var distance, out var point))
            {
                if (distance < hitDistance)
                {
                    hitDistance = distance;
                    hitPoint = point;
                    hasHit = true;
                }
            }
        }

        if (hasHit)
        {
            var bestDistance = float.MaxValue;
            for (var i = 0; i < vertices.Length; i++)
            {
                var worldPos = matrix.MultiplyPoint3x4(vertices[i]);
                var d = (worldPos - hitPoint).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    vertexWorld = worldPos;
                    vertexIndex = i;
                }
            }
        }
        return hasHit && vertexIndex >= 0;
    }

    private bool TryRaycastEdge(SkinnedMeshRenderer renderer, Dictionary<SkinnedMeshRenderer, Mesh> bakedMeshes, Ray ray, out int v1, out int v2)
    {
        v1 = -1;
        v2 = -1;
        if (bakedMeshes == null || !bakedMeshes.TryGetValue(renderer, out var bakedMesh)) return false;

        var vertices = bakedMesh.vertices;
        CollectTriangles(bakedMesh, _triangleBuffer);
        var matrix = GetRendererToWorldMatrixNoScale(renderer);
        var hasHit = false;
        var hitDistance = float.MaxValue;
        var hitPoint = Vector3.zero;
        var hitTriIdx = -1;

        for (var i = 0; i < _triangleBuffer.Count; i += 3)
        {
            var tv0 = matrix.MultiplyPoint3x4(vertices[_triangleBuffer[i]]);
            var tv1 = matrix.MultiplyPoint3x4(vertices[_triangleBuffer[i + 1]]);
            var tv2 = matrix.MultiplyPoint3x4(vertices[_triangleBuffer[i + 2]]);
            if (RayTriangle(ray, tv0, tv1, tv2, out var distance, out var point))
            {
                if (distance < hitDistance)
                {
                    hitDistance = distance;
                    hitPoint = point;
                    hasHit = true;
                    hitTriIdx = i;
                }
            }
        }

        if (hasHit)
        {
            var idx0 = _triangleBuffer[hitTriIdx];
            var idx1 = _triangleBuffer[hitTriIdx + 1];
            var idx2 = _triangleBuffer[hitTriIdx + 2];
            var p0 = matrix.MultiplyPoint3x4(vertices[idx0]);
            var p1 = matrix.MultiplyPoint3x4(vertices[idx1]);
            var p2 = matrix.MultiplyPoint3x4(vertices[idx2]);
            var d0 = HandleUtility.DistancePointLine(hitPoint, p0, p1);
            var d1 = HandleUtility.DistancePointLine(hitPoint, p1, p2);
            var d2 = HandleUtility.DistancePointLine(hitPoint, p2, p0);
            if (d0 <= d1 && d0 <= d2) { v1 = idx0; v2 = idx1; }
            else if (d1 <= d0 && d1 <= d2) { v1 = idx1; v2 = idx2; }
            else { v1 = idx2; v2 = idx0; }
        }
        return hasHit;
    }

    private bool RayTriangle(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float distance, out Vector3 point)
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

    private void CollectTriangles(Mesh mesh, List<int> buffer)
    {
        buffer.Clear();
        if (mesh == null) return;
        var subMeshCount = mesh.subMeshCount;
        if (subMeshCount <= 1)
        {
            buffer.AddRange(mesh.triangles);
            return;
        }
        for (var i = 0; i < subMeshCount; i++)
        {
            buffer.AddRange(mesh.GetTriangles(i));
        }
    }
}
#endif
