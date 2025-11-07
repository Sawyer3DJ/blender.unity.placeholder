#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// ===============================================
// PlaceholderSwitcher.cs
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
    [SerializeField] private bool keepRotation = true;
    [SerializeField] private float scaleFactor = 1f;

    // --- Parenting options ---
    [SerializeField] private Transform explicitParent = null;
    [SerializeField] private bool groupWithEmptyParent = false;
    [SerializeField] private string groupParentName = "Imported Placeholders";
    private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual }
    [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.FirstObject;
    [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;

    // --- Combine options ---
    [SerializeField] private bool combineIntoOne = false;
    private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin }
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
    private float previewDistance = 3f;
    private Mesh previewMesh;
    private Material previewMat;

    // --- Internal state ---
    private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();
    private readonly Dictionary<string, int> _nameCounters = new Dictionary<string, int>();

    [MenuItem("Tools/Placeholders/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(640, 620);
        w.Show();
    }

    private void OnEnable() => InitPreview();
    private void OnDisable() => CleanupPreview();

    private void OnGUI()
    {
        GUILayout.Label("Replace Object Placeholders", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Combining meshes bakes many objects into ONE renderer. " +
            "Per-object scripts, colliders, triggers, and events are lost. " +
            "If you need interactivity, avoid combining and consider Static Batching instead.",
            MessageType.Warning);

        EditorGUILayout.Space(6);

        // Inputs
        prefix = EditorGUILayout.TextField("Placeholder Prefix", prefix);
        targetPrefab = (GameObject)EditorGUILayout.ObjectField("Desired Asset (Prefab)", targetPrefab, typeof(GameObject), false);
        forcedName = EditorGUILayout.TextField("Forced Name (optional)", forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle("Use incremental naming", useIncrementalNaming);

        EditorGUILayout.Space(4);

        // Transform
        keepRotation = EditorGUILayout.Toggle("Keep rotation", keepRotation);
        scaleFactor = Mathf.Max(0.0001f, EditorGUILayout.FloatField("Scale", scaleFactor));

        EditorGUILayout.Space(4);

        // Parenting
        using (new EditorGUI.DisabledScope(groupWithEmptyParent))
        {
            var newParent = (Transform)EditorGUILayout.ObjectField("Parent", explicitParent, typeof(Transform), true);
            if (newParent != explicitParent)
            {
                explicitParent = newParent;
                if (explicitParent != null) groupWithEmptyParent = false;
            }
        }

        bool prevGroup = groupWithEmptyParent;
        groupWithEmptyParent = EditorGUILayout.Toggle("Group with New Empty Parent", groupWithEmptyParent);
        if (groupWithEmptyParent && explicitParent != null)
            explicitParent = null;

        using (new EditorGUI.DisabledScope(!groupWithEmptyParent))
        {
            EditorGUI.indentLevel++;
            groupParentName = EditorGUILayout.TextField("Empty Parent Name", groupParentName);
            emptyParentLocation = (EmptyParentLocation)EditorGUILayout.EnumPopup("Empty Parent Location", emptyParentLocation);
            using (new EditorGUI.DisabledScope(emptyParentLocation != EmptyParentLocation.Manual))
                manualEmptyParentPosition = EditorGUILayout.Vector3Field("Position (Manual)", manualEmptyParentPosition);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // Combine
        combineIntoOne = EditorGUILayout.Toggle("Combine objects into one", combineIntoOne);
        using (new EditorGUI.DisabledScope(!combineIntoOne))
        {
            EditorGUI.indentLevel++;
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup("Pivot", pivotMode);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // Move
        moveToWorldCoordinates = EditorGUILayout.Toggle("Move to world coordinates", moveToWorldCoordinates);
        using (new EditorGUI.DisabledScope(!moveToWorldCoordinates))
        {
            EditorGUI.indentLevel++;
            moveTargetPosition = EditorGUILayout.Vector3Field("World Position", moveTargetPosition);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // Collision
        rebuildInstancedCollision = EditorGUILayout.Toggle("Rebuild instanced collision", rebuildInstancedCollision);

        EditorGUILayout.Space(4);

        // Save
        saveAsNewPrefab = EditorGUILayout.Toggle("Save as new prefab", saveAsNewPrefab);
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
        previewMat = new Material(Shader.Find("Standard"));
    }

    private void CleanupPreview()
    {
        previewUtil?.Cleanup();
        if (previewMat != null) DestroyImmediate(previewMat);
    }

    private void DrawPreview()
    {
        if (previewUtil == null) return;
        var r = GUILayoutUtility.GetRect(10, 10, 220, 220);
        if (Event.current.type == EventType.Repaint)
        {
            previewUtil.BeginPreview(r, GUIStyle.none);
            Mesh mesh = null;
            Material[] mats = null;
            if (targetPrefab != null)
            {
                var mf = targetPrefab.GetComponentInChildren<MeshFilter>();
                var mr = targetPrefab.GetComponentInChildren<MeshRenderer>();
                if (mf != null) mesh = mf.sharedMesh;
                if (mr != null) mats = mr.sharedMaterials;
            }
            if (mesh == null) mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");

            var cam = previewUtil.camera;
            var rot = Quaternion.Euler(previewPitch, previewYaw, 0f);
            cam.transform.position = rot * (Vector3.back * previewDistance);
            cam.transform.rotation = rot;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 1000f;
            cam.Render();

            previewUtil.DrawMesh(mesh, Matrix4x4.identity, previewMat, 0);
            previewUtil.Render();
            GUI.DrawTexture(r, previewUtil.EndPreview(), ScaleMode.StretchToFill, false);
        }

        if (Event.current.type == EventType.MouseDrag && r.Contains(Event.current.mousePosition))
        {
            if (Event.current.button == 0)
            {
                previewYaw += Event.current.delta.x * 0.5f;
                previewPitch = Mathf.Clamp(previewPitch - Event.current.delta.y * 0.5f, -80, 80);
                Repaint();
            }
        }
        if (Event.current.type == EventType.ScrollWheel && r.Contains(Event.current.mousePosition))
        {
            previewDistance = Mathf.Clamp(previewDistance * (1f + Event.current.delta.y * 0.03f), 0.5f, 100f);
            Repaint();
        }
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

        _nameCounters.Clear();
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Placeholder Switcher");

        var spawned = new List<GameObject>();
        foreach (var src in candidates)
        {
            var inst = ReplaceOne(src, targetPrefab, forcedName, useIncrementalNaming, keepRotation, scaleFactor, explicitParent);
            if (inst != null) spawned.Add(inst);
        }

        EditorUtility.ClearProgressBar();
        Undo.CollapseUndoOperations(group);

        EditorUtility.DisplayDialog("Done", $"Replaced {candidates.Count} placeholders.", "Nice");
    }

    private static GameObject ReplaceOne(GameObject src, GameObject prefab, string forcedName, bool incremental, bool keepRot, float scaleFactor, Transform parent)
    {
        if (src == null || prefab == null) return null;

        var instanceObj = PrefabUtility.InstantiatePrefab(prefab, src.scene) as GameObject;
        Undo.RegisterCreatedObjectUndo(instanceObj, "Create replacement");

        instanceObj.transform.SetParent(parent != null ? parent : src.transform.parent, false);
        instanceObj.transform.localPosition = src.transform.localPosition;
        instanceObj.transform.localRotation = keepRot ? src.transform.localRotation : Quaternion.identity;
        instanceObj.transform.localScale = src.transform.localScale * scaleFactor;

        instanceObj.layer = src.layer;
        instanceObj.tag = src.tag;
        instanceObj.SetActive(src.activeSelf);

        string nameBase = !string.IsNullOrEmpty(forcedName) ? forcedName : instanceObj.name;
        instanceObj.name = incremental ? $"{nameBase}_{UnityEngine.Random.Range(0, 999):000}" : nameBase;

        Undo.DestroyObjectImmediate(src);
        return instanceObj;
    }
}
#endif
