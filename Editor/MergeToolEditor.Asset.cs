#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using System;

public partial class MergeToolEditor : Editor
{
    private const string _assetsDirectoryGUID = "9482f8935475c6e4cbce1f7e0717cd36";
    private const string _saveFolderGUID = "28089796ea7a3d24999851e7a1933b45";

    private T GetAsset<T>(string relativePath) where T : UnityEngine.Object
    {
        var folderPath = AssetDatabase.GUIDToAssetPath(_assetsDirectoryGUID);
        var path = Path.Combine(folderPath, relativePath).Replace('\\', '/');
        return AssetDatabase.LoadAssetAtPath<T>(path);
    }

    private string GetSaveFolderPath()
    {
        var folderPath = AssetDatabase.GUIDToAssetPath(_saveFolderGUID);
        if (string.IsNullOrEmpty(folderPath)) return null;
        if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
        return folderPath;
    }

    private string SaveAsset(UnityEngine.Object asset, string fileName)
    {
        var folderPath = GetSaveFolderPath();
        if (string.IsNullOrEmpty(folderPath)) return null;
        var path = Path.Combine(folderPath, fileName).Replace('\\', '/');
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        return path;
    }
}
#endif
