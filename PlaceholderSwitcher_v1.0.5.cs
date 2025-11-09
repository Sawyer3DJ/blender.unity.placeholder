/*
 * Placeholder Switcher — v1.0.5
 * ------------------------------------------------------------
 * CHANGES (v1.0.5)
 * - Toolbar restored and fixed: Open GameObject Library, Randomise All Transform Parameters, Undo.
 * - Preview background modes simplified to two buttons under the viewport:
 *      [ Current Sky (Scene) ] and [ Unity Sky ]
 *   Default = Current Sky (uses RenderSettings). Removed manual skybox picker for 1.0.0 line.
 * - Preview overlay restored: dark screen + guidance until prefix has ≥3 chars OR objects exist.
 * - Preview now draws actual placeholder meshes (not cubes) when possible.
 *   If a placeholder has no MeshFilter/MeshRenderer, falls back to a cube for that entry only.
 * - Orbit invert on Y kept; pan = Shift + LMB or MMB; Re-center View button works.
 * - Combine warning now only shows when combine is ticked (no grayed-out ghost).
 * - Scale Offset:
 *      • Mode wording clarified: “AddsToSeedValue” retained and slider affects result in that mode.
 *      • Influenced Axis always visible.
 *      • Global Scale Clamping min/max + slider always visible.
 *      • Per-axis Axis Clamping (X/Y/Z) with min/max + sliders always visible.
 *      • “Use random scaling seed” row at bottom (always visible) + Randomise Seed works.
 * - Location Offset:
 *      • Location Transform mode kept (Local or World).
 *      • XYZ vector on one line; sliders underneath.
 *      • Influenced Axis block.
 *      • Clamping block (X/Y/Z min/max + sliders).
 *      • Seed row moved to bottom, matching scale layout.
 * - Replace Block (left, above viewport):
 *      • Live prefix counter: ‘⚠️ enter ≥ 3 chars’, ‘⚠️ no assets found’, or ‘N objects found’.
 *      • “Automatically switch placeholders to scene” with warning dialog on enable (64-undo reminder).
 * - Rebuild module renamed/structured:
 *      • [ ] Rebuild Instanced Collision
 *      • [ ] Convert To Shrub
 *          Shrub Render Distance (default 1000)
 *      Conversion is applied BEFORE collision rebuild when used.
 * - “Save From Preview As…” only (removed Save Path). Enabled when a drawable mesh is available.
 * - Version tag now shows v1.0.5.
 *
 * NOTES
 * - Menu path remains: Tools > Placeholder Tools > Placeholder Switcher
 * - This file is a consolidation pass; it should drop-in replace your previous PlaceholderSwitcher.cs.
 */

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlaceholderSwitcher : EditorWindow
{
    // -------------------------
    // Inputs
    // -------------------------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject targetPrefab = null;
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false; // _001, _002…
    [SerializeField] private bool autoSwitchPlaceholdersToScene = false; // shows warning when enabled

    // -------------------------
    // Rotation Offset
    // -------------------------
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero; // for PlaceholderRotation or NewRotation
    [SerializeField] private int rotationSeed = 1234;              // used only in SeedValueOnY

    // -------------------------
    // Scale Offset (reworked)
    // -------------------------
    private enum ScaleMode { PlaceholderScale, NewScale, AddsToSeedValue }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;

    // single base factor (XYZ locked)
    [SerializeField] private float scaleBase = 1f;

    // influenced axes toggles
    [SerializeField] private bool scaleAffectsX = true;
    [SerializeField] private bool scaleAffectsY = true;
    [SerializeField] private bool scaleAffectsZ = true;

    // global clamp (applies to the base uniform factor before per-axis)
    [SerializeField] private float scaleClampMin = 0.5f;
    [SerializeField] private float scaleClampMax = 2.0f;

    // per-axis clamp (applied after global clamp)
    [SerializeField] private float scaleAxisMinX = 0.5f, scaleAxisMaxX = 2.0f;
    [SerializeField] private float scaleAxisMinY = 0.5f, scaleAxisMaxY = 2.0f;
    [SerializeField] private float scaleAxisMinZ = 0.5f, scaleAxisMaxZ = 2.0f;

    // seed
    [SerializeField] private bool useScaleSeed = false;
    [SerializeField] private int scaleSeed = 321;

    // -------------------------
    // Location Offset
    // -------------------------
    private enum LocationSpace { Local, World }
    [SerializeField] private LocationSpace locationSpace = LocationSpace.Local;

    // influenced axes toggles
    [SerializeField] private bool offsetAffectsX = true;
    [SerializeField] private bool offsetAffectsY = true;
    [SerializeField] private bool offsetAffectsZ = true;

    // base offset (XYZ)
    [SerializeField] private Vector3 locationOffset = Vector3.zero;

    // clamping per axis (min/max)
    [SerializeField] private float offsetMinX = -1f, offsetMaxX = 1f;
    [SerializeField] private float offsetMinY = -1f, offsetMaxY = 1f;
    [SerializeField] private float offsetMinZ = -1f, offsetMaxZ = 1f;

    // seed moved to bottom row (format spec)
    [SerializeField] private bool useLocationSeed = false;
    [SerializeField] private int locationSeed = 555;

    // -------------------------
    // Parenting
    // -------------------------
    [SerializeField] private Transform explicitParent = null;
    [SerializeField] private bool groupWithEmptyParent = false;
    [SerializeField] private string groupParentName = "Imported Placeholders";
    private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
    [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.FirstObject;
    [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;

    // -------------------------
    // Combine / Move
    // -------------------------
    [SerializeField] private bool combineIntoOne = false; // static content only
    private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] private PivotMode pivotMode = PivotMode.Parent;

    [SerializeField] private bool moveToWorldCoordinates = false;
    [SerializeField] private Vector3 moveTargetPosition = Vector3.zero;

    // -------------------------
    // Rebuild / Convert
    // -------------------------
    [SerializeField] private bool rebuildInstancedCollision = false;
    [SerializeField] private bool convertToShrub = false;
    [SerializeField] private int shrubRenderDistance = 1000; // default as requested

    // -------------------------
    // Preview
    // -------------------------
    private PreviewRenderUtility previewUtil;
    private float previewYaw = -30f;
    private float previewPitch = 15f;
    private float previewDistance = 3f;
    private bool previewUserAdjusted = false;
    private Mesh previewMesh;
    private Material[] previewMats;
    private Material fallbackMat;
    private Vector3 previewPivotOffset = Vector3.zero;

    private enum PreviewBg { CurrentSky, UnitySky }
    [SerializeField] private PreviewBg previewBg = PreviewBg.CurrentSky;

    // -------------------------
    // State/Helpers
    // -------------------------
    private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();
    private readonly Dictionary<string, int> _nameCounters = new Dictionary<string, int>();

    [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(980, 720);
        w.Show();
    }

    private void OnEnable() => InitPreview();
    private void OnDisable() => CleanupPreview();

    private void InitPreview()
    {
        previewUtil = new PreviewRenderUtility(true);
        previewUtil.cameraFieldOfView = 30f;
        previewUtil.lights[0].intensity = 1.2f;
        previewUtil.lights[1].intensity = 0.8f;
        fallbackMat = new Material(Shader.Find("Standard"));
        ApplyPreviewBackground();
    }

    private void CleanupPreview()
    {
        if (previewUtil != null)
        {
            previewUtil.Cleanup();
            previewUtil = null;
        }
        if (fallbackMat != null) DestroyImmediate(fallbackMat);
        fallbackMat = null;
    }

    private void ApplyPreviewBackground()
    {
        if (previewUtil == null) return;
        var cam = previewUtil.camera;
        if (previewBg == PreviewBg.CurrentSky)
        {
            if (RenderSettings.skybox != null) cam.clearFlags = CameraClearFlags.Skybox;
            else { cam.clearFlags = CameraClearFlags.Color; cam.backgroundColor = RenderSettings.ambientLight; }
        }
        else
        {
            cam.clearFlags = CameraClearFlags.Color;
            cam.backgroundColor = new Color(0.36f, 0.36f, 0.42f, 1f);
        }
    }

    // ======================================================
    // GUI
    // ======================================================
    private void OnGUI()
    {
        // ==== Title + Top Toolbar ====
        GUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18
            };
            GUILayout.Label("Placeholder Switcher", titleStyle, GUILayout.ExpandWidth(true));
        }
        GUILayout.Space(2);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open GameObject Library", GUILayout.Height(22)))
            {
                OpenGameObjectLibrary();
            }
            if (GUILayout.Button("Randomise All Transform Parameters", GUILayout.Height(22)))
            {
                RandomiseAllTransformParameters();
            }
            if (GUILayout.Button("Undo", GUILayout.Height(22)))
            {
                EditorApplication.ExecuteMenuItem("Edit/Undo");
            }
        }
        GUILayout.Space(6);

        // Split: Left (Replace + Preview + Save) | Right (Tools)
        EditorGUILayout.BeginHorizontal();

        // -------- Left Column --------
        EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(position.width * 0.48f, 420f)));

        // Replace block ABOVE the viewer
        DrawReplaceBlock();

        // Viewer
        var rect = GUILayoutUtility.GetRect(10, 10, 380, 380);
        DrawPreviewArea(rect);

        // Background buttons under the viewer
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Viewer Background:", GUILayout.Width(130));
            var was = previewBg;
            if (GUILayout.Toggle(previewBg == PreviewBg.CurrentSky, "Current Sky (Scene)", "Button", GUILayout.Width(160)))
                previewBg = PreviewBg.CurrentSky;
            if (GUILayout.Toggle(previewBg == PreviewBg.UnitySky, "Unity Sky", "Button", GUILayout.Width(120)))
                previewBg = PreviewBg.UnitySky;
            if (previewBg != was) ApplyPreviewBackground();
        }

        // Load / Save header
        GUILayout.Space(6);
        DrawSectionHeader("Load / Save");

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!CanSaveFromPreview()))
            {
                if (GUILayout.Button("Save From Preview As…", GUILayout.Height(24)))
                {
                    var path = EditorUtility.SaveFilePanelInProject("Save Prefab As", "NewPlaceholderAsset", "prefab", "Choose a location to save the prefab");
                    if (!string.IsNullOrEmpty(path))
                    {
                        SaveCurrentPreviewAsPrefab(path);
                    }
                }
            }

            if (GUILayout.Button("Load Asset From File", GUILayout.Height(24)))
            {
                string p = EditorUtility.OpenFilePanel("Load 3D Asset", "Assets", "fbx,obj,gltf,glb");
                if (!string.IsNullOrEmpty(p))
                {
                    Debug.Log($"Selected external asset path: {p} (Import via standard pipeline to use as Prefab.)");
                }
            }
        }

        if (GUILayout.Button("Re-center View", GUILayout.Height(22)))
        {
            previewUserAdjusted = false;
            previewPivotOffset = Vector3.zero;
            Repaint();
        }

        GUILayout.Space(6);
        EditorGUILayout.EndVertical();

        // -------- Right Column (scroll vertical only) --------
        EditorGUILayout.BeginVertical();
        var rightScroll = EditorGUILayout.BeginScrollView(Vector2.zero, false, true);

        DrawSectionHeader("Transform Tools");

        DrawRotationOffset();
        DrawScaleOffset();
        DrawLocationOffset();
        DrawParenting();
        DrawCombineMove();
        DrawRebuild();

        GUILayout.Space(12);
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(prefix) || targetPrefab == null || !IsPrefabAsset(targetPrefab)))
        {
            var btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 14, fixedHeight = 38 };
            if (GUILayout.Button("Switch Placeholders", btnStyle))
            {
                RunReplace();
            }
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        // bottom-left version
        var vStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.LowerLeft };
        GUILayout.FlexibleSpace();
        GUILayout.Label("v1.0.5", vStyle);
    }

    // -------------------------
    // UI Blocks
    // -------------------------
    private void DrawReplaceBlock()
    {
        DrawSectionHeader("Replace Object Placeholders");

        using (new EditorGUILayout.HorizontalScope())
        {
            prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix", "e.g. 'SS_'"), prefix);
            int count = CountPlaceholders(prefix);
            if (prefix.Length < 3)
            {
                EditorGUILayout.LabelField("⚠️ enter ≥ 3 chars", GUILayout.Width(120));
            }
            else if (count <= 0)
            {
                EditorGUILayout.LabelField("⚠️ no assets found", GUILayout.Width(120));
            }
            else
            {
                EditorGUILayout.LabelField($"{count} objects found", GUILayout.Width(120));
            }
        }

        targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), targetPrefab, typeof(GameObject), false);
        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);

        bool prevAuto = autoSwitchPlaceholdersToScene;
        autoSwitchPlaceholdersToScene = EditorGUILayout.ToggleLeft(new GUIContent("Automatically switch placeholders to scene"), autoSwitchPlaceholdersToScene);
        if (!prevAuto && autoSwitchPlaceholdersToScene)
        {
            EditorUtility.DisplayDialog(
                "Warning",
                "Placeholders will be switched with the selected asset in real time. You only have 64 undos—use them wisely!",
                "OK");
        }

        if (targetPrefab != null && !IsPrefabAsset(targetPrefab))
            EditorGUILayout.HelpBox("Selected object is not a Prefab asset. Drag a prefab from the Project window.", MessageType.Warning);
        else if (string.IsNullOrEmpty(prefix))
            EditorGUILayout.HelpBox("Enter a placeholder prefix (e.g. 'SS_').", MessageType.Info);
    }

    private void DrawRotationOffset()
    {
        DrawSectionHeader("Rotation Offset");

        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);

        if (rotationMode == RotationMode.PlaceholderRotation || rotationMode == RotationMode.NewRotation)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(rotationMode == RotationMode.PlaceholderRotation ? "Rotation (adds to placeholder)" : "Rotation (new rotation)", GUILayout.Width(210));
                rotationEuler = EditorGUILayout.Vector3Field(GUIContent.none, rotationEuler);
            }
            rotationEuler.x = EditorGUILayout.Slider("X", rotationEuler.x, -180f, 180f);
            rotationEuler.y = EditorGUILayout.Slider("Y", rotationEuler.y, -180f, 180f);
            rotationEuler.z = EditorGUILayout.Slider("Z", rotationEuler.z, -180f, 180f);
        }
        else
        {
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Random rotation seed (Y)", GUILayout.Width(180));
                rotationSeed = EditorGUILayout.IntField(rotationSeed, GUILayout.Width(120));
                if (GUILayout.Button("Randomise Seed", GUILayout.Width(140)))
                    rotationSeed = UnityEngine.Random.Range(1, 2_000_000_000);
            }
            EditorGUILayout.HelpBox("Applies a deterministic random Y rotation per object using the seed.", MessageType.Info);
        }
    }

    private void DrawScaleOffset()
    {
        DrawSectionHeader("Scale Offset");

        scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Influenced Axis", GUILayout.Width(130));
            scaleAffectsX = GUILayout.Toggle(scaleAffectsX, "X", "Button", GUILayout.Width(40));
            scaleAffectsY = GUILayout.Toggle(scaleAffectsY, "Y", "Button", GUILayout.Width(40));
            scaleAffectsZ = GUILayout.Toggle(scaleAffectsZ, "Z", "Button", GUILayout.Width(40));
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Scale", GUILayout.Width(130));
            scaleBase = EditorGUILayout.Slider(scaleBase, 0.01f, 10f);
        }

        GUILayout.Space(2);
        GUILayout.Label("Scale Clamping", EditorStyles.miniBoldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            scaleClampMin = EditorGUILayout.FloatField(new GUIContent("Min"), scaleClampMin, GUILayout.Width(150));
            scaleClampMax = EditorGUILayout.FloatField(new GUIContent("Max"), scaleClampMax, GUILayout.Width(150));
            float tmpMin = scaleClampMin, tmpMax = scaleClampMax;
            EditorGUILayout.MinMaxSlider(ref tmpMin, ref tmpMax, 0.01f, 10f);
            scaleClampMin = tmpMin; scaleClampMax = tmpMax;
        }

        GUILayout.Space(2);
        GUILayout.Label("Axis Clamping", EditorStyles.miniBoldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            DrawAxisClampRow("X", ref scaleAxisMinX, ref scaleAxisMaxX, 0.01f, 10f);
            DrawAxisClampRow("Y", ref scaleAxisMinY, ref scaleAxisMaxY, 0.01f, 10f);
            DrawAxisClampRow("Z", ref scaleAxisMinZ, ref scaleAxisMaxZ, 0.01f, 10f);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            useScaleSeed = EditorGUILayout.ToggleLeft("Use random scaling seed", useScaleSeed, GUILayout.Width(200));
            using (new EditorGUI.DisabledScope(!useScaleSeed))
            {
                scaleSeed = EditorGUILayout.IntField(scaleSeed, GUILayout.Width(120));
                if (GUILayout.Button("Randomise Seed", GUILayout.Width(140)))
                    scaleSeed = UnityEngine.Random.Range(1, 2_000_000_000);
            }
        }
    }

    private void DrawAxisClampRow(string label, ref float min, ref float max, float globalMin, float globalMax)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(label, GUILayout.Width(20));
            min = EditorGUILayout.FloatField(min, GUILayout.Width(80));
            max = EditorGUILayout.FloatField(max, GUILayout.Width(80));
            float a = min, b = max;
            EditorGUILayout.MinMaxSlider(ref a, ref b, globalMin, globalMax);
            min = a; max = b;
        }
    }

    private void DrawLocationOffset()
    {
        DrawSectionHeader("Location Offset");

        locationSpace = (LocationSpace)EditorGUILayout.EnumPopup(new GUIContent("Location Transform Mode"), locationSpace);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Location Transform", GUILayout.Width(130));
            locationOffset = EditorGUILayout.Vector3Field(GUIContent.none, locationOffset);
        }
        locationOffset.x = EditorGUILayout.Slider("X", locationOffset.x, -10f, 10f);
        locationOffset.y = EditorGUILayout.Slider("Y", locationOffset.y, -10f, 10f);
        locationOffset.z = EditorGUILayout.Slider("Z", locationOffset.z, -10f, 10f);

        GUILayout.Space(2);
        GUILayout.Label("Influenced Axis", EditorStyles.miniBoldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            offsetAffectsX = GUILayout.Toggle(offsetAffectsX, "X", "Button", GUILayout.Width(40));
            offsetAffectsY = GUILayout.Toggle(offsetAffectsY, "Y", "Button", GUILayout.Width(40));
            offsetAffectsZ = GUILayout.Toggle(offsetAffectsZ, "Z", "Button", GUILayout.Width(40));
        }

        GUILayout.Space(2);
        GUILayout.Label("Clamping", EditorStyles.miniBoldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            DrawAxisClampRow("X", ref offsetMinX, ref offsetMaxX, -10f, 10f);
            DrawAxisClampRow("Y", ref offsetMinY, ref offsetMaxY, -10f, 10f);
            DrawAxisClampRow("Z", ref offsetMinZ, ref offsetMaxZ, -10f, 10f);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            useLocationSeed = EditorGUILayout.ToggleLeft("Use random location seed", useLocationSeed, GUILayout.Width(200));
            using (new EditorGUI.DisabledScope(!useLocationSeed))
            {
                locationSeed = EditorGUILayout.IntField(locationSeed, GUILayout.Width(120));
                if (GUILayout.Button("Randomise Location", GUILayout.Width(160)))
                    locationSeed = UnityEngine.Random.Range(1, 2_000_000_000);
            }
        }
    }

    private void DrawParenting()
    {
        DrawSectionHeader("Parenting");

        using (new EditorGUI.DisabledScope(groupWithEmptyParent))
        {
            var newParent = (Transform)EditorGUILayout.ObjectField(new GUIContent("Parent", "If set, disables grouping"), explicitParent, typeof(Transform), true);
            if (newParent != explicitParent)
            {
                explicitParent = newParent;
                if (explicitParent != null) groupWithEmptyParent = false;
            }
        }

        groupWithEmptyParent = EditorGUILayout.Toggle(new GUIContent("Group with New Empty Parent"), groupWithEmptyParent);
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
    }

    private void DrawCombineMove()
    {
        DrawSectionHeader("Combine / Move");

        combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one", "Static content only"), combineIntoOne);

        using (new EditorGUI.DisabledScope(!combineIntoOne))
        {
            EditorGUI.indentLevel++;
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
            if ((pivotMode == PivotMode.SelectedObject) && Selection.activeTransform == null)
                EditorGUILayout.HelpBox("Select a Transform in the hierarchy to use as the pivot.", MessageType.Info);
            EditorGUI.indentLevel--;
        }

        if (combineIntoOne)
        {
            EditorGUILayout.HelpBox(
                "Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, and triggers are lost.\n" +
                "If you need separate interactivity, avoid combining and consider static batching or parenting.",
                MessageType.Warning);
        }

        moveToWorldCoordinates = EditorGUILayout.Toggle(new GUIContent("Move to world coordinates"), moveToWorldCoordinates);
        using (new EditorGUI.DisabledScope(!moveToWorldCoordinates))
        {
            EditorGUI.indentLevel++;
            moveTargetPosition = EditorGUILayout.Vector3Field(new GUIContent("World Position"), moveTargetPosition);
            EditorGUI.indentLevel--;
        }
    }

    private void DrawRebuild()
    {
        DrawSectionHeader("Rebuild");

        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Rebuild Instanced Collision"), rebuildInstancedCollision);

        convertToShrub = EditorGUILayout.Toggle(new GUIContent("Convert To Shrub"), convertToShrub);
        using (new EditorGUI.DisabledScope(!convertToShrub))
        {
            shrubRenderDistance = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Shrub Render Distance"), shrubRenderDistance));
        }
    }

    private void DrawSectionHeader(string text)
    {
        var s = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
        var rect = GUILayoutUtility.GetRect(10, 22, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, new Color(0.18f, 0.18f, 0.18f, 1f)); // darker row background
        GUI.Label(new Rect(rect.x + 6, rect.y, rect.width, rect.height), text, s);
    }

    // ======================================================
    // Preview
    // ======================================================
    private void DrawPreviewArea(Rect rect)
    {
        bool showOverlay = (prefix.Length < 3) || (CountPlaceholders(prefix) <= 0);

        RefreshPreviewMesh();

        if (Event.current.type == EventType.Repaint)
        {
            previewUtil.BeginPreview(rect, GUIStyle.none);

            var cam = previewUtil.camera;
            var rot = Quaternion.Euler(previewPitch, previewYaw, 0f);

            var candidates = GetPlaceholders(prefix);
            Vector3 pivot = GetPreviewPivot(candidates) + previewPivotOffset;

            if (!previewUserAdjusted)
            {
                // compute a bounds of all candidates (using their renderers)
                if (candidates.Count > 0)
                {
                    Bounds b = default;
                    bool inited = false;
                    foreach (var go in candidates)
                    {
                        if (!go) continue;
                        var rends = go.GetComponentsInChildren<Renderer>();
                        if (rends != null && rends.Length > 0)
                        {
                            foreach (var r in rends)
                            {
                                if (!inited) { b = new Bounds(r.bounds.center, r.bounds.size); inited = true; }
                                else b.Encapsulate(r.bounds);
                            }
                        }
                        else
                        {
                            var p = go.transform.position;
                            if (!inited) { b = new Bounds(p, Vector3.one * 0.5f); inited = true; }
                            else b.Encapsulate(p);
                        }
                    }

                    if (inited)
                    {
                        var halfFovRad = previewUtil.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
                        var radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
                        previewDistance = Mathf.Clamp(radius / Mathf.Tan(halfFovRad) + radius * 0.35f, 0.4f, 2000f);
                        if (pivotMode == PivotMode.BoundsCenter) pivot = b.center + previewPivotOffset;
                    }
                }
                else
                {
                    previewDistance = 1.6f;
                }
            }

            cam.transform.position = pivot + rot * (Vector3.back * previewDistance);
            cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5000f;

            // Draw placeholders with their own meshes/materials when available
            if (candidates.Count == 0)
            {
                Mesh meshToDraw = previewMesh != null ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                var mats = (previewMats != null && previewMats.Length > 0) ? previewMats : new[] { fallbackMat };
                previewUtil.DrawMesh(meshToDraw, Matrix4x4.identity, mats[0], 0);
            }
            else
            {
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var renderers = go.GetComponentsInChildren<Renderer>();
                    bool drew = false;
                    foreach (var r in renderers)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                        {
                            var mats = r.sharedMaterials;
                            int subCount = Mathf.Min(mf.sharedMesh.subMeshCount, (mats != null ? mats.Length : 0));
                            var trs = Matrix4x4.TRS(r.transform.position, r.transform.rotation, r.transform.lossyScale);
                            if (subCount > 0)
                            {
                                for (int si = 0; si < subCount; si++)
                                    previewUtil.DrawMesh(mf.sharedMesh, trs, mats[si] ? mats[si] : fallbackMat, si);
                                drew = true;
                            }
                        }
                    }
                    if (!drew)
                    {
                        // fallback cube for this object
                        var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                        var trs = Matrix4x4.TRS(go.transform.position, go.transform.rotation, go.transform.lossyScale);
                        previewUtil.DrawMesh(cube, trs, fallbackMat, 0);
                    }
                }
            }

            cam.Render();
            var tex = previewUtil.EndPreview();
            UnityEngine.GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        if (showOverlay)
        {
            var overlay = new Rect(rect.x, rect.y, rect.width, rect.height);
            EditorGUI.DrawRect(overlay, new Color(0f, 0f, 0f, 0.65f));
            var style = new GUIStyle(EditorStyles.whiteLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                wordWrap = true
            };
            var msg =
                "No preview yet.\n" +
                "• Enter at least 3 characters in Placeholder Prefix.\n" +
                "• Optionally assign a Desired Asset (Prefab).\n" +
                "Tip: Drag a prefab into this viewer to assign it.\n" +
                "Use the GameObject Library to browse assets.";
            GUI.Label(overlay, msg, style);
        }

        if (rect.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                if (Event.current.button == 0)
                {
                    previewUserAdjusted = true;
                    previewYaw += Event.current.delta.x * 0.5f;
                    previewPitch = Mathf.Clamp(previewPitch + Event.current.delta.y * 0.5f, -80, 80);
                    Repaint();
                }
                else if (Event.current.button == 2 || (Event.current.button == 0 && Event.current.shift))
                {
                    previewUserAdjusted = true;
                    float panScale = previewDistance * 0.0025f;
                    var right = Quaternion.Euler(0, previewYaw, 0) * Vector3.right;
                    var up = Vector3.up;
                    previewPivotOffset += (-right * Event.current.delta.x + up * -Event.current.delta.y) * panScale;
                    Repaint();
                }
            }
            if (Event.current.type == EventType.ScrollWheel)
            {
                previewUserAdjusted = true;
                previewDistance = Mathf.Clamp(previewDistance * (1f + Event.current.delta.y * 0.04f), 0.3f, 2000f);
                Repaint();
            }

            if ((Event.current.type == EventType.DragUpdated || Event.current.type == EventType.DragPerform) && rect.Contains(Event.current.mousePosition))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (Event.current.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        if (obj is GameObject go && IsPrefabAsset(go))
                        {
                            targetPrefab = go;
                            RefreshPreviewMesh();
                            Repaint();
                            break;
                        }
                    }
                }
                Event.current.Use();
            }
        }
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

    private List<GameObject> GetPlaceholders(string pfx)
    {
        if (string.IsNullOrEmpty(pfx) || pfx.Length < 3) return new List<GameObject>();
        var list = Resources.FindObjectsOfTypeAll<Transform>()
            .Select(t => t ? t.gameObject : null)
            .Where(go => go != null && go.scene.IsValid() && go.name.StartsWith(pfx, StringComparison.Ordinal))
            .OrderBy(go => go.name)
            .ToList();
        return list;
    }

    private int CountPlaceholders(string pfx) => GetPlaceholders(pfx).Count;

    private Vector3 GetPreviewPivot(List<GameObject> candidates)
    {
        switch (pivotMode)
        {
            case PivotMode.Parent:
                if (explicitParent) return explicitParent.position;
                if (groupWithEmptyParent && candidates.Count > 0)
                    return GetEmptyParentPositionForScene(candidates, emptyParentLocation, manualEmptyParentPosition);
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
        }
        return Vector3.zero;
    }

    private bool CanSaveFromPreview()
    {
        // Enable when there is at least one candidate or a target prefab mesh to save
        if (targetPrefab != null && IsPrefabAsset(targetPrefab)) return true;
        return CountPlaceholders(prefix) > 0;
    }

    // ======================================================
    // Core
    // ======================================================
    private static bool IsPrefabAsset(GameObject go)
    {
        var t = PrefabUtility.GetPrefabAssetType(go);
        return t != PrefabAssetType.NotAPrefab && t != PrefabAssetType.MissingAsset;
    }

    private void RunReplace()
    {
        var candidates = GetPlaceholders(prefix);

        if (candidates.Count == 0)
        {
            EditorUtility.DisplayDialog("No matches", $"No GameObjects starting with '{prefix}' were found.", "OK");
            return;
        }

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

                var inst = ReplaceOne(src, targetPrefab, forcedName, useIncrementalNaming,
                                      rotationMode, rotationEuler, rotationSeed,
                                      scaleMode, scaleBase, scaleAffectsX, scaleAffectsY, scaleAffectsZ,
                                      scaleClampMin, scaleClampMax,
                                      scaleAxisMinX, scaleAxisMaxX,
                                      scaleAxisMinY, scaleAxisMaxY,
                                      scaleAxisMinZ, scaleAxisMaxZ,
                                      useScaleSeed, scaleSeed,
                                      locationSpace, locationOffset,
                                      offsetAffectsX, offsetAffectsY, offsetAffectsZ,
                                      offsetMinX, offsetMaxX,
                                      offsetMinY, offsetMaxY,
                                      offsetMinZ, offsetMaxZ,
                                      useLocationSeed, locationSeed,
                                      groupingParent, _nameCounters);

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

        // Convert then rebuild collision
        if (convertToShrub)
        {
            if (finalRoot != null) ApplyConvertToShrub(new List<GameObject> { finalRoot }, shrubRenderDistance);
            else ApplyConvertToShrub(spawned, shrubRenderDistance);
        }

        if (rebuildInstancedCollision)
        {
            if (finalRoot != null) RebuildCollision(new List<GameObject> { finalRoot });
            else RebuildCollision(spawned);
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

    private static GameObject ReplaceOne(
        GameObject src, GameObject prefab, string forcedName, bool incremental,
        RotationMode rotMode, Vector3 rotEuler, int rotSeed,
        ScaleMode scMode, float scBase, bool scAxX, bool scAxY, bool scAxZ,
        float scClampMin, float scClampMax,
        float scAxisMinX, float scAxisMaxX,
        float scAxisMinY, float scAxisMaxY,
        float scAxisMinZ, float scAxisMaxZ,
        bool useScSeed, int scSeed,
        LocationSpace locSpace, Vector3 locOffset,
        bool offAxX, bool offAxY, bool offAxZ,
        float offMinX, float offMaxX,
        float offMinY, float offMaxY,
        float offMinZ, float offMaxZ,
        bool useLocSeed, int locSeed,
        Transform groupingParent, Dictionary<String, int> counters)
    {
        if (src == null || prefab == null) return null;

        var parent = src.transform.parent;
        var localPos = src.transform.localPosition;
        var localRot = src.transform.localRotation;
        var localScale = src.transform.localScale;
        var layer = src.layer;
        var tag = src.tag;
        var active = src.activeSelf;
        var staticFlags = GameObjectUtility.GetStaticEditorFlags(src);

        var inst = PrefabUtility.InstantiatePrefab(prefab, src.scene) as GameObject;
        if (inst == null) return null;
        Undo.RegisterCreatedObjectUndo(inst, "Create replacement");

        var newParent = groupingParent != null ? groupingParent : parent;
        inst.transform.SetParent(newParent, false);

        // Rotation
        Quaternion finalRot;
        switch (rotMode)
        {
            default:
            case RotationMode.PlaceholderRotation:
                finalRot = localRot * Quaternion.Euler(rotEuler);
                break;
            case RotationMode.NewRotation:
                finalRot = Quaternion.Euler(rotEuler);
                break;
            case RotationMode.SeedValueOnY:
            {
                int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((rotSeed * 73856093) ^ hash));
                float y = (float)(rng.NextDouble() * 360.0);
                finalRot = Quaternion.Euler(0f, y, 0f);
                break;
            }
        }

        // Scale
        float baseFactor;
        switch (scMode)
        {
            case ScaleMode.PlaceholderScale:
            case ScaleMode.NewScale:
                baseFactor = scBase;
                break;
            case ScaleMode.AddsToSeedValue:
            default:
            {
                float add = 0f;
                if (useScSeed)
                {
                    int h = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                    var rng = new System.Random(unchecked((scSeed * 19349663) ^ h));
                    float u = (float)rng.NextDouble(); // [0,1)
                    add = (u - 0.5f) * 0.5f; // small symmetric variation
                }
                baseFactor = scBase + add;
                break;
            }
        }
        baseFactor = Mathf.Clamp(baseFactor, Mathf.Min(scClampMin, scClampMax), Mathf.Max(scClampMin, scClampMax));

        Vector3 finalScale;
        if (scMode == ScaleMode.NewScale)
        {
            finalScale = new Vector3(
                Mathf.Clamp(scAxX ? baseFactor : 1f, scAxisMinX, scAxisMaxX),
                Mathf.Clamp(scAxY ? baseFactor : 1f, scAxisMinY, scAxisMaxY),
                Mathf.Clamp(scAxZ ? baseFactor : 1f, scAxisMinZ, scAxisMaxZ));
        }
        else
        {
            var mul = new Vector3(
                Mathf.Clamp(scAxX ? baseFactor : 1f, scAxisMinX, scAxisMaxX),
                Mathf.Clamp(scAxY ? baseFactor : 1f, scAxisMinY, scAxisMaxY),
                Mathf.Clamp(scAxZ ? baseFactor : 1f, scAxisMinZ, scAxisMaxZ));
            finalScale = Vector3.Scale(localScale, mul);
        }

        // Location Offset
        Vector3 offset = locOffset;

        if (useLocSeed)
        {
            int h2 = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
            var rng = new System.Random(unchecked((locSeed * 83492791) ^ h2));

            if (offAxX)
            {
                float u = (float)rng.NextDouble();
                offset.x += Mathf.Lerp(offMinX, offMaxX, u);
                offset.x = Mathf.Clamp(offset.x, Mathf.Min(offMinX, offMaxX), Mathf.Max(offMinX, offMaxX));
            }
            if (offAxY)
            {
                float u = (float)rng.NextDouble();
                offset.y += Mathf.Lerp(offMinY, offMaxY, u);
                offset.y = Mathf.Clamp(offset.y, Mathf.Min(offMinY, offMaxY), Mathf.Max(offMinY, offMaxY));
            }
            if (offAxZ)
            {
                float u = (float)rng.NextDouble();
                offset.z += Mathf.Lerp(offMinZ, offMaxZ, u);
                offset.z = Mathf.Clamp(offset.z, Mathf.Min(offMinZ, offMaxZ), Mathf.Max(offMinZ, offMaxZ));
            }
        }
        else
        {
            if (offAxX) offset.x = Mathf.Clamp(offset.x, Mathf.Min(offMinX, offMaxX), Mathf.Max(offMinX, offMaxX));
            if (offAxY) offset.y = Mathf.Clamp(offset.y, Mathf.Min(offMinY, offMaxY), Mathf.Max(offMinY, offMaxY));
            if (offAxZ) offset.z = Mathf.Clamp(offset.z, Mathf.Min(offMinZ, offMaxZ), Mathf.Max(offMinZ, offMaxZ));
        }

        inst.transform.localRotation = finalRot;

        if (locSpace == LocationSpace.Local)
            inst.transform.localPosition = localPos + new Vector3(offAxX ? offset.x : 0f, offAxY ? offset.y : 0f, offAxZ ? offset.z : 0f);
        else
            inst.transform.position = src.transform.position + new Vector3(offAxX ? offset.x : 0f, offAxY ? offset.y : 0f, offAxZ ? offset.z : 0f);

        inst.transform.localScale = finalScale;

        inst.layer = layer;
        try { inst.tag = tag; } catch { }
        GameObjectUtility.SetStaticEditorFlags(inst, staticFlags);
        inst.SetActive(active);

        if (!string.IsNullOrEmpty(forcedName)) inst.name = ApplyIncremental(forcedName, incremental, counters);
        else inst.name = ApplyIncremental(inst.name, incremental, counters);

        Undo.DestroyObjectImmediate(src);
        return inst;
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

    private void SaveCurrentPreviewAsPrefab(string path)
    {
        // If targetPrefab is set, save that mesh; otherwise try to save a merged snapshot of first placeholder
        GameObject temp = null;
        try
        {
            if (targetPrefab != null)
            {
                temp = new GameObject(string.IsNullOrEmpty(forcedName) ? "PreviewAsset" : forcedName);
                var mf = temp.AddComponent<MeshFilter>();
                var mr = temp.AddComponent<MeshRenderer>();
                mf.sharedMesh = previewMesh != null ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                if (previewMats != null && previewMats.Length > 0) mr.sharedMaterials = previewMats;
                else mr.sharedMaterial = fallbackMat;
            }
            else
            {
                var cand = GetPlaceholders(prefix).FirstOrDefault();
                if (cand == null)
                {
                    Debug.LogWarning("Nothing to save from preview.");
                    return;
                }
                temp = UnityEngine.Object.Instantiate(cand);
                temp.name = string.IsNullOrEmpty(forcedName) ? (cand.name + "_Preview") : forcedName;
            }

            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, path);
            if (prefab != null) Debug.Log($"Saved prefab: {path}");
            else Debug.LogError("Failed to save prefab.");
        }
        finally
        {
            if (temp != null) DestroyImmediate(temp);
        }
    }

    private void OpenGameObjectLibrary()
    {
        EditorGUIUtility.ShowObjectPicker<GameObject>(null, false, "", 0);
        wantsMouseMove = true;
        EditorApplication.update += PollObjectPicker;
    }

    private void PollObjectPicker()
    {
        string cmd = Event.current != null ? Event.current.commandName : null;
        var obj = EditorGUIUtility.GetObjectPickerObject() as GameObject;
        if (obj != null && IsPrefabAsset(obj))
        {
            targetPrefab = obj;
            RefreshPreviewMesh();
            Repaint();
        }
        if (cmd == "ObjectSelectorClosed")
        {
            EditorApplication.update -= PollObjectPicker;
        }
    }

    private void RandomiseAllTransformParameters()
    {
        rotationMode = RotationMode.PlaceholderRotation;
        rotationEuler = new Vector3(UnityEngine.Random.Range(-15f, 15f), UnityEngine.Random.Range(-180f, 180f), UnityEngine.Random.Range(-15f, 15f));
        rotationSeed = UnityEngine.Random.Range(1, 2_000_000_000);

        scaleMode = ScaleMode.AddsToSeedValue;
        scaleBase = UnityEngine.Random.Range(0.6f, 1.6f);
        useScaleSeed = true;
        scaleSeed = UnityEngine.Random.Range(1, 2_000_000_000);
        scaleClampMin = 0.4f; scaleClampMax = 2.4f;
        scaleAxisMinX = 0.5f; scaleAxisMaxX = 2.0f;
        scaleAxisMinY = 0.5f; scaleAxisMaxY = 2.0f;
        scaleAxisMinZ = 0.5f; scaleAxisMaxZ = 2.0f;

        locationSpace = LocationSpace.Local;
        locationOffset = new Vector3(UnityEngine.Random.Range(-0.3f, 0.3f), 0f, UnityEngine.Random.Range(-0.3f, 0.3f));
        offsetAffectsX = offsetAffectsY = offsetAffectsZ = true;
        offsetMinX = -1f; offsetMaxX = 1f;
        offsetMinY = -0.2f; offsetMaxY = 0.6f;
        offsetMinZ = -1f; offsetMaxZ = 1f;
        useLocationSeed = true;
        locationSeed = UnityEngine.Random.Range(1, 2_000_000_000);

        Repaint();
    }

    // ======================================================
    // Rebuild helpers
    // ======================================================
    private void ApplyConvertToShrub(List<GameObject> roots, int renderDistance)
    {
        if (roots == null || roots.Count == 0) return;
        var type = FindTypeByMonoScriptNames(new[] { "ConvertToShrub" });
        if (type == null)
        {
            Debug.LogWarning("ConvertToShrub type not found. Skipping conversion.");
            return;
        }

        foreach (var go in roots)
        {
            if (!go) continue;
            var comp = go.GetComponent(type);
            if (comp == null) comp = Undo.AddComponent(go, type);

            SetIntMemberIfExists(comp, "RenderDistance", renderDistance);
            SetIntMemberIfExists(comp, "renderDistance", renderDistance);

            var m = type.GetMethod("Convert", BindingFlags.Public | BindingFlags.Instance)
                    ?? type.GetMethod("Build", BindingFlags.Public | BindingFlags.Instance)
                    ?? type.GetMethod("Apply", BindingFlags.Public | BindingFlags.Instance);
            if (m != null)
            {
                try { m.Invoke(comp, null); } catch { }
            }
        }
    }

    private void RebuildCollision(List<GameObject> roots)
    {
        if (roots == null || roots.Count == 0) return;

        var instancedType = FindTypeByMonoScriptNames(new[]
        {
            "Instanced Mesh Collider",
            "Instanced Mess Collider",
            "InstancedMeshCollider",
            "InstancedMeshCollision",
        });

        foreach (var go in roots)
        {
            if (!go) continue;

            if (instancedType != null && typeof(Component).IsAssignableFrom(instancedType))
            {
                foreach (var c in go.GetComponents(instancedType)) if (c) Undo.DestroyObjectImmediate(c as Component);
                var comp = Undo.AddComponent(go, instancedType);

                var m = instancedType.GetMethod("Rebuild", BindingFlags.Public | BindingFlags.Instance)
                        ?? instancedType.GetMethod("Build", BindingFlags.Public | BindingFlags.Instance)
                        ?? instancedType.GetMethod("Setup", BindingFlags.Public | BindingFlags.Instance);
                if (m != null)
                {
                    try { m.Invoke(comp, null); } catch { }
                }
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
    }

    private static void SetIntMemberIfExists(object instance, string name, int value)
    {
        var t = instance.GetType();
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null && f.FieldType == typeof(int)) { f.SetValue(instance, value); return; }
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null && p.CanWrite && p.PropertyType == typeof(int)) { p.SetValue(instance, value, null); return; }
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
