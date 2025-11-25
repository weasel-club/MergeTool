#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

public interface IMergeTarget
{
    SkinnedMeshRenderer FaceRenderer { get; }
    SkinnedMeshRenderer BodyRenderer { get; }
    bool EnableSymmetry { get; }
    float SymmetryTolerance { get; }
    int NormalSmoothDepth { get; }
    float NormalSmoothStrength { get; }
    IList<OperationRecord> Operations { get; }
}

[Serializable]
public struct VertexPair
{
    public int faceIndex;
    public int bodyIndex;
    public Vector3 worldOffset;
}

[Serializable]
public struct EdgeSplitData
{
    public int v1;
    public int v2;
}

public struct SplitDependency
{
    public int midIndex;
    public int parent1Index;
    public int parent2Index;
}

public enum OperationKind
{
    Pair,
    FaceSplit,
    BodySplit
}

[Serializable]
public struct OperationRecord
{
    public OperationKind kind;
    public EdgeSplitData split;
    public VertexPair pair;
}

public class MergeTool : MonoBehaviour, IEditorOnly, IMergeTarget
{
    [Header("Renderers")]
    public SkinnedMeshRenderer faceMesh;
    public SkinnedMeshRenderer bodyMesh;

    [Header("Application State")]
    public Mesh appliedFaceMeshBefore;
    public Mesh appliedBodyMeshBefore;
    public Mesh appliedFaceMeshAfter;
    public Mesh appliedBodyMeshAfter;
    public bool isApplied;

    [Header("Operations")]
    [HideInInspector] public List<OperationRecord> operationLog = new List<OperationRecord>();

    [Header("Settings")]
    public bool enableSymmetry = true;
    public float symmetryTolerance = 0.0005f;
    [Range(0, 10)] public int normalSmoothDepth = 3;
    [Range(0f, 1f)] public float normalSmoothStrength = 1f;

    SkinnedMeshRenderer IMergeTarget.FaceRenderer => faceMesh;
    SkinnedMeshRenderer IMergeTarget.BodyRenderer => bodyMesh;
    bool IMergeTarget.EnableSymmetry => enableSymmetry;
    float IMergeTarget.SymmetryTolerance => symmetryTolerance;
    int IMergeTarget.NormalSmoothDepth => normalSmoothDepth;
    float IMergeTarget.NormalSmoothStrength => normalSmoothStrength;
    IList<OperationRecord> IMergeTarget.Operations => operationLog;
}
#endif
