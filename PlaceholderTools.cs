#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;


public class PlaceholderSwitcherWindow : EditorWindow
{
private string prefix = "SS_";                     // e.g. SS_ or ABCD_
private GameObject targetPrefab;                    // the prefab to place instead
private string forcedName = "";                    // optional: set all instances to this exact name


// New options
private bool keepRotation = true;
private bool keepScale = true;
private bool groupWithEmptyParent = false;
private string groupParentName = "Imported Placeholders";

[MenuItem("Tools/Placeholders/Switcher")] 
public static void ShowWindow()
{
    var w = GetWindow<PlaceholderSwitcherWindow>(true, "Placeholder Switcher");
    w.minSize = new Vector2(460, 240);
    w.Show();
}

private void OnGUI()
{
    GUILayout.Label("Replace Blender placeholders in the open scene(s)", EditorStyles.boldLabel);
    EditorGUILayout.Space(4);

    // Inputs
    prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix", "Names starting with this will be replaced (e.g. 'SS_')"), prefix);

    using (new EditorGUILayout.HorizontalScope())
    {
        targetPrefab = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Desired Asset (Prefab)", "Pick the prefab you want to use for replacement"),
            targetPrefab, typeof(GameObject), false);
    }

    forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)", "If set, every new instance will get this exact name (e.g. '6569')"), forcedName);

    // Options
    EditorGUILayout.Space(4);
    keepRotation = EditorGUILayout.Toggle(new GUIContent("Keep rotation", "If enabled, the replacement keeps the placeholder's local rotation"), keepRotation);
    keepScale = EditorGUILayout.Toggle(new GUIContent("Keep scale", "If enabled, the replacement keeps the placeholder's local scale"), keepScale);

    EditorGUILayout.Space(2);
    groupWithEmptyParent = EditorGUILayout.Toggle(new GUIContent("Group with empty parent (optional)", "Create/use a shared empty parent and place all new instances under it"), groupWithEmptyParent);
    using (new EditorGUI.DisabledScope(!groupWithEmptyParent))
    {
        groupParentName = EditorGUILayout.TextField(new GUIContent("Empty Parent Name", "Name of the grouping parent GameObject"), groupParentName);
    }

    EditorGUILayout.Space(8);

    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(prefix) || targetPrefab == null || !IsPrefabAsset(targetPrefab)))
    {
        if (GUILayout.Button("Switch Placeholders", GUILayout.Height(32)))
        {
            SwitchPlaceholders();
        }
    }

    // Light help / validation
    if (targetPrefab != null && !IsPrefabAsset(targetPrefab))
    {
        EditorGUILayout.HelpBox("Selected object is not a Prefab asset. Drag a prefab from the Project window.", MessageType.Warning);
    }
    else if (string.IsNullOrEmpty(prefix))
    {
        EditorGUILayout.HelpBox("Enter a placeholder prefix (e.g. 'SS_').", MessageType.Info);
    }
}

private static bool IsPrefabAsset(GameObject go)
{
    var t = PrefabUtility.GetPrefabAssetType(go);
    return t != PrefabAssetType.NotAPrefab && t != PrefabAssetType.MissingAsset;
}

private void SwitchPlaceholders()
{
    // Gather targets across all open scenes
    var sceneObjects = Resources.FindObjectsOfTypeAll<Transform>()
        .Select(t => t.gameObject)
        .Where(go => go != null && go.scene.IsValid() && go.name.StartsWith(prefix))
        .ToList();

    if (sceneObjects.Count == 0)
    {
        EditorUtility.DisplayDialog("No matches",
            $"No GameObjects starting with '{prefix}' were found in open scenes.",
            "OK");
        return;
    }

    if (!EditorUtility.DisplayDialog("Confirm Replacement",
            $"Replace {sceneObjects.Count} object(s) that start with '{prefix}'?



Prefab: {targetPrefab.name}
Keep rotation: {keepRotation}
Keep scale: {keepScale}
Group with empty parent: {groupWithEmptyParent}",
"Replace", "Cancel"))
return;


    // Optional: create/find grouping parent per scene
    var parentByScene = new Dictionary<Scene, Transform>();
    if (groupWithEmptyParent)
    {
        foreach (var s in Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt))
        {
            if (!s.IsValid() || !s.isLoaded) continue;
            var parent = FindOrCreateGroupParentInScene(s, groupParentName);
            parentByScene[s] = parent;
        }
    }

    Undo.IncrementCurrentGroup();
    int group = Undo.GetCurrentGroup();
    Undo.SetCurrentGroupName("Switch Placeholders");

    try
    {
        int i = 0;
        foreach (var src in sceneObjects)
        {
            i++;
            if (EditorUtility.DisplayCancelableProgressBar("Switching Placeholders",
                    $"Replacing {i}/{sceneObjects.Count}: {src.name}", (float)i / sceneObjects.Count))
            {
                break;
            }

            Transform groupingParent = null;
            if (groupWithEmptyParent)
            {
                if (parentByScene.TryGetValue(src.scene, out var p) && p != null)
                    groupingParent = p;
            }

            ReplaceOne(src, targetPrefab, forcedName, keepRotation, keepScale, groupingParent);
        }
    }
    finally
    {
        EditorUtility.ClearProgressBar();
        Undo.CollapseUndoOperations(group);
    }

    EditorUtility.DisplayDialog("Done", "Placeholder replacement complete.", "Nice");
}

private static Transform FindOrCreateGroupParentInScene(Scene scene, string parentName)
{
    // Try to find an existing root object with this name in the scene
    foreach (var root in scene.GetRootGameObjects())
    {
        if (root != null && root.name == parentName)
            return root.transform;
    }
    // Create a new root object
    var go = new GameObject(parentName);
    Undo.RegisterCreatedObjectUndo(go, "Create Group Parent");
    SceneManager.MoveGameObjectToScene(go, scene);
    return go.transform;
}

private static void ReplaceOne(GameObject src, GameObject prefab, string forcedName, bool keepRot, bool keepScl, Transform groupingParent)
{
    // Cache source data
    var parent = src.transform.parent;
    var localPos = src.transform.localPosition;
    var localRot = src.transform.localRotation;
    var localScale = src.transform.localScale;
    var layer = src.layer;
    var tag = src.tag;
    var active = src.activeSelf;
    var staticFlags = GameObjectUtility.GetStaticEditorFlags(src);

    // Instantiate prefab as a connected instance in the same scene
    var instanceObj = PrefabUtility.InstantiatePrefab(prefab, src.scene) as GameObject;
    if (instanceObj == null)
    {
        Debug.LogError($"Failed to instantiate prefab for {src.name}");
        return;
    }

    Undo.RegisterCreatedObjectUndo(instanceObj, "Create replacement");

    // Choose parent: grouping parent overrides, else original parent
    var newParent = groupingParent != null ? groupingParent : parent;
    instanceObj.transform.SetParent(newParent, false);

    // Restore transform parts with user control
    instanceObj.transform.localPosition = localPos;
    if (keepRot) instanceObj.transform.localRotation = localRot;
    if (keepScl) instanceObj.transform.localScale = localScale;

    // Restore metadata
    instanceObj.layer = layer;
    try { instanceObj.tag = tag; } catch { /* tag may be missing on prefab */ }
    GameObjectUtility.SetStaticEditorFlags(instanceObj, staticFlags);
    instanceObj.SetActive(active);

    if (!string.IsNullOrEmpty(forcedName))
        instanceObj.name = forcedName;

    // Remove the placeholder
    Undo.DestroyObjectImmediate(src);
}



}
#endif


