#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// ===============================================
// PlaceholderTool.cs
// Menu: Tools > Placeholder Tools > Placeholder Switcher
// ===============================================
public class PlaceholderSwitcher : EditorWindow
{
    // ---------- Inputs ----------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject targetPrefab = null;
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false; // _001, _002 per base name

    // ---------- Rotation ----------
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero; // for PlaceholderRotation or NewRotation
    [SerializeField] private long rotationSeed = 1234;             // up to 10 digits

    // ---------- Scale ----------
    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private Vector3 scaleXYZ = Vector3.one;       // per-axis UI value (also additive to seed)
    [SerializeField] private long scaleSeed = 321;                 // up to 10 digits
    [SerializeField] private float scaleRandomMin = 0.8f;          // min uniform factor
    [SerializeField] private float scaleRandomMax = 1.2f;          // max uniform factor

    // ---------- Location Offset ----------
    private enum LocationSpace { Local, World }
    [SerializeField] private LocationSpace locationSpace = LocationSpace.Local;
    [SerializeField] private bool lockX = false, lockY = false, lockZ = false;
    [SerializeField] private Vector3 locationOffset = Vector3.zero; // base offset applied to every object
    [SerializeField] private bool useLocationSeed = false;
    [SerializeField] private long locationSeed = 987654321;         // up to 10 digits
    [SerializeField] private Vector2 clampX = new Vector2(-1f, 1f);
    [SerializeField] private Vector2 clampY = new Vector2( 0f, 0f);
    [SerializeField] private Vector2 clampZ = new Vector2(-1f, 1f);

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

    // ---------- Move To ----------
    private enum MoveTarget { None, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinates, SelectedObject, Parent }
    [SerializeField] private MoveTarget moveTarget = MoveTarget.None;
    [SerializeField] private Vector3 worldCoordinate = Vector3.zero;

    // ---------- Collision / Save ----------
    [SerializeField] private bool rebuildInstancedCollision = false;
    [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

    // ---------- Preview ----------
    private enum PreviewBg { CurrentSkybox, BasicScene }
    [SerializeField] private PreviewBg previewBackground = PreviewBg.CurrentSkybox;

    private PreviewRenderUtility previewUtil;
    private float previewYaw = -30f;
    private float previewPitch = 15f;
    private float previewDistance = 1.6f;
    private bool previewUserAdjusted = false;
    private Mesh previewMesh;
    private Material[] previewMats;
    private Material fallbackMat;
    private Vector3 previewPivotOffset = Vector3.zero; // for panning

    // ---------- SceneView mirror ----------
    private SceneView _viewportWindow;
    [SerializeField] private bool autoSyncViewport = true;  // larger checkbox in UI
    private bool _updateHooked = false;

    // ---------- State ----------
    private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();
    private readonly Dictionary<string, int> _nameCounters = new Dictionary<string, int>();

    // ---------- Local "parameter undo" stack ----------
    private struct ParamSnapshot
    {
        public string prefix;
        public GameObject targetPrefab;
        public string forcedName;
        public bool useIncrementalNaming;

        public RotationMode rotationMode;
        public Vector3 rotationEuler;
        public long rotationSeed;

        public ScaleMode scaleMode;
        public Vector3 scaleXYZ;
        public long scaleSeed;
        public float scaleRandomMin;
        public float scaleRandomMax;

        public LocationSpace locationSpace;
        public bool lockX, lockY, lockZ;
        public Vector3 locationOffset;
        public bool useLocationSeed;
        public long locationSeed;
        public Vector2 clampX, clampY, clampZ;

        public Transform explicitParent;
        public bool groupWithEmptyParent;
        public string groupParentName;
        public EmptyParentLocation emptyParentLocation;
        public Vector3 manualEmptyParentPosition;

        public bool combineIntoOne;
        public PivotMode pivotMode;

        public MoveTarget moveTarget;
        public Vector3 worldCoordinate;

        public bool rebuildInstancedCollision;
        public string savePath;

        public PreviewBg previewBackground;
        public float previewYaw, previewPitch, previewDistance;
        public Vector3 previewPivotOffset;

        public bool autoSyncViewport;
    }
    private readonly Stack<ParamSnapshot> _undo = new Stack<ParamSnapshot>(64);
    private const int UndoCap = 64;

    [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(1100, 740);
        w.Show();
    }

    private void OnEnable()
    {
        InitPreview();
        HookEditorUpdate(true);
    }

    private void OnDisable()
    {
        HookEditorUpdate(false);
        CleanupPreview();
        CloseViewportWindow();
    }

    private void HookEditorUpdate(bool on)
    {
        if (on && !_updateHooked)
        {
            EditorApplication.update += EditorUpdate;
            _updateHooked = true;
        }
        else if (!on && _updateHooked)
        {
            EditorApplication.update -= EditorUpdate;
            _updateHooked = false;
        }
    }

    private void EditorUpdate()
    {
        // Keep our external SceneView synced if requested
        if (_viewportWindow != null && autoSyncViewport)
        {
            SyncViewportWindow();
        }
    }

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
        previewUtil?.Cleanup();
        if (fallbackMat != null) DestroyImmediate(fallbackMat);
        previewUtil = null;
        fallbackMat = null;
    }

    // ------------------------------------------------------
    // UI
    // ------------------------------------------------------
    private void OnGUI()
    {
        // Header row: big title + 3 buttons equal width
        GUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        var title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, alignment = TextAnchor.MiddleLeft };
        GUILayout.Label("Placeholder Switcher", title, GUILayout.Height(24));
        GUILayout.FlexibleSpace();

        float bW = 190f;
        if (GUILayout.Button("Open GameObject Library", GUILayout.Width(bW), GUILayout.Height(22)))
        {
            EditorGUIUtility.ShowObjectPicker<GameObject>(targetPrefab, false, "", 9991);
        }
        if (GUILayout.Button("Randomise All Parameters", GUILayout.Width(bW), GUILayout.Height(22)))
        {
            PushSnapshot();
            RandomiseAllParameters(); // excludes parenting & pivot
        }
        using (new EditorGUI.DisabledScope(_undo.Count == 0))
        {
            if (GUILayout.Button("Undo", GUILayout.Width(bW), GUILayout.Height(22)))
            {
                PopSnapshot();
                Repaint();
            }
        }
        EditorGUILayout.EndHorizontal();

        // Handle ObjectPicker result
        if (Event.current.commandName == "ObjectSelectorUpdated" && EditorGUIUtility.GetObjectPickerControlID() == 9991)
        {
            var picked = EditorGUIUtility.GetObjectPickerObject() as GameObject;
            if (picked != null) { targetPrefab = picked; Repaint(); }
        }

        // Split: Preview (left) | Controls (right)
        EditorGUILayout.BeginHorizontal();

        // -------- Left: Preview column --------
        EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(position.width * 0.52f, 560f)));
        DrawPreviewArea();
        EditorGUILayout.EndVertical();

        // -------- Right: Controls column --------
        EditorGUILayout.BeginVertical();
        DrawControls();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        // Version tag bottom-left
        var v = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.LowerLeft };
        Rect r = new Rect(6, position.height - 18, 120, 16);
        GUI.Label(r, "v.1.0.0", v);
    }

    private void DrawControls()
    {
        var sec = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        float labelW = 190f;
        var oldLW = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = labelW;

        GUILayout.Label("Replace Object Placeholders", sec);

        // Inputs + live object count
        EditorGUILayout.BeginHorizontal();
        string prev = prefix;
        prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix", "e.g. 'SS_'"), prefix);
        int count = CountPlaceholders(prefix);
        string status;
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3) status = "enter ≥ 3 chars";
        else status = (count > 0 ? $"{count} objects found" : "⚠️ no assets found");
        GUILayout.Label(status, EditorStyles.miniLabel, GUILayout.Width(140));
        EditorGUILayout.EndHorizontal();

        targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), targetPrefab, typeof(GameObject), false);
        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)", "If set, overrides source/prefab name"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming", "Names like Base_001, Base_002…"), useIncrementalNaming);
        if (prev != prefix) Repaint();

        EditorGUILayout.Space(8);

        // ---------- Rotation ----------
        GUILayout.Label("Rotation", sec);
        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);
        if (rotationMode == RotationMode.PlaceholderRotation || rotationMode == RotationMode.NewRotation)
        {
            rotationEuler = EditorGUILayout.Vector3Field(new GUIContent(rotationMode == RotationMode.PlaceholderRotation ?
                "Rotation (adds to placeholder)" : "Rotation (new rotation)"), rotationEuler);
        }
        else
        {
            rotationEuler = EditorGUILayout.Vector3Field(new GUIContent("Rotation offset (added to seeded Y)"), rotationEuler);
            EditorGUILayout.BeginHorizontal();
            rotationSeed = EditorGUILayout.LongField(new GUIContent("Random rotation seed (Y)"), rotationSeed, GUILayout.MaxWidth( labelW + 150f ));
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130))) rotationSeed = NewSeed10Digits();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("Per-object deterministic Y rotation from seed; offset above is added on top.", MessageType.Info);
        }

        EditorGUILayout.Space(8);

        // ---------- Scale ----------
        GUILayout.Label("Scale", sec);
        scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

        if (scaleMode == ScaleMode.SeedValue)
        {
            scaleXYZ = SafeVector3(
                EditorGUILayout.Vector3Field(new GUIContent("Scale (adds to seeded uniform)"), scaleXYZ,
                GUILayout.MaxWidth(labelW + 220f)), 0.0001f);

            EditorGUILayout.BeginHorizontal();
            scaleSeed = EditorGUILayout.LongField(new GUIContent("Random scaling seed"), scaleSeed, GUILayout.MaxWidth(labelW + 150f));
            GUILayout.Space(6);
            GUILayout.Label("Scale clamping", GUILayout.Width(110));
            float minV = scaleRandomMin, maxV = scaleRandomMax;
            EditorGUILayout.MinMaxSlider(ref minV, ref maxV, 0.0001f, 10f, GUILayout.Width(160));
            scaleRandomMin = EditorGUILayout.FloatField(minV, GUILayout.Width(60));
            scaleRandomMax = EditorGUILayout.FloatField(maxV, GUILayout.Width(60));
            GUILayout.Space(6);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130))) scaleSeed = NewSeed10Digits();
            EditorGUILayout.EndHorizontal();

            if (scaleRandomMax < scaleRandomMin) (scaleRandomMin, scaleRandomMax) = (scaleRandomMax, scaleRandomMin);
            EditorGUILayout.HelpBox("Generates a uniform scale factor per object in [Min..Max], then adds the XYZ offset above.", MessageType.Info);
        }
        else
        {
            scaleXYZ = SafeVector3(
                EditorGUILayout.Vector3Field(new GUIContent(scaleMode == ScaleMode.PlaceholderScale ?
                "Scale (multiplies placeholder scale)" : "Scale (new)"), scaleXYZ, GUILayout.MaxWidth(labelW + 220f)), 0.0001f);
        }

        EditorGUILayout.Space(8);

        // ---------- Location Offset ----------
        GUILayout.Label("Location Offset", sec);
        locationSpace = (LocationSpace)EditorGUILayout.EnumPopup(new GUIContent("Location space"), locationSpace);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Influenced Axis", GUILayout.Width(labelW));
        lockX = !GUILayout.Toggle(!lockX, lockX ? "X locked" : "X", EditorStyles.miniButtonLeft, GUILayout.Width(70));
        lockY = !GUILayout.Toggle(!lockY, lockY ? "Y locked" : "Y", EditorStyles.miniButtonMid, GUILayout.Width(70));
        lockZ = !GUILayout.Toggle(!lockZ, lockZ ? "Z locked" : "Z", EditorStyles.miniButtonRight, GUILayout.Width(70));
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        locationOffset = EditorGUILayout.Vector3Field(new GUIContent("Location Transform"), locationOffset, GUILayout.MaxWidth(labelW + 220f));
        GUILayout.Space(6);
        float sx = locationOffset.x, sy = locationOffset.y, sz = locationOffset.z;
        sx = GUILayout.HorizontalSlider(sx, -10f, 10f, GUILayout.Width(90));
        sy = GUILayout.HorizontalSlider(sy, -10f, 10f, GUILayout.Width(90));
        sz = GUILayout.HorizontalSlider(sz, -10f, 10f, GUILayout.Width(90));
        locationOffset = new Vector3(sx, sy, sz);
        EditorGUILayout.EndHorizontal();

        useLocationSeed = EditorGUILayout.Toggle(new GUIContent("Use random location seed"), useLocationSeed);
        using (new EditorGUI.DisabledScope(!useLocationSeed))
        {
            EditorGUILayout.BeginHorizontal();
            locationSeed = EditorGUILayout.LongField(new GUIContent("Random location seed"), locationSeed, GUILayout.MaxWidth(labelW + 150f));
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130))) locationSeed = NewSeed10Digits();
            EditorGUILayout.EndHorizontal();

            ClampRow("Location clamping X", ref clampX, -10f, 10f, labelW);
            ClampRow("Location clamping Y", ref clampY, -10f, 10f, labelW);
            ClampRow("Location clamping Z", ref clampZ, -10f, 10f, labelW);
        }

        EditorGUILayout.Space(8);

        // ---------- Parenting ----------
        GUILayout.Label("Parenting", sec);
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

        EditorGUILayout.Space(8);

        // ---------- Combine / Move ----------
        GUILayout.Label("Combine / Move", sec);
        combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one", "Static content only"), combineIntoOne);
        using (new EditorGUI.DisabledScope(!combineIntoOne))
        {
            EditorGUI.indentLevel++;
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
            if ((pivotMode == PivotMode.SelectedObject) && Selection.activeTransform == null)
                EditorGUILayout.HelpBox("Select a Transform in the hierarchy to use as the pivot.", MessageType.Info);

            if (combineIntoOne)
            {
                EditorGUILayout.HelpBox(
                    "Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, triggers, and events are lost.\n" +
                    "If you still want to move many objects together but keep individual behaviour, parent them under an empty GameObject " +
                    "and use **Static Batching** instead of combining.",
                    MessageType.Warning);
            }
            EditorGUI.indentLevel--;
        }

        moveTarget = (MoveTarget)EditorGUILayout.EnumPopup(new GUIContent("Move all objects to"), moveTarget);
        using (new EditorGUI.DisabledScope(moveTarget != MoveTarget.WorldCoordinates))
        {
            worldCoordinate = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), worldCoordinate);
        }

        EditorGUILayout.Space(6);
        GUILayout.Label("Rebuild instanced collision", sec);
        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Enable"), rebuildInstancedCollision);

        EditorGUIUtility.labelWidth = oldLW;
    }

    private static void ClampRow(string label, ref Vector2 range, float min, float max, float labelW)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(labelW));
        float a = range.x, b = range.y;
        EditorGUILayout.MinMaxSlider(ref a, ref b, min, max, GUILayout.Width(200));
        a = EditorGUILayout.FloatField(a, GUILayout.Width(60));
        b = EditorGUILayout.FloatField(b, GUILayout.Width(60));
        if (b < a) (a, b) = (b, a);
        range = new Vector2(a, b);
        EditorGUILayout.EndHorizontal();
    }

    // ------------------------------------------------------
    // Preview Area (left)
    // ------------------------------------------------------
    private void DrawPreviewArea()
    {
        // Viewport area
        var rect = GUILayoutUtility.GetRect(10, 10, 520, 520);
        DrawPreview(rect);

        // Background options + open/sync/close viewport buttons
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Viewer Background", GUILayout.Width(140));
        if (GUILayout.Toggle(previewBackground == PreviewBg.CurrentSkybox, "Current Skybox", EditorStyles.miniButtonLeft))
        {
            previewBackground = PreviewBg.CurrentSkybox; ApplyPreviewBackground(); Repaint();
        }
        if (GUILayout.Toggle(previewBackground == PreviewBg.BasicScene, "Basic Scene", EditorStyles.miniButtonRight))
        {
            previewBackground = PreviewBg.BasicScene; ApplyPreviewBackground(); Repaint();
        }

        GUILayout.FlexibleSpace();

        if (GUILayout.Button("Open Viewport", GUILayout.Height(20), GUILayout.Width(120)))
            OpenViewportWindow();

        // Big checkbox style for auto-sync
        var bigToggle = new GUIStyle(EditorStyles.toggle) { fontSize = 12 };
        autoSyncViewport = EditorGUILayout.ToggleLeft(new GUIContent("Auto-sync"), autoSyncViewport, bigToggle, GUILayout.Width(100));

        using (new EditorGUI.DisabledScope(_viewportWindow == null))
        {
            if (GUILayout.Button("Close Viewport", GUILayout.Height(20), GUILayout.Width(120)))
                CloseViewportWindow();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Controls", "LMB: Orbit (Y inverted)   Shift+LMB: Pan   Wheel: Zoom", EditorStyles.miniLabel);

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Re-center View", GUILayout.Height(22)))
        {
            previewUserAdjusted = false;
            previewPivotOffset = Vector3.zero;
            Repaint();
        }

        EditorGUILayout.Space(6);

        // Save path + file actions
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

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Load Asset From File", GUILayout.Height(24)))
        {
            var path = EditorUtility.OpenFilePanel("Import Model", "", "fbx,FBX,obj,OBJ,gltf,GLTF,glb,GLB");
            if (!string.IsNullOrEmpty(path))
                Debug.Log("Note: direct importing external model files is not handled by this tool. Drag a prefab from the Project or pick one via the Library.");
        }

        bool single = CountPlaceholders(prefix) == 1 && !string.IsNullOrEmpty(savePath);
        using (new EditorGUI.DisabledScope(!single || targetPrefab == null))
        {
            if (GUILayout.Button("Save From Preview As…", GUILayout.Height(24)))
            {
                var p = EditorUtility.SaveFilePanelInProject("Save Prefab As",
                    System.IO.Path.GetFileNameWithoutExtension(savePath), "prefab",
                    "Choose save path for preview object");
                if (!string.IsNullOrEmpty(p))
                {
                    savePath = p;
                    Debug.Log("Use 'Switch Placeholders' (and Combine if needed) to produce the actual prefab at this path.");
                }
            }
        }
        EditorGUILayout.EndHorizontal();

        // Context warnings
        int c = CountPlaceholders(prefix);
        if (c == 0)
            EditorGUILayout.HelpBox("Nothing to save: enter a prefix (≥ 3 chars) and pick a Desired Asset to enable preview.", MessageType.Info);
        else if (c > 1)
            EditorGUILayout.HelpBox("Multiple placeholders detected. Enable 'Combine objects into one' to save them as a single asset.", MessageType.Warning);

        EditorGUILayout.Space(8);

        // Big main action button lives under the viewer
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(prefix) || prefix.Length < 3 || targetPrefab == null || !IsPrefabAsset(targetPrefab)))
        {
            var big = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 42 };
            if (GUILayout.Button("Switch Placeholders", big))
                RunReplace();
        }
    }

    private void ApplyPreviewBackground()
    {
        if (previewUtil == null) return;
        var cam = previewUtil.camera;
        switch (previewBackground)
        {
            case PreviewBg.CurrentSkybox:
                cam.clearFlags = RenderSettings.skybox ? CameraClearFlags.Skybox : CameraClearFlags.Color;
                cam.backgroundColor = RenderSettings.ambientLight;
                break;
            case PreviewBg.BasicScene:
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = new Color(0.18f, 0.18f, 0.2f, 1f);
                break;
        }
    }

    private void RefreshPreviewMesh()
    {
        previewMesh = null; previewMats = null;
        if (targetPrefab == null) return;

        var mrs = targetPrefab.GetComponentsInChildren<MeshRenderer>(true);
        if (mrs != null && mrs.Length > 0)
        {
            var mf = mrs[0].GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) previewMesh = mf.sharedMesh;
            var matList = new List<Material>();
            foreach (var mr in mrs) if (mr != null) matList.AddRange(mr.sharedMaterials);
            if (matList.Count > 0) previewMats = matList.ToArray();
        }
        else
        {
            var mf = targetPrefab.GetComponentInChildren<MeshFilter>(true);
            if (mf != null && mf.sharedMesh != null) previewMesh = mf.sharedMesh;
        }
    }

    private void DrawPreview(Rect rect)
    {
        if (previewUtil == null)
        {
            EditorGUI.LabelField(rect, "Preview unavailable");
            return;
        }

        if (targetPrefab == null || string.IsNullOrEmpty(prefix) || prefix.Length < 3)
        {
            GUI.Label(rect,
                "Enter a prefix (≥ 3 chars) and choose a Desired Asset (Prefab)\n—or drag a prefab here from the Project—to view preview.",
                new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter });
        }

        RefreshPreviewMesh();

        var candidatesAll = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && !string.IsNullOrEmpty(prefix) && t.gameObject.name.StartsWith(prefix))
            .Select(t => t.gameObject)
            .Take(600)
            .ToList();

        var previewPivot = GetPreviewPivot(candidatesAll) + previewPivotOffset;

        if (!previewUserAdjusted)
        {
            var mesh = previewMesh != null ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (candidatesAll.Count > 0 && mesh != null)
            {
                var boundsWS = new Bounds(candidatesAll[0].transform.position, Vector3.zero);
                foreach (var go in candidatesAll)
                {
                    if (!go) continue;
                    var rot = GetPreviewObjectRotation(go.transform);
                    var scl = GetPreviewObjectScale(go.transform);
                    var pos = GetPreviewObjectPosition(go.transform);
                    boundsWS.Encapsulate(TransformBounds(mesh.bounds, pos, rot, scl));
                }
                var halfFovRad = previewUtil.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
                var radius = Mathf.Max(boundsWS.extents.x, boundsWS.extents.y, boundsWS.extents.z);
                previewDistance = Mathf.Clamp(radius / Mathf.Tan(halfFovRad) + radius * 0.45f, 0.6f, 2000f);
                if (pivotMode == PivotMode.BoundsCenter) previewPivot = boundsWS.center + previewPivotOffset;
            }
            else previewDistance = 1.7f;
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

            if (candidatesAll.Count == 0)
            {
                previewUtil.DrawMesh(mesh, Matrix4x4.identity, mats[0], 0);
            }
            else
            {
                foreach (var go in candidatesAll)
                {
                    if (!go) continue;
                    var rotObj = GetPreviewObjectRotation(go.transform);
                    var scl = GetPreviewObjectScale(go.transform);
                    var pos = GetPreviewObjectPosition(go.transform);
                    var trs = Matrix4x4.TRS(pos, rotObj, scl);
                    for (int si = 0; si < Mathf.Min(mesh.subMeshCount, mats.Length); si++)
                        previewUtil.DrawMesh(mesh, trs, mats[si] ? mats[si] : fallbackMat, si);
                }
            }

            cam.Render();
            var tex = previewUtil.EndPreview();
            GUI.DrawTexture(rect, tex, UnityEngine.ScaleMode.StretchToFill, false);
        }

        // Drag-drop prefab into viewer to set Desired Asset
        if (rect.Contains(Event.current.mousePosition))
        {
            if ((Event.current.type == EventType.DragUpdated || Event.current.type == EventType.DragPerform))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (Event.current.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        if (obj is GameObject go && IsPrefabAsset(go)) { targetPrefab = go; Repaint(); break; }
                    }
                }
                Event.current.Use();
            }
        }

        // Orbit / Zoom / Pan
        if (rect.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                if (Event.current.button == 0 && !Event.current.shift) // orbit
                {
                    previewUserAdjusted = true;
                    previewYaw += Event.current.delta.x * 0.5f;
                    previewPitch = Mathf.Clamp(previewPitch + Event.current.delta.y * 0.5f, -80, 80); // Y inverted
                    Repaint();
                }
                else if (Event.current.shift && Event.current.button == 0) // pan with Shift+LMB
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
                previewDistance = Mathf.Clamp(previewDistance * (1f + Event.current.delta.y * 0.04f), 0.3f, 2000f);
                Repaint();
            }
        }
    }

    // ----- SceneView support -----
    private void OpenViewportWindow()
    {
        if (_viewportWindow == null)
        {
            _viewportWindow = EditorWindow.CreateWindow<SceneView>("Preview Viewport");
            _viewportWindow.orthographic = false;
            _viewportWindow.in2DMode = false;
        }
        SyncViewportWindow(forceReframe:true);
        _viewportWindow.Repaint();
        _viewportWindow.Focus();
    }

    private void CloseViewportWindow()
    {
        if (_viewportWindow != null)
        {
            _viewportWindow.Close();
            _viewportWindow = null;
        }
    }

    private void SyncViewportWindow(bool forceReframe = false)
    {
        if (_viewportWindow == null) return;

        var candidates = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && !string.IsNullOrEmpty(prefix) && t.gameObject.name.StartsWith(prefix))
            .Select(t => t.gameObject).ToList();

        Vector3 pivot = GetPreviewPivot(candidates) + previewPivotOffset;
        Quaternion rot = Quaternion.Euler(previewPitch, previewYaw, 0f);
        float size = Mathf.Max(previewDistance, 5f);

        // SceneView.LookAt handles both pivot and rotation; size roughly sets distance
        _viewportWindow.LookAt(pivot, rot, size, true, forceReframe);
    }

    // ----- Preview object transforms -----
    private Vector3 GetPreviewObjectPosition(Transform t)
    {
        Vector3 pos = t.position;

        Vector3 seeded = Vector3.zero;
        if (useLocationSeed)
        {
            int baseHash = t.GetInstanceID() ^ (t.name.GetHashCode() << 1);
            var rng = new System.Random(MixSeedWithHash(locationSeed, baseHash));
            float rx = Mathf.Lerp(clampX.x, clampX.y, (float)rng.NextDouble());
            float ry = Mathf.Lerp(clampY.x, clampY.y, (float)rng.NextDouble());
            float rz = Mathf.Lerp(clampZ.x, clampZ.y, (float)rng.NextDouble());
            seeded = new Vector3(rx, ry, rz);
        }

        Vector3 delta = locationOffset + seeded;
        if (lockX) delta.x = 0f;
        if (lockY) delta.y = 0f;
        if (lockZ) delta.z = 0f;

        if (locationSpace == LocationSpace.Local)
            return t.TransformPoint(t.worldToLocalMatrix.MultiplyPoint3x4(t.position) + delta); // local move
        else
            return pos + delta; // world move
    }

    private Vector3 GetPreviewPivot(List<GameObject> candidates)
    {
        switch (pivotMode)
        {
            case PivotMode.Parent:
                if (explicitParent) return explicitParent.position;
                if (groupWithEmptyParent)
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
                return Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero;
        }
        return Vector3.zero;
    }

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
                int hash = t.GetInstanceID() ^ (t.name.GetHashCode() << 1);
                var rng = new System.Random(MixSeedWithHash(rotationSeed, hash));
                float y = (float)(rng.NextDouble() * 360.0);
                return Quaternion.Euler(rotationEuler.x, rotationEuler.y + y, rotationEuler.z);
            }
        }
        return t.rotation;
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
                int hash = t.GetInstanceID() ^ (t.name.GetHashCode() << 1);
                var rng = new System.Random(MixSeedWithHash(scaleSeed, hash));
                float minv = SafePositive(scaleRandomMin, 0.0001f);
                float maxv = SafePositive(scaleRandomMax, 0.0001f);
                if (maxv < minv) { var tmp = minv; minv = maxv; maxv = tmp; }
                float f = Mathf.Lerp(minv, maxv, (float)rng.NextDouble());
                return new Vector3(f + scaleXYZ.x, f + scaleXYZ.y, f + scaleXYZ.z);
            }
        }
        return t.localScale;
    }

    // ------------------------------------------------------
    // Core logic
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
            .Where(go => go != null && go.scene.IsValid() && !string.IsNullOrEmpty(prefix) && go.name.StartsWith(prefix))
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

                var inst = ReplaceOne(src, targetPrefab, forcedName, useIncrementalNaming,
                    rotationMode, rotationEuler, rotationSeed,
                    scaleMode, scaleXYZ, scaleSeed, scaleRandomMin, scaleRandomMax,
                    locationSpace, lockX, lockY, lockZ, locationOffset, useLocationSeed, locationSeed, clampX, clampY, clampZ,
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

        if (moveTarget != MoveTarget.None)
        {
            Vector3 targetPos = Vector3.zero;
            switch (moveTarget)
            {
                case MoveTarget.FirstObject:  if (spawned.Count > 0) targetPos = spawned[0].transform.position; break;
                case MoveTarget.BoundsCenter: targetPos = GetWorldCenter(spawned); break;
                case MoveTarget.WorldOrigin:  targetPos = Vector3.zero; break;
                case MoveTarget.WorldCoordinates: targetPos = worldCoordinate; break;
                case MoveTarget.SelectedObject: if (Selection.activeTransform) targetPos = Selection.activeTransform.position; break;
                case MoveTarget.Parent:
                    if (explicitParent) targetPos = explicitParent.position;
                    else if (spawned.Count > 0) { var gp = GetGroupParentForScene(spawned[0].scene); targetPos = gp ? gp.position : Vector3.zero; }
                    break;
            }

            if (finalRoot != null) finalRoot.transform.position = targetPos;
            else if (spawned.Count > 0)
            {
                var center = GetWorldCenter(spawned);
                var delta = targetPos - center;
                foreach (var go in spawned) if (go != null) go.transform.position += delta;
            }
        }

        if (rebuildInstancedCollision)
        {
            if (finalRoot != null) TryRebuildInstancedCollision(finalRoot);
            else foreach (var go in spawned) if (go != null) TryRebuildInstancedCollision(go);
        }

        if (!string.IsNullOrEmpty(savePath) && (finalRoot != null))
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(finalRoot, savePath);
            if (prefab != null) Debug.Log($"Saved prefab: {savePath}");
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

    private Vector3 GetEmptyParentPositionForScene(List<GameObject> sceneCandidates, EmptyParentLocation loc, Vector3 manual)
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
        RotationMode rotMode, Vector3 rotEuler, long rotSeed,
        ScaleMode scMode, Vector3 scXYZ, long scSeed, float scMin, float scMax,
        LocationSpace locSpace, bool lX, bool lY, bool lZ, Vector3 locBase, bool useLocSeed, long locSeed,
        Vector2 clampX, Vector2 clampY, Vector2 clampZ,
        Transform groupingParent, Dictionary<string, int> counters)
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
                var rng = new System.Random(MixSeedWithHash(rotSeed, hash));
                float y = (float)(rng.NextDouble() * 360.0);
                finalRot = Quaternion.Euler(rotEuler.x, rotEuler.y + y, rotEuler.z);
                break;
            }
        }

        // Scale
        Vector3 finalScale;
        switch (scMode)
        {
            default:
            case ScaleMode.PlaceholderScale:
                finalScale = Vector3.Scale(localScale, SafeVector3(scXYZ, 0.0001f));
                break;
            case ScaleMode.NewScale:
                finalScale = SafeVector3(scXYZ, 0.0001f);
                break;
            case ScaleMode.SeedValue:
            {
                int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                var rng = new System.Random(MixSeedWithHash(scSeed, hash));
                float minv = SafePositive(scMin, 0.0001f);
                float maxv = SafePositive(scMax, 0.0001f);
                if (maxv < minv) { var tmp = minv; minv = maxv; maxv = tmp; }
                float f = Mathf.Lerp(minv, maxv, (float)rng.NextDouble());
                finalScale = new Vector3(f + scXYZ.x, f + scXYZ.y, f + scXYZ.z);
                break;
            }
        }

        // Location offset
        Vector3 delta = locBase;
        if (useLocSeed)
        {
            int h = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
            var rng = new System.Random(MixSeedWithHash(locSeed, h));
            float dx = Mathf.Lerp(clampX.x, clampX.y, (float)rng.NextDouble());
            float dy = Mathf.Lerp(clampY.x, clampY.y, (float)rng.NextDouble());
            float dz = Mathf.Lerp(clampZ.x, clampZ.y, (float)rng.NextDouble());
            delta += new Vector3(dx, dy, dz);
        }
        if (lX) delta.x = 0f; if (lY) delta.y = 0f; if (lZ) delta.z = 0f;

        Vector3 finalPos;
        if (locSpace == LocationSpace.Local)
        {
            var m = Matrix4x4.TRS(localPos, localRot, Vector3.one);
            finalPos = m.MultiplyPoint3x4(delta);
        }
        else
        {
            finalPos = localPos + delta;
        }

        // Apply transform & metadata
        inst.transform.localPosition = finalPos;
        inst.transform.localRotation = finalRot;
        inst.transform.localScale = finalScale;

        inst.layer = layer;
        try { inst.tag = tag; } catch { }
        GameObjectUtility.SetStaticEditorFlags(inst, staticFlags);
        inst.SetActive(active);

        // Naming
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

    // -------- Helpers: numeric safety --------
    private static float SafePositive(float v, float min) => (!float.IsFinite(v) || v < min) ? min : v;
    private static Vector3 SafeVector3(Vector3 v, float componentMin)
    {
        v.x = SafePositive(v.x, componentMin);
        v.y = SafePositive(v.y, componentMin);
        v.z = SafePositive(v.z, componentMin);
        return v;
    }

    private static Bounds TransformBounds(Bounds b, Vector3 pos, Quaternion rot, Vector3 scl)
    {
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

    private int CountPlaceholders(string pfx)
    {
        if (string.IsNullOrEmpty(pfx) || pfx.Length < 3) return 0;
        return Resources.FindObjectsOfTypeAll<Transform>()
            .Count(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(pfx));
    }

    // ---------- Seed + random helpers ----------
    private static long NewSeed10Digits()
    {
        uint a = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        uint b = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        long mixed = ((long)a << 21) ^ (long)b;
        mixed = Math.Abs(mixed % 9_999_999_999L) + 1L;
        return mixed;
    }
    private static int SeedToInt(long seed)
    {
        unchecked
        {
            long z = seed + unchecked((long)0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * unchecked((long)0xBF58476D1CE4E5B9UL);
            z = (z ^ (z >> 27)) * unchecked((long)0x94D049BB133111EBUL);
            z = z ^ (z >> 31);
            return (int)z;
        }
    }
    private static int MixSeedWithHash(long seed, int hash)
    {
        unchecked
        {
            long s = seed ^ ((long)hash << 16);
            return SeedToInt(s);
        }
    }

    // ---------- Param randomise / undo ----------
    private void PushSnapshot()
    {
        if (_undo.Count >= UndoCap)
        {
            var tmp = new Stack<ParamSnapshot>(UndoCap);
            var arr = _undo.ToArray();
            for (int i = arr.Length - 2; i >= 0; --i) tmp.Push(arr[i]);
            _undo.Clear();
            foreach (var s in tmp) _undo.Push(s);
        }
        _undo.Push(Capture());
    }
    private void PopSnapshot()
    {
        if (_undo.Count == 0) return;
        Apply(_undo.Pop());
    }
    private ParamSnapshot Capture()
    {
        return new ParamSnapshot
        {
            prefix = prefix, targetPrefab = targetPrefab, forcedName = forcedName, useIncrementalNaming = useIncrementalNaming,
            rotationMode = rotationMode, rotationEuler = rotationEuler, rotationSeed = rotationSeed,
            scaleMode = scaleMode, scaleXYZ = scaleXYZ, scaleSeed = scaleSeed, scaleRandomMin = scaleRandomMin, scaleRandomMax = scaleRandomMax,
            locationSpace = locationSpace, lockX = lockX, lockY = lockY, lockZ = lockZ, locationOffset = locationOffset,
            useLocationSeed = useLocationSeed, locationSeed = locationSeed, clampX = clampX, clampY = clampY, clampZ = clampZ,
            explicitParent = explicitParent, groupWithEmptyParent = groupWithEmptyParent, groupParentName = groupParentName,
            emptyParentLocation = emptyParentLocation, manualEmptyParentPosition = manualEmptyParentPosition,
            combineIntoOne = combineIntoOne, pivotMode = pivotMode,
            moveTarget = moveTarget, worldCoordinate = worldCoordinate,
            rebuildInstancedCollision = rebuildInstancedCollision, savePath = savePath,
            previewBackground = previewBackground, previewYaw = previewYaw, previewPitch = previewPitch, previewDistance = previewDistance,
            previewPivotOffset = previewPivotOffset,
            autoSyncViewport = autoSyncViewport
        };
    }
    private void Apply(ParamSnapshot s)
    {
        prefix = s.prefix; targetPrefab = s.targetPrefab; forcedName = s.forcedName; useIncrementalNaming = s.useIncrementalNaming;
        rotationMode = s.rotationMode; rotationEuler = s.rotationEuler; rotationSeed = s.rotationSeed;
        scaleMode = s.scaleMode; scaleXYZ = s.scaleXYZ; scaleSeed = s.scaleSeed; scaleRandomMin = s.scaleRandomMin; scaleRandomMax = s.scaleRandomMax;
        locationSpace = s.locationSpace; lockX = s.lockX; lockY = s.lockY; lockZ = s.lockZ; locationOffset = s.locationOffset;
        useLocationSeed = s.useLocationSeed; locationSeed = s.locationSeed; clampX = s.clampX; clampY = s.clampY; clampZ = s.clampZ;
        explicitParent = s.explicitParent; groupWithEmptyParent = s.groupWithEmptyParent; groupParentName = s.groupParentName;
        emptyParentLocation = s.emptyParentLocation; manualEmptyParentPosition = s.manualEmptyParentPosition;
        combineIntoOne = s.combineIntoOne; pivotMode = s.pivotMode;
        moveTarget = s.moveTarget; worldCoordinate = s.worldCoordinate;
        rebuildInstancedCollision = s.rebuildInstancedCollision; savePath = s.savePath;
        previewBackground = s.previewBackground; previewYaw = s.previewYaw; previewPitch = s.previewPitch; previewDistance = s.previewDistance;
        previewPivotOffset = s.previewPivotOffset;
        autoSyncViewport = s.autoSyncViewport;
    }
    private void RandomiseAllParameters()
    {
        var rng = new System.Random(Environment.TickCount);

        rotationMode = (RotationMode)rng.Next(0, 3);
        rotationEuler = new Vector3((float)rng.NextDouble() * 45f, (float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 45f);
        rotationSeed = NewSeed10Digits();

        scaleMode = (ScaleMode)rng.Next(0, 3);
        scaleXYZ = new Vector3(0f, 0f, 0f) + new Vector3((float)rng.NextDouble() * 0.5f, (float)rng.NextDouble() * 0.5f, (float)rng.NextDouble() * 0.5f);
        scaleSeed = NewSeed10Digits();
        scaleRandomMin = 0.6f + (float)rng.NextDouble() * 0.2f;
        scaleRandomMax = Mathf.Max(scaleRandomMin + 0.1f, scaleRandomMin + (float)rng.NextDouble() * 0.8f);

        locationSpace = (LocationSpace)rng.Next(0, 2);
        lockX = rng.NextDouble() < 0.2; lockY = rng.NextDouble() < 0.2; lockZ = rng.NextDouble() < 0.2;
        locationOffset = new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 1f, (float)rng.NextDouble() * 2f - 1f);
        useLocationSeed = rng.NextDouble() < 0.7;
        locationSeed = NewSeed10Digits();
        clampX = new Vector2(-1, 1); clampY = new Vector2(0, 0); clampZ = new Vector2(-1, 1);

        // Do not touch parenting/pivot per your request.
        Repaint();
    }
}
#endif
