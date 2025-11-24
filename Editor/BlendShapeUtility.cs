#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

public class BlendShapeFrameData
{
    public float weight;
    public Vector3[] deltaVertices;
    public Vector3[] deltaNormals;
    public Vector3[] deltaTangents;
}

public class BlendShapeData
{
    public string name;
    public List<BlendShapeFrameData> frames = new List<BlendShapeFrameData>();
}

public static class BlendShapeUtility
{
    public static List<BlendShapeData> Capture(Mesh source)
    {
        var result = new List<BlendShapeData>();
        if (source == null) return result;
        var shapeCount = source.blendShapeCount;
        var vertexCount = source.vertexCount;
        var v = new Vector3[vertexCount];
        var n = new Vector3[vertexCount];
        var t = new Vector3[vertexCount];
        for (var shape = 0; shape < shapeCount; shape++)
        {
            var data = new BlendShapeData { name = source.GetBlendShapeName(shape) };
            var frameCount = source.GetBlendShapeFrameCount(shape);
            for (var frame = 0; frame < frameCount; frame++)
            {
                source.GetBlendShapeFrameVertices(shape, frame, v, n, t);
                var frameData = new BlendShapeFrameData
                {
                    weight = source.GetBlendShapeFrameWeight(shape, frame),
                    deltaVertices = (Vector3[])v.Clone(),
                    deltaNormals = (Vector3[])n.Clone(),
                    deltaTangents = (Vector3[])t.Clone()
                };
                data.frames.Add(frameData);
            }
            result.Add(data);
        }
        return result;
    }

    public static void Apply(Mesh target, List<BlendShapeData> data, List<SplitDependency> dependencies)
    {
        if (target == null || data == null) return;
        var targetCount = target.vertexCount;
        var parentMap = BuildParentLookup(dependencies);
        target.ClearBlendShapes();
        for (var i = 0; i < data.Count; i++)
        {
            var shape = data[i];
            for (var f = 0; f < shape.frames.Count; f++)
            {
                var frame = shape.frames[f];
                var v = new Vector3[targetCount];
                var n = new Vector3[targetCount];
                var t = new Vector3[targetCount];
                CopyFrameData(frame.deltaVertices, v);
                CopyFrameData(frame.deltaNormals, n);
                CopyFrameData(frame.deltaTangents, t);
                ExtendFrameData(frame, v, n, t, parentMap);
                target.AddBlendShapeFrame(shape.name, frame.weight, v, n, t);
            }
        }
    }

    private static Dictionary<int, (int parentA, int parentB)> BuildParentLookup(List<SplitDependency> dependencies)
    {
        var map = new Dictionary<int, (int parentA, int parentB)>();
        if (dependencies == null) return map;
        for (var i = 0; i < dependencies.Count; i++)
        {
            var dep = dependencies[i];
            map[dep.midIndex] = (dep.parent1Index, dep.parent2Index);
        }
        return map;
    }

    private static void CopyFrameData(Vector3[] source, Vector3[] target)
    {
        if (source == null || target == null) return;
        var length = Mathf.Min(source.Length, target.Length);
        for (var i = 0; i < length; i++) target[i] = source[i];
    }

    private static void ExtendFrameData(BlendShapeFrameData frame, Vector3[] v, Vector3[] n, Vector3[] t, Dictionary<int, (int parentA, int parentB)> parents)
    {
        if (v == null || frame == null) return;
        var srcLength = frame.deltaVertices != null ? frame.deltaVertices.Length : 0;
        if (parents.Count == 0) return;
        for (var i = srcLength; i < v.Length; i++)
        {
            if (!parents.TryGetValue(i, out var parent)) continue;
            var validA = parent.parentA >= 0 && parent.parentA < srcLength;
            var validB = parent.parentB >= 0 && parent.parentB < srcLength;
            if (!validA || !validB) continue;
            v[i] = (frame.deltaVertices[parent.parentA] + frame.deltaVertices[parent.parentB]) * 0.5f;
            n[i] = (frame.deltaNormals[parent.parentA] + frame.deltaNormals[parent.parentB]) * 0.5f;
            t[i] = (frame.deltaTangents[parent.parentA] + frame.deltaTangents[parent.parentB]) * 0.5f;
        }
    }
}
#endif
