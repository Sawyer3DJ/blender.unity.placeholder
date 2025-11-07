#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// ===============================================
// PlaceholderTool.cs
// Tools > Placeholders > Placeholder Switcher
// ===============================================
public class PlaceholderSwitcher : EditorWindow
{
    // --- Input options ---
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject targetPrefab = null;
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false;

    // --- Transform options ---
    private enum RotationMode { Offset, Absolute }
    [SerializeField] private RotationMode rotationMode = RotationMode.Offset;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero; // degrees
    [SerializeField] private float scaleFactor = 1f;               // final = placeholder.localScale * scaleFactor

    // --- Parenting options ---
    [SerializeField] private Transform explicitParent = null;  // overrides grouping if set
    [SerializeField] private bool groupWithEmptyParent = false;
    [SerializeField] private string groupParentName = "Imported Placeholders";
    private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
    [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.FirstObject;
    [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;

    // --- Combine options ---
    [SerializeField] private bool combineIntoOne = false; // static only
    private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] private PivotMode pivotMode = PivotMode.Parent;

    // --- Move options ---
    [SerializeField] private bool moveToWorldCoordinates = false;
    [SerializeField] private Vector3 moveTargetPosition = Vector3.zero;

    // --- Collision / Save options ---
    [SerializeField] private bool rebuildInstancedCollision = false;
    [SerializeField] private bool saveAsNewPrefab = false;
    [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

    // --- Preview ---
    private PreviewRenderUtility previewUtil;
    private float previewYaw = -30f;
    private float previewPitch = 15f;
    private float previewDistance = 1.6f; // closer default
    private bool previewUserAdjusted = false;
    private Mesh previewMesh;
    private Material[] previewMats;
    private Material fallbackMat;

    // --- Internal state ---
    private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();
    private readonly Dictionary<string, int> _nameCounters = new Dictionary<string, int>();

    [MenuItem("Tools/Placeholders/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(740, 700);
        w.Show();
    }

    private void OnEnable() => InitPreview();
    private void OnDisable() => CleanupPreview();

    private void OnGUI()
    {
        GUILayout.Label("Replace Object Placeholders", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Combining meshes bakes many objects into ONE renderer. Per-object scripts, colliders, triggers, and events are lost. " +
            "If you need interactivity, avoid combining and consider Static Batching instead.",
            MessageType.Warning);

        EditorGUILayout.Space(6);

        // Inputs
        prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix", "e.g. 'SS_'"), prefix);
        targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)", "Prefab to instantiate"), targetPrefab, typeof(GameObject), false);
        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)", "Base name for new instances / combined object"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming", "Appends _001, _002, ..."), useIncrementalNaming);

        EditorGUILayout.Space(4);

        // Transform
        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode", "Offset = placeholder rot + this. Absolute = ignore placeholder rot."), rotationMode);
        rotationEuler = EditorGUILayout.Vector3Field(new GUIContent("Rotation (Euler °)"), rotationEuler);
        scaleFactor = Mathf.Max(0.0001f, EditorGUILayout.FloatField(new GUIContent("Scale", "Final scale = placeholder scale × this"), scaleFactor));

        EditorGUILayout.Space(4);

        // Parenting
        using (new EditorGUI.DisabledScope(groupWithEmptyParent))
        {
            var newParent = (Transform)EditorGUILayout.ObjectField(new GUIContent("Parent", "If set, disables grouping"), explicitParent, typeof(Transform), true);
            if (newParent != explicitParent)
            {
                explicitParent = newParent;
                if (explicitParent != null) groupWithEmptyParent = false;
            }
        }

        groupWithEmptyParent = EditorGUILayout.Toggle(new GUIContent("Group with New Empty Parent", "Create or reuse an empty parent per scene"), groupWithEmptyParent);
        if (groupWithEmptyParent && explicitParent != null) explicitParent = null;

        using (new EditorGUI.DisabledScope(!groupWithEmptyParent))
        {
            EditorGUI.indentLevel++;
            groupParentName = EditorGUILayout.TextField(new GUIContent("Empty Parent Name"), groupParentName);
            emptyParentLocation = (EmptyParentLocation)EditorGUILayout.EnumPopup(new GUIContent("Empty Parent Location"), emptyParentLocation);
            using (new EditorGUI.DisabledScope(emptyParentLocation != EmptyParentLocation.Manual))
                manualEmptyParentPosition = EditorGUILayout.Vector3Field(new GUIContent("Position (Manual)"), manualEmptyParentPosition);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // Combine
        combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one", "Static content only"), combineIntoOne);
        using (new EditorGUI.DisabledScope(!combineIntoOne))
        {
            EditorGUI.indentLevel++;
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot", "Origin for the combined object AND preview centering"), pivotMode);
            if ((pivotMode == PivotMode.SelectedObject) && Selection.activeTransform == null)
                EditorGUILayout.HelpBox("Select a Transform in the hierarchy to use as the pivot.", MessageType.Info);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // Move
        moveToWorldCoordinates = EditorGUILayout.Toggle(new GUIContent("Move to world coordinates", "After replacement/combining, move result to position"), moveToWorldCoordinates);
        using (new EditorGUI.DisabledScope(!moveToWorldCoordinates))
        {
            EditorGUI.indentLevel++;
            moveTargetPosition = EditorGUILayout.Vector3Field(new GUIContent("World Position"), moveTargetPosition);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // Collision
        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Rebuild instanced collision", "Reattach your instanced collider script or use MeshCollider"), rebuildInstancedCollision);

        EditorGUILayout.Space(4);

        // Save
        saveAsNewPrefab = EditorGUILayout.Toggle(new GUIContent("Save as new prefab"), saveAsNewPrefab);
        using (new EditorGUI.DisabledScope(!saveAsNewPrefab))
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.BeginHorizontal();
            savePath = EditorGUILayout.TextField("Save Path", savePath);
            if (GUILayout.Button("Select…", GUILayout.Width(80)))
            {
                var suggested = System.IO.Path.GetFileName(savePath);
                var path = EditorUtility.SaveFilePanelInProject("Save Prefab As",
                    string.IsNullOrEmpty(suggested) ? "CombinedPlaceholder" : suggested,
                    "prefab", "Choose save path");
                if (!string.IsNullOrEmpty(path)) savePath = path;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(10);

        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(prefix) || targetPrefab == null || !IsPrefabAsset(targetPrefab)))
        {
            if (GUILayout.Button("Switch Placeholders", GUILayout.Height(34)))
                RunReplace();
        }

        if (targetPrefab != null && !IsPrefabAsset(targetPrefab))
            EditorGUILayout.HelpBox("Selected object is not a Prefab asset. Drag from Project window.", MessageType.Warning);
        else if (string.IsNullOrEmpty(prefix))
            EditorGUILayout.HelpBox("Enter a placeholder prefix (e.g. 'SS_').", MessageType.Info);

        DrawPreview();
    }

    // ---------------- PREVIEW ----------------
    private void InitPreview()
    {
        previewUtil = new PreviewRenderUtility(true);
        previewUtil.cameraFieldOfView = 30f;
        previewUtil.lights[0].intensity = 1.2f;
        previewUtil.lights[1].intensity = 0.8f;
        previewUtil.camera.backgroundColor = Color.white;     // plain white
        previewUtil.camera.clearFlags = CameraClearFlags.Color;
        fallbackMat = new Material(Shader.Find("Standard"));
    }

    private void CleanupPreview()
    {
        previewUtil?.Cleanup();
        if (fallbackMat != null) DestroyImmediate(fallbackMat);
    }

    private void RefreshPreviewMesh()
    {
        previewMesh = null; previewMats = null;
        if (targetPrefab == null) return;
        var mf = targetPrefab.GetComponentInChildren<MeshFilter>();
        var mr = targetPrefab.GetComponentInChildren<MeshRenderer>();
        if (mf != null && mf.sharedMesh != null) previewMesh = mf.sharedMesh;
        if (mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) previewMats = mr.sharedMaterials;
    }

    private void DrawPreview()
    {
        if (previewUtil == null) return;
        var rect = GUILayoutUtility.GetRect(10, 10, 280, 280);

        RefreshPreviewMesh();

        // Find placeholders to preview
        var candidates = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
            .Select(t => t.gameObject)
            .Take(400)
            .ToList();

        // Determine pivot point for preview camera
        var previewPivot = GetPreviewPivot(candidates);

        // Auto-fit distance (unless user touched the camera)
        if (!previewUserAdjusted)
        {
            var mesh = previewMesh != null ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (candidates.Count > 0 && mesh != null)
            {
                var boundsWS = new Bounds(candidates[0].transform.position, Vector3.zero);
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var rot = (rotationMode == RotationMode.Offset)
                        ? go.transform.rotation * Quaternion.Euler(rotationEuler)
                        : Quaternion.Euler(rotationEuler);
                    var scl = go.transform.localScale * scaleFactor;
                    boundsWS.Encapsulate(TransformBounds(mesh.bounds, go.transform.position, rot, scl));
                }
                var halfFovRad = previewUtil.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
                var radius = Mathf.Max(boundsWS.extents.x, boundsWS.extents.y, boundsWS.extents.z);
                previewDistance = Mathf.Clamp(radius / Mathf.Tan(halfFovRad) + radius * 0.25f, 0.4f, 2000f);
                // If pivot mode is BoundsCenter, recentre to that too
                if (pivotMode == PivotMode.BoundsCenter) previewPivot = boundsWS.center;
            }
            else
            {
                previewDistance = 1.6f;
            }
        }

        if (Event.current.type == EventType.Repaint)
        {
            previewUtil.BeginPreview(rect, GUIStyle.none);

            var cam = previewUtil.camera;
            var rot = Quaternion.Euler(previewPitch, previewYaw, 0f);
            cam.transform.position = previewPivot + rot * (Vector3.back * previewDistance);
            cam.transform.rotation = Quaternion.LookRotation(previewPivot - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5000f;

            Mesh mesh = previewMesh != null ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var mats = (previewMats != null && previewMats.Length > 0) ? previewMats : new[] { fallbackMat };

            if (candidates.Count == 0)
            {
                previewUtil.DrawMesh(mesh, Matrix4x4.identity, mats[0], 0);
            }
            else
            {
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var rotObj = (rotationMode == RotationMode.Offset)
                        ? go.transform.rotation * Quaternion.Euler(rotationEuler)
                        : Quaternion.Euler(rotationEuler);
                    var trs = Matrix4x4.TRS(go.transform.position, rotObj, go.transform.localScale * scaleFactor);
                    for (int si = 0; si < Mathf.Min(mesh.subMeshCount, mats.Length); si++)
                        previewUtil.DrawMesh(mesh, trs, mats[si] ? mats[si] : fallbackMat, si);
                }
            }

            cam.Render();
            var tex = previewUtil.EndPreview();
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        // Orbit / Zoom controls
        if (Event.current.type == EventType.MouseDrag && rect.Contains(Event.current.mousePosition))
        {
            if (Event.current.button == 0)
            {
                previewUserAdjusted = true;
                previewYaw += Event.current.delta.x * 0.5f;
                previewPitch = Mathf.Clamp(previewPitch - Event.current.delta.y * 0.5f, -80, 80);
                Repaint();
            }
        }
        if (Event.current.type == EventType.ScrollWheel && rect.Contains(Event.current.mousePosition))
        {
            previewUserAdjusted = true;
            previewDistance = Mathf.Clamp(previewDistance * (1f + Event.current.delta.y * 0.03f), 0.3f, 2000f);
            Repaint();
        }
    }

    private Vector3 GetPreviewPivot(List<GameObject> candidates)
    {
        switch (pivotMode)
        {
            case PivotMode.Parent:
                if (explicitParent) return explicitParent.position;
                if (groupWithEmptyParent)
                {
                    // Approximate where the empty parent would be created
                    return GetEmptyParentPositionForScene(
                        candidates.Where(c => c && c.scene.IsValid()).ToList(),
                        emptyParentLocation,
                        manualEmptyParentPosition);
                }
                goto case PivotMode.BoundsCenter;

            case PivotMode.FirstObject:
                return candidates.Count > 0 && candidates[0] ? candidates[0].transform.position : Vector3.zero;

            case PivotMode.BoundsCenter:
                if (candidates.Count == 0) return Vector3.zero;
                var b = new Bounds(candidates[0].transform.position, Vector3.zero);
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var r = go.GetComponent<Renderer>();
                    if (r) b.Encapsulate(r.bounds);
                    else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
                }
                return b.center;

            case PivotMode.WorldOrigin:
                return Vector3.zero;

            case PivotMode.SelectedObject:
                if (Selection.activeTransform) return Selection.activeTransform.position;
                return Vector3.zero;

            default: return Vector3.zero;
        }
    }

    private static Bounds TransformBounds(Bounds b, Vector3 pos, Quaternion rot, Vector3 scl)
    {
        // Transform AABB corners and re-encapsulate
        var corners = new Vector3[8];
        var ext = b.extents;
        var c = b.center;
        int i = 0;
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
            corners[i++] = new Vector3(c.x + ext.x * x, c.y + ext.y * y, c.z + ext.z * z);

        var bb = new Bounds(pos + rot * Vector3.Scale(corners[0], scl), Vector3.zero);
        for (int k = 1; k < 8; k++)
            bb.Encapsulate(pos + rot * Vector3.Scale(corners[k], scl));
        return bb;
    }

    // ---------------- CORE LOGIC ----------------
    private static bool IsPrefabAsset(GameObject go)
    {
        var t = PrefabUtility.GetPrefabAssetType(go);
        return t != PrefabAssetType.NotAPrefab && t != PrefabAssetType.MissingAsset;
    }

    private void RunReplace()
    {
        var candidates = Resources.FindObjectsOfTypeAll<Transform>()
            .Select(t => t ? t.gameObject : null)
            .Where(go => go != null && go.scene.IsValid() && go.name.StartsWith(prefix))
            .ToList();

        if (candidates.Count == 0)
        {
            EditorUtility.DisplayDialog("No matches", $"No GameObjects starting with '{prefix}' were found.", "OK");
            return;
        }

        // Map candidates per scene (for empty-parent placement)
        var candidatesByScene = new Dictionary<Scene, List<GameObject>>();
        foreach (var go in candidates)
        {
            if (!candidatesByScene.TryGetValue(go.scene, out var list)) { list = new List<GameObject>(); candidatesByScene[go.scene] = list; }
            list.Add(go);
        }

        _groupParentByScene.Clear();
        if (explicitParent == null && groupWithEmptyParent)
        {
            foreach (var kv in candidatesByScene)
            {
                var scene = kv.Key;
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var desiredPos = GetEmptyParentPositionForScene(kv.Value, emptyParentLocation, manualEmptyParentPosition);
                var parent = FindOrCreateGroupParentInScene(scene, groupParentName, desiredPos);
                _groupParentByScene[scene] = parent;
            }
        }

        _nameCounters.Clear();
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Placeholder Switcher");

        var spawned = new List<GameObject>();

        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var src = candidates[i];
                if (src == null) continue;
                if (EditorUtility.DisplayCancelableProgressBar("Switching Placeholders", $"Replacing {i + 1}/{candidates.Count}: {src.name}", (float)(i + 1) / candidates.Count))
                    break;

                Transform groupingParent = explicitParent != null ? explicitParent : null;
                if (groupingParent == null && groupWithEmptyParent)
                {
                    if (_groupParentByScene.TryGetValue(src.scene, out var gp) && gp != null)
                        groupingParent = gp;
                }

                var inst = ReplaceOne(src, targetPrefab, forcedName, useIncrementalNaming, rotationMode, rotationEuler, scaleFactor, groupingParent, _nameCounters);
                if (inst != null) spawned.Add(inst);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Undo.CollapseUndoOperations(group);
        }

        GameObject finalRoot = null;

        if (combineIntoOne && spawned.Count > 0)
        {
            finalRoot = CombineInstances(spawned, pivotMode, explicitParent, GetGroupParentForScene(spawned[0].scene), forcedName);
            foreach (var go in spawned) if (go != null) Undo.DestroyObjectImmediate(go);
        }

        if (moveToWorldCoordinates)
        {
            if (finalRoot != null) finalRoot.transform.position = moveTargetPosition;
            else if (spawned.Count > 0)
            {
                var center = GetWorldCenter(spawned);
                var delta = moveTargetPosition - center;
                foreach (var go in spawned) if (go != null) go.transform.position += delta;
            }
        }

        if (rebuildInstancedCollision)
        {
            if (finalRoot != null) TryRebuildInstancedCollision(finalRoot);
            else foreach (var go in spawned) if (go != null) TryRebuildInstancedCollision(go);
        }

        if (saveAsNewPrefab)
        {
            if (string.IsNullOrEmpty(savePath)) EditorUtility.DisplayDialog("Save path missing", "Choose a prefab save path.", "OK");
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

    private static Transform FindOrCreateGroupParentInScene(Scene scene, string parentName, Vector3 position)
    {
        foreach (var root in scene.GetRootGameObjects())
            if (root != null && root.name == parentName) return root.transform;
        var go = new GameObject(parentName);
        go.transform.position = position;
        Undo.RegisterCreatedObjectUndo(go, "Create Group Parent");
        SceneManager.MoveGameObjectToScene(go, scene);
        return go.transform;
    }

    private static Vector3 GetEmptyParentPositionForScene(List<GameObject> sceneCandidates, EmptyParentLocation loc, Vector3 manual)
    {
        if (loc == EmptyParentLocation.SelectedObject && Selection.activeTransform)
            return Selection.activeTransform.position;

        if (sceneCandidates == null || sceneCandidates.Count == 0)
            return loc == EmptyParentLocation.Manual ? manual : Vector3.zero;

        switch (loc)
        {
            case EmptyParentLocation.FirstObject:
                return sceneCandidates[0] ? sceneCandidates[0].transform.position : Vector3.zero;
            case EmptyParentLocation.BoundsCenter:
                var b = new Bounds(sceneCandidates[0].transform.position, Vector3.zero);
                foreach (var go in sceneCandidates)
                {
                    if (!go) continue;
                    var r = go.GetComponent<Renderer>();
                    if (r != null) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
                }
                return b.center;
            case EmptyParentLocation.WorldOrigin:
                return Vector3.zero;
            case EmptyParentLocation.Manual:
                return manual;
            case EmptyParentLocation.SelectedObject:
                return Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero;
        }
        return Vector3.zero;
    }

    private static Vector3 GetWorldCenter(List<GameObject> objects)
    {
        var b = new Bounds();
        bool init = false;
        foreach (var go in objects)
        {
            if (!go) continue;
            var r = go.GetComponent<Renderer>();
            var center = r ? r.bounds.center : go.transform.position;
            if (!init) { b = new Bounds(center, Vector3.zero); init = true; }
            else b.Encapsulate(center);
        }
        return init ? b.center : Vector3.zero;
    }

    private static GameObject ReplaceOne(GameObject src, GameObject prefab, string forcedName, bool incremental,
        RotationMode rotMode, Vector3 rotEuler, float scaleFactor, Transform groupingParent, Dictionary<string, int> counters)
    {
        if (src == null || prefab == null) return null;

        // Cache
        var parent = src.transform.parent;
        var localPos = src.transform.localPosition;
        var localRot = src.transform.localRotation;
        var localScale = src.transform.localScale;
        var layer = src.layer;
        var tag = src.tag;
        var active = src.activeSelf;
        var staticFlags = GameObjectUtility.GetStaticEditorFlags(src);

        var instanceObj = PrefabUtility.InstantiatePrefab(prefab, src.scene) as GameObject;
        if (instanceObj == null) return null;
        Undo.RegisterCreatedObjectUndo(instanceObj, "Create replacement");

        var newParent = groupingParent != null ? groupingParent : parent;
        instanceObj.transform.SetParent(newParent, false);

        instanceObj.transform.localPosition = localPos;
        instanceObj.transform.localRotation = (rotMode == RotationMode.Offset)
            ? localRot * Quaternion.Euler(rotEuler)
            : Quaternion.Euler(rotEuler);
        instanceObj.transform.localScale = localScale * scaleFactor;

        instanceObj.layer = layer;
        try { instanceObj.tag = tag; } catch { }
        GameObjectUtility.SetStaticEditorFlags(instanceObj, staticFlags);
        instanceObj.SetActive(active);

        // Naming
        if (!string.IsNullOrEmpty(forcedName)) instanceObj.name = ApplyIncremental(forcedName, incremental, counters);
        else instanceObj.name = ApplyIncremental(instanceObj.name, incremental, counters);

        Undo.DestroyObjectImmediate(src); // Remove placeholder
        return instanceObj;
    }

    private static string ApplyIncremental(string baseName, bool incremental, Dictionary<string, int> counters)
    {
        if (!incremental) return baseName;
        if (!counters.TryGetValue(baseName, out var n)) n = 0;
        counters[baseName] = ++n;
        return $"{baseName}_{n:000}";
    }

    private static GameObject CombineInstances(List<GameObject> instances, PivotMode pivotMode, Transform explicitParent, Transform groupParent, string forcedName)
    {
        var filters = new List<MeshFilter>();
        var renderers = new List<MeshRenderer>();
        foreach (var go in instances)
        {
            if (!go) continue;
            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            if (mf && mf.sharedMesh && mr) { filters.Add(mf); renderers.Add(mr); }
        }
        if (filters.Count == 0) { Debug.LogWarning("No MeshFilters found to combine."); return null; }

        Vector3 pivotWS;
        switch (pivotMode)
        {
            default:
            case PivotMode.Parent:
                pivotWS = explicitParent ? explicitParent.position : (groupParent ? groupParent.position : Vector3.zero); break;
            case PivotMode.FirstObject:
                pivotWS = filters[0].transform.position; break;
            case PivotMode.BoundsCenter:
                var b = new Bounds(filters[0].transform.position, Vector3.zero);
                foreach (var mf in filters) { var r = mf.GetComponent<Renderer>(); if (r) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(mf.transform.position, Vector3.zero)); }
                pivotWS = b.center; break;
            case PivotMode.WorldOrigin:
                pivotWS = Vector3.zero; break;
            case PivotMode.SelectedObject:
                pivotWS = Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero; break;
        }
        var pivotToWorld = Matrix4x4.TRS(pivotWS, Quaternion.identity, Vector3.one);

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
                combines.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = pivotToWorld.inverse * mf.transform.localToWorldMatrix });
                materials.Add(mats[s]);
            }
        }

        var finalMesh = new Mesh { name = "Combined_Mesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        finalMesh.CombineMeshes(combines.ToArray(), false, true, false);
        finalMesh.RecalculateBounds();
        if (!finalMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal)) finalMesh.RecalculateNormals();

        var result = new GameObject(string.IsNullOrEmpty(forcedName) ? "Combined Object" : forcedName);
        Undo.RegisterCreatedObjectUndo(result, "Create combined object");
        var parent = explicitParent ? explicitParent : groupParent;
        if (parent) result.transform.SetParent(parent, false);
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
        if (preferredParent) root.transform.SetParent(preferredParent, false);
        foreach (var go in instances) if (go) go.transform.SetParent(root.transform, true);
        return root;
    }

    // Rebuild custom instanced collider (best-effort) or use MeshCollider fallback
    private static void TryRebuildInstancedCollision(GameObject go)
    {
        if (!go) return;
        var type = FindTypeByMonoScriptNames(new[]
        {
            "Instanced Mesh Collider",
            "Instanced Mess Collider",
            "InstancedMeshCollider",
            "InstancedMeshCollision",
        });
        if (type != null && typeof(Component).IsAssignableFrom(type))
        {
            foreach (var c in go.GetComponents(type)) if (c) Undo.DestroyObjectImmediate(c as Component);
            var comp = Undo.AddComponent(go, type);
            var m = type.GetMethod("Rebuild", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?? type.GetMethod("Build", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?? type.GetMethod("Setup", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (m != null) { try { m.Invoke(comp, null); } catch { } }
        }
        else
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf && mf.sharedMesh)
            {
                var mc = go.GetComponent<MeshCollider>();
                if (!mc) mc = Undo.AddComponent<MeshCollider>(go);
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
            if (!ms) continue; var n = ms.name;
            if (names.Contains(n)) { var t = ms.GetClass(); if (t != null) return t; }
        }
        foreach (var n in names) { var t = Type.GetType(n); if (t != null) return t; }
        return null;
    }
}
#endif
