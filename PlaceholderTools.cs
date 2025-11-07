using System.Collections.Generic;
using System.Linq;
using UnityEngine;


// ------------------------------------------------------------
//  PlaceholderMap: create one via Assets > Create > Placeholder Map
// ------------------------------------------------------------
[CreateAssetMenu(fileName = "PlaceholderMap", menuName = "Placeholders/Placeholder Map", order = 0)]
public class PlaceholderMap : ScriptableObject
{
[System.Serializable]
public class Entry
{
[Tooltip("Names starting with this (e.g. 'SS_swingshot') will be replaced")] public string prefix = "SS_";
[Tooltip("Prefab to instantiate in place of the placeholder")] public GameObject prefab;
[Tooltip("Optional: force the new instance name (e.g. '6569')")] public string forcedName = "";
}


public List<Entry> entries = new List<Entry>();



}


// ------------------------------------------------------------
//  Runtime replacer (optional): attach to a bootstrap GameObject
//  Only needed if you prefer to swap placeholders at runtime.
// ------------------------------------------------------------
public class RuntimePlaceholderReplacer : MonoBehaviour
{
[Tooltip("Mapping from SS_ prefixes to actual prefabs")] public PlaceholderMap map;
[Tooltip("Replace on Start() and then disable this component")] public bool replaceOnStart = true;


void Start()
{
    if (!replaceOnStart || map == null) return;
    ReplaceAllInScene(map);
    enabled = false;
}

public static void ReplaceAllInScene(PlaceholderMap map)
{
    if (map == null) return;
    // Find all active scene objects whose name starts with any configured prefix
    var all = FindObjectsOfType<Transform>(true).Select(t => t.gameObject);
    foreach (var go in all)
    {
        var entry = map.entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.prefix) && go.name.StartsWith(e.prefix));
        if (entry != null && entry.prefab != null)
        {
            Replace(go, entry.prefab, entry.forcedName);
        }
    }
}

private static void Replace(GameObject src, GameObject prefab, string forcedName)
{
    var parent = src.transform.parent;
    var localPos = src.transform.localPosition;
    var localRot = src.transform.localRotation;
    var localScale = src.transform.localScale;
    var layer = src.layer;
    var tag = src.tag;
    var active = src.activeSelf;

    var instance = Instantiate(prefab);
    instance.transform.SetParent(parent, false);
    instance.transform.localPosition = localPos;
    instance.transform.localRotation = localRot;
    instance.transform.localScale = localScale;
    instance.layer = layer;
    try { instance.tag = tag; } catch { }
    if (!string.IsNullOrEmpty(forcedName)) instance.name = forcedName;
    instance.SetActive(active);

    DestroyImmediate(src);
}



}


#if UNITY_EDITOR
using UnityEditor;


// ------------------------------------------------------------
//  Editor replacer: Tools > Placeholders > Replace In Open Scenes
// ------------------------------------------------------------
public static class EditorPlaceholderReplacer
{
[MenuItem("Tools/Placeholders/Replace In Open Scenes")]
public static void ReplaceInOpenScenes()
{
var map = FindPlaceholderMapAsset();
if (map == null)
{
EditorUtility.DisplayDialog("Placeholder Map not found",
"Create one via Assets > Create > Placeholders > Placeholder Map and select it in Project view.",
"OK");
return;
}


    var sceneObjects = UnityEngine.Object.FindObjectsOfType<Transform>(true)
        .Select(t => t.gameObject)
        .Where(go => go.scene.IsValid());

    var toReplace = new List<(GameObject go, PlaceholderMap.Entry entry)>();
    foreach (var go in sceneObjects)
    {
        var entry = map.entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.prefix) && go.name.StartsWith(e.prefix));
        if (entry != null && entry.prefab != null)
            toReplace.Add((go, entry));
    }

    if (toReplace.Count == 0)
    {
        EditorUtility.DisplayDialog("Nothing to replace", "No GameObjects starting with any configured prefix were found.", "OK");
        return;
    }

    Undo.IncrementCurrentGroup();
    int group = Undo.GetCurrentGroup();
    Undo.SetCurrentGroupName("Replace Placeholders");

    int replaced = 0;
    foreach (var (go, entry) in toReplace)
    {
        ReplaceOne(go, entry);
        replaced++;
    }

    Undo.CollapseUndoOperations(group);
    EditorUtility.DisplayDialog("Placeholders replaced", $"Replaced {replaced} object(s).", "Nice");
}

private static void ReplaceOne(GameObject src, PlaceholderMap.Entry entry)
{
    var prefab = entry.prefab;
    if (prefab == null) return;

    var parent = src.transform.parent;
    var localPos = src.transform.localPosition;
    var localRot = src.transform.localRotation;
    var localScale = src.transform.localScale;
    var layer = src.layer;
    var tag = src.tag;
    var active = src.activeSelf;
    var staticFlags = GameObjectUtility.GetStaticEditorFlags(src);

    // Create prefab instance with connection preserved
    var scene = src.scene;
    var instanceObj = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
    if (instanceObj == null)
    {
        Debug.LogError($"Failed to instantiate prefab for {src.name}");
        return;
    }

    Undo.RegisterCreatedObjectUndo(instanceObj, "Create replacement");
    instanceObj.transform.SetParent(parent, false);
    instanceObj.transform.localPosition = localPos;
    instanceObj.transform.localRotation = localRot;
    instanceObj.transform.localScale = localScale;
    instanceObj.layer = layer;
    try { instanceObj.tag = tag; } catch { }
    GameObjectUtility.SetStaticEditorFlags(instanceObj, staticFlags);
    instanceObj.SetActive(active);

    if (!string.IsNullOrEmpty(entry.forcedName))
        instanceObj.name = entry.forcedName;

    Undo.DestroyObjectImmediate(src);
}

private static PlaceholderMap FindPlaceholderMapAsset()
{
    // Prefer currently selected PlaceholderMap in Project view
    var selected = Selection.activeObject as PlaceholderMap;
    if (selected != null) return selected;

    // Otherwise, try to find one anywhere in the project (first match)
    var guids = AssetDatabase.FindAssets("t:PlaceholderMap");
    if (guids != null && guids.Length > 0)
    {
        var path = AssetDatabase.GUIDToAssetPath(guids[0]);
        return AssetDatabase.LoadAssetAtPath<PlaceholderMap>(path);
    }
    return null;
}



}
#endif


