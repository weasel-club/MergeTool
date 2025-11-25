#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class PreviewRenderer : IDisposable
{
    private readonly int _layer;
    private readonly Material _sourceMaterial;
    private readonly List<GameObject> _clones = new List<GameObject>();
    private readonly List<Material> _materials = new List<Material>();
    private GameObject _cameraGO;
    private RenderTexture _rt;

    public PreviewRenderer(int previewLayer, Material baseMaterial)
    {
        _layer = previewLayer;
        _sourceMaterial = baseMaterial;
    }

    public void Draw(SceneView sceneView, params (SkinnedMeshRenderer renderer, Mesh mesh, float alpha, bool wireframe)[] entries)
    {
        if (sceneView == null || sceneView.camera == null) return;
        if (entries == null || entries.Length == 0) return;

        try
        {
            SetupCamera(sceneView);
            SetupLight(sceneView);
            SpawnClones(entries);
            if (_rt == null || _cameraGO == null) return;

            _cameraGO.GetComponent<Camera>().Render();
            var prevMask = sceneView.camera.cullingMask;
            sceneView.camera.cullingMask = 1 << _layer;
            try
            {
                Handles.BeginGUI();
                var pixelWidth = sceneView.camera.pixelWidth;
                var pixelHeight = sceneView.camera.pixelHeight;
                var ppp = EditorGUIUtility.pixelsPerPoint;
                var logicalWidth = pixelWidth / ppp;
                var logicalHeight = pixelHeight / ppp;
                GUI.DrawTexture(new Rect(0, 0, logicalWidth, logicalHeight), _rt, ScaleMode.StretchToFill, true);
                Handles.EndGUI();
            }
            finally
            {
                sceneView.camera.cullingMask = prevMask;
            }
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_cameraGO != null) UnityEngine.Object.DestroyImmediate(_cameraGO);
        foreach (var go in _clones) if (go != null) UnityEngine.Object.DestroyImmediate(go);
        foreach (var mat in _materials) if (mat != null) UnityEngine.Object.DestroyImmediate(mat);
        _clones.Clear();
        _materials.Clear();
        if (_rt != null) RenderTexture.ReleaseTemporary(_rt);
        _rt = null;
        _cameraGO = null;
    }

    private void SetupCamera(SceneView sceneView)
    {
        _cameraGO = new GameObject("MergeToolPreviewCamera");
        var camera = _cameraGO.AddComponent<Camera>();
        camera.CopyFrom(sceneView.camera);
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.cullingMask = 1 << _layer;

        var pixelWidth = sceneView.camera.pixelWidth;
        var pixelHeight = sceneView.camera.pixelHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0) return;

        _rt = RenderTexture.GetTemporary(pixelWidth, pixelHeight, 24, RenderTextureFormat.ARGB32);
        _rt.name = "MergeToolPreviewRT";
        camera.targetTexture = _rt;
    }

    private void SetupLight(SceneView sceneView)
    {
        var light = _cameraGO.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1f;
        light.transform.rotation = sceneView.camera.transform.rotation;
    }

    private void SpawnClones((SkinnedMeshRenderer renderer, Mesh mesh, float alpha, bool wireframe)[] entries)
    {
        foreach (var entry in entries)
        {
            if (entry.renderer == null || entry.mesh == null) continue;
            var clone = new GameObject("MergeToolPreview_" + entry.renderer.name);
            _clones.Add(clone);
            clone.transform.SetPositionAndRotation(entry.renderer.transform.position, entry.renderer.transform.rotation);
            clone.transform.localScale = Vector3.one;
            SetLayerRecursively(clone, _layer);

            var mf = clone.AddComponent<MeshFilter>();
            mf.sharedMesh = entry.mesh;
            var mr = clone.AddComponent<MeshRenderer>();
            var mat = new Material(_sourceMaterial);
            ConfigureMaterial(mat, entry.alpha, entry.wireframe);
            mr.sharedMaterial = mat;
            _materials.Add(mat);
        }
    }

    private void ConfigureMaterial(Material material, float alpha, bool wireframe)
    {
        if (material == null) return;
        var isTransparent = alpha < 1f;
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
        material.SetFloat("_LineWidth", Mathf.Max(0.01f, material.GetFloat("_LineWidth")));
        material.SetInt("_ShowWireframe", wireframe ? 1 : 0);
    }

    private void SetLayerRecursively(GameObject obj, int newLayer)
    {
        obj.layer = newLayer;
        foreach (Transform child in obj.transform) SetLayerRecursively(child.gameObject, newLayer);
    }
}
#endif
