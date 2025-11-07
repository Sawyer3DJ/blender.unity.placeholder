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
    private const string VersionLabel = "v.1.0.0";

    // -----------------------------
    // Inputs (left column, above viewer)
    // -----------------------------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject targetPrefab = null;
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false;
    [SerializeField] private bool autoSwitch = false;

    // -----------------------------
    // Viewer & background
    // -----------------------------
    private enum ViewerBg { CurrentSkybox, UnitySkybox, ManualSkybox }
    [SerializeField] private ViewerBg viewerBg = ViewerBg.CurrentSkybox;

    private PreviewRenderUtility preview;
    private float pvYaw = -30f, pvPitch = 15f, pvDist = 6f;
    private Vector3 pvPan = Vector3.zero;
    private bool pvUserAdjusted = false;
    private Material fallbackMat;

    // Manual skybox material (preview-only)
    [SerializeField] private Material manualSkyboxMat = null;

    // Orbit controls
    private bool orbitInvertY = true;   // default ON
    private bool orbitInvertX = false;  // default OFF

    // External viewport
    private SceneView externalViewport;
    [SerializeField] private bool autoSyncViewport = true;

    // Library windows
    private LibraryWindow libraryWindow;
    private SkyboxLibraryWindow skyboxLibraryWindow;

    // -----------------------------
    // Transform Tools (right column)
    // -----------------------------
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero;
    [SerializeField] private long rotationSeed = 1234;

    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private float scaleUnified = 1f;
    [SerializeField] private long scaleSeed = 321;
    [SerializeField] private bool scaleAffectX = true, scaleAffectY = true, scaleAffectZ = true;
    [SerializeField] private float scaleClampMinX = 0.8f, scaleClampMaxX = 1.2f;
    [SerializeField] private float scaleClampMinY = 0.8f, scaleClampMaxY = 1.2f;
    [SerializeField] private float scaleClampMinZ = 0.8f, scaleClampMaxZ = 1.2f;

    private enum OffsetSpace { Local, World }
    [SerializeField] private OffsetSpace locationSpace = OffsetSpace.Local;
    [SerializeField] private Vector3 locationOffset = Vector3.zero;
    [SerializeField] private long locationSeed = 4567;
    [SerializeField] private bool locAffectX = true, locAffectY = true, locAffectZ = true;
    [SerializeField] private float locClampMinX = -1f, locClampMaxX = 1f;
    [SerializeField] private float locClampMinY = -1f, locClampMaxY = 1f;
    [SerializeField] private float locClampMinZ = -1f, locClampMaxZ = 1f;

    [SerializeField] private Transform explicitParent = null;
    [SerializeField] private bool groupWithEmptyParent = false;
    [SerializeField] private string groupParentName = "Imported Placeholders";
    private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
    [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.FirstObject;
    [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;

    [SerializeField] private bool combineIntoOne = false;
    private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] private PivotMode pivotMode = PivotMode.Parent;

    private enum MoveTarget { None, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinates, SelectedObject, Parent }
    [SerializeField] private MoveTarget moveAllTo = MoveTarget.None;
    [SerializeField] private Vector3 moveWorldCoordinate = Vector3.zero;

    [SerializeField] private bool convertToShrub = false;
    [SerializeField] private int  shrubRenderDistance = 1000;
    [SerializeField] private bool rebuildInstancedCollision = false;

    [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

    private readonly Dictionary<Scene, Transform> groupParentCache = new Dictionary<Scene, Transform>();
    private readonly List<ParamSnapshot> undoStack = new List<ParamSnapshot>();
    private int undoIndex = -1;

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
        PushUndoSnapshot();
    }

    private void OnDisable()
    {
        if (preview != null) { preview.Cleanup(); preview = null; }
        if (fallbackMat != null) DestroyImmediate(fallbackMat);
        fallbackMat = null;

        if (libraryWindow != null) { libraryWindow.Close(); libraryWindow = null; }
        if (skyboxLibraryWindow != null) { skyboxLibraryWindow.Close(); skyboxLibraryWindow = null; }
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

    private void OnGUI()
    {
        EnsurePreview();
        DrawTitleRow();

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.BeginVertical(GUILayout.MinWidth(LeftColMinWidth));
        DrawReplaceSectionLeft();
        DrawViewer();
        EditorGUILayout.EndVertical();

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

        GUILayout.FlexibleSpace();
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(VersionLabel, EditorStyles.miniLabel, GUILayout.Width(80));
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        if (autoSyncViewport) UpdateExternalViewport();
        if (autoSwitch && CanSwitchNow())
        {
            if (!autoSwitchWarningShown)
            {
                autoSwitchWarningShown = true;
                EditorUtility.DisplayDialog(
                    "Auto Switch Enabled",
                    "Warning: While Auto Switch is ON, whichever model you preview will automatically replace the current game objects matched by the prefix.\nYou have 64 undos. Use them wisely!",
                    "OK");
            }
            RunSwitch(replaceMode: targetPrefab != null);
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

            if (GUILayout.Button("Open GameObject Library", GUILayout.Height(22), GUILayout.Width(200)))
                OpenLibrary();
            if (GUILayout.Button("Randomize All Parameters", GUILayout.Height(22), GUILayout.Width(200)))
            {
                RandomizeAll(); Repaint();
            }
            GUI.enabled = CanUndoLocal();
            if (GUILayout.Button("Undo", GUILayout.Height(22), GUILayout.Width(90)))
            {
                UndoLocal(); Repaint();
            }
            GUI.enabled = true;

            GUILayout.EndHorizontal();
        }
    }

    // ---------------- LEFT: Replace section ----------------
    private void DrawReplaceSectionLeft()
    {
        using (new DarkHeaderScope(SectionHeaderHeight))
        {
            GUILayout.Label("Replace Object Placeholders", EditorStyles.boldLabel, GUILayout.Height(SectionHeaderHeight));
        }

        // Prefix + live status
        EditorGUILayout.BeginHorizontal();
        var newPrefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
        if (newPrefix != prefix) { prefix = newPrefix; pvUserAdjusted = false; }
        int found = CountPlaceholders(prefix);
        string status = (prefix.Length < 3) ? "enter ≥ 3 chars"
                        : (found == 0) ? "⚠️ no assets found"
                        : $"{found} object(s) found";
        GUILayout.Label(status, EditorStyles.miniLabel, GUILayout.Width(150));
        EditorGUILayout.EndHorizontal();

        var obj = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), targetPrefab, typeof(GameObject), false);
        if (obj != targetPrefab) { targetPrefab = obj; pvUserAdjusted = false; }

        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);

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
        return CountPlaceholders(prefix) > 0;
    }

    // ---------------- LEFT: Viewer ----------------
    private void DrawViewer()
    {
        EditorGUILayout.Space(6);
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
            if (GUILayout.Toggle(viewerBg == ViewerBg.ManualSkybox, "Manual Skybox", "Button", GUILayout.Width(120)))
            {
                if (viewerBg != ViewerBg.ManualSkybox) { viewerBg = ViewerBg.ManualSkybox; ApplyViewerBackground(); }
            }

            // External viewport controls
            autoSyncViewport = GUILayout.Toggle(autoSyncViewport, "Auto-Sync Model View to Viewport", GUILayout.Width(240));
            if (GUILayout.Button("Open Viewport", GUILayout.Width(110))) OpenViewport();
            if (GUILayout.Button("Close Viewport", GUILayout.Width(110))) CloseViewport();
            GUILayout.EndHorizontal();
        }

        // Manual skybox selector (only when ManualSkybox)
        if (viewerBg == ViewerBg.ManualSkybox)
        {
            EditorGUILayout.BeginHorizontal();
            manualSkyboxMat = (Material)EditorGUILayout.ObjectField(new GUIContent("Preview Skybox"), manualSkyboxMat, typeof(Material), false);
            if (GUILayout.Button("Skybox Library", GUILayout.Width(130)))
            {
                OpenSkyboxLibrary();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("Manual skybox is preview-only. Your scene's RenderSettings are restored after drawing.", MessageType.Info);
        }

        var rect = GUILayoutUtility.GetRect(10, 10, 420, 420);
        DrawPreview(rect);

        // Controls under viewer
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Re-center View", GUILayout.Height(24), GUILayout.Width(130)))
        {
            pvPan = Vector3.zero; pvUserAdjusted = false; Repaint();
        }
        GUILayout.FlexibleSpace();
        GUILayout.Label("Load / Save", EditorStyles.miniBoldLabel, GUILayout.Width(80));
        EditorGUILayout.EndHorizontal();

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

        EditorGUILayout.BeginHorizontal();
        bool exactlyOne = CountPlaceholders(prefix) == 1;
        GUI.enabled = exactlyOne;
        if (GUILayout.Button("Save From Preview As…", GUILayout.Height(28)))
            SaveFromPreviewSingle();
        GUI.enabled = true;

        if (GUILayout.Button("Load Asset From File", GUILayout.Height(28), GUILayout.Width(180)))
        {
            EditorUtility.DisplayDialog("Import Info",
                "To load external models (.fbx/.obj/.gltf), import them into the Project first. Then drag them here or pick via the library.",
                "OK");
        }
        EditorGUILayout.EndHorizontal();

        int count = CountPlaceholders(prefix);
        if (count == 0)
            EditorGUILayout.HelpBox("Nothing to save, search for objects via a prefix to enable saving.", MessageType.Info);
        else if (count > 1 && !combineIntoOne)
            EditorGUILayout.HelpBox("Multiple placeholders detected. Combine all objects first to enable single-object Save From Preview.", MessageType.Warning);

        EditorGUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        GUI.enabled = CanSwitchNow();
        if (GUILayout.Button("Switch Placeholders", GUILayout.Height(36)))
            RunSwitch(replaceMode: targetPrefab != null);
        GUI.enabled = true;

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
            case ViewerBg.ManualSkybox:
                cam.clearFlags = CameraClearFlags.Skybox;
                break;
            case ViewerBg.UnitySkybox:
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = new Color(0.58f, 0.76f, 0.96f); // "Unity-like" sky tint
                break;
        }
    }

    private void DrawPreview(Rect rect)
    {
        if (preview == null) return;

        var candidates = (prefix.Length >= 3)
            ? Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
                .Select(t => t.gameObject).ToList()
            : new List<GameObject>();

        bool hasDesired = targetPrefab != null;
        bool hasAny = candidates.Count > 0;

        Vector3 pivot = GetPreviewPivot(candidates) + pvPan;

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

        // Temporarily override RenderSettings.skybox for ManualSkybox
        var prevSkybox = RenderSettings.skybox;
        bool overrideSkybox = viewerBg == ViewerBg.ManualSkybox && manualSkyboxMat != null;
        if (overrideSkybox) RenderSettings.skybox = manualSkyboxMat;

        try
        {
            if (Event.current.type == EventType.Repaint)
            {
                preview.BeginPreview(rect, GUIStyle.none);

                var cam = preview.camera;
                var rot = Quaternion.Euler(pvPitch, pvYaw, 0f);
                cam.transform.position = pivot + rot * (Vector3.back * pvDist);
                cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 10000f;

                if (hasAny)
                {
                    if (hasDesired)
                    {
                        var meshes = GetMeshesFromPrefab(targetPrefab);
                        if (meshes.Count == 0)
                        {
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
                    DrawHelperOverlay(rect,
                        "No objects to preview.\n\nEnter a 3+ character prefix and/or pick a Desired Asset (Prefab).\n" +
                        "Tip: Open the GameObject Library to find assets.\nTry seeds and Location Offset to craft variants — have fun!");
                }

                preview.camera.Render();
                var tex = preview.EndPreview();
                GUI.DrawTexture(rect, tex, UnityEngine.ScaleMode.StretchToFill, false); // NOTE: fully-qualified to avoid enum clash
            }
        }
        finally
        {
            // Restore project skybox immediately
            if (overrideSkybox) RenderSettings.skybox = prevSkybox;
        }

        // Mouse controls
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
            }
            if (Event.current.type == EventType.MouseDrag && Event.current.button == 0 && Event.current.shift)
            {
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

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Controls", EditorStyles.miniLabel, GUILayout.Width(60));
        orbitInvertY = GUILayout.Toggle(orbitInvertY, "Y Inverted", GUILayout.Width(90));
        orbitInvertX = GUILayout.Toggle(orbitInvertX, "X Inverted", GUILayout.Width(90));
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    private void DrawHelperOverlay(Rect rect, string message)
    {
        var c = new Color(0f, 0f, 0f, 0.5f);
        EditorGUI.DrawRect(rect, c);
        var style = new GUIStyle(EditorStyles.wordWrappedLabel)
        { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
        GUI.Label(rect, message, style);
    }

    private void UpdateExternalViewport()
    {
        if (externalViewport == null) return;

        var candidates = (prefix.Length >= 3)
            ? Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
                .Select(t => t.gameObject).ToList()
            : new List<GameObject>();

        Bounds b;
        if (!TryCalcBoundsForPreview(candidates, targetPrefab, out b)) return;

        var pivot = b.center;
        float dist = Mathf.Max(b.extents.magnitude * 2f, 2f);

        var dir = Quaternion.Euler(pvPitch, pvYaw, 0f) * Vector3.back;
        var pos = pivot + dir * dist;
        externalViewport.LookAtDirect(pivot, Quaternion.LookRotation(pivot - pos, Vector3.up), dist);
        externalViewport.Repaint();
    }

    private void OpenViewport()
    {
        if (externalViewport == null)
        {
            externalViewport = ScriptableObject.CreateInstance<SceneView>();
            externalViewport.titleContent = new GUIContent("Placeholder Viewport");
            externalViewport.ShowUtility();
            externalViewport.wantsMouseMove = true;
        }
    }

    private void CloseViewport()
    {
        if (externalViewport != null) { externalViewport.Close(); externalViewport = null; }
    }

    private void OpenLibrary()
    {
        if (libraryWindow == null)
        {
            libraryWindow = ScriptableObject.CreateInstance<LibraryWindow>();
            libraryWindow.Init(this);
            libraryWindow.ShowUtility();
            var r = position;
            libraryWindow.position = new Rect(r.xMax + 6, r.yMin, Mathf.Max(520, r.width * 0.45f), Mathf.Max(520, r.height - 40));
        }
        else libraryWindow.Focus();
    }

    private void OpenSkyboxLibrary()
    {
        if (skyboxLibraryWindow == null)
        {
            skyboxLibraryWindow = ScriptableObject.CreateInstance<SkyboxLibraryWindow>();
            skyboxLibraryWindow.Init(this);
            skyboxLibraryWindow.ShowUtility();
            var r = position;
            skyboxLibraryWindow.position = new Rect(r.xMax + 6, r.yMin, Mathf.Max(420, r.width * 0.38f), Mathf.Max(520, r.height - 40));
        }
        else skyboxLibraryWindow.Focus();
    }

    private bool TryCalcBoundsForPreview(List<GameObject> candidates, GameObject desired, out Bounds b)
    {
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

    // ---------------- RIGHT: Transform Tools ----------------
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
        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(rotationMode == RotationMode.PlaceholderRotation ? "Rotation (adds to placeholder)" :
                        rotationMode == RotationMode.NewRotation ? "Rotation (new rotation)" :
                        "Rotation (offset on top of seeded Y)", GUILayout.Width(240));
        rotationEuler = EditorGUILayout.Vector3Field(GUIContent.none, rotationEuler);
        EditorGUILayout.EndHorizontal();
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
        scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(scaleMode == ScaleMode.PlaceholderScale ? "Scale (multiplies placeholder scale)" :
                        scaleMode == ScaleMode.NewScale ? "Scale (new uniform)" :
                        "Scale (seeded uniform + offset)", GUILayout.Width(240));
        scaleUnified = Mathf.Max(0.0001f, EditorGUILayout.FloatField(GUIContent.none, scaleUnified));
        EditorGUILayout.EndHorizontal();
        scaleUnified = EditorGUILayout.Slider(scaleUnified, 0.0001f, 10f);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Influenced Axes", GUILayout.Width(120));
        scaleAffectX = GUILayout.Toggle(scaleAffectX, "X", "Button", GUILayout.Width(30));
        scaleAffectY = GUILayout.Toggle(scaleAffectY, "Y", "Button", GUILayout.Width(30));
        scaleAffectZ = GUILayout.Toggle(scaleAffectZ, "Z", "Button", GUILayout.Width(30));
        EditorGUILayout.EndHorizontal();

        if (scaleMode == ScaleMode.SeedValue)
        {
            EditorGUILayout.BeginHorizontal();
            scaleSeed = SafeInt64(EditorGUILayout.LongField(new GUIContent("Random scaling seed"), scaleSeed), 1, 9999999999L);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                scaleSeed = UnityEngine.Random.Range(1, int.MaxValue);
            EditorGUILayout.EndHorizontal();

            GUILayout.Label("Scale Clamping", EditorStyles.miniBoldLabel);
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

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Location Transform", GUILayout.Width(160));
        locationOffset = EditorGUILayout.Vector3Field(GUIContent.none, locationOffset);
        EditorGUILayout.EndHorizontal();
        DrawVector3Sliders(ref locationOffset, -100f, 100f);

        EditorGUILayout.BeginHorizontal();
        locationSeed = SafeInt64(EditorGUILayout.LongField(new GUIContent("Random location seed"), locationSeed), 1, 9999999999L);
        if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
            locationSeed = UnityEngine.Random.Range(1, int.MaxValue);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Influenced Axes", GUILayout.Width(120));
        locAffectX = GUILayout.Toggle(locAffectX, "X", "Button", GUILayout.Width(30));
        locAffectY = GUILayout.Toggle(locAffectY, "Y", "Button", GUILayout.Width(30));
        locAffectZ = GUILayout.Toggle(locAffectZ, "Z", "Button", GUILayout.Width(30));
        EditorGUILayout.EndHorizontal();

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
                "Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, and triggers are lost.\n" +
                "Tip: If you need interactivity but want to move many objects together, parent them under an empty and consider Static Batching.",
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

        convertToShrub = EditorGUILayout.Toggle(new GUIContent("Convert to Shrub"), convertToShrub);
        using (new EditorGUI.DisabledScope(!convertToShrub))
        {
            shrubRenderDistance = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent("Shrub Render Distance"), shrubRenderDistance));
        }

        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Rebuild instanced collision"), rebuildInstancedCollision);
        EditorGUILayout.Space(4);
    }

    // ---------------- ACTIONS ----------------
    private static bool IsPrefabAsset(GameObject go)
    {
        if (!go) return false;
        var t = PrefabUtility.GetPrefabAssetType(go);
        return t != PrefabAssetType.NotAPrefab && t != PrefabAssetType.MissingAsset;
    }

    private bool autoSwitchWarningShown = false;

    private void RunSwitch(bool replaceMode)
    {
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

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Placeholder Switcher");

        var spawned = new List<GameObject>();
        bool operateOnPlaceholders = !replaceMode;

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
                    ApplyTransforms(src, out _, out _);
                    if (groupingParent) src.transform.SetParent(groupingParent, true);
                    spawned.Add(src);
                }
                else
                {
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

        if (combineIntoOne && spawned.Count > 0)
        {
            finalRoot = CombineInstances(spawned, pivotMode, explicitParent, GetGroupParentForScene(spawned[0].scene), string.IsNullOrEmpty(forcedName) ? "Combined Object" : forcedName);
            if (!operateOnPlaceholders)
            {
                foreach (var go in spawned) if (go != null && go != finalRoot) Undo.DestroyObjectImmediate(go);
            }
        }

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

        var newParent = groupingParent ? groupingParent : parent;
        inst.transform.SetParent(newParent, false);

        inst.transform.localPosition = localPos;
        inst.transform.localRotation = localRot;
        inst.transform.localScale = localScale;

        inst.layer = layer;
        try { inst.tag = tag; } catch { }
        GameObjectUtility.SetStaticEditorFlags(inst, staticFlags);
        inst.SetActive(active);

        if (!string.IsNullOrEmpty(forcedBase)) inst.name = ApplyIncremental(forcedBase, incremental);
        else inst.name = ApplyIncremental(inst.name, incremental);

        Undo.DestroyObjectImmediate(src);
        return inst;
    }

    private void ApplyTransforms(GameObject go, out Quaternion outRot, out Vector3 outScale)
    {
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

        Vector3 s = go.transform.localScale;
        Vector3 finalS = s;
        switch (scaleMode)
        {
            case ScaleMode.PlaceholderScale:
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

                    finalS = new Vector3(scaleAffectX ? rx : s.x,
                                         scaleAffectY ? ry : s.y,
                                         scaleAffectZ ? rz : s.z);

                    if (scaleAffectX) finalS.x *= scaleUnified;
                    if (scaleAffectY) finalS.y *= scaleUnified;
                    if (scaleAffectZ) finalS.z *= scaleUnified;
                }
                break;
        }
        go.transform.localScale = finalS;

        {
            Vector3 offset = locationOffset;
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

        var so = new SerializedObject((Component)existing);
        SerializedProperty rd = so.FindProperty("RenderDistance") ?? so.FindProperty("renderDistance") ?? so.FindProperty("distance");
        if (rd != null && rd.propertyType == SerializedPropertyType.Integer)
        {
            rd.intValue = renderDistance;
            so.ApplyModifiedProperties();
        }

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
        int num;
        if (!TempCounter.TryGetValue(baseName, out num)) num = 0;
        num++;
        TempCounter[baseName] = num;
        return $"{baseName}_{num:000}";
    }
    private static readonly Dictionary<string, int> TempCounter = new Dictionary<string, int>();

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

        if (!float.IsFinite(min)) min = hardMin;
        if (!float.IsFinite(max)) max = hardMax;
        if (max < min) { var t = min; min = max; max = t; }

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

    // -------- local param undo --------
    [Serializable] private class ParamSnapshot { public string json; }

    private string SnapshotParamsToJson() => JsonUtility.ToJson(this, false);
    private void RestoreParamsFromJson(string json) => JsonUtility.FromJsonOverwrite(json, this);

    private void PushUndoSnapshot()
    {
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
        var r = new System.Random();

        rotationMode = RotationMode.SeedValueOnY;
        rotationSeed = r.Next(1, int.MaxValue);
        rotationEuler = new Vector3(
            (float)(r.NextDouble() * 30f - 15f),
            (float)(r.NextDouble() * 30f - 15f),
            (float)(r.NextDouble() * 30f - 15f));

        scaleMode = ScaleMode.SeedValue;
        scaleSeed = r.Next(1, int.MaxValue);
        scaleUnified = (float)(0.8 + r.NextDouble() * 0.8);
        scaleAffectX = scaleAffectY = scaleAffectZ = true;
        scaleClampMinX = 0.5f; scaleClampMaxX = 2.0f;
        scaleClampMinY = 0.5f; scaleClampMaxY = 2.0f;
        scaleClampMinZ = 0.5f; scaleClampMaxZ = 2.0f;

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

        go.transform.SetParent(null, true);
        Undo.DestroyObjectImmediate(root);
    }

    // -------- Themed header scope --------
    private struct DarkHeaderScope : IDisposable
    {
        private readonly Rect rect;
        public DarkHeaderScope(float height)
        {
            rect = GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.18f, 0.18f, 0.18f));
            GUILayout.Space(-height + 2f);
        }
        public void Dispose() { }
    }

    // -------- Prefab library window --------
    private class LibraryWindow : EditorWindow
    {
        private string search = "";
        private float thumbSize = 96f;
        private Vector2 scroll;
        private List<GameObject> prefabs = new List<GameObject>();
        private PlaceholderSwitcher owner;

        public void Init(PlaceholderSwitcher o) { owner = o; Refresh(); }

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
                if (owner != null) { owner.targetPrefab = p; owner.Repaint(); }
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
                all = all.Where(x => x.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            prefabs = all.ToList();
        }
    }

    // -------- Skybox library window --------
    private class SkyboxLibraryWindow : EditorWindow
    {
        private string search = "";
        private float thumbSize = 96f;
        private Vector2 scroll;
        private List<Material> mats = new List<Material>();
        private PlaceholderSwitcher owner;

        public void Init(PlaceholderSwitcher o) { owner = o; Refresh(); }

        private void OnGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Skybox Library", EditorStyles.boldLabel);
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
            foreach (var m in mats)
            {
                if (i > 0 && i % perRow == 0)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }
                DrawThumb(m);
                i++;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
        }

        private void DrawThumb(Material m)
        {
            GUILayout.BeginVertical(GUILayout.Width(thumbSize + 8));
            var tex = AssetPreview.GetAssetPreview(m) ?? AssetPreview.GetMiniThumbnail(m);
            if (GUILayout.Button(tex, GUILayout.Width(thumbSize), GUILayout.Height(thumbSize)))
            {
                if (owner != null) { owner.manualSkyboxMat = m; owner.viewerBg = ViewerBg.ManualSkybox; owner.ApplyViewerBackground(); owner.Repaint(); }
            }
            GUILayout.Label(m.name, EditorStyles.miniLabel, GUILayout.Width(thumbSize + 8));
            GUILayout.EndVertical();
        }

        private void Refresh()
        {
            string[] guids = AssetDatabase.FindAssets("t:Material");
            mats = guids.Select(g => AssetDatabase.GUIDToAssetPath(g))
                        .Select(p => AssetDatabase.LoadAssetAtPath<Material>(p))
                        .Where(mat => mat != null && mat.shader != null && mat.shader.name.StartsWith("Skybox", StringComparison.OrdinalIgnoreCase))
                        .Where(mat => string.IsNullOrEmpty(search) || mat.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
        }
    }
}
#endif
