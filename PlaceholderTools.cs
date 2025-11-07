#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;


// Single, editor-only tool providing all options in one window.
// File name: PlaceholderTool.cs
// Menu: Tools > Placeholders > Placeholder Switcher
public class PlaceholderSwitcher : EditorWindow
{
// --- Inputs ---
[SerializeField] private string prefix = "SS_";                  // Names starting with this will be replaced
[SerializeField] private GameObject targetPrefab = null;          // Prefab to instantiate
[SerializeField] private string forcedName = "";                 // Optional forced name for new objects


// --- Transform options ---
[SerializeField] private bool keepRotation = true;
[SerializeField] private bool keepScale = true;
[SerializeField] private float scaleMultiply = 1f;                // Multiplicative scaler (applies to kept or prefab scale)

// --- Parenting options ---
[SerializeField] private Transform explicitParent = null;         // If set, overrides grouping
[SerializeField] private bool groupWithEmptyParent = false;       // Create/reuse a scene root parent
[SerializeField] private string groupParentName = "Imported Placeholders";

// --- Combine options ---
[SerializeField] private bool combineIntoOne = false;             // Bake into a single mesh
private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin }
[SerializeField] private PivotMode pivotMode = PivotMode.Parent;  // Pivot/origin for the combined object

// --- Collision / save options ---
[SerializeField] private bool rebuildInstancedCollision = false;  // Best-effort reattach of custom collider script (or MeshCollider fallback)
[SerializeField] private bool saveAsNewPrefab = false;
[SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

// Cache
private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();

[MenuItem("Tools/Placeholders/Placeholder Switcher")] 
public static void ShowWindow()
{
    var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
    w.minSize = new Vector2(560, 420);
    w.Show();
}

private void OnGUI()
{
    GUILayout.Label("Replace Object Placeholders in the open scene(s)", EditorStyles.boldLabel);

    // Static warning about combining breaking interactivity
    EditorGUILayout.HelpBox(
        "Combining meshes bakes many objects into ONE renderer. Per-object scripts, colliders, triggers, and events are lost. If you need interactivity, avoid combining and consider Static Batching instead.",
        MessageType.Warning);

    EditorGUILayout.Space(6);

    // Inputs
    prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix", "Names starting with this will be replaced (e.g. 'SS_')"), prefix);
    targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)", "Pick the prefab used for replacement"), targetPrefab, typeof(GameObject), false);
    forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)", "If set, every new instance (or the combined object) gets this exact name"), forcedName);

    EditorGUILayout.Space(4);

    // Transform options
    keepRotation = EditorGUILayout.Toggle(new GUIContent("Keep rotation", "Copy placeholder's local rotation"), keepRotation);
    keepScale = EditorGUILayout.Toggle(new GUIContent("Keep scale", "Copy placeholder's local scale"), keepScale);
    using (new EditorGUI.DisabledScope(!keepScale))
    {
        EditorGUI.indentLevel++;
        scaleMultiply = Mathf.Max(0.0001f, EditorGUILayout.FloatField(new GUIContent("Scale Multiply", "Final scale = (kept scale OR prefab's default scale) × this"), scaleMultiply));
        EditorGUI.indentLevel--;
    }

    EditorGUILayout.Space(4);

    // Parenting block (explicit parent OR group with empty parent)
    explicitParent = (Transform)EditorGUILayout.ObjectField(new GUIContent("Parent", "Optional: pick an existing Transform in the scene. If set, disables grouping."), explicitParent, typeof(Transform), true);
    using (new EditorGUI.DisabledScope(explicitParent != null))
    {
        groupWithEmptyParent = EditorGUILayout.Toggle(new GUIContent("Group with empty parent (optional)", "Create/reuse a scene root parent and place new instances under it."), groupWithEmptyParent);
        using (new EditorGUI.DisabledScope(!groupWithEmptyParent))
        {
            EditorGUI.indentLevel++;
            groupParentName = EditorGUILayout.TextField(new GUIContent("Empty Parent Name", "Name of the grouping parent to create or reuse per scene"), groupParentName);
            EditorGUI.indentLevel--;
        }
    }

    EditorGUILayout.Space(4);

    // Combine options
    combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one", "Bake spawned instances into a single MeshRenderer (static content only)"), combineIntoOne);
    using (new EditorGUI.DisabledScope(!combineIntoOne))
    {
        EditorGUI.indentLevel++;
        pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot", "Choose the origin for the combined object"), pivotMode);
        EditorGUI.indentLevel--;
    }

    EditorGUILayout.Space(4);

    // Collision rebuild
    rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Rebuild instanced collision", "Try to attach your project's 'Instanced Mesh Collider' style component; fallback to MeshCollider if not found"), rebuildInstancedCollision);

    EditorGUILayout.Space(4);

    // Save options
    saveAsNewPrefab = EditorGUILayout.Toggle(new GUIContent("Save as new prefab", "Save the result as a prefab (combined object if combining; otherwise a grouped root containing the new instances)"), saveAsNewPrefab);
    using (new EditorGUI.DisabledScope(!saveAsNewPrefab))
    {
        EditorGUI.indentLevel++;
        EditorGUILayout.BeginHorizontal();
        savePath = EditorGUILayout.TextField("Save Path", savePath);
        if (GUILayout.Button("Select…", GUILayout.Width(80)))
        {
            var suggested = System.IO.Path.GetFileName(savePath);
            var path = EditorUtility.SaveFilePanelInProject("Save Prefab As", string.IsNullOrEmpty(suggested) ? "CombinedPlaceholder" : suggested, "prefab", "Choose a location to save the prefab");
            if (!string.IsNullOrEmpty(path)) savePath = path;
        }
        EditorGUILayout.EndHorizontal();
        EditorGUI.indentLevel--;
    }

    EditorGUILayout.Space(10);

    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(prefix) || targetPrefab == null || !IsPrefabAsset(targetPrefab)))
    {
        if (GUILayout.Button("Switch Placeholders", GUILayout.Height(34)))
        {
            RunReplace();
        }
    }

    // Validation hints
    if (targetPrefab != null && !IsPrefabAsset(targetPrefab))
    {
        EditorGUILayout.HelpBox("Selected object is not a Prefab asset. Drag a prefab from the Project window.", MessageType.Warning);
    }
    else if (string.IsNullOrEmpty(prefix))
    {
        EditorGUILayout.HelpBox("Enter a placeholder prefix (e.g. 'SS_').", MessageType.Info);
    }
}

// --- Core logic ---
private static bool IsPrefabAsset(GameObject go)
{
    var t = PrefabUtility.GetPrefabAssetType(go);
    return t != PrefabAssetType.NotAPrefab && t != PrefabAssetType.MissingAsset;
}

private void RunReplace()
{
    // Collect candidates in open scenes
    var candidates = Resources.FindObjectsOfTypeAll<Transform>()
        .Select(t => t.gameObject)
        .Where(go => go != null && go.scene.IsValid() && go.name.StartsWith(prefix))
        .ToList();

    if (candidates.Count == 0)
    {
        EditorUtility.DisplayDialog("No matches", $"No GameObjects starting with '{prefix}' were found in open scenes.", "OK");
        return;
    }

    _groupParentByScene.Clear();
    if (explicitParent == null && groupWithEmptyParent)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (!s.IsValid() || !s.isLoaded) continue;
            var p = FindOrCreateGroupParentInScene(s, groupParentName);
            _groupParentByScene[s] = p;
        }
    }

    Undo.IncrementCurrentGroup();
    int group = Undo.GetCurrentGroup();
    Undo.SetCurrentGroupName("Placeholder Switcher");

    var spawned = new List<GameObject>(candidates.Count);

    try
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            var src = candidates[i];
            if (EditorUtility.DisplayCancelableProgressBar("Switching Placeholders", $"Replacing {i + 1}/{candidates.Count}: {src.name}", (float)(i + 1) / candidates.Count))
                break;

            Transform groupingParent = explicitParent != null ? explicitParent : null;
            if (groupingParent == null && groupWithEmptyParent)
            {
                if (_groupParentByScene.TryGetValue(src.scene, out var gp) && gp != null)
                    groupingParent = gp;
            }

            var inst = ReplaceOne(src, targetPrefab, forcedName, keepRotation, keepScale, scaleMultiply, groupingParent);
            if (inst != null) spawned.Add(inst);
        }
    }
    finally
    {
        EditorUtility.ClearProgressBar();
        Undo.CollapseUndoOperations(group);
    }

    GameObject finalRoot = null; // used for saving or combined result

    if (combineIntoOne && spawned.Count > 0)
    {
        finalRoot = CombineInstances(spawned, pivotMode, explicitParent, GetGroupParentForScene(spawned[0].scene), forcedName);
        foreach (var go in spawned) Undo.DestroyObjectImmediate(go);
    }
    else if (!combineIntoOne && saveAsNewPrefab && spawned.Count > 0)
    {
        // Create a tidy root if user wants a prefab even without combining
        finalRoot = new GameObject(string.IsNullOrEmpty(forcedName) ? ($"{prefix} Group") : forcedName);
        Undo.RegisterCreatedObjectUndo(finalRoot, "Create save root");
        var preferredParent = explicitParent != null ? explicitParent : GetGroupParentForScene(spawned[0].scene);
        if (preferredParent != null) finalRoot.transform.SetParent(preferredParent, false);
        foreach (var child in spawned) child.transform.SetParent(finalRoot.transform, true);
    }

    // Rebuild collision
    if (rebuildInstancedCollision)
    {
        if (finalRoot != null)
        {
            TryRebuildInstancedCollision(finalRoot);
        }
        else
        {
            foreach (var go in spawned) TryRebuildInstancedCollision(go);
        }
    }

    // Save prefab if requested
    if (saveAsNewPrefab)
    {
        if (string.IsNullOrEmpty(savePath))
        {
            EditorUtility.DisplayDialog("Save path missing", "Please choose a prefab save path.", "OK");
        }
        else
        {
            var toSave = finalRoot != null ? finalRoot : (spawned.Count == 1 ? spawned[0] : MakeTempGroupForSaving(spawned, forcedName, explicitParent));
            if (toSave != null)
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(toSave, savePath);
                if (prefab != null) Debug.Log($"Saved prefab: {savePath}"); else Debug.LogError("Failed to save prefab.");
            }
        }
    }

    EditorUtility.DisplayDialog("Done", $"Replaced {candidates.Count} placeholder(s)." + (combineIntoOne ? " Combined into one." : ""), "Nice");
}

private Transform GetGroupParentForScene(Scene scene)
{
    if (_groupParentByScene.TryGetValue(scene, out var t)) return t;
    return null;
}

private static Transform FindOrCreateGroupParentInScene(Scene scene, string parentName)
{
    foreach (var root in scene.GetRootGameObjects())
    {
        if (root != null && root.name == parentName) return root.transform;
    }
    var go = new GameObject(parentName);
    Undo.RegisterCreatedObjectUndo(go, "Create Group Parent");
    SceneManager.MoveGameObjectToScene(go, scene);
    return go.transform;
}

private static GameObject ReplaceOne(GameObject src, GameObject prefab, string forcedName, bool keepRot, bool keepScl, float sclMul, Transform groupingParent)
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
        return null;
    }

    Undo.RegisterCreatedObjectUndo(instanceObj, "Create replacement");

    // Choose parent: explicit/grouping overrides, else original parent
    var newParent = groupingParent != null ? groupingParent : parent;
    instanceObj.transform.SetParent(newParent, false);

    // Restore transform with options
    instanceObj.transform.localPosition = localPos;
    instanceObj.transform.localRotation = keepRot ? localRot : Quaternion.identity;
    instanceObj.transform.localScale = (keepScl ? localScale : instanceObj.transform.localScale) * sclMul;

    // Metadata
    instanceObj.layer = layer;
    try { instanceObj.tag = tag; } catch { }
    GameObjectUtility.SetStaticEditorFlags(instanceObj, staticFlags);
    instanceObj.SetActive(active);

    if (!string.IsNullOrEmpty(forcedName)) instanceObj.name = forcedName;

    // Remove placeholder
    Undo.DestroyObjectImmediate(src);
    return instanceObj;
}

private static GameObject CombineInstances(List<GameObject> instances, PivotMode pivotMode, Transform explicitParent, Transform groupParent, string forcedName)
{
    // Collect all MeshFilters + Renderers from instances
    var filters = new List<MeshFilter>();
    var renderers = new List<MeshRenderer>();
    for (int i = 0; i < instances.Count; i++)
    {
        var mf = instances[i].GetComponent<MeshFilter>();
        var mr = instances[i].GetComponent<MeshRenderer>();
        if (mf != null && mf.sharedMesh != null && mr != null)
        {
            filters.Add(mf);
            renderers.Add(mr);
        }
    }
    if (filters.Count == 0)
    {
        Debug.LogWarning("No MeshFilters found to combine. Skipping combine.");
        return null;
    }

    // Determine world-space pivot position
    Vector3 pivotWS;
    switch (pivotMode)
    {
        default:
        case PivotMode.Parent:
            if (explicitParent != null) pivotWS = explicitParent.position;
            else if (groupParent != null) pivotWS = groupParent.position;
            else pivotWS = Vector3.zero;
            break;
        case PivotMode.FirstObject:
            pivotWS = instances[0].transform.position;
            break;
        case PivotMode.BoundsCenter:
            var b = new Bounds(instances[0].transform.position, Vector3.zero);
            foreach (var go in instances)
            {
                var r = go.GetComponent<Renderer>();
                if (r != null) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
            }
            pivotWS = b.center;
            break;
        case PivotMode.WorldOrigin:
            pivotWS = Vector3.zero;
            break;
    }
    var pivotToWorld = Matrix4x4.TRS(pivotWS, Quaternion.identity, Vector3.one);

    // Build CombineInstances per submesh, and an aligned materials list (1:1 with submeshes)
    var combines = new List<CombineInstance>();
    var materials = new List<Material>();
    for (int i = 0; i < filters.Count; i++)
    {
        var mf = filters[i];
        var mr = renderers[i];
        var mesh = mf.sharedMesh;
        var mats = mr.sharedMaterials;
        int subCount = Mathf.Min(mesh.subMeshCount, mats.Length);

        for (int s = 0; s < subCount; s++)
        {
            var ci = new CombineInstance
            {
                mesh = mesh,
                subMeshIndex = s,
                transform = pivotToWorld.inverse * mf.transform.localToWorldMatrix
            };
            combines.Add(ci);
            materials.Add(mats[s]);
        }
    }

    var finalMesh = new Mesh { name = "Combined_Mesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
    finalMesh.CombineMeshes(combines.ToArray(), /*mergeSubMeshes*/ false, /*useMatrices*/ true, /*hasLightmapData*/ false);
    finalMesh.RecalculateBounds();
    if (!finalMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal)) finalMesh.RecalculateNormals();

    // Create result object
    var result = new GameObject(string.IsNullOrEmpty(forcedName) ? "Combined Object" : forcedName);
    Undo.RegisterCreatedObjectUndo(result, "Create combined object");
    var parent = explicitParent != null ? explicitParent : groupParent;
    if (parent != null) result.transform.SetParent(parent, false);
    result.transform.position = pivotWS;

    var mrf = result.AddComponent<MeshFilter>();
    var mrr = result.AddComponent<MeshRenderer>();
    mrf.sharedMesh = finalMesh;
    mrr.sharedMaterials = materials.ToArray();

    return result;
}

private static GameObject MakeTempGroupForSaving(List<GameObject> instances, string forcedName, Transform preferredParent)
{
    var root = new GameObject(string.IsNullOrEmpty(forcedName) ? "PlaceholderGroup" : forcedName);
    Undo.RegisterCreatedObjectUndo(root, "Create temp root for prefab save");
    if (preferredParent != null) root.transform.SetParent(preferredParent, false);
    foreach (var go in instances) go.transform.SetParent(root.transform, true);
    return root;
}

// Best-effort rebuild of custom instanced collider, with MeshCollider fallback
private static void TryRebuildInstancedCollision(GameObject go)
{
    var type = FindTypeByMonoScriptNames(new[]
    {
        "Instanced Mesh Collider",
        "Instanced Mess Collider",
        "InstancedMeshCollider",
        "InstancedMeshCollision",
    });

    if (type != null && typeof(Component).IsAssignableFrom(type))
    {
        foreach (var c in go.GetComponents(type)) Undo.DestroyObjectImmediate(c as Component);
        var comp = Undo.AddComponent(go, type);

        var m = type.GetMethod("Rebuild", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? type.GetMethod("Build", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? type.GetMethod("Setup", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (m != null)
        {
            try { m.Invoke(comp, null); } catch { }
        }
    }
    else
    {
        var mf = go.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            var mc = go.GetComponent<MeshCollider>();
            if (mc == null) mc = Undo.AddComponent<MeshCollider>(go);
            mc.sharedMesh = mf.sharedMesh;
            mc.convex = false;
        }
    }
}

private static Type FindTypeByMonoScriptNames(IEnumerable<string> names)
{
    var guids = AssetDatabase.FindAssets("t:MonoScript");
    foreach (var g in guids)
    {
        var path = AssetDatabase.GUIDToAssetPath(g);
        var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
        if (ms == null) continue;
        var n = ms.name;
        if (names.Contains(n))
        {
            var t = ms.GetClass();
            if (t != null) return t;
        }
    }
    foreach (var n in names)
    {
        var t = Type.GetType(n);
        if (t != null) return t;
    }
    return null;
}



}
#endif

