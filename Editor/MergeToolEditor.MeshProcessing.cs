#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

public partial class MergeToolEditor
{
    private void UpdateMeshesIfDirty()
    {
        if (!HasMeshesAssigned()) return;
        if (!_topologyDirty && !_deformDirty && _faceWorkspace?.ResultMesh != null && _bodyWorkspace?.ResultMesh != null) return;

        PrepareWorkspaces();
        ApplyOperationsToWorkspaces();
        ApplyPairAlignment();
        ApplyBlendShapes();
        BuildGraphs();
        BakePreviews();
        SmoothNormals();

        _topologyDirty = false;
        _deformDirty = false;
    }

    private void PrepareWorkspaces()
    {
        if (_faceWorkspace == null || _faceWorkspace.Renderer != _target.faceMesh)
            _faceWorkspace = new MeshWorkspace(_target.faceMesh);
        if (_bodyWorkspace == null || _bodyWorkspace.Renderer != _target.bodyMesh)
            _bodyWorkspace = new MeshWorkspace(_target.bodyMesh);
        _faceWorkspace.PrepareSource();
        _bodyWorkspace.PrepareSource();
    }

    private void ApplyOperationsToWorkspaces()
    {
        if (_target.operationLog == null) return;
        foreach (var op in _target.operationLog)
        {
            switch (op.kind)
            {
                case OperationKind.FaceSplit:
                    _faceWorkspace.ApplySplits(new[] { op.split });
                    break;
                case OperationKind.BodySplit:
                    _bodyWorkspace.ApplySplits(new[] { op.split });
                    break;
            }
        }
    }

    private void ApplyPairAlignment()
    {
        _pairCache.Refresh();
        var context = BuildPairContext();
        if (context.IsValid()) PairAligner.ApplyPairs(context, _pairCache.Pairs);
    }

    private PairContext BuildPairContext()
    {
        return new PairContext
        {
            FaceWorkspace = _faceWorkspace,
            BodyWorkspace = _bodyWorkspace,
            FaceVertices = _faceWorkspace?.GetVertices(),
            BodyVertices = _bodyWorkspace?.GetVertices(),
            FaceLocalToWorld = _faceWorkspace?.LocalToWorld ?? Matrix4x4.identity,
            BodyLocalToWorld = _bodyWorkspace?.LocalToWorld ?? Matrix4x4.identity,
            WorldToFace = _faceWorkspace?.WorldToLocal ?? Matrix4x4.identity,
            WorldToBody = _bodyWorkspace?.WorldToLocal ?? Matrix4x4.identity,
            FaceSkinToWorld = _faceWorkspace?.SkinToWorld,
            BodySkinToWorld = _bodyWorkspace?.SkinToWorld,
            FaceGroups = _faceWorkspace?.CoincidentGroups ?? new Dictionary<int, List<int>>(),
            BodyGroups = _bodyWorkspace?.CoincidentGroups ?? new Dictionary<int, List<int>>()
        };
    }

    private void ApplyBlendShapes()
    {
        _faceWorkspace.ApplyBlendShapes(null);
        _bodyWorkspace.ApplyBlendShapes(null);
    }

    private void BuildGraphs()
    {
        _faceWorkspace.RebuildGraph();
        _bodyWorkspace.RebuildGraph();
    }

    private void BakePreviews()
    {
        _faceWorkspace.BakeSkinnedMesh();
        _bodyWorkspace.BakeSkinnedMesh();
    }

    private void SmoothNormals()
    {
        NormalSmoother.Smooth(_faceWorkspace, _bodyWorkspace, _pairCache.Pairs, _target.normalSmoothDepth, _target.normalSmoothStrength);
        _faceWorkspace.ApplyNormals(_faceWorkspace.PreviewMesh?.normals);
        _bodyWorkspace.ApplyNormals(_bodyWorkspace.PreviewMesh?.normals);
    }

    private void ApplyChangesToScene()
    {
        UpdateMeshesIfDirty();
        if (_faceWorkspace?.ResultMesh == null && _bodyWorkspace?.ResultMesh == null) return;
        var folderPath = GetSaveFolderPath();
        if (string.IsNullOrEmpty(folderPath)) return;

        var faceApplied = SaveAndApplyMesh(_faceWorkspace, _target.faceMesh, "Face_Modified", out var faceOriginal);
        var bodyApplied = SaveAndApplyMesh(_bodyWorkspace, _target.bodyMesh, "Body_Modified", out var bodyOriginal);
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

    private Mesh SaveAndApplyMesh(MeshWorkspace workspace, SkinnedMeshRenderer renderer, string suffix, out Mesh originalMesh)
    {
        originalMesh = null;
        if (workspace == null || renderer == null) return null;
        if (workspace.ResultMesh == null) return null;

        var newMesh = UnityEngine.Object.Instantiate(workspace.ResultMesh);
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

    private void RevertAppliedMeshes()
    {
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
