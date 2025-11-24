#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

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

public class MergeTool : MonoBehaviour, IEditorOnly
{
    public SkinnedMeshRenderer faceMesh;
    public SkinnedMeshRenderer bodyMesh;
    public Mesh appliedFaceMeshBefore;
    public Mesh appliedBodyMeshBefore;
    public Mesh appliedFaceMeshAfter;
    public Mesh appliedBodyMeshAfter;
    public bool isApplied;
    [HideInInspector]
    public List<OperationRecord> operationLog = new List<OperationRecord>();
    public bool enableSymmetry = true;
    public float symmetryTolerance = 0.0005f;
    [Range(0, 10)]
    public int normalSmoothDepth = 3;
    [Range(0f, 1f)]
    public float normalSmoothStrength = 1f;
}
#endif
