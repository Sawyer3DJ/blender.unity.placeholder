#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlaceholderSwitcher : EditorWindow
{
    private const string MENU_PATH = "Tools/Placeholder Tools/Placeholder Switcher";

    [MenuItem(MENU_PATH)]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(1080, 700);
        w.Show();
    }

    // ---------- Replace (top-left over viewer) ----------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private UnityEngine.Object desiredAsset;
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false;
    [SerializeField] private bool autoSwitchLive = false;

    // ---------- Rotation / Scale / Location (right) ----------
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero;
    [SerializeField] private int rotationSeed = 1234;

    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private float scaleUnified = 1f;
    [SerializeField] private bool scaleAffectX = true, scaleAffectY = true, scaleAffectZ = true;
    [SerializeField] private int scaleSeed = 321;
    [SerializeField] private float scaleClampMinX = 0.5f, scaleClampMaxX = 2.0f;
    [SerializeField] private float scaleClampMinY = 0.5f, scaleClampMaxY = 2.0f;
    [SerializeField] private float scaleClampMinZ = 0.5f, scaleClampMaxZ = 2.0f;

    private enum LocationSpace { Local, World }
    [SerializeField] private LocationSpace locationSpace = LocationSpace.Local;
    [SerializeField] private Vector3 locationOffset = Vector3.zero;
    [SerializeField] private int locationSeed = 4567;
    [SerializeField] private bool locAffectX = true, locAffectY = true, locAffectZ = true;
    [SerializeField] private float locClampMinX = -1f, locClampMaxX = 1f;
    [SerializeField] private float locClampMinY = -1f, locClampMaxY = 1f;
    [SerializeField] private float locClampMinZ = -1f, locClampMaxZ = 1f;

    // ---------- Parenting / Combine / Move ----------
    [SerializeField] private Transform explicitParent = null;
    [SerializeField] private bool groupWithEmptyParent = false;
    [SerializeField] private string groupParentName = "Imported Placeholders";
    private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
    [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.BoundsCenter;
    [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;

    [SerializeField] private bool combineIntoOne = false;
    private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] private PivotMode pivotMode = PivotMode.Parent;

    private enum MoveTarget { None, Parent, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinates, SelectedObject }
    [SerializeField] private MoveTarget moveTarget = MoveTarget.None;
    [SerializeField] private Vector3 moveWorldPosition = Vector3.zero;

    // ---------- Shrub + collision ----------
    [SerializeField] private bool convertToShrub = false;
    [SerializeField] private int shrubRenderDistance = 1000;
    [SerializeField] private bool rebuildInstancedCollision = false;

    // ---------- Viewer ----------
    private enum ViewerBg { CurrentSkybox, UnitySkybox }
    [SerializeField] private ViewerBg viewerBg = ViewerBg.CurrentSkybox;

    private PreviewRenderUtility preview;
    private Material fallbackMat;
    private float yaw = -30f, pitch = 15f, dist = 6f;
    private bool orbitInvertY = true, orbitInvertX = false;
    private Vector3 pivotOffset = Vector3.zero;
    private bool userAdjustedCam = false;
    private GUIStyle overlayStyle;

    // Cached “multi-part” preview of the Desired Asset
    private struct PreviewPart
    {
        public Mesh mesh;
        public Matrix4x4 local;   // local to prefab root
        public Material[] mats;
    }
    private List<PreviewPart> previewParts; // null = use built-in cube

    // state
    private readonly Dictionary<Scene, Transform> groupParents = new Dictionary<Scene, Transform>();
    private readonly Dictionary<string, int> nameCounters = new Dictionary<string, int>();
    [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

    // aux windows
    private GameObjectLibraryWindow libraryWindow;
    private ExternalViewportWindow viewportWindow;
    [SerializeField] private bool autoSyncViewportCamera = true;

    // UI
    private Vector2 rightScroll;

    // ===== lifecycle =====
    private void OnEnable()
    {
        InitPreview();
    }
    private void OnDisable()
    {
        CleanupPreview();
        CloseViewport();
        if (libraryWindow != null) libraryWindow.Close();
    }

    private void InitPreview()
    {
        preview = new PreviewRenderUtility(true);
        preview.cameraFieldOfView = 30f;
        preview.lights[0].intensity = 1.2f;
        preview.lights[1].intensity = 0.8f;
        fallbackMat = new Material(Shader.Find("Standard"));
        overlayStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12,
            wordWrap = true,
            normal = { textColor = Color.white }
        };
        BuildPreviewParts(); // start with none -> cube fallback
    }
    private void CleanupPreview()
    {
        preview?.Cleanup();
        preview = null;
        if (fallbackMat) DestroyImmediate(fallbackMat);
        fallbackMat = null;
        previewParts = null;
    }

    // ===== GUI =====
    private void OnGUI()
    {
        DrawTitleRow();
        EditorGUILayout.Space(2);

        EditorGUILayout.BeginHorizontal();

        // LEFT column (viewer + replace + load/save)
        EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(560f, position.width * 0.55f)));
        DrawReplaceBlockAboveViewer();
        DrawViewerBlock();
        DrawLoadSaveBlock();
        DrawBigButtons();
        EditorGUILayout.EndVertical();

        // RIGHT column (vertical scroll only)
        EditorGUILayout.BeginVertical();
        rightScroll = EditorGUILayout.BeginScrollView(rightScroll, false, true); // no horizontal scroll
        DrawTransformTools();
        EditorGUILayout.Space(6);
        DrawParenting();
        EditorGUILayout.Space(6);
        DrawCombineMove();
        EditorGUILayout.Space(6);
        DrawShrubCollision();
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        // keep helpers in front
        if (viewportWindow != null)
        {
            UpdateExternalViewport();
            viewportWindow.ForceInFront();
        }
        if (libraryWindow != null) libraryWindow.ForceInFront();

        // live mode
        if (autoSwitchLive && desiredAsset is GameObject && HasMinimumPrefix() && CountPlaceholdersInOpenScenes() > 0)
        {
            if (Event.current.type == EventType.Repaint)
                RunReplace();
        }
    }

    private void DrawTitleRow()
    {
        var title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 20, alignment = TextAnchor.MiddleLeft };
        var sub = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };

        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        GUILayout.Label("Placeholder Switcher", title);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Open GameObject Library", GUILayout.Height(22))) OpenLibrary();
        if (GUILayout.Button("Randomize All Parameters", GUILayout.Height(22))) RandomizeAll();
        GUI.enabled = Undo.GetCurrentGroup() != 0;
        if (GUILayout.Button("Undo", GUILayout.Height(22))) Undo.PerformUndo();
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        // Section headings row (bigger)
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Replace Object Placeholders", sub);
        GUILayout.FlexibleSpace();
        GUILayout.Label("Transform Tools", sub);
        EditorGUILayout.EndHorizontal();
    }

    // ---------- Replace (over viewer) ----------
    private void DrawReplaceBlockAboveViewer()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // prefix + found count / warning
        EditorGUILayout.BeginHorizontal();
        prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
        int found = HasMinimumPrefix() ? CountPlaceholdersInOpenScenes() : 0;
        if (!HasMinimumPrefix())
            EditorGUILayout.LabelField("⚠️ enter ≥ 3 chars", EditorStyles.miniLabel, GUILayout.Width(140));
        else if (found <= 0)
            EditorGUILayout.LabelField("⚠️ no assets found", EditorStyles.miniLabel, GUILayout.Width(140));
        else
            EditorGUILayout.LabelField($"{found} objects found", EditorStyles.miniLabel, GUILayout.Width(140));
        EditorGUILayout.EndHorizontal();

        // desired asset + picker
        EditorGUILayout.BeginHorizontal();
        desiredAsset = EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), desiredAsset, typeof(GameObject), false);
        if (GUILayout.Button("Select…", GUILayout.Width(64))) ShowObjectPickerForPrefab();
        if (Event.current.commandName == "ObjectSelectorUpdated" || Event.current.commandName == "ObjectSelectorClosed")
        {
            var picked = EditorGUIUtility.GetObjectPickerObject();
            if (picked is GameObject go) { desiredAsset = go; BuildPreviewParts(); Repaint(); }
        }
        EditorGUILayout.EndHorizontal();

        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);

        bool before = autoSwitchLive;
        autoSwitchLive = EditorGUILayout.ToggleLeft(new GUIContent("Automatically switch placeholders in scene (live)"),
            autoSwitchLive);
        if (autoSwitchLive && !before)
        {
            EditorUtility.DisplayDialog("Live switching enabled",
                "Warning: placeholders will be switched with selected objects in real time.\nYou have 64 undo steps—use them wisely!",
                "OK");
        }

        EditorGUILayout.EndVertical();
    }

    // ---------- Viewer ----------
    private void DrawViewerBlock()
    {
        var rect = GUILayoutUtility.GetRect(10, 10, 400, Mathf.Clamp(position.height * 0.48f, 360f, 620f));

        // overlay only when <3 chars
        bool showOverlay = !HasMinimumPrefix();
        DrawPreview(rect, showOverlay);

        // background + viewport controls under the viewer
        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        GUILayout.Label("Viewer Background", GUILayout.Width(120));
        if (GUILayout.Toggle(viewerBg == ViewerBg.CurrentSkybox, "Current Skybox", EditorStyles.miniButtonLeft)) viewerBg = ViewerBg.CurrentSkybox;
        if (GUILayout.Toggle(viewerBg == ViewerBg.UnitySkybox,   "Unity Skybox",   EditorStyles.miniButtonRight)) viewerBg = ViewerBg.UnitySkybox;

        GUILayout.FlexibleSpace();

        // big, prominent “auto-sync” toggle
        bool newSync = GUILayout.Toggle(autoSyncViewportCamera, "Auto-Sync model view to Viewport", "Button", GUILayout.Width(240));
        if (newSync != autoSyncViewportCamera) { autoSyncViewportCamera = newSync; Repaint(); }

        if (GUILayout.Button("Open Viewport", GUILayout.Width(120))) OpenViewport();
        if (GUILayout.Button("Close Viewport", GUILayout.Width(120))) CloseViewport();
        EditorGUILayout.EndHorizontal();

        // control legend + re-center
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Controls  •  LMB: Orbit (Y inverted)   Shift+LMB: Pan   Wheel: Zoom", EditorStyles.miniLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Re-center View", GUILayout.Width(120)))
        {
            userAdjustedCam = false;
            pivotOffset = Vector3.zero;
            Repaint();
        }
        EditorGUILayout.EndHorizontal();

        // Save path sits below viewer + controls
        EditorGUILayout.BeginHorizontal();
        savePath = EditorGUILayout.TextField(new GUIContent("Save Path"), savePath);
        if (GUILayout.Button("Select…", GUILayout.Width(70)))
        {
            var suggested = System.IO.Path.GetFileName(savePath);
            var path = EditorUtility.SaveFilePanelInProject("Save Prefab As",
                string.IsNullOrEmpty(suggested) ? "CombinedPlaceholder" : suggested,
                "prefab", "Choose a location");
            if (!string.IsNullOrEmpty(path)) savePath = path;
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawLoadSaveBlock()
    {
        // subheading row (dark)
        DrawSectionHeader("Load / Save");

        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        if (GUILayout.Button("Load Asset From File", GUILayout.Height(24)))
        {
            var path = EditorUtility.OpenFilePanel("Load Model/Prefab", Application.dataPath, "prefab,fbx,obj,gltf,glb,glTF");
            if (!string.IsNullOrEmpty(path))
                EditorUtility.DisplayDialog("Note", "Please import models into the Project first, then select them with the picker.", "OK");
        }

        GUILayout.FlexibleSpace();

        bool canSaveSingle = CountPlaceholdersInOpenScenes() == 1 && desiredAsset is GameObject;
        GUI.enabled = canSaveSingle;
        if (GUILayout.Button("Save From Preview As…", GUILayout.Width(190), GUILayout.Height(24)))
            SaveFromPreviewSingle();
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        int count = CountPlaceholdersInOpenScenes();
        if (!HasMinimumPrefix())
            EditorGUILayout.HelpBox("Enter a prefix (≥ 3 chars) to preview and save.", MessageType.Info);
        else if (count == 0)
            EditorGUILayout.HelpBox("No placeholders found with that prefix.", MessageType.Info);
        else if (count > 1)
            EditorGUILayout.HelpBox("Multiple placeholders detected. Enable 'Combine objects into one' to save them as a single asset.", MessageType.Warning);
    }

    private void DrawBigButtons()
    {
        EditorGUILayout.Space(6);
        GUIStyle big = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 42 };
        GUI.enabled = HasMinimumPrefix() && desiredAsset is GameObject;
        if (GUILayout.Button("Switch Placeholders", big)) RunReplace();
        GUI.enabled = true;
    }

    // ---------- Right: Transform Tools ----------
    private void DrawTransformTools()
    {
        // Rotation Offset
        DrawSectionHeader("Rotation Offset");
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);

        // X/Y/Z same row + sliders under each (like before)
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent("Rotation (adds to placeholder)"), GUILayout.Width(200));
        rotationEuler.x = EditorGUILayout.FloatField("X", rotationEuler.x);
        rotationEuler.y = EditorGUILayout.FloatField("Y", rotationEuler.y);
        rotationEuler.z = EditorGUILayout.FloatField("Z", rotationEuler.z);
        EditorGUILayout.EndHorizontal();
        rotationEuler.x = SliderNarrow(rotationEuler.x, -360f, 360f);
        rotationEuler.y = SliderNarrow(rotationEuler.y, -360f, 360f);
        rotationEuler.z = SliderNarrow(rotationEuler.z, -360f, 360f);

        if (rotationMode == RotationMode.SeedValueOnY)
        {
            EditorGUILayout.BeginHorizontal();
            rotationSeed = ClampInt(EditorGUILayout.IntField("Random rotation seed (Y)", rotationSeed), 1, int.MaxValue);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                rotationSeed = UnityEngine.Random.Range(1, int.MaxValue);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("Per-object deterministic Y rotation from seed; offset above is added on top.", MessageType.Info);
        }
        EditorGUILayout.EndVertical();

        // Scale Offset
        DrawSectionHeader("Scale Offset");
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent("Scale (multiplies placeholder scale)"), GUILayout.Width(220));
        scaleUnified = Mathf.Max(0.0001f, EditorGUILayout.FloatField(scaleUnified));
        EditorGUILayout.EndHorizontal();
        scaleUnified = SliderNarrow(scaleUnified, 0.05f, 8f);

        GUILayout.Space(4);
        GUILayout.Label("Influenced Axes", EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        scaleAffectX = GUILayout.Toggle(scaleAffectX, "X", "Button", GUILayout.Width(30));
        scaleAffectY = GUILayout.Toggle(scaleAffectY, "Y", "Button", GUILayout.Width(30));
        scaleAffectZ = GUILayout.Toggle(scaleAffectZ, "Z", "Button", GUILayout.Width(30));
        EditorGUILayout.EndHorizontal();

        if (scaleMode == ScaleMode.SeedValue)
        {
            GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            scaleSeed = ClampInt(EditorGUILayout.IntField("Random scaling seed", scaleSeed), 1, int.MaxValue);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                scaleSeed = UnityEngine.Random.Range(1, int.MaxValue);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(2);
            GUILayout.Label("Scale clamping", EditorStyles.miniBoldLabel);
            DrawMinMaxRow("X Min/Max", ref scaleClampMinX, ref scaleClampMaxX, 0.05f, 8f);
            DrawMinMaxRow("Y Min/Max", ref scaleClampMinY, ref scaleClampMaxY, 0.05f, 8f);
            DrawMinMaxRow("Z Min/Max", ref scaleClampMinZ, ref scaleClampMaxZ, 0.05f, 8f);
        }
        EditorGUILayout.EndVertical();

        // Location Offset
        DrawSectionHeader("Location Offset");
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        locationSpace = (LocationSpace)EditorGUILayout.EnumPopup(new GUIContent("Location Transform Mode"), locationSpace);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent("Location Transform"), GUILayout.Width(160));
        locationOffset.x = EditorGUILayout.FloatField("X", locationOffset.x);
        locationOffset.y = EditorGUILayout.FloatField("Y", locationOffset.y);
        locationOffset.z = EditorGUILayout.FloatField("Z", locationOffset.z);
        EditorGUILayout.EndHorizontal();
        locationOffset.x = SliderNarrow(locationOffset.x, -10f, 10f);
        locationOffset.y = SliderNarrow(locationOffset.y, -10f, 10f);
        locationOffset.z = SliderNarrow(locationOffset.z, -10f, 10f);

        GUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        locationSeed = ClampInt(EditorGUILayout.IntField("Random location seed", locationSeed), 1, int.MaxValue);
        if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
            locationSeed = UnityEngine.Random.Range(1, int.MaxValue);
        EditorGUILayout.EndHorizontal();

        GUILayout.Label("Influenced Axes", EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        locAffectX = GUILayout.Toggle(locAffectX, "X", "Button", GUILayout.Width(30));
        locAffectY = GUILayout.Toggle(locAffectY, "Y", "Button", GUILayout.Width(30));
        locAffectZ = GUILayout.Toggle(locAffectZ, "Z", "Button", GUILayout.Width(30));
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(2);
        GUILayout.Label("Clamping", EditorStyles.miniBoldLabel);
        DrawMinMaxRow("X Min/Max", ref locClampMinX, ref locClampMaxX, -100f, 100f);
        DrawMinMaxRow("Y Min/Max", ref locClampMinY, ref locClampMaxY, -100f, 100f);
        DrawMinMaxRow("Z Min/Max", ref locClampMinZ, ref locClampMaxZ, -100f, 100f);

        EditorGUILayout.EndVertical();
    }

    private void DrawParenting()
    {
        DrawSectionHeader("Parenting");
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        explicitParent = (Transform)EditorGUILayout.ObjectField(new GUIContent("Parent (optional)"), explicitParent, typeof(Transform), true);
        groupWithEmptyParent = EditorGUILayout.Toggle(new GUIContent("Group with New Empty Parent"), groupWithEmptyParent);
        using (new EditorGUI.DisabledScope(!groupWithEmptyParent))
        {
            groupParentName = EditorGUILayout.TextField(new GUIContent("Empty Parent Name"), groupParentName);
            emptyParentLocation = (EmptyParentLocation)EditorGUILayout.EnumPopup(new GUIContent("Empty Parent Location"), emptyParentLocation);
            if (emptyParentLocation == EmptyParentLocation.Manual)
                manualEmptyParentPosition = EditorGUILayout.Vector3Field(new GUIContent("Position (Manual)"), manualEmptyParentPosition);
        }
        EditorGUILayout.EndVertical();
    }

    private void DrawCombineMove()
    {
        DrawSectionHeader("Combine / Move");
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one"), combineIntoOne);
        using (new EditorGUI.DisabledScope(!combineIntoOne))
        {
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
            EditorGUILayout.HelpBox("Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, and triggers are lost. If you need to move many separate objects together, put them under a parent instead of combining.", MessageType.Warning);
        }

        moveTarget = (MoveTarget)EditorGUILayout.EnumPopup(new GUIContent("Move all objects to"), moveTarget);
        using (new EditorGUI.DisabledScope(moveTarget != MoveTarget.WorldCoordinates))
        {
            moveWorldPosition = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), moveWorldPosition);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawShrubCollision()
    {
        DrawSectionHeader("Rebuild Instanced Collision");
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        convertToShrub = EditorGUILayout.Toggle(new GUIContent("Convert to Shrub"), convertToShrub);
        using (new EditorGUI.DisabledScope(!convertToShrub))
        {
            shrubRenderDistance = ClampInt(EditorGUILayout.IntField(new GUIContent("Shrub Render Distance"), shrubRenderDistance), 1, int.MaxValue);
        }
        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Enable"), rebuildInstancedCollision);
        EditorGUILayout.EndVertical();
    }

    // ===== Preview =====
    private void DrawPreview(Rect rect, bool showOverlay)
    {
        if (preview == null) return;

        // camera background
        var cam = preview.camera;
        if (viewerBg == ViewerBg.CurrentSkybox)
        {
            cam.clearFlags = RenderSettings.skybox ? CameraClearFlags.Skybox : CameraClearFlags.Color;
            if (cam.clearFlags == CameraClearFlags.Color) cam.backgroundColor = RenderSettings.ambientLight;
        }
        else
        {
            cam.clearFlags = CameraClearFlags.Color;
            cam.backgroundColor = new Color(0.36f, 0.46f, 0.65f, 1f); // “Unity” sky colour
        }

        // gather placeholders
        var candidates = HasMinimumPrefix()
            ? Resources.FindObjectsOfTypeAll<Transform>().Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix)).Select(t => t.gameObject).Take(600).ToList()
            : new List<GameObject>();

        // framing
        var pivot = ComputePreviewPivot(candidates) + pivotOffset;
        if (!userAdjustedCam)
        {
            if (candidates.Count > 0)
            {
                var b = new Bounds(candidates[0].transform.position, Vector3.zero);
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var r = go.GetComponent<Renderer>();
                    if (r) b.Encapsulate(r.bounds);
                    else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
                }
                float radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
                float halfFov = preview.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
                dist = Mathf.Clamp(radius / Mathf.Tan(halfFov) + radius * 0.25f, 1f, 5000f);
            }
            else dist = 6f;
        }

        // draw
        if (Event.current.type == EventType.Repaint)
        {
            preview.BeginPreview(rect, GUIStyle.none);

            var invY = orbitInvertY ? -1f : 1f;
            var invX = orbitInvertX ? -1f : 1f;
            var rot = Quaternion.Euler(invY * pitch, invX * yaw, 0f);
            cam.transform.position = pivot + rot * (Vector3.back * dist);
            cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 10000f;

            // choose a fallback one-part cube if no desiredAsset
            if (candidates.Count == 0)
            {
                // empty scene – draw a cube just to show something
                var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                preview.DrawMesh(cube, Matrix4x4.identity, fallbackMat, 0);
            }
            else
            {
                // draw each placeholder either with prefab parts or with a cube fallback
                var parts = (previewParts != null && previewParts.Count > 0) ? previewParts : null;
                var cube = parts == null ? Resources.GetBuiltinResource<Mesh>("Cube.fbx") : null;

                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var tr = go.transform;
                    var rotObj = GetPreviewRotation(tr);
                    var sclObj = GetPreviewScale(tr);
                    var posObj = GetPreviewPosition(tr);
                    var rootTRS = Matrix4x4.TRS(posObj, rotObj, sclObj);

                    if (parts != null)
                    {
                        foreach (var p in parts)
                        {
                            var mtx = rootTRS * p.local; // local matrix of that child
                            var mats = p.mats != null && p.mats.Length > 0 ? p.mats : new[] { fallbackMat };
                            int subCount = Mathf.Min(p.mesh != null ? p.mesh.subMeshCount : 0, mats.Length);
                            for (int s = 0; s < subCount; s++)
                                preview.DrawMesh(p.mesh, mtx, mats[s] ? mats[s] : fallbackMat, s);
                        }
                    }
                    else
                    {
                        preview.DrawMesh(cube, rootTRS, fallbackMat, 0);
                    }
                }
            }

            cam.Render();
            var tex = preview.EndPreview();
            GUI.DrawTexture(rect, tex, UnityEngine.ScaleMode.StretchToFill, false); // fully-qualified (no enum clash)
        }

        // overlay (instructions) only when the prefix is too short
        if (showOverlay)
        {
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.78f));
            GUI.Label(rect,
                "Enter a prefix (≥ 3 chars) to preview placeholders.\n" +
                "Tip: ‘Open GameObject Library’ to pick a Desired Asset quickly.\n\n" +
                "Try random seeds and Location Offset to synthesize new variations.",
                overlayStyle);
        }

        // mouse controls in rect
        if (rect.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                if (Event.current.button == 0)
                {
                    userAdjustedCam = true;
                    yaw += Event.current.delta.x * 0.5f;
                    pitch = Mathf.Clamp(pitch - Event.current.delta.y * 0.5f, -80, 80);
                    Repaint();
                }
                else if (Event.current.button == 2 || (Event.current.button == 0 && Event.current.shift))
                {
                    userAdjustedCam = true;
                    float panScale = dist * 0.0025f;
                    var right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
                    var up = Vector3.up;
                    pivotOffset += (-right * Event.current.delta.x + up * Event.current.delta.y) * panScale;
                    Repaint();
                }
            }
            if (Event.current.type == EventType.ScrollWheel)
            {
                userAdjustedCam = true;
                dist = Mathf.Clamp(dist * (1f + Event.current.delta.y * 0.04f), 0.3f, 5000f);
                Repaint();
            }
        }
    }

    private void BuildPreviewParts()
    {
        previewParts = null;
        var go = desiredAsset as GameObject;
        if (!go) return;

        // collect all MeshFilters in the prefab with their local matrices and materials
        var mfs = go.GetComponentsInChildren<MeshFilter>(true);
        if (mfs == null || mfs.Length == 0) return;
        previewParts = new List<PreviewPart>(mfs.Length);

        foreach (var mf in mfs)
        {
            if (!mf || !mf.sharedMesh) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            var mats = mr ? mr.sharedMaterials : null;
            // matrix from prefab root to this child
            var local = go.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            previewParts.Add(new PreviewPart
            {
                mesh = mf.sharedMesh,
                local = local,
                mats = mats
            });
        }
    }

    private Vector3 ComputePreviewPivot(List<GameObject> candidates)
    {
        if (candidates == null || candidates.Count == 0) return Vector3.zero;
        switch (pivotMode)
        {
            case PivotMode.Parent:
                if (explicitParent) return explicitParent.position;
                return BoundsCenter(candidates).center;
            case PivotMode.FirstObject: return candidates[0].transform.position;
            case PivotMode.BoundsCenter: return BoundsCenter(candidates).center;
            case PivotMode.WorldOrigin: return Vector3.zero;
            case PivotMode.SelectedObject: return Selection.activeTransform ? Selection.activeTransform.position : BoundsCenter(candidates).center;
        }
        return Vector3.zero;
    }
    private Bounds BoundsCenter(List<GameObject> list)
    {
        var b = new Bounds(list[0].transform.position, Vector3.zero);
        foreach (var go in list)
        {
            if (!go) continue;
            var r = go.GetComponent<Renderer>();
            if (r) b.Encapsulate(r.bounds);
            else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
        }
        return b;
    }

    private Quaternion GetPreviewRotation(Transform t)
    {
        switch (rotationMode)
        {
            case RotationMode.NewRotation:
                return Quaternion.Euler(rotationEuler);
            case RotationMode.SeedValueOnY:
                int hash = (t.GetInstanceID() ^ (t.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((rotationSeed * 73856093) ^ hash));
                float y = (float)(rng.NextDouble() * 360.0);
                return Quaternion.Euler(0, y, 0) * Quaternion.Euler(rotationEuler);
            default:
                return t.rotation * Quaternion.Euler(rotationEuler);
        }
    }
    private Vector3 GetPreviewScale(Transform t)
    {
        Vector3 scale = t.localScale;
        float sx = scaleAffectX ? scaleUnified : 1f;
        float sy = scaleAffectY ? scaleUnified : 1f;
        float sz = scaleAffectZ ? scaleUnified : 1f;

        switch (scaleMode)
        {
            case ScaleMode.NewScale: return new Vector3(sx, sy, sz);
            case ScaleMode.SeedValue:
                {
                    int hash = (t.GetInstanceID() ^ (t.name.GetHashCode() << 1));
                    var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                    float rx = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxX - scaleClampMinX) + scaleClampMinX, scaleClampMinX, scaleClampMaxX);
                    float ry = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxY - scaleClampMinY) + scaleClampMinY, scaleClampMinY, scaleClampMaxY);
                    float rz = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxZ - scaleClampMinZ) + scaleClampMinZ, scaleClampMinZ, scaleClampMaxZ);
                    if (!scaleAffectX) rx = 1f; if (!scaleAffectY) ry = 1f; if (!scaleAffectZ) rz = 1f;
                    return new Vector3(rx, ry, rz);
                }
            default:
                return new Vector3(scale.x * sx, scale.y * sy, scale.z * sz);
        }
    }
    private Vector3 GetPreviewPosition(Transform t)
    {
        int hash = (t.GetInstanceID() ^ (t.name.GetHashCode() << 1));
        var rng = new System.Random(unchecked((locationSeed * 83492791) ^ hash));
        float rx = Mathf.Clamp(((float)rng.NextDouble() * (locClampMaxX - locClampMinX) + locClampMinX), locClampMinX, locClampMaxX);
        float ry = Mathf.Clamp(((float)rng.NextDouble() * (locClampMaxY - locClampMinY) + locClampMinY), locClampMinY, locClampMaxY);
        float rz = Mathf.Clamp(((float)rng.NextDouble() * (locClampMaxZ - locClampMinZ) + locClampMinZ), locClampMinZ, locClampMaxZ);
        if (!locAffectX) rx = 0f; if (!locAffectY) ry = 0f; if (!locAffectZ) rz = 0f;
        Vector3 add = locationOffset + new Vector3(rx, ry, rz);
        if (locationSpace == LocationSpace.World) return t.position + add;
        return t.position + t.TransformDirection(add);
    }

    // ===== Replace core =====
    private void RunReplace()
    {
        if (!(desiredAsset is GameObject prefab))
        {
            Debug.LogWarning("Pick a Desired Asset (Prefab) first.");
            return;
        }

        var candidates = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
            .Select(t => t.gameObject).OrderBy(g => g.name).ToList();

        if (candidates.Count == 0)
        {
            EditorUtility.DisplayDialog("No matches", $"No GameObjects starting with '{prefix}' were found.", "OK");
            return;
        }

        groupParents.Clear();
        if (explicitParent == null && groupWithEmptyParent)
        {
            var byScene = candidates.GroupBy(g => g.scene);
            foreach (var g in byScene)
            {
                var scene = g.Key;
                var pos = GetEmptyParentPositionForScene(g.ToList(), emptyParentLocation, manualEmptyParentPosition);
                var parent = FindOrCreateGroupParentInScene(scene, groupParentName, pos);
                groupParents[scene] = parent;
            }
        }

        nameCounters.Clear();
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

                Transform parent = explicitParent;
                if (parent == null && groupWithEmptyParent)
                    if (groupParents.TryGetValue(src.scene, out var gp) && gp != null) parent = gp;

                var inst = ReplaceOne(src, prefab, parent);
                if (inst) spawned.Add(inst);
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
            foreach (var go in spawned) if (go) Undo.DestroyObjectImmediate(go);
        }

        if (moveTarget != MoveTarget.None)
        {
            var targetPos = ComputeMoveTarget(spawned, finalRoot);
            if (finalRoot != null) finalRoot.transform.position = targetPos;
            else
            {
                var center = GetWorldCenter(spawned);
                var delta = targetPos - center;
                foreach (var go in spawned) if (go) go.transform.position += delta;
            }
        }

        if (convertToShrub)
        {
            if (finalRoot != null) TryConvertToShrub(finalRoot, shrubRenderDistance);
            else foreach (var go in spawned) if (go) TryConvertToShrub(go, shrubRenderDistance);
        }

        if (rebuildInstancedCollision)
        {
            if (finalRoot != null) TryRebuildInstancedCollision(finalRoot);
            else foreach (var go in spawned) if (go) TryRebuildInstancedCollision(go);
        }

        EditorUtility.DisplayDialog("Done", $"Replaced {candidates.Count} placeholder(s)." + (combineIntoOne ? " Combined into one." : ""), "Nice");
    }

    private GameObject ReplaceOne(GameObject src, GameObject prefab, Transform parentOverride)
    {
        var parent = src.transform.parent;
        var localPos = src.transform.localPosition;
        var localRot = src.transform.localRotation;
        var localScale = src.transform.localScale;
        var layer = src.layer;
        var tag = src.tag;
        var active = src.activeSelf;
        var staticFlags = GameObjectUtility.GetStaticEditorFlags(src);

        var inst = PrefabUtility.InstantiatePrefab(prefab, src.scene) as GameObject;
        if (!inst) return null;
        Undo.RegisterCreatedObjectUndo(inst, "Create replacement");

        var newParent = parentOverride ? parentOverride : parent;
        inst.transform.SetParent(newParent, false);

        // rotation
        Quaternion finalRot;
        switch (rotationMode)
        {
            case RotationMode.NewRotation:
                finalRot = Quaternion.Euler(rotationEuler); break;
            case RotationMode.SeedValueOnY:
                int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((rotationSeed * 73856093) ^ hash));
                float y = (float)(rng.NextDouble() * 360.0);
                finalRot = Quaternion.Euler(0, y, 0) * Quaternion.Euler(rotationEuler); break;
            default:
                finalRot = localRot * Quaternion.Euler(rotationEuler); break;
        }

        // scale
        Vector3 finalScale = localScale;
        float sx = scaleAffectX ? scaleUnified : 1f;
        float sy = scaleAffectY ? scaleUnified : 1f;
        float sz = scaleAffectZ ? scaleUnified : 1f;
        switch (scaleMode)
        {
            case ScaleMode.NewScale: finalScale = new Vector3(sx, sy, sz); break;
            case ScaleMode.SeedValue:
            {
                int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                float rx = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxX - scaleClampMinX) + scaleClampMinX, scaleClampMinX, scaleClampMaxX);
                float ry = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxY - scaleClampMinY) + scaleClampMinY, scaleClampMinY, scaleClampMaxY);
                float rz = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxZ - scaleClampMinZ) + scaleClampMinZ, scaleClampMinZ, scaleClampMaxZ);
                if (!scaleAffectX) rx = 1f; if (!scaleAffectY) ry = 1f; if (!scaleAffectZ) rz = 1f;
                finalScale = new Vector3(rx, ry, rz);
                break;
            }
            default: finalScale = new Vector3(localScale.x * sx, localScale.y * sy, localScale.z * sz); break;
        }

        // position (offset + clamped random)
        int h = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
        var r = new System.Random(unchecked((locationSeed * 83492791) ^ h));
        float ox = Mathf.Clamp(((float)r.NextDouble() * (locClampMaxX - locClampMinX) + locClampMinX), locClampMinX, locClampMaxX);
        float oy = Mathf.Clamp(((float)r.NextDouble() * (locClampMaxY - locClampMinY) + locClampMinY), locClampMinY, locClampMaxY);
        float oz = Mathf.Clamp(((float)r.NextDouble() * (locClampMaxZ - locClampMinZ) + locClampMinZ), locClampMinZ, locClampMaxZ);
        if (!locAffectX) ox = 0f; if (!locAffectY) oy = 0f; if (!locAffectZ) oz = 0f;
        Vector3 add = locationOffset + new Vector3(ox, oy, oz);

        inst.transform.localPosition = localPos;
        if (locationSpace == LocationSpace.Local) inst.transform.position += src.transform.TransformDirection(add);
        else inst.transform.position += add;

        inst.transform.localRotation = finalRot;
        inst.transform.localScale = finalScale;

        inst.layer = layer;
        try { inst.tag = tag; } catch { }
        GameObjectUtility.SetStaticEditorFlags(inst, staticFlags);
        inst.SetActive(active);

        if (!string.IsNullOrEmpty(forcedName)) inst.name = ApplyIncremental(forcedName);
        else if (useIncrementalNaming) inst.name = ApplyIncremental(inst.name);

        Undo.DestroyObjectImmediate(src);
        return inst;
    }

    private string ApplyIncremental(string baseName)
    {
        if (!useIncrementalNaming) return baseName;
        if (!nameCounters.TryGetValue(baseName, out var n)) n = 0;
        nameCounters[baseName] = ++n;
        return $"{baseName}_{n:000}";
    }

    private bool HasMinimumPrefix() => !string.IsNullOrEmpty(prefix) && prefix.Length >= 3;

    private int CountPlaceholdersInOpenScenes()
    {
        if (!HasMinimumPrefix()) return 0;
        int count = 0;
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix)) count++;
        return count;
    }

    private Transform FindOrCreateGroupParentInScene(Scene scene, string parentName, Vector3 position)
    {
        foreach (var root in scene.GetRootGameObjects())
            if (root && root.name == parentName) return root.transform;
        var go = new GameObject(parentName);
        go.transform.position = position;
        Undo.RegisterCreatedObjectUndo(go, "Create Group Parent");
        SceneManager.MoveGameObjectToScene(go, scene);
        return go.transform;
    }
    private Transform GetGroupParentForScene(Scene scene)
    {
        if (groupParents.TryGetValue(scene, out var t)) return t;
        return null;
    }
    private Vector3 GetEmptyParentPositionForScene(List<GameObject> sceneCandidates, EmptyParentLocation loc, Vector3 manual)
    {
        if (loc == EmptyParentLocation.SelectedObject && Selection.activeTransform)
            return Selection.activeTransform.position;

        if (sceneCandidates == null || sceneCandidates.Count == 0)
            return loc == EmptyParentLocation.Manual ? manual : Vector3.zero;

        switch (loc)
        {
            case EmptyParentLocation.FirstObject: return sceneCandidates[0] ? sceneCandidates[0].transform.position : Vector3.zero;
            case EmptyParentLocation.BoundsCenter: return BoundsCenter(sceneCandidates).center;
            case EmptyParentLocation.WorldOrigin: return Vector3.zero;
            case EmptyParentLocation.Manual: return manual;
            case EmptyParentLocation.SelectedObject: return Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero;
        }
        return Vector3.zero;
    }
    private Vector3 GetWorldCenter(List<GameObject> objects)
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

    private GameObject CombineInstances(List<GameObject> instances, PivotMode pivotMode, Transform explicitP, Transform groupP, string forced)
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
            case PivotMode.Parent: pivotWS = explicitP ? explicitP.position : (groupP ? groupP.position : Vector3.zero); break;
            case PivotMode.FirstObject: pivotWS = filters[0].transform.position; break;
            case PivotMode.BoundsCenter: pivotWS = BoundsCenter(filters.Select(f => f.gameObject).ToList()).center; break;
            case PivotMode.WorldOrigin: pivotWS = Vector3.zero; break;
            case PivotMode.SelectedObject: pivotWS = Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero; break;
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
            int sub = Mathf.Min(mesh.subMeshCount, mats.Length);
            for (int s = 0; s < sub; s++)
            {
                combines.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = pivotToWorld.inverse * mf.transform.localToWorldMatrix });
                materials.Add(mats[s]);
            }
        }

        var finalMesh = new Mesh { name = "Combined_Mesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        finalMesh.CombineMeshes(combines.ToArray(), false, true, false);
        finalMesh.RecalculateBounds();
        if (!finalMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal)) finalMesh.RecalculateNormals();

        var result = new GameObject(string.IsNullOrEmpty(forced) ? "Combined Object" : forced);
        Undo.RegisterCreatedObjectUndo(result, "Create combined object");
        var parent = explicitP ? explicitP : groupP;
        if (parent) result.transform.SetParent(parent, false);
        result.transform.position = pivotWS;

        var mrf = result.AddComponent<MeshFilter>();
        var mrr = result.AddComponent<MeshRenderer>();
        mrf.sharedMesh = finalMesh;
        mrr.sharedMaterials = materials.ToArray();
        return result;
    }

    private Vector3 ComputeMoveTarget(List<GameObject> spawned, GameObject finalRoot)
    {
        switch (moveTarget)
        {
            case MoveTarget.Parent:
                if (explicitParent) return explicitParent.position;
                var gp = spawned.Count > 0 ? GetGroupParentForScene(spawned[0].scene) : null;
                if (gp) return gp.position;
                return Vector3.zero;
            case MoveTarget.FirstObject: return spawned.Count > 0 && spawned[0] ? spawned[0].transform.position : Vector3.zero;
            case MoveTarget.BoundsCenter: return GetWorldCenter(spawned);
            case MoveTarget.WorldOrigin: return Vector3.zero;
            case MoveTarget.WorldCoordinates: return moveWorldPosition;
            case MoveTarget.SelectedObject: return Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero;
            default: return finalRoot ? finalRoot.transform.position : GetWorldCenter(spawned);
        }
    }

    private void TryConvertToShrub(GameObject go, int renderDistance)
    {
        var type = Type.GetType("ConverterShrub") ?? FindTypeByMonoScriptNames(new[] { "ConverterShrub" });
        if (type == null)
        {
            Debug.LogWarning("ConverterShrub type not found. Skipping shrub conversion.");
            return;
        }

        var comp = go.GetComponent(type) ?? go.AddComponent(type);

        var pInfo = type.GetProperty("RenderDistance");
        if (pInfo != null && pInfo.CanWrite)
        {
            try { pInfo.SetValue(comp, renderDistance); } catch { }
        }
        else
        {
            var fInfo = type.GetField("RenderDistance");
            if (fInfo != null && !fInfo.IsInitOnly)
            {
                try { fInfo.SetValue(comp, renderDistance); } catch { }
            }
        }

        var build = type.GetMethod("Convert", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?? type.GetMethod("Build", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?? type.GetMethod("Apply", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (build != null)
        {
            try { build.Invoke(comp, null); } catch { }
        }
    }

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
            foreach (var c in go.GetComponents(type)) if (c) UnityEngine.Object.DestroyImmediate(c as Component);
            var comp = go.AddComponent(type);
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
                if (!mc) mc = go.AddComponent<MeshCollider>();
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

    // ===== Object picker / Library / Viewport =====
    private void ShowObjectPickerForPrefab() => EditorGUIUtility.ShowObjectPicker<GameObject>(desiredAsset as GameObject, false, "", 987654);

    private void OpenLibrary()
    {
        if (libraryWindow == null) libraryWindow = ScriptableObject.CreateInstance<GameObjectLibraryWindow>();
        libraryWindow.Init(this, (go) => { desiredAsset = go; BuildPreviewParts(); Repaint(); });
        libraryWindow.titleContent = new GUIContent("GameObject Library");
        libraryWindow.ShowUtility();
        libraryWindow.Focus();
    }

    private void OpenViewport()
    {
        if (viewportWindow == null) viewportWindow = ScriptableObject.CreateInstance<ExternalViewportWindow>();
        viewportWindow.Init(this);
        viewportWindow.titleContent = new GUIContent("Model Viewport");
        viewportWindow.ShowUtility();
        viewportWindow.Focus();
        UpdateExternalViewport();
    }
    private void CloseViewport()
    {
        if (viewportWindow != null) { viewportWindow.Close(); viewportWindow = null; }
    }
    private void UpdateExternalViewport()
    {
        if (viewportWindow == null || preview == null) return;
        if (!autoSyncViewportCamera) return;
        viewportWindow.CopyFrom(preview, yaw, pitch, dist, pivotOffset, orbitInvertX, orbitInvertY);
    }

    // ===== Randomize (no naming/parent/pivot touched) =====
    private void RandomizeAll()
    {
        Undo.RecordObject(this, "Randomize All Parameters");

        var rng = new System.Random(Environment.TickCount ^ GetHashCode());
        float R(float a, float b) => (float)(a + (b - a) * rng.NextDouble());
        void MinMax(ref float lo, ref float hi, float a, float b) { if (a <= b) { lo = a; hi = b; } else { lo = b; hi = a; } }

        rotationEuler = new Vector3((float)Math.Round(R(-45f, 45f), 3), (float)Math.Round(R(-180f, 180f), 3), (float)Math.Round(R(-45f, 45f), 3));
        if (rotationMode == RotationMode.SeedValueOnY) rotationSeed = rng.Next(1, int.MaxValue);

        scaleUnified = Mathf.Max(0.0001f, (float)Math.Round(R(0.5f, 2.0f), 3));
        if (!scaleAffectX && !scaleAffectY && !scaleAffectZ) scaleAffectX = scaleAffectY = scaleAffectZ = true;
        if (scaleMode == ScaleMode.SeedValue)
        {
            scaleSeed = rng.Next(1, int.MaxValue);
            MinMax(ref scaleClampMinX, ref scaleClampMaxX, R(0.5f, 1.2f), R(1.0f, 2.0f));
            MinMax(ref scaleClampMinY, ref scaleClampMaxY, R(0.5f, 1.2f), R(1.0f, 2.0f));
            MinMax(ref scaleClampMinZ, ref scaleClampMaxZ, R(0.5f, 1.2f), R(1.0f, 2.0f));
        }

        locationOffset = new Vector3((float)Math.Round(R(-1.0f, 1.0f), 3), (float)Math.Round(R(-1.0f, 1.0f), 3), (float)Math.Round(R(-1.0f, 1.0f), 3));
        locationSeed = rng.Next(1, int.MaxValue);
        if (!locAffectX && !locAffectY && !locAffectZ) locAffectX = locAffectY = locAffectZ = true;
        MinMax(ref locClampMinX, ref locClampMaxX, R(-5f, -0.5f), R(0.5f, 5f));
        MinMax(ref locClampMinY, ref locClampMaxY, R(-5f, -0.5f), R(0.5f, 5f));
        MinMax(ref locClampMinZ, ref locClampMaxZ, R(-5f, -0.5f), R(0.5f, 5f));

        Repaint();
    }

    // ===== UI helpers =====
    private void DrawSectionHeader(string text)
    {
        var st = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
        var r = EditorGUILayout.GetControlRect(false, 20);
        EditorGUI.DrawRect(r, new Color(0.22f, 0.22f, 0.22f));
        r.x += 6; r.y += 2; GUI.Label(r, text, st);
    }
    private float SliderNarrow(float v, float min, float max)
    {
        var r = EditorGUILayout.GetControlRect(false, 14);
        return GUI.HorizontalSlider(r, v, min, max);
    }
    private void DrawMinMaxRow(string label, ref float min, ref float max, float hardMin, float hardMax)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(86));
        GUILayout.Label("Min", GUILayout.Width(26));
        min = Mathf.Clamp(EditorGUILayout.FloatField(min, GUILayout.Width(60)), hardMin, hardMax);
        GUILayout.Label("Max", GUILayout.Width(30));
        max = Mathf.Clamp(EditorGUILayout.FloatField(max, GUILayout.Width(60)), hardMin, hardMax);
        EditorGUILayout.EndHorizontal();
        var r = EditorGUILayout.GetControlRect(false, 14);
        EditorGUI.MinMaxSlider(r, ref min, ref max, hardMin, hardMax);
    }
    private int ClampInt(int v, int min, int max) => v < min ? min : (v > max ? max : v);

    // ===== Nested helper windows =====
    public class GameObjectLibraryWindow : EditorWindow
    {
        private PlaceholderSwitcher owner;
        private Action<GameObject> onPick;
        private Vector2 scroll;
        private float thumbSize = 80f;
        private List<GameObject> prefabs;
        private string search = "";

        public void Init(PlaceholderSwitcher o, Action<GameObject> pick)
        {
            owner = o; onPick = pick;
            Collect();
            position = new Rect(Screen.currentResolution.width - 560, 120, 540, 720);
        }
        private void Collect()
        {
            // Only prefabs (GameObjects) – matches Desired Asset picker behaviour
            var guids = AssetDatabase.FindAssets("t:prefab");
            prefabs = guids.Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g))).Where(g => g).ToList();
        }
        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("GameObject Library");
            GUILayout.FlexibleSpace();
            GUILayout.Label("Thumb", GUILayout.Width(40));
            thumbSize = GUILayout.HorizontalSlider(thumbSize, 48, 160, GUILayout.Width(160));
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70))) Collect();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(48));
            string ns = EditorGUILayout.TextField(search);
            if (ns != search) { search = ns; Repaint(); }
            EditorGUILayout.EndHorizontal();

            int cols = Mathf.Max(1, (int)((position.width - 20) / (thumbSize + 12)));
            int i = 0;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.BeginHorizontal();
            foreach (var go in prefabs)
            {
                if (!string.IsNullOrEmpty(search) && !go.name.ToLowerInvariant().Contains(search.ToLowerInvariant()))
                    continue;

                EditorGUILayout.BeginVertical(GUILayout.Width(thumbSize + 8));
                var tex = AssetPreview.GetAssetPreview(go) ?? AssetPreview.GetMiniThumbnail(go);
                if (GUILayout.Button(tex, GUILayout.Width(thumbSize), GUILayout.Height(thumbSize)))
                {
                    onPick?.Invoke(go);
                }
                GUILayout.Label(go.name, EditorStyles.miniLabel, GUILayout.Width(thumbSize + 8));
                EditorGUILayout.EndVertical();

                i++;
                if (i % cols == 0)
                {
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }
        public void ForceInFront() { if (focusedWindow != this) Focus(); }
    }

    public class ExternalViewportWindow : EditorWindow
    {
        private float yaw, pitch, dist;
        private Vector3 pivotOffset;
        private bool invX, invY;

        public void Init(PlaceholderSwitcher owner)
        {
            position = new Rect(120, 120, 640, 420);
        }
        public void CopyFrom(PreviewRenderUtility src, float y, float p, float d, Vector3 off, bool ix, bool iy)
        {
            yaw = y; pitch = p; dist = d; pivotOffset = off; invX = ix; invY = iy;
            Repaint();
        }
        private void OnGUI()
        {
            var r = GUILayoutUtility.GetRect(position.width - 8, position.height - 8);
            EditorGUI.DrawRect(r, new Color(0.08f, 0.08f, 0.08f));
            GUI.Label(r, $"External Viewport\nYaw {yaw:F1}  Pitch {pitch:F1}  Dist {dist:F2}\n(Auto-Sync copies the tool camera here.)", EditorStyles.centeredGreyMiniLabel);
        }
        public void ForceInFront() { if (focusedWindow != this) Focus(); }
    }
}
#endif
