#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// ===============================================
// PlaceholderTool.cs
// Menu: Tools > Placeholders > Placeholder Switcher
// ===============================================
public class PlaceholderSwitcher : EditorWindow
{
    // ---------- Inputs ----------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject targetPrefab = null;

    // ---------- Naming ----------
    private enum OriginalNameSource { Placeholder, Prefab }
    [SerializeField] private OriginalNameSource originalNameSource = OriginalNameSource.Prefab;
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false;
    private readonly Dictionary<string,int> _nameCounters = new Dictionary<string,int>();

    // ---------- Rotation ----------
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero; // added on top of placeholder OR used as absolute (per mode)
    [SerializeField] private long rotationSeed = 1234;             // up to 10 digits

    // ---------- Scale ----------
    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private Vector3 scaleXYZ = Vector3.one;       // shown for all modes; in Seed mode it's added to uniform seed
    [SerializeField] private long scaleSeed = 321;
    [SerializeField] private float scaleRandomMin = 0.8f;
    [SerializeField] private float scaleRandomMax = 1.2f;

    // ---------- Location Offset (NEW) ----------
    private enum LocationOffsetMode { ObjectOrigin, WorldOrigin }
    [SerializeField] private LocationOffsetMode locationMode = LocationOffsetMode.ObjectOrigin;
    [SerializeField] private bool axisXEnabled = true;
    [SerializeField] private bool axisYEnabled = true;
    [SerializeField] private bool axisZEnabled = true;
    [SerializeField] private Vector3 locationOffset = Vector3.zero;  // base offset
    [SerializeField] private long locationSeed = 9998887777;         // adds per-axis random within clamps
    [SerializeField] private Vector3 locationClampMin = Vector3.zero;
    [SerializeField] private Vector3 locationClampMax = Vector3.zero;

    // ---------- Parenting ----------
    [SerializeField] private Transform explicitParent = null;
    [SerializeField] private bool groupWithEmptyParent = false;
    [SerializeField] private string groupParentName = "Imported Placeholders";
    private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
    [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.FirstObject;
    [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;

    // ---------- Combine ----------
    [SerializeField] private bool combineIntoOne = false; // static only
    private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] private PivotMode pivotMode = PivotMode.Parent;

    // ---------- Move / Final Position ----------
    private enum MoveToMode { None, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinates, SelectedObject, Parent }
    [SerializeField] private MoveToMode moveToMode = MoveToMode.None;
    [SerializeField] private Vector3 moveWorldCoordinate = Vector3.zero;

    // ---------- Collision ----------
    [SerializeField] private bool rebuildInstancedCollision = false;

    // ---------- Save (under viewer) ----------
    [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

    // ---------- Preview ----------
    private enum PreviewBg { CurrentSkybox, BasicScene, Viewport }
    [SerializeField] private PreviewBg previewBackground = PreviewBg.CurrentSkybox;

    private PreviewRenderUtility previewUtil;
    private float previewYaw = -30f;
    private float previewPitch = 15f;
    private float previewDistance = 2.2f;
    private bool previewUserAdjusted = false;
    private Vector3 previewPivotOffset = Vector3.zero;

    private struct PrefabDraw
    {
        public Mesh mesh;
        public Material[] mats;
        public Matrix4x4 localMatrix;   // prefab local -> root
        public Bounds localBounds;
    }
    private readonly List<PrefabDraw> prefabMeshes = new List<PrefabDraw>();
    private Material fallbackMat;

    // ---------- State ----------
    private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();
    private Vector2 _rightScroll;

    // Snapshot for “Randomise settings / Undo”
    [Serializable] private struct SettingsSnapshot
    {
        public RotationMode rotMode; public Vector3 rot; public long rotSeed;
        public ScaleMode scMode; public Vector3 sc; public long scSeed; public float scMin; public float scMax;
        public LocationOffsetMode locMode; public Vector3 loc; public long locSeed; public Vector3 locMin; public Vector3 locMax;
        public bool axX; public bool axY; public bool axZ;
    }
    private SettingsSnapshot _lastRandomSnapshot;
    private bool _hasSnapshot = false;

    [MenuItem("Tools/Placeholders/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(1200, 820);
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
        if (!previewUtil.camera.TryGetComponent<Skybox>(out _))
            previewUtil.camera.gameObject.AddComponent<Skybox>();
        fallbackMat = new Material(Shader.Find("Standard"));
        ApplyPreviewBackground();
        RebuildPrefabMeshCache();
    }

    private void CleanupPreview()
    {
        previewUtil?.Cleanup();
        if (fallbackMat) DestroyImmediate(fallbackMat);
        previewUtil = null; fallbackMat = null;
        prefabMeshes.Clear();
    }

    // ------------------------------------------------------
    // UI
    // ------------------------------------------------------
    private void OnGUI()
    {
        // Centered big title
        var big = new GUIStyle(EditorStyles.largeLabel) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 22 };
        GUILayout.Label("Placeholder Switcher", big);
        EditorGUILayout.Space(6);

        // Split: Viewer (left) | Options (right)
        EditorGUILayout.BeginHorizontal();

        float leftWidth = Mathf.Max(position.width * 0.60f, 640f);
        EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
        DrawPreviewArea(leftWidth);
        EditorGUILayout.EndVertical();

        float rightWidth = Mathf.Clamp(position.width - leftWidth - 24f, 460f, 640f);
        EditorGUILayout.BeginVertical(GUILayout.Width(rightWidth));
        _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll);

        float oldLW = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 220f;

        DrawControls(rightWidth);

        EditorGUIUtility.labelWidth = oldLW;
        EditorGUILayout.EndScrollView();

        // Randomise / Undo row (sticks to bottom of right pane)
        EditorGUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Randomise Settings (seeds)", GUILayout.Height(24)))
        {
            _lastRandomSnapshot = new SettingsSnapshot {
                rotMode = rotationMode, rot = rotationEuler, rotSeed = rotationSeed,
                scMode = scaleMode, sc = scaleXYZ, scSeed = scaleSeed, scMin = scaleRandomMin, scMax = scaleRandomMax,
                locMode = locationMode, loc = locationOffset, locSeed = locationSeed,
                locMin = locationClampMin, locMax = locationClampMax,
                axX = axisXEnabled, axY = axisYEnabled, axZ = axisZEnabled
            };
            _hasSnapshot = true;

            var rng = new System.Random();
            rotationSeed = rng.NextInt64(1, 9_000_000_000);   // up to 10 digits
            scaleSeed    = rng.NextInt64(1, 9_000_000_000);
            locationSeed = rng.NextInt64(1, 9_000_000_000);
            Repaint();
        }
        using (new EditorGUI.DisabledScope(!_hasSnapshot))
        {
            if (GUILayout.Button("Undo", GUILayout.Width(80), GUILayout.Height(24)))
            {
                rotationMode = _lastRandomSnapshot.rotMode; rotationEuler = _lastRandomSnapshot.rot; rotationSeed = _lastRandomSnapshot.rotSeed;
                scaleMode = _lastRandomSnapshot.scMode; scaleXYZ = _lastRandomSnapshot.sc; scaleSeed = _lastRandomSnapshot.scSeed;
                scaleRandomMin = _lastRandomSnapshot.scMin; scaleRandomMax = _lastRandomSnapshot.scMax;
                locationMode = _lastRandomSnapshot.locMode; locationOffset = _lastRandomSnapshot.loc; locationSeed = _lastRandomSnapshot.locSeed;
                locationClampMin = _lastRandomSnapshot.locMin; locationClampMax = _lastRandomSnapshot.locMax;
                axisXEnabled = _lastRandomSnapshot.axX; axisYEnabled = _lastRandomSnapshot.axY; axisZEnabled = _lastRandomSnapshot.axZ;
                _hasSnapshot = false; Repaint();
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical(); // right
        EditorGUILayout.EndHorizontal(); // split
    }

    private GUIStyle SectionHeaderStyle()
    {
        return new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
    }

    private void DrawControls(float rightWidth)
    {
        GUILayout.Label("Replace Object Placeholders", SectionHeaderStyle());

        // Prefix with live count
        int liveCount = CountPlaceholders(prefix);
        EditorGUILayout.BeginHorizontal();
        prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix", "e.g. 'SS_' (min 3 chars)"), prefix);
        if (prefix.Length >= 3)
        {
            if (liveCount <= 0) GUILayout.Label("⚠️ no assets found", EditorStyles.miniBoldLabel, GUILayout.Width(140));
            else GUILayout.Label($"{liveCount} object(s) found", EditorStyles.miniLabel, GUILayout.Width(140));
        }
        EditorGUILayout.EndHorizontal();

        // Desired asset
        var newPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), targetPrefab, typeof(GameObject), false);
        if (newPrefab != targetPrefab) { targetPrefab = newPrefab; RebuildPrefabMeshCache(); }

        // Naming
        using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(forcedName)))
        {
            originalNameSource = (OriginalNameSource)EditorGUILayout.EnumPopup(new GUIContent("Original name source"), originalNameSource);
        }
        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);

        EditorGUILayout.Space(6);

        // Rotation
        GUILayout.Label("Rotation", SectionHeaderStyle());
        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);
        rotationEuler = DrawVector3WithSliders(
            rotationMode == RotationMode.NewRotation ? "Rotation (new)" :
            rotationMode == RotationMode.PlaceholderRotation ? "Rotation (adds to placeholder)" :
            "Rotation (offset added to seeded Y)",
            rotationEuler, -360f, 360f);

        if (rotationMode == RotationMode.SeedValueOnY)
        {
            EditorGUILayout.BeginHorizontal();
            rotationSeed = EditorGUILayout.LongField(new GUIContent("Random rotation seed (Y)"), rotationSeed);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                rotationSeed = UnityEngine.Random.Range(1, int.MaxValue);
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(6);

        // Scale
        GUILayout.Label("Scale", SectionHeaderStyle());
        scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

        scaleXYZ = DrawVector3WithSliders(
            scaleMode == ScaleMode.PlaceholderScale ? "Scale (multiplies placeholder)" :
            scaleMode == ScaleMode.NewScale ? "Scale (new)" :
            "Scale (adds to seeded uniform)",
            scaleXYZ, 0.001f, 8f);

        if (scaleMode == ScaleMode.SeedValue)
        {
            scaleSeed = EditorGUILayout.LongField(new GUIContent("Random scaling seed"), scaleSeed);
            GUILayout.Label("Scale clamping");
            EditorGUI.indentLevel++;
            scaleRandomMin = DrawFloatWithSlider("Min", scaleRandomMin, 0.001f, 8f);
            scaleRandomMax = DrawFloatWithSlider("Max", scaleRandomMax, 0.001f, 8f);
            if (scaleRandomMax < scaleRandomMin) (scaleRandomMin, scaleRandomMax) = (scaleRandomMax, scaleRandomMin);
            EditorGUI.indentLevel--;
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130))) scaleSeed = UnityEngine.Random.Range(1, int.MaxValue);
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(6);

        // Location Offset
        GUILayout.Label("Location Offset", SectionHeaderStyle());
        locationMode = (LocationOffsetMode)EditorGUILayout.EnumPopup(new GUIContent("Location offset mode"), locationMode);

        // Influenced Axis buttons
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent("Influenced Axis"), GUILayout.Width(220));
        axisXEnabled = GUILayout.Toggle(axisXEnabled, axisXEnabled ? "X" : "X (locked)", "Button", GUILayout.Width(90));
        axisYEnabled = GUILayout.Toggle(axisYEnabled, axisYEnabled ? "Y" : "Y (locked)", "Button", GUILayout.Width(90));
        axisZEnabled = GUILayout.Toggle(axisZEnabled, axisZEnabled ? "Z" : "Z (locked)", "Button", GUILayout.Width(90));
        EditorGUILayout.EndHorizontal();

        // Base offset
        using (new EditorGUI.DisabledScope(!axisXEnabled && !axisYEnabled && !axisZEnabled))
        {
            locationOffset = DrawVector3WithSliders("Location Transform", locationOffset, -100f, 100f,
                !axisXEnabled, !axisYEnabled, !axisZEnabled);
        }

        // Seed + clamps
        locationSeed = EditorGUILayout.LongField(new GUIContent("Random location seed"), locationSeed);
        GUILayout.Label("Location clamping (per-axis)");
        EditorGUI.indentLevel++;
        locationClampMin.x = DrawFloatWithSlider("Min X", locationClampMin.x, -100f, 100f);
        locationClampMax.x = DrawFloatWithSlider("Max X", Mathf.Max(locationClampMax.x, locationClampMin.x), -100f, 100f);
        locationClampMin.y = DrawFloatWithSlider("Min Y", locationClampMin.y, -100f, 100f);
        locationClampMax.y = DrawFloatWithSlider("Max Y", Mathf.Max(locationClampMax.y, locationClampMin.y), -100f, 100f);
        locationClampMin.z = DrawFloatWithSlider("Min Z", locationClampMin.z, -100f, 100f);
        locationClampMax.z = DrawFloatWithSlider("Max Z", Mathf.Max(locationClampMax.z, locationClampMin.z), -100f, 100f);
        EditorGUI.indentLevel--;
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Randomise Seed", GUILayout.Width(130))) locationSeed = UnityEngine.Random.Range(1, int.MaxValue);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6);

        // Parenting
        GUILayout.Label("Parenting", SectionHeaderStyle());
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

        EditorGUILayout.Space(6);

        // Combine / Move
        GUILayout.Label("Combine / Move", SectionHeaderStyle());
        combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one", "Static content only"), combineIntoOne);
        using (new EditorGUI.DisabledScope(!combineIntoOne))
        {
            EditorGUI.indentLevel++;
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
            if (combineIntoOne)
            {
                EditorGUILayout.HelpBox(
                    "Combining bakes all spawned instances into ONE mesh/renderer. Parent choices affect placement. " +
                    "Pivot determines the combined origin. Per-object behaviours (scripts/colliders/triggers) are lost.",
                    MessageType.Warning);
            }
            EditorGUI.indentLevel--;
        }

        moveToMode = (MoveToMode)EditorGUILayout.EnumPopup(new GUIContent("Move all objects to"), moveToMode);
        using (new EditorGUI.DisabledScope(moveToMode != MoveToMode.WorldCoordinates))
        {
            moveWorldCoordinate = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), moveWorldCoordinate);
        }

        GUILayout.Label("Rebuild Instanced Collision", SectionHeaderStyle());
        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Rebuild instanced collision"), rebuildInstancedCollision);
    }

    // ------------------------------------------------------
    // Preview Area
    // ------------------------------------------------------
    private void DrawPreviewArea(float leftWidth)
    {
        float viewerHeight = Mathf.Max(position.height * 0.62f, 480f);
        var rect = GUILayoutUtility.GetRect(leftWidth - 24, viewerHeight);
        DrawPreview(rect);

        EditorGUILayout.Space(4);

        // Background buttons
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Viewer Background", GUILayout.Width(130));
        DrawBgButton("Current Skybox", PreviewBg.CurrentSkybox);
        DrawBgButton("Basic Scene", PreviewBg.BasicScene);
        DrawBgButton("Viewport", PreviewBg.Viewport);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Controls", "LMB: Orbit (Y inverted)   Shift+LMB: Pan   Wheel: Zoom", EditorStyles.miniLabel);

        // Re-center view
        if (GUILayout.Button("Re-center View", GUILayout.Height(22)))
        {
            previewUserAdjusted = false;
            previewPivotOffset = Vector3.zero;
            previewYaw = -30f;
            previewPitch = 15f;
            previewDistance = 2.2f;
            Repaint();
        }

        EditorGUILayout.Space(6);

        // Save path + buttons
        EditorGUILayout.BeginHorizontal();
        savePath = EditorGUILayout.TextField("Save Path", savePath);
        if (GUILayout.Button("Select…", GUILayout.Width(90)))
        {
            var suggested = Path.GetFileName(savePath);
            var path = EditorUtility.SaveFilePanelInProject("Save Prefab As",
                string.IsNullOrEmpty(suggested) ? "CombinedPlaceholder" : suggested,
                "prefab", "Choose save path");
            if (!string.IsNullOrEmpty(path)) savePath = path;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(8);

        // Load + Save
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Load Asset From File", GUILayout.Height(30)))
            LoadAssetFromFile();

        var canPreviewSave = CanSaveFromPreview(out string previewSaveHint);
        using (new EditorGUI.DisabledScope(!canPreviewSave || string.IsNullOrEmpty(savePath)))
        {
            if (GUILayout.Button(new GUIContent("Save From Preview As…", previewSaveHint), GUILayout.Height(30)))
                SaveFromPreview();
        }
        EditorGUILayout.EndHorizontal();

        if (!canPreviewSave)
        {
            int c = CountPlaceholders(prefix);
            if (c == 0)
                EditorGUILayout.HelpBox("Nothing to save, search for objects via a prefix to enable saving.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Multiple placeholders detected. Enable “Combine objects into one” to save them as a single asset.", MessageType.Info);
        }

        EditorGUILayout.Space(8);

        // Execute
        bool canSwitch = prefix != null && prefix.Length >= 3 && targetPrefab != null && IsPrefabAsset(targetPrefab);
        using (new EditorGUI.DisabledScope(!canSwitch))
        {
            if (GUILayout.Button("Switch Placeholders", GUILayout.Height(38)))
                RunReplace();
        }
    }

    private void DrawBgButton(string label, PreviewBg mode)
    {
        bool on = previewBackground == mode;
        if (GUILayout.Toggle(on, label, "Button", GUILayout.Width(140)))
        {
            if (previewBackground != mode)
            {
                previewBackground = mode;
                ApplyPreviewBackground();
            }
        }
    }

    private void ApplyPreviewBackground()
    {
        if (previewUtil == null) return;
        var cam = previewUtil.camera;
        var sky = cam.GetComponent<Skybox>();

        switch (previewBackground)
        {
            case PreviewBg.CurrentSkybox:
                cam.clearFlags = CameraClearFlags.Skybox;
                if (sky) { sky.enabled = true; sky.material = RenderSettings.skybox; }
                break;
            case PreviewBg.BasicScene:
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = new Color(0.58f, 0.63f, 0.70f, 1f); // flat “scene-ish”
                if (sky) sky.enabled = false;
                break;
            case PreviewBg.Viewport:
                var sv = SceneView.lastActiveSceneView;
                if (sv != null && sv.camera != null)
                {
                    cam.clearFlags = sv.camera.clearFlags;
                    if (sky) sky.enabled = (sv.camera.clearFlags == CameraClearFlags.Skybox);
                    if (sky && sky.enabled) sky.material = RenderSettings.skybox;
                    if (sv.camera.clearFlags == CameraClearFlags.Color)
                        cam.backgroundColor = sv.camera.backgroundColor;
                }
                else
                {
                    cam.clearFlags = CameraClearFlags.Color;
                    cam.backgroundColor = new Color(0.3f, 0.3f, 0.3f, 1f);
                    if (sky) sky.enabled = false;
                }
                break;
        }
    }

    private void RebuildPrefabMeshCache()
    {
        prefabMeshes.Clear();
        if (!targetPrefab) return;

        var mfs = targetPrefab.GetComponentsInChildren<MeshFilter>(true);
        foreach (var mf in mfs)
        {
            if (!mf.sharedMesh) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            var mats = (mr && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) ? mr.sharedMaterials : null;

            var local = Matrix4x4.TRS(mf.transform.localPosition, mf.transform.localRotation, mf.transform.localScale);
            Transform t = mf.transform.parent;
            while (t && t != targetPrefab.transform)
            {
                local = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * local;
                t = t.parent;
            }

            prefabMeshes.Add(new PrefabDraw
            {
                mesh = mf.sharedMesh,
                mats = mats,
                localMatrix = local,
                localBounds = mf.sharedMesh.bounds
            });
        }

        if (prefabMeshes.Count == 0)
        {
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (cube)
            {
                prefabMeshes.Add(new PrefabDraw
                {
                    mesh = cube,
                    mats = null,
                    localMatrix = Matrix4x4.identity,
                    localBounds = cube.bounds
                });
            }
        }
    }

    private void DrawPreview(Rect rect)
    {
        if (previewUtil == null) return;

        HandleDragAndDrop(rect);

        bool ready = (prefix != null && prefix.Length >= 3 && targetPrefab != null);
        if (!ready)
        {
            EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(0.15f, 0.15f, 0.15f) : Color.gray);
            GUI.Label(rect, "Enter a prefix (≥ 3 chars) and choose a Desired Asset (Prefab)\n—or drag a prefab here from the Project—to view preview.", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        var candidates = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
            .Select(t => t.gameObject)
            .Take(1000)
            .ToList();

        if (candidates.Count == 0)
        {
            EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(0.15f, 0.15f, 0.15f) : Color.gray);
            GUI.Label(rect, $"No GameObjects found starting with '{prefix}'.", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        // Compute world-space bounds with transforms + location offset
        var previewPivot = GetPreviewPivot(candidates) + previewPivotOffset;

        if (!previewUserAdjusted)
        {
            var boundsWS = new Bounds(candidates[0].transform.position, Vector3.zero);
            foreach (var go in candidates)
            {
                if (!go) continue;

                var rotObj = GetPreviewObjectRotation(go.transform);
                var sclObj = GetPreviewObjectScale(go.transform);
                var pos = GetPreviewObjectPosition(go.transform, rotObj); // includes location offset

                foreach (var pd in prefabMeshes)
                {
                    if (!pd.mesh) continue;
                    var world = Matrix4x4.TRS(pos, rotObj, sclObj) * pd.localMatrix;
                    boundsWS.Encapsulate(TransformBounds(pd.localBounds, world));
                }
            }

            var halfFovRad = previewUtil.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
            var radius = Mathf.Max(boundsWS.extents.x, boundsWS.extents.y, boundsWS.extents.z);
            previewDistance = Mathf.Clamp(radius / Mathf.Tan(halfFovRad) + radius * 0.30f, 0.5f, 3000f);
            if (pivotMode == PivotMode.BoundsCenter) previewPivot = boundsWS.center + previewPivotOffset;
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

            foreach (var go in candidates)
            {
                if (!go) continue;
                var rotObj = GetPreviewObjectRotation(go.transform);
                var sclObj = GetPreviewObjectScale(go.transform);
                var posObj = GetPreviewObjectPosition(go.transform, rotObj);

                foreach (var pd in prefabMeshes)
                {
                    if (!pd.mesh) continue;
                    var mats = (pd.mats != null && pd.mats.Length > 0) ? pd.mats : new[] { fallbackMat };
                    var world = Matrix4x4.TRS(posObj, rotObj, sclObj) * pd.localMatrix;
                    for (int si = 0; si < Mathf.Min(pd.mesh.subMeshCount, mats.Length); si++)
                        previewUtil.DrawMesh(pd.mesh, world, mats[si] ? mats[si] : fallbackMat, si);
                }
            }

            cam.Render();
            var tex = previewUtil.EndPreview();
            GUI.DrawTexture(rect, tex, UnityEngine.ScaleMode.StretchToFill, false);
        }

        // Orbit / Zoom / Pan (Y inverted, pan = Shift+LMB)
        if (rect.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                if (Event.current.button == 0 && !Event.current.shift)
                {
                    previewUserAdjusted = true;
                    previewYaw += Event.current.delta.x * 0.5f;
                    previewPitch = Mathf.Clamp(previewPitch + Event.current.delta.y * 0.5f, -80, 80); // inverted
                    Repaint();
                }
                else if (Event.current.button == 0 && Event.current.shift)
                {
                    previewUserAdjusted = true;
                    float panScale = previewDistance * 0.0025f;
                    var right = Quaternion.Euler(0, previewYaw, 0) * Vector3.right;
                    var up = Vector3.up;
                    previewPivotOffset += (-right * Event.current.delta.x + up * Event.current.delta.y) * panScale;
                    Repaint();
                }
            }
            if (Event.current.type == EventType.ScrollWheel)
            {
                previewUserAdjusted = true;
                previewDistance = Mathf.Clamp(previewDistance * (1f + Event.current.delta.y * 0.04f), 0.3f, 3000f);
                Repaint();
            }
        }
    }

    // Preview helpers
    private Quaternion GetPreviewObjectRotation(Transform t)
    {
        switch (rotationMode)
        {
            case RotationMode.PlaceholderRotation:
                return t.rotation * Quaternion.Euler(rotationEuler);
            case RotationMode.NewRotation:
                return Quaternion.Euler(rotationEuler);
            case RotationMode.SeedValueOnY:
            {
                int h = StableHash(t);
                var rng = new System.Random(SeedToInt(rotationSeed, h, 73856093));
                float y = (float)(rng.NextDouble() * 360.0);
                return Quaternion.Euler(rotationEuler.x, y + rotationEuler.y, rotationEuler.z);
            }
            default: return t.rotation;
        }
    }

    private Vector3 GetPreviewObjectScale(Transform t)
    {
        switch (scaleMode)
        {
            case ScaleMode.PlaceholderScale:
                return Vector3.Scale(t.localScale, SafeVector3(scaleXYZ, 0.0001f));
            case ScaleMode.NewScale:
                return SafeVector3(scaleXYZ, 0.0001f);
            case ScaleMode.SeedValue:
            {
                int h = StableHash(t);
                var rng = new System.Random(SeedToInt(scaleSeed, h, 19349663));
                float f = Mathf.Lerp(scaleRandomMin, scaleRandomMax, (float)rng.NextDouble());
                return new Vector3(f, f, f) + scaleXYZ;
            }
            default: return t.localScale;
        }
    }

    private Vector3 GetPreviewObjectPosition(Transform t, Quaternion finalRot)
    {
        // Base: world position of placeholder
        var worldPos = t.position;

        // Seeded per-axis offset
        Vector3 seeded = Vector3.zero;
        int h = StableHash(t);
        var rng = new System.Random(SeedToInt(locationSeed, h, 83492791));
        if (axisXEnabled) seeded.x = Mathf.Lerp(locationClampMin.x, locationClampMax.x, (float)rng.NextDouble());
        if (axisYEnabled) seeded.y = Mathf.Lerp(locationClampMin.y, locationClampMax.y, (float)rng.NextDouble());
        if (axisZEnabled) seeded.z = Mathf.Lerp(locationClampMin.z, locationClampMax.z, (float)rng.NextDouble());

        Vector3 totalLocal = new Vector3(
            axisXEnabled ? locationOffset.x + seeded.x : 0f,
            axisYEnabled ? locationOffset.y + seeded.y : 0f,
            axisZEnabled ? locationOffset.z + seeded.z : 0f);

        if (locationMode == LocationOffsetMode.ObjectOrigin)
        {
            // along the object’s (final) orientation
            var worldDelta = finalRot * totalLocal;
            return worldPos + worldDelta;
        }
        else
        {
            // along world axes
            return worldPos + totalLocal;
        }
    }

    private void HandleDragAndDrop(Rect rect)
    {
        var evt = Event.current;
        if (!rect.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated)
        {
            if (DragAndDrop.objectReferences != null && DragAndDrop.objectReferences.Length > 0)
            {
                var anyGo = DragAndDrop.objectReferences[0] as GameObject;
                if (anyGo != null && IsPrefabAsset(anyGo))
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    evt.Use();
                }
            }
        }
        else if (evt.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            foreach (var obj in DragAndDrop.objectReferences)
            {
                var go = obj as GameObject;
                if (go != null && IsPrefabAsset(go))
                {
                    targetPrefab = go;
                    RebuildPrefabMeshCache();
                    GUI.changed = true;
                    Repaint();
                    break;
                }
            }
            evt.Use();
        }
    }

    // ------------------------------------------------------
    // Actions
    // ------------------------------------------------------
    private bool CanSaveFromPreview(out string hint)
    {
        hint = "";
        int count = CountPlaceholders(prefix);
        if (count == 0) { hint = "Nothing to save. Enter a prefix first."; return false; }
        if (count == 1) return true;
        if (combineIntoOne) return true;
        hint = "Multiple placeholders found. Enable “Combine objects into one”.";
        return false;
    }

    private int CountPlaceholders(string pfx)
    {
        if (string.IsNullOrEmpty(pfx) || pfx.Length < 3) return 0;
        return Resources.FindObjectsOfTypeAll<Transform>()
            .Count(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(pfx));
    }

    private void SaveFromPreview()
    {
        var candidates = Resources.FindObjectsOfTypeAll<Transform>()
            .Select(t => t ? t.gameObject : null)
            .Where(go => go != null && go.scene.IsValid() && go.name.StartsWith(prefix))
            .OrderBy(go => go.name)
            .ToList();

        if (candidates.Count == 0)
        {
            EditorUtility.DisplayDialog("Nothing to save", "No GameObjects matched the current prefix.", "OK");
            return;
        }
        if (string.IsNullOrEmpty(savePath))
        {
            EditorUtility.DisplayDialog("Save path missing", "Choose a prefab save path.", "OK");
            return;
        }
        if (candidates.Count > 1 && !combineIntoOne)
        {
            EditorUtility.DisplayDialog("Combine required", "Enable “Combine objects into one” to save multiple placeholders into a single asset.", "OK");
            return;
        }

        var tempRoot = new GameObject("~PreviewTemp");
        try
        {
            var scene = candidates[0].scene;
            SceneManager.MoveGameObjectToScene(tempRoot, scene);

            var temps = new List<GameObject>(candidates.Count);
            foreach (var src in candidates)
            {
                var inst = PrefabUtility.InstantiatePrefab(targetPrefab, src.scene) as GameObject;
                if (!inst) continue;

                // parent temp root
                inst.transform.SetParent(tempRoot.transform, false);

                // Rotation
                var finalRot = ComputeFinalRotation(src.transform, rotationMode, rotationEuler, rotationSeed);

                // Scale
                var finalScale = ComputeFinalScale(src.transform, scaleMode, scaleXYZ, scaleSeed, scaleRandomMin, scaleRandomMax);

                // Position (includes Location Offset)
                var finalPos = ComputeFinalWorldPosition(src.transform, finalRot);

                inst.transform.position = finalPos;
                inst.transform.rotation = finalRot;
                inst.transform.localScale = finalScale;

                temps.Add(inst);
            }

            GameObject toSave;
            if (candidates.Count == 1 && !combineIntoOne)
                toSave = temps[0];
            else
                toSave = CombineInstances(temps, pivotMode, explicitParent, GetGroupParentForScene(temps[0].scene),
                    string.IsNullOrEmpty(forcedName) ? "Combined Object" : forcedName);

            if (toSave == null)
            {
                EditorUtility.DisplayDialog("Save failed", "Could not build a savable object.", "OK");
                return;
            }

            if (rebuildInstancedCollision) TryRebuildInstancedCollision(toSave);

            var saved = PrefabUtility.SaveAsPrefabAsset(toSave, savePath);
            if (saved != null) Debug.Log($"Saved prefab: {savePath}"); else Debug.LogError("Failed to save prefab.");

            if (toSave != temps[0]) Undo.DestroyObjectImmediate(toSave);
        }
        finally
        {
            Undo.DestroyObjectImmediate(tempRoot);
        }
    }

    private void LoadAssetFromFile()
    {
        string path = EditorUtility.OpenFilePanel("Load 3D Asset", "", "fbx,obj,prefab,gltf,glb");
        if (string.IsNullOrEmpty(path)) return;

        string importDir = "Assets/ImportedByPlaceholderSwitcher";
        if (!AssetDatabase.IsValidFolder(importDir))
            AssetDatabase.CreateFolder("Assets", "ImportedByPlaceholderSwitcher");

        string fileName = Path.GetFileName(path);
        string destPath = Path.Combine(importDir, fileName).Replace("\\", "/");

        FileUtil.ReplaceFile(path, destPath);
        AssetDatabase.ImportAsset(destPath);

        var go = AssetDatabase.LoadAssetAtPath<GameObject>(destPath);
        if (go == null)
        {
            EditorUtility.DisplayDialog("Import failed", $"Could not load '{fileName}' as a GameObject.\nIf this is glTF, ensure a glTF importer package is installed.", "OK");
            return;
        }

        targetPrefab = go;
        RebuildPrefabMeshCache();
        Repaint();
    }

    // ------------------------------------------------------
    // Core replacement
    // ------------------------------------------------------
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

                var inst = ReplaceOne(
                    src, targetPrefab,
                    originalNameSource,
                    forcedName, useIncrementalNaming,
                    rotationMode, rotationEuler, rotationSeed,
                    scaleMode, scaleXYZ, scaleSeed, scaleRandomMin, scaleRandomMax,
                    locationMode, axisXEnabled, axisYEnabled, axisZEnabled, locationOffset, locationSeed, locationClampMin, locationClampMax,
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
            finalRoot = CombineInstances(spawned, pivotMode, explicitParent, GetGroupParentForScene(spawned[0].scene),
                string.IsNullOrEmpty(forcedName) ? null : forcedName);
            foreach (var go in spawned) if (go != null) Undo.DestroyObjectImmediate(go);
        }

        // Move all objects to …
        if (moveToMode != MoveToMode.None)
        {
            Vector3? target = ComputeMoveTarget(spawned, moveToMode);
            if (target.HasValue)
            {
                if (finalRoot != null) finalRoot.transform.position = target.Value;
                else if (spawned.Count > 0)
                {
                    var center = GetWorldCenter(spawned);
                    var delta = target.Value - center;
                    foreach (var go in spawned) if (go != null) go.transform.position += delta;
                }
            }
        }

        if (rebuildInstancedCollision)
        {
            if (finalRoot != null) TryRebuildInstancedCollision(finalRoot);
            else foreach (var go in spawned) if (go != null) TryRebuildInstancedCollision(go);
        }

        EditorUtility.DisplayDialog("Done", $"Replaced {candidates.Count} placeholder(s)." + (combineIntoOne ? " Combined into one." : ""), "Nice");
    }

    private Vector3? ComputeMoveTarget(List<GameObject> spawned, MoveToMode mode)
    {
        if (spawned == null || spawned.Count == 0) return null;
        switch (mode)
        {
            case MoveToMode.FirstObject:   return spawned[0].transform.position;
            case MoveToMode.BoundsCenter:
                var b = new Bounds(spawned[0].transform.position, Vector3.zero);
                foreach (var go in spawned) { var r = go.GetComponent<Renderer>(); if (r) b.Encapsulate(r.bounds); else b.Encapsulate(go.transform.position); }
                return b.center;
            case MoveToMode.WorldOrigin:   return Vector3.zero;
            case MoveToMode.WorldCoordinates: return moveWorldCoordinate;
            case MoveToMode.SelectedObject: return Selection.activeTransform ? Selection.activeTransform.position : (Vector3?)null;
            case MoveToMode.Parent:
                if (explicitParent) return explicitParent.position;
                var gp = GetGroupParentForScene(spawned[0].scene);
                return gp ? gp.position : (Vector3?)null;
            default: return null;
        }
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
        GameObject src, GameObject prefab,
        OriginalNameSource nameSource,
        string forced, bool incremental,
        RotationMode rotMode, Vector3 rotEuler, long rotSeed,
        ScaleMode scMode, Vector3 scXYZ, long scSeed, float scMin, float scMax,
        LocationOffsetMode locMode, bool axX, bool axY, bool axZ, Vector3 locBase, long locSeed, Vector3 locMin, Vector3 locMax,
        Transform groupingParent, Dictionary<string,int> counters)
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

        var inst = PrefabUtility.InstantiatePrefab(prefab, src.scene) as GameObject;
        if (inst == null) return null;
        Undo.RegisterCreatedObjectUndo(inst, "Create replacement");

        var newParent = groupingParent != null ? groupingParent : parent;
        inst.transform.SetParent(newParent, false);

        // Rotation
        var finalRot = ComputeFinalRotation(src.transform, rotMode, rotEuler, rotSeed);

        // Scale
        var finalScale = ComputeFinalScale(src.transform, scMode, scXYZ, scSeed, scMin, scMax);

        // Position (includes Location Offset)
        var finalWorldPos = ComputeFinalWorldPosition(src.transform, finalRot,
            locMode, axX, axY, axZ, locBase, locSeed, locMin, locMax);

        // Apply transform & metadata
        inst.transform.position = finalWorldPos;
        inst.transform.rotation = finalRot;
        inst.transform.localScale = finalScale;

        inst.layer = layer;
        try { inst.tag = tag; } catch { }
        GameObjectUtility.SetStaticEditorFlags(inst, staticFlags);
        inst.SetActive(active);

        // Naming
        string baseName = string.IsNullOrEmpty(forced)
            ? (nameSource == OriginalNameSource.Placeholder ? src.name : prefab.name)
            : forced;
        inst.name = ApplyIncremental(baseName, incremental, counters);

        Undo.DestroyObjectImmediate(src);
        return inst;
    }

    // --- Shared compute used by preview/save/replace ---
    private static Quaternion ComputeFinalRotation(Transform src, RotationMode mode, Vector3 rotEuler, long rotSeed)
    {
        switch (mode)
        {
            default:
            case RotationMode.PlaceholderRotation:
                return src.rotation * Quaternion.Euler(rotEuler);
            case RotationMode.NewRotation:
                return Quaternion.Euler(rotEuler);
            case RotationMode.SeedValueOnY:
            {
                int h = StableHash(src);
                var rng = new System.Random(SeedToInt(rotSeed, h, 73856093));
                float y = (float)(rng.NextDouble() * 360.0);
                return Quaternion.Euler(rotEuler.x, y + rotEuler.y, rotEuler.z);
            }
        }
    }

    private static Vector3 ComputeFinalScale(Transform src, ScaleMode mode, Vector3 scXYZ, long scSeed, float scMin, float scMax)
    {
        switch (mode)
        {
            default:
            case ScaleMode.PlaceholderScale:
                return Vector3.Scale(src.localScale, SafeVector3(scXYZ, 0.0001f));
            case ScaleMode.NewScale:
                return SafeVector3(scXYZ, 0.0001f);
            case ScaleMode.SeedValue:
            {
                int h = StableHash(src);
                var rng = new System.Random(SeedToInt(scSeed, h, 19349663));
                float f = Mathf.Lerp(scMin, scMax, (float)rng.NextDouble());
                return new Vector3(f, f, f) + scXYZ;
            }
        }
    }

    private Vector3 ComputeFinalWorldPosition(Transform src, Quaternion finalRot)
    {
        return ComputeFinalWorldPosition(
            src, finalRot, locationMode, axisXEnabled, axisYEnabled, axisZEnabled,
            locationOffset, locationSeed, locationClampMin, locationClampMax);
    }

    private static Vector3 ComputeFinalWorldPosition(
        Transform src, Quaternion finalRot,
        LocationOffsetMode locMode, bool axX, bool axY, bool axZ,
        Vector3 locBase, long locSeed, Vector3 locMin, Vector3 locMax)
    {
        var baseWorld = src.position;

        // Seeded delta
        int h = StableHash(src);
        var rng = new System.Random(SeedToInt(locSeed, h, 83492791));
        Vector3 seeded = Vector3.zero;
        if (axX) seeded.x = Mathf.Lerp(locMin.x, locMax.x, (float)rng.NextDouble());
        if (axY) seeded.y = Mathf.Lerp(locMin.y, locMax.y, (float)rng.NextDouble());
        if (axZ) seeded.z = Mathf.Lerp(locMin.z, locMax.z, (float)rng.NextDouble());

        Vector3 localDelta = new Vector3(
            axX ? locBase.x + seeded.x : 0f,
            axY ? locBase.y + seeded.y : 0f,
            axZ ? locBase.z + seeded.z : 0f);

        if (locMode == LocationOffsetMode.ObjectOrigin)
        {
            // move along object axes (final rotation)
            return baseWorld + (finalRot * localDelta);
        }
        else
        {
            // move along world axes
            return baseWorld + localDelta;
        }
    }

    private static string ApplyIncremental(string baseName, bool incremental, Dictionary<string,int> counters)
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

    // -------- UI helpers: sliders next to fields --------
    private static float DrawFloatWithSlider(string label, float value, float min, float max, float fieldWidth = 0f)
    {
        EditorGUILayout.BeginHorizontal();
        if (!string.IsNullOrEmpty(label)) GUILayout.Label(label, GUILayout.Width(180));
        if (fieldWidth > 0f) value = EditorGUILayout.FloatField(value, GUILayout.Width(fieldWidth));
        else value = EditorGUILayout.FloatField(value);
        value = GUILayout.HorizontalSlider(value, min, max, GUILayout.MinWidth(80));
        EditorGUILayout.EndHorizontal();
        return value;
    }

    private static Vector3 DrawVector3WithSliders(string label, Vector3 v, float min, float max,
        bool disableX = false, bool disableY = false, bool disableZ = false)
    {
        GUILayout.Label(label);
        EditorGUI.indentLevel++;
        using (new EditorGUI.DisabledScope(disableX)) v.x = DrawFloatWithSlider("X", v.x, min, max);
        using (new EditorGUI.DisabledScope(disableY)) v.y = DrawFloatWithSlider("Y", v.y, min, max);
        using (new EditorGUI.DisabledScope(disableZ)) v.z = DrawFloatWithSlider("Z", v.z, min, max);
        EditorGUI.indentLevel--;
        return v;
    }

    // -------- Misc helpers --------
    private static int StableHash(Transform t) => t ? (t.GetInstanceID() ^ (t.name.GetHashCode() << 1)) : 0;
    private static int SeedToInt(long seed, int objHash, int salt)
    {
        // Mix 64-bit seed with object hash and a salt to get a 32-bit deterministic seed for System.Random
        unchecked
        {
            int s = (int)(seed ^ (seed >> 32));
            int h = objHash * 16777619 ^ salt;
            return s ^ h;
        }
    }

    private static float SafePositive(float v, float min) => (!float.IsFinite(v) || v < min) ? min : v;
    private static Vector3 SafeVector3(Vector3 v, float componentMin)
    {
        v.x = SafePositive(v.x, componentMin);
        v.y = SafePositive(v.y, componentMin);
        v.z = SafePositive(v.z, componentMin);
        return v;
    }

    private static Bounds TransformBounds(Bounds local, Matrix4x4 m)
    {
        var ext = local.extents; var c = local.center;
        var corners = new Vector3[8]; int i = 0;
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
            corners[i++] = new Vector3(c.x + ext.x * x, c.y + ext.y * y, c.z + ext.z * z);
        var w0 = m.MultiplyPoint3x4(corners[0]);
        var bb = new Bounds(w0, Vector3.zero);
        for (int k = 1; k < 8; k++) bb.Encapsulate(m.MultiplyPoint3x4(corners[k]));
        return bb;
    }
}
#endif
