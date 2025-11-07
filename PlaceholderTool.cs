#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlaceholderSwitcher : EditorWindow
{
    // -----------------------------
    // Header / layout constants
    // -----------------------------
    private const float TitleBarHeight = 30f;
    private const float SectionHeaderHeight = 22f;
    private const float RightColMinWidth = 420f;
    private const float LeftColMinWidth  = 520f;
    private const int   MaxLocalUndoSnapshots = 64;

    // Version label
    private const string VersionLabel = "v.1.0.0";

    // -----------------------------
    // Inputs (left column, above viewer)
    // -----------------------------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject targetPrefab = null;
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false; // Base_001, _002...

    // Auto-switch (executes Switch Placeholders automatically)
    [SerializeField] private bool autoSwitch = false;

    // -----------------------------
    // Viewer & background
    // -----------------------------
    private enum ViewerBg { CurrentSkybox, UnitySkybox }
    [SerializeField] private ViewerBg viewerBg = ViewerBg.CurrentSkybox;

    private PreviewRenderUtility preview;
    private float pvYaw = -30f, pvPitch = 15f, pvDist = 6f;
    private Vector3 pvPan = Vector3.zero;
    private bool pvUserAdjusted = false;
    private Material fallbackMat;
    private Material unitySkyboxMat; // simulated "unity skybox" (fallback)
    private bool orbitInvertY = true;   // default ON per your request
    private bool orbitInvertX = false;  // default OFF

    // External viewport
    private SceneView externalViewport;
    [SerializeField] private bool autoSyncViewport = true; // on by default, reflects correctly

    // Library window
    private LibraryWindow libraryWindow;

    // -----------------------------
    // Transform Tools (right column)
    // -----------------------------
    // Rotation Offset
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero; // offset or absolute, depending on mode
    [SerializeField] private long rotationSeed = 1234;

    // Scale Offset (single value applied to selected axes, with per-axis clamping)
    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private float scaleUnified = 1f;       // one value for X/Y/Z
    [SerializeField] private long scaleSeed = 321;
    [SerializeField] private bool scaleAffectX = true, scaleAffectY = true, scaleAffectZ = true;
    [SerializeField] private float scaleClampMinX = 0.8f, scaleClampMaxX = 1.2f;
    [SerializeField] private float scaleClampMinY = 0.8f, scaleClampMaxY = 1.2f;
    [SerializeField] private float scaleClampMinZ = 0.8f, scaleClampMaxZ = 1.2f;

    // Location Offset
    private enum OffsetSpace { Local, World }
    [SerializeField] private OffsetSpace locationSpace = OffsetSpace.Local;
    [SerializeField] private Vector3 locationOffset = Vector3.zero;
    [SerializeField] private long locationSeed = 4567;
    [SerializeField] private bool locAffectX = true, locAffectY = true, locAffectZ = true;
    [SerializeField] private float locClampMinX = -1f, locClampMaxX = 1f;
    [SerializeField] private float locClampMinY = -1f, locClampMaxY = 1f;
    [SerializeField] private float locClampMinZ = -1f, locClampMaxZ = 1f;

    // Parenting
    [SerializeField] private Transform explicitParent = null;
    [SerializeField] private bool groupWithEmptyParent = false;
    [SerializeField] private string groupParentName = "Imported Placeholders";
    private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
    [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.FirstObject;
    [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;

    // Combine / Move
    [SerializeField] private bool combineIntoOne = false; // WARNING shown only when enabled
    private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] private PivotMode pivotMode = PivotMode.Parent;

    private enum MoveTarget { None, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinates, SelectedObject, Parent }
    [SerializeField] private MoveTarget moveAllTo = MoveTarget.None;
    [SerializeField] private Vector3 moveWorldCoordinate = Vector3.zero;

    // Convert to Shrub + Collision
    [SerializeField] private bool convertToShrub = false;         // ConvertToShrub
    [SerializeField] private int shrubRenderDistance = 1000;      // default 1000
    [SerializeField] private bool rebuildInstancedCollision = false;

    // Save path & actions under viewer
    [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

    // -----------------------------
    // State / helpers
    // -----------------------------
    private readonly Dictionary<Scene, Transform> groupParentCache = new Dictionary<Scene, Transform>();
    private readonly Dictionary<string, int> incrementalCounters = new Dictionary<string, int>(); // BaseName -> index
    private readonly List<ParamSnapshot> undoStack = new List<ParamSnapshot>();
    private int undoIndex = -1;

    // Menu
    [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(1140, 720);
        w.Show();
    }

    private void OnEnable()
    {
        EnsurePreview();
        fallbackMat = new Material(Shader.Find("Standard"));
        // a gentle blue as "UnitySkybox" fallback color; we still use Color mode here
        unitySkyboxMat = null; // we simulate as a color background instead
        PushUndoSnapshot(); // initial
    }

    private void OnDisable()
    {
        if (preview != null)
        {
            preview.Cleanup();
            preview = null;
        }
        if (fallbackMat != null) DestroyImmediate(fallbackMat);
        fallbackMat = null;
        unitySkyboxMat = null;
        if (libraryWindow != null) { libraryWindow.Close(); libraryWindow = null; }
        if (externalViewport != null) { externalViewport.Close(); externalViewport = null; }
    }

    private void EnsurePreview()
    {
        if (preview != null) return;
        preview = new PreviewRenderUtility(true);
        preview.cameraFieldOfView = 30f;
        preview.lights[0].intensity = 1.2f;
        preview.lights[1].intensity = 0.8f;
        ApplyViewerBackground();
    }

    // -----------------------------
    // UI
    // -----------------------------
    private void OnGUI()
    {
        EnsurePreview();

        // Title row
        DrawTitleRow();

        // Two-column layout
        EditorGUILayout.BeginHorizontal();

        // LEFT COLUMN
        EditorGUILayout.BeginVertical(GUILayout.MinWidth(LeftColMinWidth));
        DrawReplaceSectionLeft(); // "Replace Object Placeholders" + prefix status
        DrawViewer();             // big viewer + buttons + save path + save/load
        EditorGUILayout.EndVertical();

        // RIGHT COLUMN
        EditorGUILayout.BeginVertical(GUILayout.MinWidth(RightColMinWidth));
        DrawRightColumnHeader("Transform Tools");
        DrawRotationOffset();
        DrawScaleOffset();
        DrawLocationOffset();
        DrawParenting();
        DrawCombineAndMove();
        DrawConvertAndCollision();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        // Footer version label
        GUILayout.FlexibleSpace();
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(VersionLabel, EditorStyles.miniLabel, GUILayout.Width(80));
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        // External viewport auto-sync (every OnGUI)
        if (autoSyncViewport) UpdateExternalViewport();

        // Auto-switch: if enabled and we have a valid setup, run it
        if (autoSwitch)
        {
            // Safety prompt
            if (!autoSwitchWarningShown)
            {
                autoSwitchWarningShown = true;
                EditorUtility.DisplayDialog(
                    "Auto Switch Enabled",
                    "Warning: While Auto Switch is ON, whichever model you preview will automatically replace the current game objects matched by the prefix.\nYou have 64 undos. Use them wisely!",
                    "OK");
            }
            if (CanSwitchNow())
            {
                RunSwitch(replaceMode: targetPrefab != null);
            }
        }
    }

    private void DrawTitleRow()
    {
        using (new DarkHeaderScope(TitleBarHeight))
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Placeholder Switcher", EditorStyles.boldLabel, GUILayout.Height(TitleBarHeight));
            GUILayout.FlexibleSpace();

            // Top-right buttons
            if (GUILayout.Button("Open GameObject Library", GUILayout.Height(22), GUILayout.Width(200)))
            {
                OpenLibrary();
            }
            if (GUILayout.Button("Randomize All Parameters", GUILayout.Height(22), GUILayout.Width(200)))
            {
                RandomizeAll();
                Repaint();
            }
            GUI.enabled = CanUndoLocal();
            if (GUILayout.Button("Undo", GUILayout.Height(22), GUILayout.Width(90)))
            {
                UndoLocal();
                Repaint();
            }
            GUI.enabled = true;

            GUILayout.EndHorizontal();
        }
    }

    // -----------------------------
    // LEFT: Replace Object Placeholders + Viewer
    // -----------------------------
    private void DrawReplaceSectionLeft()
    {
        using (new DarkHeaderScope(SectionHeaderHeight))
        {
            GUILayout.Label("Replace Object Placeholders", EditorStyles.boldLabel, GUILayout.Height(SectionHeaderHeight));
        }

        // Fields
        EditorGUILayout.Space(1);

        // Prefix + status
        EditorGUILayout.BeginHorizontal();
        var newPrefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
        if (newPrefix != prefix) { prefix = newPrefix; pvUserAdjusted = false; }
        // Live status
        int found = CountPlaceholders(prefix);
        var status = (prefix.Length < 3) ? "enter ≥ 3 chars"
                   : (found == 0) ? "⚠️ no assets found"
                   : $"{found} object(s) found";
        GUILayout.Label(status, EditorStyles.miniLabel, GUILayout.Width(150));
        EditorGUILayout.EndHorizontal();

        // Desired prefab + drag area hint
        var obj = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), targetPrefab, typeof(GameObject), false);
        if (obj != targetPrefab) { targetPrefab = obj; pvUserAdjusted = false; }

        // Forced name + incremental
        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);

        // Auto Switch + warning inline (smaller toggle near actions too—primary toggle here)
        autoSwitch = EditorGUILayout.Toggle(new GUIContent("Auto Switch Placeholders"), autoSwitch);
    }

    private int CountPlaceholders(string pfx)
    {
        if (string.IsNullOrEmpty(pfx) || pfx.Length < 3) return 0;
        return Resources.FindObjectsOfTypeAll<Transform>()
            .Count(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(pfx));
    }

    private bool CanSwitchNow()
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3) return false;
        // We allow operation with or without targetPrefab:
        // - With prefab: replace placeholders with prefab
        // - Without prefab: operate on placeholders directly (transform/combine/save)
        return CountPlaceholders(prefix) > 0;
    }

    // Viewer + actions/saves
    private void DrawViewer()
    {
        EditorGUILayout.Space(6);
        // Viewer background buttons row
        using (new DarkHeaderScope(SectionHeaderHeight))
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Viewer Background", EditorStyles.boldLabel, GUILayout.Height(SectionHeaderHeight));
            GUILayout.FlexibleSpace();
            if (GUILayout.Toggle(viewerBg == ViewerBg.CurrentSkybox, "Current Skybox", "Button", GUILayout.Width(120)))
            {
                if (viewerBg != ViewerBg.CurrentSkybox) { viewerBg = ViewerBg.CurrentSkybox; ApplyViewerBackground(); }
            }
            if (GUILayout.Toggle(viewerBg == ViewerBg.UnitySkybox, "Unity Skybox", "Button", GUILayout.Width(120)))
            {
                if (viewerBg != ViewerBg.UnitySkybox) { viewerBg = ViewerBg.UnitySkybox; ApplyViewerBackground(); }
            }
            // External viewport buttons + big auto-sync checkbox
            bool prevAuto = autoSyncViewport;
            autoSyncViewport = GUILayout.Toggle(autoSyncViewport, "Auto-Sync Model View to Viewport", GUILayout.Width(240));
            if (autoSyncViewport && externalViewport == null) OpenViewport();
            if (!autoSyncViewport && externalViewport != null) { /* keep open unless closed explicitly */ }
            if (GUILayout.Button("Open Viewport", GUILayout.Width(110))) OpenViewport();
            if (GUILayout.Button("Close Viewport", GUILayout.Width(110))) CloseViewport();
            GUILayout.EndHorizontal();
        }

        // Viewport Rect
        var rect = GUILayoutUtility.GetRect(10, 10, 420, 420);
        DrawPreview(rect);

        // Re-center + Save/Load row
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Re-center View", GUILayout.Height(24), GUILayout.Width(130)))
        {
            pvPan = Vector3.zero; pvUserAdjusted = false;
            Repaint();
        }

        GUILayout.FlexibleSpace();

        // Save path field
        GUILayout.Label("Load / Save", EditorStyles.miniBoldLabel, GUILayout.Width(80));
        EditorGUILayout.EndHorizontal();

        // Save path picker
        EditorGUILayout.BeginHorizontal();
        savePath = EditorGUILayout.TextField(new GUIContent("Save Path"), savePath);
        if (GUILayout.Button("Select…", GUILayout.Width(80)))
        {
            var suggested = System.IO.Path.GetFileName(savePath);
            var path = EditorUtility.SaveFilePanelInProject("Save Prefab As",
                string.IsNullOrEmpty(suggested) ? "CombinedPlaceholder" : suggested,
                "prefab", "Choose save path");
            if (!string.IsNullOrEmpty(path)) savePath = path;
        }
        EditorGUILayout.EndHorizontal();

        // Save / Load row
        EditorGUILayout.BeginHorizontal();
        // Save From Preview As…
        bool exactlyOne = CountPlaceholders(prefix) == 1;
        GUI.enabled = exactlyOne;
        if (GUILayout.Button("Save From Preview As…", GUILayout.Height(28)))
        {
            SaveFromPreviewSingle();
        }
        GUI.enabled = true;

        if (GUILayout.Button("Load Asset From File", GUILayout.Height(28), GUILayout.Width(180)))
        {
            EditorUtility.DisplayDialog("Import Info",
                "To load external models (.fbx/.obj/.gltf), import them into the Project first. Then drag them here or pick via the library.",
                "OK");
        }
        EditorGUILayout.EndHorizontal();

        // Save info warnings
        int count = CountPlaceholders(prefix);
        if (count == 0)
        {
            EditorGUILayout.HelpBox("Nothing to save, search for objects via a prefix to enable saving.", MessageType.Info);
        }
        else if (count > 1 && !combineIntoOne)
        {
            EditorGUILayout.HelpBox("Multiple placeholders detected. Combine all objects first to enable single-object Save From Preview.", MessageType.Warning);
        }

        // Big action row
        EditorGUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        // Primary Switch button
        GUI.enabled = CanSwitchNow();
        if (GUILayout.Button("Switch Placeholders", GUILayout.Height(36)))
        {
            RunSwitch(replaceMode: targetPrefab != null);
        }
        GUI.enabled = true;

        // Smaller auto-switch toggle + inline warning hint
        autoSwitch = GUILayout.Toggle(autoSwitch, "Auto Switch", GUILayout.Width(110));
        EditorGUILayout.EndHorizontal();
    }

    private void ApplyViewerBackground()
    {
        if (preview == null) return;
        var cam = preview.camera;
        switch (viewerBg)
        {
            case ViewerBg.CurrentSkybox:
                cam.clearFlags = CameraClearFlags.Skybox;
                break;
            case ViewerBg.UnitySkybox:
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = new Color(0.58f, 0.76f, 0.96f); // soft blue "unity-ish"
                break;
        }
    }

    private void DrawPreview(Rect rect)
    {
        if (preview == null) return;

        // Gather candidates
        var candidates = (prefix.Length >= 3)
            ? Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
                .Select(t => t.gameObject)
                .ToList()
            : new List<GameObject>();

        bool hasDesired = targetPrefab != null;
        bool hasAny = candidates.Count > 0;

        // Frame pivot
        Vector3 pivot = GetPreviewPivot(candidates) + pvPan;

        // Auto-fit distance unless user adjusted
        if (!pvUserAdjusted)
        {
            Bounds b;
            if (TryCalcBoundsForPreview(candidates, hasDesired ? targetPrefab : null, out b))
            {
                var halfFovRad = preview.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
                var radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
                pvDist = Mathf.Clamp(radius / Mathf.Tan(halfFovRad) + radius * 0.4f, 0.8f, 5000f);
                pivot = b.center;
            }
            else
            {
                pvDist = 6f;
            }
        }

        // Paint preview
        if (Event.current.type == EventType.Repaint)
        {
            preview.BeginPreview(rect, GUIStyle.none);

            var cam = preview.camera;
            var rot = Quaternion.Euler(pvPitch, pvYaw, 0f);
            cam.transform.position = pivot + rot * (Vector3.back * pvDist);
            cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 10000f;

            // Draw placeholders (their own meshes) OR desired prefab as instances at placeholder positions
            if (hasAny)
            {
                if (hasDesired)
                {
                    // draw desired mesh at placeholder transforms
                    var meshes = GetMeshesFromPrefab(targetPrefab);
                    if (meshes.Count == 0)
                    {
                        // fallback: cube
                        var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                        foreach (var go in candidates)
                        {
                            if (!go) continue;
                            var mtx = Matrix4x4.TRS(go.transform.position, Quaternion.identity, Vector3.one);
                            preview.DrawMesh(cube, mtx, fallbackMat, 0);
                        }
                    }
                    else
                    {
                        foreach (var go in candidates)
                        {
                            if (!go) continue;
                            foreach (var (mesh, mats) in meshes)
                            {
                                var mtx = Matrix4x4.TRS(go.transform.position, Quaternion.identity, Vector3.one);
                                if (mats != null && mats.Length > 0)
                                {
                                    for (int i = 0; i < Mathf.Min(mesh.subMeshCount, mats.Length); i++)
                                        preview.DrawMesh(mesh, mtx, mats[i] ? mats[i] : fallbackMat, i);
                                }
                                else
                                {
                                    preview.DrawMesh(mesh, mtx, fallbackMat, 0);
                                }
                            }
                        }
                    }
                }
                else
                {
                    // draw actual placeholder meshes
                    foreach (var go in candidates)
                    {
                        if (!go) continue;
                        var mf = go.GetComponent<MeshFilter>();
                        var mr = go.GetComponent<MeshRenderer>();
                        if (mf != null && mf.sharedMesh != null)
                        {
                            var mtx = go.transform.localToWorldMatrix;
                            var mats = (mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0)
                                ? mr.sharedMaterials
                                : new[] { fallbackMat };
                            for (int s = 0; s < Mathf.Min(mf.sharedMesh.subMeshCount, mats.Length); s++)
                                preview.DrawMesh(mf.sharedMesh, mtx, mats[s] ? mats[s] : fallbackMat, s);
                        }
                        else
                        {
                            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                            var mtx = go.transform.localToWorldMatrix;
                            preview.DrawMesh(cube, mtx, fallbackMat, 0);
                        }
                    }
                }
            }
            else
            {
                // No items -> helper overlay
                DrawHelperOverlay(rect, "No objects to preview.\n\nEnter a 3+ character prefix and/or pick a Desired Asset (Prefab).\nTip: Open the GameObject Library to find assets.\nTry seeds and Location Offset to create variants — have fun!");
            }

            preview.camera.Render();
            var tex = preview.EndPreview();
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        // Input
        if (rect.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                if (Event.current.button == 0) // orbit
                {
                    pvUserAdjusted = true;
                    float invY = orbitInvertY ? -1f : 1f;
                    float invX = orbitInvertX ? -1f : 1f;
                    pvYaw   += invX * Event.current.delta.x * 0.5f;
                    pvPitch = Mathf.Clamp(pvPitch + invY * Event.current.delta.y * 0.5f, -80f, 80f);
                    Repaint();
                }
                else if (Event.current.button == 0 && Event.current.shift) { /* reserved */ }
                else if (Event.current.button == 0 && Event.current.modifiers == EventModifiers.Shift) { /* reserved */ }
            }

            if (Event.current.type == EventType.MouseDrag && Event.current.button == 0 && Event.current.shift)
            {
                // SHIFT + LMB = Pan
                pvUserAdjusted = true;
                float panScale = pvDist * 0.0025f;
                var right = Quaternion.Euler(0, pvYaw, 0) * Vector3.right;
                var up = Vector3.up;
                pvPan += (-right * Event.current.delta.x + up * Event.current.delta.y) * panScale;
                Repaint();
            }

            if (Event.current.type == EventType.ScrollWheel)
            {
                pvUserAdjusted = true;
                pvDist = Mathf.Clamp(pvDist * (1f + Event.current.delta.y * 0.04f), 0.3f, 20000f);
                Repaint();
            }
        }

        // Orbit inversion toggles
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Controls", EditorStyles.miniLabel, GUILayout.Width(60));
        orbitInvertY = GUILayout.Toggle(orbitInvertY, "Y Inverted", GUILayout.Width(90));
        orbitInvertX = GUILayout.Toggle(orbitInvertX, "X Inverted", GUILayout.Width(90));
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    private void DrawHelperOverlay(Rect rect, string message)
    {
        // Darkened backdrop
        var c = new Color(0f, 0f, 0f, 0.5f);
        EditorGUI.DrawRect(rect, c);

        // Text centered
        var style = new GUIStyle(EditorStyles.wordWrappedLabel);
        style.alignment = TextAnchor.MiddleCenter;
        style.normal.textColor = Color.white;
        GUI.Label(rect, message, style);
    }

    private void UpdateExternalViewport()
    {
        if (externalViewport == null) return;

        // Focus camera on preview pivot
        var candidates = (prefix.Length >= 3)
            ? Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
                .Select(t => t.gameObject).ToList()
            : new List<GameObject>();

        Bounds b;
        if (!TryCalcBoundsForPreview(candidates, targetPrefab, out b))
            return;

        var sceneCam = externalViewport.camera;
        if (sceneCam == null) return;

        var pivot = b.center;
        float dist = Mathf.Max(b.extents.magnitude * 2f, 2f);

        // set scene view
        var dir = Quaternion.Euler(pvPitch, pvYaw, 0f) * Vector3.back;
        var pos = pivot + dir * dist;
        externalViewport.LookAtDirect(pivot, Quaternion.LookRotation(pivot - pos, Vector3.up), dist);
        externalViewport.Repaint();
    }

    private void OpenViewport()
    {
        if (externalViewport == null)
        {
            // Utility SceneView tends to stay on top
            externalViewport = ScriptableObject.CreateInstance<SceneView>();
            externalViewport.titleContent = new GUIContent("Placeholder Viewport");
            externalViewport.ShowUtility(); // best-effort "always-on-top"
            externalViewport.wantsMouseMove = true;
        }
    }

    private void CloseViewport()
    {
        if (externalViewport != null)
        {
            externalViewport.Close();
            externalViewport = null;
        }
    }

    private void OpenLibrary()
    {
        if (libraryWindow == null)
        {
            libraryWindow = ScriptableObject.CreateInstance<LibraryWindow>();
            libraryWindow.Init(this);
            libraryWindow.ShowUtility(); // utility tends to float on top
            // position: snap to right of main tool
            var r = position;
            libraryWindow.position = new Rect(r.xMax + 6, r.yMin, Mathf.Max(520, r.width * 0.45f), Mathf.Max(520, r.height - 40));
        }
        else
        {
            libraryWindow.Focus();
        }
    }

    private bool TryCalcBoundsForPreview(List<GameObject> candidates, GameObject desired, out Bounds b)
    {
        // If placeholders exist, use their bounds; else fallback to desired prefab's first mesh
        if (candidates != null && candidates.Count > 0)
        {
            Renderer first = null;
            foreach (var go in candidates)
            {
                if (!go) continue;
                var r = go.GetComponent<Renderer>();
                if (r) { first = r; break; }
            }
            if (first != null)
            {
                b = new Bounds(first.bounds.center, Vector3.zero);
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var r = go.GetComponent<Renderer>();
                    if (r) b.Encapsulate(r.bounds);
                    else b.Encapsulate(new Bounds(go.transform.position, Vector3.one));
                }
                return true;
            }
        }

        if (desired != null)
        {
            var meshes = GetMeshesFromPrefab(desired);
            if (meshes.Count > 0)
            {
                // approximate
                var mesh = meshes[0].mesh;
                b = new Bounds(Vector3.zero, mesh.bounds.size);
                return true;
            }
        }

        b = new Bounds(Vector3.zero, Vector3.one * 2f);
        return false;
    }

    private Vector3 GetPreviewPivot(List<GameObject> candidates)
    {
        switch (pivotMode)
        {
            case PivotMode.Parent:
                if (explicitParent) return explicitParent.position;
                if (groupWithEmptyParent)
                    return GetEmptyParentPositionForScene(candidates, emptyParentLocation, manualEmptyParentPosition);
                return Vector3.zero;
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

    // -----------------------------
    // RIGHT: Transform Tools
    // -----------------------------
    private void DrawRightColumnHeader(string title)
    {
        using (new DarkHeaderScope(SectionHeaderHeight))
        {
            GUILayout.Label(title, EditorStyles.boldLabel, GUILayout.Height(SectionHeaderHeight));
        }
        EditorGUILayout.Space(2);
    }

    private void DrawRotationOffset()
    {
        using (new DarkHeaderScope(SectionHeaderHeight)) { GUILayout.Label("Rotation Offset", EditorStyles.boldLabel); }

        // Mode
        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);

        // XYZ on same row
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(rotationMode == RotationMode.PlaceholderRotation ? "Rotation (adds to placeholder)" :
                        rotationMode == RotationMode.NewRotation ? "Rotation (new rotation)" : "Rotation (offset on top of seeded Y)",
                        GUILayout.Width(240));
        rotationEuler = EditorGUILayout.Vector3Field(GUIContent.none, rotationEuler);
        EditorGUILayout.EndHorizontal();
        // Sliders under
        DrawVector3Sliders(ref rotationEuler, -360f, 360f);

        if (rotationMode == RotationMode.SeedValueOnY)
        {
            EditorGUILayout.BeginHorizontal();
            rotationSeed = SafeInt64(EditorGUILayout.LongField(new GUIContent("Random rotation seed (Y)"), rotationSeed), 1, 9999999999L);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                rotationSeed = UnityEngine.Random.Range(1, int.MaxValue);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("Deterministic per-object Y rotation using the seed. The XYZ above is applied on top.", MessageType.Info);
        }
        EditorGUILayout.Space(4);
    }

    private void DrawScaleOffset()
    {
        using (new DarkHeaderScope(SectionHeaderHeight)) { GUILayout.Label("Scale Offset", EditorStyles.boldLabel); }

        // Mode
        scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

        // Unified value on same row
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(scaleMode == ScaleMode.PlaceholderScale ? "Scale (multiplies placeholder scale)" :
                        scaleMode == ScaleMode.NewScale ? "Scale (new uniform)" :
                        "Scale (seeded uniform + offset)",
                        GUILayout.Width(240));
        scaleUnified = Mathf.Max(0.0001f, EditorGUILayout.FloatField(GUIContent.none, scaleUnified));
        EditorGUILayout.EndHorizontal();
        // Slider under
        scaleUnified = EditorGUILayout.Slider(scaleUnified, 0.0001f, 10f);

        // Influence Axes row
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Influenced Axes", GUILayout.Width(120));
        scaleAffectX = GUILayout.Toggle(scaleAffectX, "X", "Button", GUILayout.Width(30));
        scaleAffectY = GUILayout.Toggle(scaleAffectY, "Y", "Button", GUILayout.Width(30));
        scaleAffectZ = GUILayout.Toggle(scaleAffectZ, "Z", "Button", GUILayout.Width(30));
        EditorGUILayout.EndHorizontal();

        if (scaleMode == ScaleMode.SeedValue)
        {
            // Seed row
            EditorGUILayout.BeginHorizontal();
            scaleSeed = SafeInt64(EditorGUILayout.LongField(new GUIContent("Random scaling seed"), scaleSeed), 1, 9999999999L);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                scaleSeed = UnityEngine.Random.Range(1, int.MaxValue);
            EditorGUILayout.EndHorizontal();

            // Scale clamping sub-row label
            GUILayout.Label("Scale Clamping", EditorStyles.miniBoldLabel);

            // Per-axis clamping
            DrawMinMaxRow("X Min/Max", ref scaleClampMinX, ref scaleClampMaxX, 0.0001f, 100f);
            DrawMinMaxRow("Y Min/Max", ref scaleClampMinY, ref scaleClampMaxY, 0.0001f, 100f);
            DrawMinMaxRow("Z Min/Max", ref scaleClampMinZ, ref scaleClampMaxZ, 0.0001f, 100f);
        }

        EditorGUILayout.Space(4);
    }

    private void DrawLocationOffset()
    {
        using (new DarkHeaderScope(SectionHeaderHeight)) { GUILayout.Label("Location Offset", EditorStyles.boldLabel); }

        locationSpace = (OffsetSpace)EditorGUILayout.EnumPopup(new GUIContent("Location Transform Mode"), locationSpace);

        // XYZ on same row
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Location Transform", GUILayout.Width(160));
        locationOffset = EditorGUILayout.Vector3Field(GUIContent.none, locationOffset);
        EditorGUILayout.EndHorizontal();
        // Sliders under
        DrawVector3Sliders(ref locationOffset, -100f, 100f);

        // Seed row
        EditorGUILayout.BeginHorizontal();
        locationSeed = SafeInt64(EditorGUILayout.LongField(new GUIContent("Random location seed"), locationSeed), 1, 9999999999L);
        if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
            locationSeed = UnityEngine.Random.Range(1, int.MaxValue);
        EditorGUILayout.EndHorizontal();

        // Influence axis section (under seed, per your request)
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Influenced Axes", GUILayout.Width(120));
        locAffectX = GUILayout.Toggle(locAffectX, "X", "Button", GUILayout.Width(30));
        locAffectY = GUILayout.Toggle(locAffectY, "Y", "Button", GUILayout.Width(30));
        locAffectZ = GUILayout.Toggle(locAffectZ, "Z", "Button", GUILayout.Width(30));
        EditorGUILayout.EndHorizontal();

        // Subheader + per-axis clamping
        GUILayout.Label("Location Clamping", EditorStyles.miniBoldLabel);
        DrawMinMaxRow("X Min/Max", ref locClampMinX, ref locClampMaxX, -1000f, 1000f);
        DrawMinMaxRow("Y Min/Max", ref locClampMinY, ref locClampMaxY, -1000f, 1000f);
        DrawMinMaxRow("Z Min/Max", ref locClampMinZ, ref locClampMaxZ, -1000f, 1000f);

        EditorGUILayout.Space(4);
    }

    private void DrawParenting()
    {
        using (new DarkHeaderScope(SectionHeaderHeight)) { GUILayout.Label("Parenting", EditorStyles.boldLabel); }
        using (new EditorGUI.DisabledScope(groupWithEmptyParent))
        {
            var newParent = (Transform)EditorGUILayout.ObjectField(new GUIContent("Parent (optional)"), explicitParent, typeof(Transform), true);
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
        EditorGUILayout.Space(4);
    }

    private void DrawCombineAndMove()
    {
        using (new DarkHeaderScope(SectionHeaderHeight)) { GUILayout.Label("Combine / Move", EditorStyles.boldLabel); }

        combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one", "Static content only"), combineIntoOne);
        if (combineIntoOne)
        {
            EditorGUILayout.HelpBox(
                "Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, and triggers are lost.\nTip: If you need interactivity but want to move many objects together, parent them under an empty and consider Static Batching.",
                MessageType.Warning);
        }

        using (new EditorGUI.DisabledScope(!combineIntoOne))
        {
            EditorGUI.indentLevel++;
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
            if ((pivotMode == PivotMode.SelectedObject) && Selection.activeTransform == null)
                EditorGUILayout.HelpBox("Select a Transform to use as the pivot.", MessageType.Info);
            EditorGUI.indentLevel--;
        }

        // Move all
        moveAllTo = (MoveTarget)EditorGUILayout.EnumPopup(new GUIContent("Move all objects to"), moveAllTo);
        using (new EditorGUI.DisabledScope(moveAllTo != MoveTarget.WorldCoordinates))
        {
            EditorGUI.indentLevel++;
            moveWorldCoordinate = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), moveWorldCoordinate);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);
    }

    private void DrawConvertAndCollision()
    {
        using (new DarkHeaderScope(SectionHeaderHeight)) { GUILayout.Label("Rebuild Instanced Collision", EditorStyles.boldLabel); }

        // Convert to Shrub (above collision, per order of operations)
        convertToShrub = EditorGUILayout.Toggle(new GUIContent("Convert to Shrub"), convertToShrub);
        using (new EditorGUI.DisabledScope(!convertToShrub))
        {
            shrubRenderDistance = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent("Shrub Render Distance"), shrubRenderDistance));
        }

        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Rebuild instanced collision"), rebuildInstancedCollision);
        EditorGUILayout.Space(4);
    }

    // -----------------------------
    // Actions
    // -----------------------------
    private static bool IsPrefabAsset(GameObject go)
    {
        if (!go) return false;
        var t = PrefabUtility.GetPrefabAssetType(go);
        return t != PrefabAssetType.NotAPrefab && t != PrefabAssetType.MissingAsset;
    }

    private bool autoSwitchWarningShown = false;

    private void RunSwitch(bool replaceMode)
    {
        // Collect candidates
        var candidates = Resources.FindObjectsOfTypeAll<Transform>()
            .Select(t => t ? t.gameObject : null)
            .Where(go => go && go.scene.IsValid() && go.name.StartsWith(prefix))
            .OrderBy(go => go.name)
            .ToList();

        if (candidates.Count == 0)
        {
            EditorUtility.DisplayDialog("No matches", $"No GameObjects starting with '{prefix}' were found.", "OK");
            return;
        }

        // Per-scene grouping setup (for new empty parents)
        var candidatesByScene = new Dictionary<Scene, List<GameObject>>();
        foreach (var go in candidates)
        {
            if (!candidatesByScene.TryGetValue(go.scene, out var list)) { list = new List<GameObject>(); candidatesByScene[go.scene] = list; }
            list.Add(go);
        }

        groupParentCache.Clear();
        if (explicitParent == null && groupWithEmptyParent)
        {
            foreach (var kv in candidatesByScene)
            {
                var scene = kv.Key;
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var desiredPos = GetEmptyParentPositionForScene(kv.Value, emptyParentLocation, manualEmptyParentPosition);
                var parent = FindOrCreateGroupParentInScene(scene, groupParentName, desiredPos);
                groupParentCache[scene] = parent;
            }
        }

        incrementalCounters.Clear();
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Placeholder Switcher");

        var spawned = new List<GameObject>();
        bool operateOnPlaceholders = !replaceMode; // when no prefab, operate directly on placeholders (per your request)

        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var src = candidates[i];
                if (!src) continue;
                if (EditorUtility.DisplayCancelableProgressBar("Processing Placeholders", $"Processing {i + 1}/{candidates.Count}: {src.name}", (float)(i + 1) / candidates.Count))
                    break;

                Transform groupingParent = explicitParent ? explicitParent : null;
                if (groupingParent == null && groupWithEmptyParent)
                {
                    if (groupParentCache.TryGetValue(src.scene, out var gp) && gp != null)
                        groupingParent = gp;
                }

                if (operateOnPlaceholders)
                {
                    // Transform the placeholder itself, keep it for later combine/save
                    ApplyTransforms(src, out _, out _);
                    // Optionally reparent
                    if (groupingParent) src.transform.SetParent(groupingParent, true);
                    spawned.Add(src);
                }
                else
                {
                    // Replace with prefab instance
                    if (targetPrefab == null || !IsPrefabAsset(targetPrefab))
                    {
                        Debug.LogError("Selected object is not a Prefab asset. Drag a prefab from the Project window.");
                        continue;
                    }

                    var inst = ReplaceOne(src, targetPrefab, forcedName, useIncrementalNaming, groupingParent);
                    if (inst) { ApplyTransforms(inst, out _, out _); spawned.Add(inst); }
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Undo.CollapseUndoOperations(group);
        }

        GameObject finalRoot = null;

        // Combine if requested
        if (combineIntoOne && spawned.Count > 0)
        {
            finalRoot = CombineInstances(spawned, pivotMode, explicitParent, GetGroupParentForScene(spawned[0].scene), string.IsNullOrEmpty(forcedName) ? "Combined Object" : forcedName);
            if (!operateOnPlaceholders)
            {
                // Remove the spawned instances if combined
                foreach (var go in spawned) if (go != null && go != finalRoot) Undo.DestroyObjectImmediate(go);
            }
        }

        // Move all
        if (moveAllTo != MoveTarget.None)
        {
            if (finalRoot != null)
            {
                finalRoot.transform.position = GetMoveTargetPosition(moveAllTo, spawned, finalRoot);
            }
            else
            {
                var tgt = GetMoveTargetPosition(moveAllTo, spawned, null);
                var center = GetWorldCenter(spawned);
                var delta = tgt - center;
                foreach (var go in spawned) if (go) go.transform.position += delta;
            }
        }

        // Convert to Shrub first (if needed), then rebuild collision
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

        EditorUtility.DisplayDialog("Done", $"Processed {candidates.Count} placeholder(s).{(combineIntoOne ? " Combined into one." : "")}", "Nice");
    }

    private Vector3 GetMoveTargetPosition(MoveTarget t, List<GameObject> set, GameObject finalRootIfAny)
    {
        switch (t)
        {
            case MoveTarget.FirstObject:
                return set.Count > 0 && set[0] ? set[0].transform.position : Vector3.zero;
            case MoveTarget.BoundsCenter:
                return GetWorldCenter(set);
            case MoveTarget.WorldOrigin:
                return Vector3.zero;
            case MoveTarget.WorldCoordinates:
                return moveWorldCoordinate;
            case MoveTarget.SelectedObject:
                return Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero;
            case MoveTarget.Parent:
                if (finalRootIfAny && finalRootIfAny.transform.parent) return finalRootIfAny.transform.parent.position;
                if (!finalRootIfAny && explicitParent) return explicitParent.position;
                return Vector3.zero;
            case MoveTarget.None:
            default:
                return Vector3.zero;
        }
    }

    private GameObject ReplaceOne(GameObject src, GameObject prefab, string forcedBase, bool incremental, Transform groupingParent)
    {
        if (!src || !prefab) return null;

        // cache
        var parent = src.transform.parent;
        var localPos = src.transform.localPosition;
        var localRot = src.transform.localRotation;
        var localScale = src.transform.localScale;
        var layer = src.layer;
        var tag = src.tag;
        var active = src.activeSelf;
        var staticFlags = GameObjectUtility.GetStaticEditorFlags(src);

        // instantiate prefab
        var inst = PrefabUtility.InstantiatePrefab(prefab, src.scene) as GameObject;
        if (!inst) return null;
        Undo.RegisterCreatedObjectUndo(inst, "Create replacement");

        // parent: explicit/grouping overrides, else original parent
        var newParent = groupingParent ? groupingParent : parent;
        inst.transform.SetParent(newParent, false);

        // restore basic transform; the offset/seed transforms are applied separately
        inst.transform.localPosition = localPos;
        inst.transform.localRotation = localRot;
        inst.transform.localScale = localScale;

        // meta
        inst.layer = layer;
        try { inst.tag = tag; } catch { }
        GameObjectUtility.SetStaticEditorFlags(inst, staticFlags);
        inst.SetActive(active);

        // naming
        if (!string.IsNullOrEmpty(forcedBase)) inst.name = ApplyIncremental(forcedBase, incremental);
        else inst.name = ApplyIncremental(inst.name, incremental);

        Undo.DestroyObjectImmediate(src);
        return inst;
    }

    private void ApplyTransforms(GameObject go, out Quaternion outRot, out Vector3 outScale)
    {
        // Rotation
        Quaternion finalRot;
        switch (rotationMode)
        {
            default:
            case RotationMode.PlaceholderRotation:
                finalRot = go.transform.localRotation * Quaternion.Euler(rotationEuler);
                break;
            case RotationMode.NewRotation:
                finalRot = Quaternion.Euler(rotationEuler);
                break;
            case RotationMode.SeedValueOnY:
                {
                    int hash = (go.GetInstanceID() ^ (go.name.GetHashCode() << 1));
                    var rng = new System.Random(unchecked((int)((rotationSeed * 73856093L) ^ hash)));
                    float y = (float)(rng.NextDouble() * 360.0);
                    finalRot = Quaternion.Euler(0f, y, 0f) * Quaternion.Euler(rotationEuler);
                }
                break;
        }
        go.transform.localRotation = finalRot;

        // Scale
        Vector3 s = go.transform.localScale;
        Vector3 finalS = s;
        switch (scaleMode)
        {
            case ScaleMode.PlaceholderScale:
                // multiply existing by unified
                ApplyScaleUnified(ref finalS, scaleUnified);
                break;
            case ScaleMode.NewScale:
                finalS = Vector3.one;
                ApplyScaleUnified(ref finalS, scaleUnified);
                break;
            case ScaleMode.SeedValue:
                {
                    int hash = (go.GetInstanceID() ^ (go.name.GetHashCode() << 1));
                    var rng = new System.Random(unchecked((int)((scaleSeed * 19349663L) ^ hash)));

                    float rx = Mathf.Lerp(scaleClampMinX, scaleClampMaxX, (float)rng.NextDouble());
                    float ry = Mathf.Lerp(scaleClampMinY, scaleClampMaxY, (float)rng.NextDouble());
                    float rz = Mathf.Lerp(scaleClampMinZ, scaleClampMaxZ, (float)rng.NextDouble());

                    // baseline: seeded clamped scale per axis
                    finalS = new Vector3(scaleAffectX ? rx : s.x,
                                         scaleAffectY ? ry : s.y,
                                         scaleAffectZ ? rz : s.z);

                    // then apply unified offset multiplier
                    if (scaleAffectX) finalS.x *= scaleUnified;
                    if (scaleAffectY) finalS.y *= scaleUnified;
                    if (scaleAffectZ) finalS.z *= scaleUnified;
                }
                break;
        }
        go.transform.localScale = finalS;

        // Location offset
        {
            Vector3 offset = locationOffset;
            // seeded per-axis additive within clamps
            int hash = (go.GetInstanceID() ^ (go.name.GetHashCode() << 1));
            var rng = new System.Random(unchecked((int)((locationSeed * 83492791L) ^ hash)));
            float ox = Mathf.Lerp(locClampMinX, locClampMaxX, (float)rng.NextDouble());
            float oy = Mathf.Lerp(locClampMinY, locClampMaxY, (float)rng.NextDouble());
            float oz = Mathf.Lerp(locClampMinZ, locClampMaxZ, (float)rng.NextDouble());

            if (locAffectX) offset.x += ox;
            if (locAffectY) offset.y += oy;
            if (locAffectZ) offset.z += oz;

            if (locationSpace == OffsetSpace.Local)
                go.transform.localPosition += offset;
            else
                go.transform.position += offset;
        }

        outRot = finalRot;
        outScale = go.transform.localScale;
    }

    private void ApplyScaleUnified(ref Vector3 v, float mul)
    {
        if (scaleAffectX) v.x *= mul;
        if (scaleAffectY) v.y *= mul;
        if (scaleAffectZ) v.z *= mul;
    }

    private static Vector3 GetWorldCenter(List<GameObject> objects)
    {
        var b = new Bounds();
        bool init = false;
        foreach (var go in objects)
        {
            if (!go) continue;
            var r = go.GetComponent<Renderer>();
            var c = r ? r.bounds.center : go.transform.position;
            if (!init) { b = new Bounds(c, Vector3.zero); init = true; }
            else b.Encapsulate(c);
        }
        return init ? b.center : Vector3.zero;
    }

    private Transform GetGroupParentForScene(Scene scene)
    {
        if (groupParentCache.TryGetValue(scene, out var t)) return t;
        return null;
    }

    private static Transform FindOrCreateGroupParentInScene(Scene scene, string parentName, Vector3 position)
    {
        foreach (var root in scene.GetRootGameObjects())
            if (root && root.name == parentName) return root.transform;
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
                    if (r) b.Encapsulate(r.bounds);
                    else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
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

    private static GameObject CombineInstances(List<GameObject> instances, PivotMode pivotMode, Transform explicitParent, Transform groupParent, string finalName)
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

        var result = new GameObject(string.IsNullOrEmpty(finalName) ? "Combined Object" : finalName);
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

    private void TryConvertToShrub(GameObject go, int renderDistance)
    {
        if (!go) return;
        var type = Type.GetType("ConvertToShrub") ?? FindTypeByMonoName("ConvertToShrub");
        if (type == null)
        {
            Debug.LogWarning("ConvertToShrub script not found. Skipping shrub conversion.");
            return;
        }
        var existing = go.GetComponent(type);
        if (!existing) existing = Undo.AddComponent(go, type);

        // try set "Render Distance" or similar property
        var so = new SerializedObject((Component)existing);
        SerializedProperty rd = so.FindProperty("RenderDistance") ?? so.FindProperty("renderDistance") ?? so.FindProperty("distance");
        if (rd != null && rd.propertyType == SerializedPropertyType.Integer)
        {
            rd.intValue = renderDistance;
            so.ApplyModifiedProperties();
        }

        // invoke common method names if present
        var m = type.GetMethod("Rebuild", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? type.GetMethod("Build", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? type.GetMethod("Setup", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (m != null) { try { m.Invoke(existing, null); } catch { } }
    }

    private static Type FindTypeByMonoName(string className)
    {
        var guids = AssetDatabase.FindAssets("t:MonoScript");
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (!ms) continue;
            if (ms.name == className)
            {
                var t = ms.GetClass();
                if (t != null) return t;
            }
        }
        return null;
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
            if (!ms) continue;
            var n = ms.name;
            if (names.Contains(n))
            {
                var t = ms.GetClass();
                if (t != null) return t;
            }
        }
        foreach (var n in names) { var t = Type.GetType(n); if (t != null) return t; }
        return null;
    }

    private static string ApplyIncremental(string baseName, bool incremental)
    {
        if (!incremental) return baseName;
        // We intentionally keep a static counter per-run only; names will still be unique via Unity if necessary
        int num;
        if (!TempCounter.TryGetValue(baseName, out num)) num = 0;
        num++;
        TempCounter[baseName] = num;
        return $"{baseName}_{num:000}";
    }
    private static readonly Dictionary<string, int> TempCounter = new Dictionary<string, int>();

    // -----------------------------
    // UTILITIES
    // -----------------------------
    private static List<(Mesh mesh, Material[] mats)> GetMeshesFromPrefab(GameObject prefab)
    {
        var result = new List<(Mesh, Material[])>();
        if (!prefab) return result;
        var mfs = prefab.GetComponentsInChildren<MeshFilter>(true);
        foreach (var mf in mfs)
        {
            if (mf.sharedMesh == null) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            var mats = (mr && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) ? mr.sharedMaterials : null;
            result.Add((mf.sharedMesh, mats));
        }
        return result;
    }

    private static long SafeInt64(long v, long min, long max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    private void DrawMinMaxRow(string label, ref float min, ref float max, float hardMin, float hardMax)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(100));
        min = EditorGUILayout.FloatField("Min", min);
        max = EditorGUILayout.FloatField("Max", max);
        EditorGUILayout.EndHorizontal();
        // Keep sane
        if (!float.IsFinite(min)) min = hardMin;
        if (!float.IsFinite(max)) max = hardMax;
        if (max < min) { var t = min; min = max; max = t; }
        // Sliders under (shared track)
        float sMin = min, sMax = max;
        EditorGUILayout.MinMaxSlider(ref sMin, ref sMax, hardMin, hardMax);
        min = sMin; max = sMax;
    }

    private void DrawVector3Sliders(ref Vector3 v, float min, float max)
    {
        float x = v.x, y = v.y, z = v.z;
        EditorGUILayout.MinMaxSlider(new GUIContent("X"), ref x, ref x, min, max); v.x = x;
        EditorGUILayout.MinMaxSlider(new GUIContent("Y"), ref y, ref y, min, max); v.y = y;
        EditorGUILayout.MinMaxSlider(new GUIContent("Z"), ref z, ref z, min, max); v.z = z;
    }

    // -----------------------------
    // Local undo for parameters (not scene)
    // -----------------------------
    [Serializable]
    private class ParamSnapshot
    {
        public string json;
    }

    private string SnapshotParamsToJson()
    {
        var dto = JsonUtility.ToJson(this, false);
        return dto;
    }

    private void RestoreParamsFromJson(string json)
    {
        JsonUtility.FromJsonOverwrite(json, this);
    }

    private void PushUndoSnapshot()
    {
        // Trim forward history
        if (undoIndex >= 0 && undoIndex < undoStack.Count - 1)
            undoStack.RemoveRange(undoIndex + 1, undoStack.Count - (undoIndex + 1));
        undoStack.Add(new ParamSnapshot { json = SnapshotParamsToJson() });
        undoIndex = undoStack.Count - 1;
        if (undoStack.Count > MaxLocalUndoSnapshots)
        {
            undoStack.RemoveAt(0);
            undoIndex = undoStack.Count - 1;
        }
    }

    private bool CanUndoLocal() => undoIndex > 0;

    private void UndoLocal()
    {
        if (!CanUndoLocal()) return;
        undoIndex--;
        RestoreParamsFromJson(undoStack[undoIndex].json);
    }

    private void RandomizeAll()
    {
        // Only from Rotation onward; do not touch prefix, desired asset, naming, parent, pivot
        var r = new System.Random();
        // Rotation
        rotationMode = RotationMode.SeedValueOnY;
        rotationSeed = r.Next(1, int.MaxValue);
        rotationEuler = new Vector3(
            (float)(r.NextDouble() * 30f - 15f),
            (float)(r.NextDouble() * 30f - 15f),
            (float)(r.NextDouble() * 30f - 15f)
        );

        // Scale seed + clamping
        scaleMode = ScaleMode.SeedValue;
        scaleSeed = r.Next(1, int.MaxValue);
        scaleUnified = (float)(0.8 + r.NextDouble() * 0.8); // 0.8..1.6
        scaleAffectX = scaleAffectY = scaleAffectZ = true;

        // widen clamping variety
        scaleClampMinX = 0.5f; scaleClampMaxX = 2.0f;
        scaleClampMinY = 0.5f; scaleClampMaxY = 2.0f;
        scaleClampMinZ = 0.5f; scaleClampMaxZ = 2.0f;

        // Location offset
        locationSpace = OffsetSpace.Local;
        locationSeed = r.Next(1, int.MaxValue);
        locationOffset = new Vector3(
            (float)(r.NextDouble() * 2f - 1f),
            (float)(r.NextDouble() * 2f - 1f),
            (float)(r.NextDouble() * 2f - 1f));
        locAffectX = locAffectY = locAffectZ = true;

        locClampMinX = -2f; locClampMaxX = 2f;
        locClampMinY = -2f; locClampMaxY = 2f;
        locClampMinZ = -2f; locClampMaxZ = 2f;

        PushUndoSnapshot();
    }

    private void SaveFromPreviewSingle()
    {
        int count = CountPlaceholders(prefix);
        if (count != 1)
        {
            EditorUtility.DisplayDialog("Not a single object", "This action requires exactly one placeholder match.", "OK");
            return;
        }

        // Create a temporary root and save
        var go = Resources.FindObjectsOfTypeAll<Transform>()
            .Select(t => t ? t.gameObject : null)
            .FirstOrDefault(g => g && g.scene.IsValid() && g.name.StartsWith(prefix));
        if (!go) return;

        var root = new GameObject(string.IsNullOrEmpty(forcedName) ? "SavedFromPreview" : forcedName);
        Undo.RegisterCreatedObjectUndo(root, "Create temp save root");
        root.transform.position = go.transform.position;
        go.transform.SetParent(root.transform, true);

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, savePath);
        if (prefab != null) Debug.Log($"Saved prefab: {savePath}"); else Debug.LogError("Failed to save prefab.");

        // detach
        go.transform.SetParent(null, true);
        Undo.DestroyObjectImmediate(root);
    }

    // -----------------------------
    // Small utility classes
    // -----------------------------
    private struct DarkHeaderScope : IDisposable
    {
        private readonly Color prev;
        public DarkHeaderScope(float height)
        {
            prev = GUI.color;
            EditorGUILayout.BeginVertical(GUILayout.Height(height));
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true)), new Color(0.18f, 0.18f, 0.18f));
            GUILayout.Space(-height + 2f);
            GUI.color = Color.white;
        }
        public void Dispose()
        {
            EditorGUILayout.EndVertical();
            GUI.color = prev;
        }
    }

    // Custom Library Window (always-on-top utility)
    private class LibraryWindow : EditorWindow
    {
        private string search = "";
        private float thumbSize = 96f;
        private Vector2 scroll;
        private List<GameObject> prefabs = new List<GameObject>();
        private PlaceholderSwitcher owner;

        public void Init(PlaceholderSwitcher o)
        {
            owner = o;
            Refresh();
        }

        private void OnGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("GameObject Library", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Thumb Size", GUILayout.Width(70));
            thumbSize = GUILayout.HorizontalSlider(thumbSize, 48f, 196f, GUILayout.Width(160));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(50));
            var ns = GUILayout.TextField(search);
            if (ns != search) { search = ns; Refresh(); }
            if (GUILayout.Button("Refresh", GUILayout.Width(80))) Refresh();
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll);
            float col = Mathf.Max(1, Mathf.Floor(position.width / (thumbSize + 16f)));
            int perRow = Mathf.Clamp((int)col, 1, 8);

            int i = 0;
            GUILayout.BeginHorizontal();
            foreach (var p in prefabs)
            {
                if (i > 0 && i % perRow == 0)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }
                DrawThumb(p);
                i++;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
        }

        private void DrawThumb(GameObject p)
        {
            GUILayout.BeginVertical(GUILayout.Width(thumbSize + 8));
            var tex = AssetPreview.GetAssetPreview(p) ?? AssetPreview.GetMiniThumbnail(p);
            if (GUILayout.Button(tex, GUILayout.Width(thumbSize), GUILayout.Height(thumbSize)))
            {
                if (owner != null)
                {
                    owner.targetPrefab = p;
                    owner.Repaint();
                }
            }
            GUILayout.Label(p.name, EditorStyles.miniLabel, GUILayout.Width(thumbSize + 8));
            GUILayout.EndVertical();
        }

        private void Refresh()
        {
            var guids = AssetDatabase.FindAssets("t:Prefab");
            var all = guids.Select(g => AssetDatabase.GUIDToAssetPath(g))
                           .Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(p))
                           .Where(x => x != null);
            if (!string.IsNullOrEmpty(search))
            {
                all = all.Where(x => x.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            prefabs = all.ToList();
        }
    }
}
#endif
