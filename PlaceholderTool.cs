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

    // ---------- Move ----------
    [SerializeField] private bool moveToWorldCoordinates = false;
    [SerializeField] private Vector3 moveTargetPosition = Vector3.zero;

    // ---------- Collision / Save ----------
    [SerializeField] private bool rebuildInstancedCollision = false;
    [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

    // ---------- Preview ----------
    private enum PreviewBg { CurrentSkybox, BasicScene, Viewport }
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

    // ---------- State ----------
    private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();
    private readonly Dictionary<string, int> _nameCounters = new Dictionary<string, int>(); // <- fixed CS1525 typo here

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

        public Transform explicitParent;
        public bool groupWithEmptyParent;
        public string groupParentName;
        public EmptyParentLocation emptyParentLocation;
        public Vector3 manualEmptyParentPosition;

        public bool combineIntoOne;
        public PivotMode pivotMode;

        public bool moveToWorldCoordinates;
        public Vector3 moveTargetPosition;

        public bool rebuildInstancedCollision;
        public string savePath;

        public PreviewBg previewBackground;
        public float previewYaw, previewPitch, previewDistance;
        public Vector3 previewPivotOffset;
    }
    private readonly Stack<ParamSnapshot> _undo = new Stack<ParamSnapshot>(64);
    private const int UndoCap = 64;

    [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(1000, 720);
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
        // Top toolbar (right aligned)
        GUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        if (GUILayout.Button("Open GameObject Library", GUILayout.Height(22)))
        {
            var w = GameObjectLibraryWindow.Open();
            GameObjectLibraryWindow.OnPick = (go) =>
            {
                targetPrefab = go;
                Repaint();
            };
        }

        if (GUILayout.Button("Randomise All Parameters", GUILayout.Height(22)))
        {
            PushSnapshot();
            RandomiseAllParameters();
        }

        using (new EditorGUI.DisabledScope(_undo.Count == 0))
        {
            if (GUILayout.Button("Undo", GUILayout.Height(22)))
            {
                PopSnapshot();
                Repaint();
            }
        }
        EditorGUILayout.EndHorizontal();

        // Split: Preview (left) | Controls (right)
        EditorGUILayout.BeginHorizontal();

        // -------- Left: Preview column --------
        EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(position.width * 0.5f, 520f)));
        DrawPreviewArea(); // includes background select + controls + file ops
        EditorGUILayout.EndVertical();

        // -------- Right: Controls column --------
        EditorGUILayout.BeginVertical();
        DrawControls();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();
    }

    private void DrawControls()
    {
        // Section title
        var sec = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        GUILayout.Label("Replace Object Placeholders", sec);

        // Inputs
        EditorGUILayout.BeginHorizontal();
        var prevPrefix = prefix;
        prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix", "e.g. 'SS_'"), prefix);
        int count = CountPlaceholders(prefix);
        var status = (string.IsNullOrEmpty(prefix) || prefix.Length < 2)
            ? " "
            : (count > 0 ? $"{count} objects found" : "⚠️ no assets found");
        GUILayout.Label(status, EditorStyles.miniLabel, GUILayout.Width(140));
        EditorGUILayout.EndHorizontal();

        targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), targetPrefab, typeof(GameObject), false);
        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)", "If set, overrides source/prefab name"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming", "Names like Base_001, Base_002…"), useIncrementalNaming);

        if (prevPrefix != prefix) Repaint();

        EditorGUILayout.Space(8);

        // ---------- Rotation ----------
        GUILayout.Label("Rotation", sec);
        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);

        if (rotationMode == RotationMode.PlaceholderRotation || rotationMode == RotationMode.NewRotation)
        {
            rotationEuler = EditorGUILayout.Vector3Field(
                new GUIContent(rotationMode == RotationMode.PlaceholderRotation ? "Rotation (adds to placeholder)" : "Rotation (new rotation)"),
                rotationEuler);
        }
        else // SeedValueOnY
        {
            using (new EditorGUI.IndentLevelScope())
            {
                rotationEuler = EditorGUILayout.Vector3Field(new GUIContent("Rotation offset (added to seeded Y)"), rotationEuler);
                EditorGUILayout.BeginHorizontal();
                rotationSeed = EditorGUILayout.LongField(new GUIContent("Random rotation seed (Y)"), rotationSeed);
                if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                    rotationSeed = NewSeed10Digits();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.HelpBox("Per-object deterministic Y rotation from seed; offset above is added on top.", MessageType.Info);
            }
        }

        EditorGUILayout.Space(8);

        // ---------- Scale ----------
        GUILayout.Label("Scale", sec);
        scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

        if (scaleMode == ScaleMode.PlaceholderScale || scaleMode == ScaleMode.NewScale)
        {
            scaleXYZ = SafeVector3(EditorGUILayout.Vector3Field(
                new GUIContent(scaleMode == ScaleMode.PlaceholderScale ? "Scale (multiplies placeholder scale)" : "Scale (new)"),
                scaleXYZ), 0.0001f);
        }
        else // SeedValue
        {
            using (new EditorGUI.IndentLevelScope())
            {
                // base offset (adds to seeded uniform)
                scaleXYZ = SafeVector3(EditorGUILayout.Vector3Field(new GUIContent("Scale (adds to seeded uniform)"), scaleXYZ), 0.0001f);

                EditorGUILayout.BeginHorizontal();
                scaleSeed = EditorGUILayout.LongField(new GUIContent("Random scaling seed"), scaleSeed);
                GUILayout.Space(8);
                GUILayout.Label("Scale clamping", GUILayout.Width(110));
                scaleRandomMin = SafePositive(EditorGUILayout.FloatField("Min", scaleRandomMin), 0.0001f);
                scaleRandomMax = SafePositive(EditorGUILayout.FloatField("Max", scaleRandomMax), 0.0001f);
                GUILayout.Space(8);
                if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                    scaleSeed = NewSeed10Digits();
                EditorGUILayout.EndHorizontal();

                if (scaleRandomMax < scaleRandomMin) (scaleRandomMin, scaleRandomMax) = (scaleRandomMax, scaleRandomMin);

                EditorGUILayout.HelpBox("Generates a uniform scale factor per object in [Min..Max], then adds the XYZ offset above.", MessageType.Info);
            }
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
                    "Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, and triggers are lost. " +
                    "Existing parenting/group choices will determine where the combined result is placed.",
                    MessageType.Warning);
            }
            EditorGUI.indentLevel--;
        }

        moveToWorldCoordinates = EditorGUILayout.Toggle(new GUIContent("Move to world coordinates"), moveToWorldCoordinates);
        using (new EditorGUI.DisabledScope(!moveToWorldCoordinates))
        {
            EditorGUI.indentLevel++;
            moveTargetPosition = EditorGUILayout.Vector3Field(new GUIContent("World Position"), moveTargetPosition);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(6);
        GUILayout.Label("Rebuild instanced collision", sec);
        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Enable"), rebuildInstancedCollision);

        EditorGUILayout.Space(12);

        // Main action (make big)
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(prefix) || targetPrefab == null || !IsPrefabAsset(targetPrefab)))
        {
            var big = new GUIStyle(GUI.skin.button) { fontSize = 14, fixedHeight = 40 };
            if (GUILayout.Button("Switch Placeholders", big))
                RunReplace();
        }

        if (targetPrefab != null && !IsPrefabAsset(targetPrefab))
            EditorGUILayout.HelpBox("Selected object is not a Prefab asset. Drag a prefab from the Project window.", MessageType.Warning);
        else if (string.IsNullOrEmpty(prefix))
            EditorGUILayout.HelpBox("Enter a placeholder prefix (e.g. 'SS_').", MessageType.Info);
    }

    // ------------------------------------------------------
    // Preview Area (left)
    // ------------------------------------------------------
    private void DrawPreviewArea()
    {
        // Title (left column)
        var title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.UpperLeft };
        GUILayout.Label("Placeholder Switcher", title);
        GUILayout.Space(2);

        // Viewport
        var rect = GUILayoutUtility.GetRect(10, 10, 480, 480);
        DrawPreview(rect);

        // Background selector + controls + file ops
        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Viewer Background", GUILayout.Width(120));
        if (GUILayout.Toggle(previewBackground == PreviewBg.CurrentSkybox, "Current Skybox", EditorStyles.miniButtonLeft))
        {
            previewBackground = PreviewBg.CurrentSkybox;
            ApplyPreviewBackground();
        }
        if (GUILayout.Toggle(previewBackground == PreviewBg.BasicScene, "Basic Scene", EditorStyles.miniButtonMid))
        {
            previewBackground = PreviewBg.BasicScene;
            ApplyPreviewBackground();
        }
        if (GUILayout.Toggle(previewBackground == PreviewBg.Viewport, "Viewport", EditorStyles.miniButtonRight))
        {
            previewBackground = PreviewBg.Viewport;
            ApplyPreviewBackground();
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

        // Save path & preview actions
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

        bool canSaveSingle = CountPlaceholders(prefix) == 1 && !string.IsNullOrEmpty(savePath);
        using (new EditorGUI.DisabledScope(!canSaveSingle || targetPrefab == null))
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

        // Status
        int c = CountPlaceholders(prefix);
        if (c == 0)
            EditorGUILayout.HelpBox("Nothing to save: enter a prefix and pick a Desired Asset to enable preview.", MessageType.Info);
        else if (c > 1)
            EditorGUILayout.HelpBox("Multiple placeholders detected. Enable 'Combine objects into one' to save them as a single asset.", MessageType.Warning);
    }

    private void ApplyPreviewBackground()
    {
        if (previewUtil == null) return;
        var cam = previewUtil.camera;
        switch (previewBackground)
        {
            case PreviewBg.CurrentSkybox:
                cam.clearFlags = RenderSettings.skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                cam.backgroundColor = RenderSettings.ambientLight;
                break;
            case PreviewBg.BasicScene:
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = new Color(0.35f, 0.35f, 0.35f, 1f);
                break;
            case PreviewBg.Viewport:
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 1f);
                break;
        }
    }

    private void RefreshPreviewMesh()
    {
        previewMesh = null; previewMats = null;
        if (targetPrefab == null) return;
        var mr = targetPrefab.GetComponentInChildren<MeshRenderer>();
        var mf = mr ? mr.GetComponent<MeshFilter>() : targetPrefab.GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) previewMesh = mf.sharedMesh;
        if (mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) previewMats = mr.sharedMaterials;
    }

    private void DrawPreview(Rect rect)
    {
        if (previewUtil == null)
        {
            EditorGUI.LabelField(rect, "Preview unavailable");
            return;
        }

        bool hasPrefix = !string.IsNullOrEmpty(prefix) && prefix.Length >= 2;
        if (!hasPrefix || targetPrefab == null)
        {
            GUI.Label(rect, "Enter a prefix (≥ 2 chars) and choose a Desired Asset (Prefab)\n—or drag a prefab here from the Project—to view preview.",
                new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter });
        }

        RefreshPreviewMesh();

        var candidatesAll = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
            .Select(t => t.gameObject)
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
                    boundsWS.Encapsulate(TransformBounds(mesh.bounds, go.transform.position, rot, scl));
                }
                var halfFovRad = previewUtil.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
                var radius = Mathf.Max(boundsWS.extents.x, boundsWS.extents.y, boundsWS.extents.z);
                previewDistance = Mathf.Clamp(radius / Mathf.Tan(halfFovRad) + radius * 0.4f, 0.5f, 2000f);
                if (pivotMode == PivotMode.BoundsCenter) previewPivot = boundsWS.center + previewPivotOffset;
            }
            else previewDistance = 1.6f;
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
                    var trs = Matrix4x4.TRS(go.transform.position, rotObj, GetPreviewObjectScale(go.transform));
                    for (int si = 0; si < Mathf.Min(mesh.subMeshCount, mats.Length); si++)
                        previewUtil.DrawMesh(mesh, trs, mats[si] ? mats[si] : fallbackMat, si);
                }
            }

            cam.Render();
            var tex = previewUtil.EndPreview();
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

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

    // ---------- Preview helpers ----------
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

                var inst = ReplaceOne(src, targetPrefab, forcedName, useIncrementalNaming,
                    rotationMode, rotationEuler, rotationSeed,
                    scaleMode, scaleXYZ, scaleSeed, scaleRandomMin, scaleRandomMax,
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

        // Apply transform & metadata
        inst.transform.localPosition = localPos;
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

    private static GameObject MakeTempGroupForSaving(List<GameObject> instances, string forcedName, Transform preferredParent)
    {
        var root = new GameObject(string.IsNullOrEmpty(forcedName) ? "PlaceholderGroup" : forcedName);
        Undo.RegisterCreatedObjectUndo(root, "Create temp root for prefab save");
        if (preferredParent) root.transform.SetParent(preferredParent, false);
        foreach (var go in instances) if (go) go.transform.SetParent(root.transform, true);
        return root;
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

    // Utility for transforming bounds
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
        if (string.IsNullOrEmpty(pfx)) return 0;
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
            long z = seed + 0x9E3779B97F4A7C15L;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9L;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBL;
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
            // Simple cap: discard oldest by rebuilding smaller stack
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
            explicitParent = explicitParent, groupWithEmptyParent = groupWithEmptyParent, groupParentName = groupParentName,
            emptyParentLocation = emptyParentLocation, manualEmptyParentPosition = manualEmptyParentPosition,
            combineIntoOne = combineIntoOne, pivotMode = pivotMode,
            moveToWorldCoordinates = moveToWorldCoordinates, moveTargetPosition = moveTargetPosition,
            rebuildInstancedCollision = rebuildInstancedCollision, savePath = savePath,
            previewBackground = previewBackground, previewYaw = previewYaw, previewPitch = previewPitch, previewDistance = previewDistance,
            previewPivotOffset = previewPivotOffset
        };
    }
    private void Apply(ParamSnapshot s)
    {
        prefix = s.prefix; targetPrefab = s.targetPrefab; forcedName = s.forcedName; useIncrementalNaming = s.useIncrementalNaming;
        rotationMode = s.rotationMode; rotationEuler = s.rotationEuler; rotationSeed = s.rotationSeed;
        scaleMode = s.scaleMode; scaleXYZ = s.scaleXYZ; scaleSeed = s.scaleSeed; scaleRandomMin = s.scaleRandomMin; scaleRandomMax = s.scaleRandomMax;
        explicitParent = s.explicitParent; groupWithEmptyParent = s.groupWithEmptyParent; groupParentName = s.groupParentName;
        emptyParentLocation = s.emptyParentLocation; manualEmptyParentPosition = s.manualEmptyParentPosition;
        combineIntoOne = s.combineIntoOne; pivotMode = s.pivotMode;
        moveToWorldCoordinates = s.moveToWorldCoordinates; moveTargetPosition = s.moveTargetPosition;
        rebuildInstancedCollision = s.rebuildInstancedCollision; savePath = s.savePath;
        previewBackground = s.previewBackground; previewYaw = s.previewYaw; previewPitch = s.previewPitch; previewDistance = s.previewDistance;
        previewPivotOffset = s.previewPivotOffset;
    }
    private void RandomiseAllParameters()
    {
        // Only from Rotation onward
        var rng = new System.Random(Environment.TickCount);

        rotationMode = (RotationMode)rng.Next(0, 3);
        rotationEuler = new Vector3((float)rng.NextDouble() * 45f, (float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 45f);
        rotationSeed = NewSeed10Digits();

        scaleMode = (ScaleMode)rng.Next(0, 3);
        scaleXYZ = new Vector3(1f, 1f, 1f) + new Vector3((float)rng.NextDouble() * 0.5f, (float)rng.NextDouble() * 0.5f, (float)rng.NextDouble() * 0.5f);
        scaleSeed = NewSeed10Digits();
        scaleRandomMin = 0.6f + (float)rng.NextDouble() * 0.2f;
        scaleRandomMax = Mathf.Max(scaleRandomMin + 0.1f, scaleRandomMin + (float)rng.NextDouble() * 0.8f);

        groupWithEmptyParent = rng.NextDouble() < 0.5;
        emptyParentLocation = (EmptyParentLocation)rng.Next(0, 5);

        combineIntoOne = rng.NextDouble() < 0.3;
        pivotMode = (PivotMode)rng.Next(0, 5);

        moveToWorldCoordinates = rng.NextDouble() < 0.3;
        moveTargetPosition = new Vector3((float)rng.NextDouble() * 10f, 0f, (float)rng.NextDouble() * 10f);

        rebuildInstancedCollision = rng.NextDouble() < 0.5;

        Repaint();
    }
}

// --------------------------------------------------------------
// Simple prefab library window (thumbnail picker)
// --------------------------------------------------------------
public class GameObjectLibraryWindow : EditorWindow
{
    private Vector2 _scroll;
    private float _thumbSize = 96f;
    private string _search = "";
    private List<GameObject> _prefabs = new List<GameObject>();
    private double _nextRepaint;

    public static Action<GameObject> OnPick;

    public static GameObjectLibraryWindow Open()
    {
        var w = GetWindow<GameObjectLibraryWindow>("Prefab Library");
        w.minSize = new Vector2(400, 300);
        w.RefreshList();
        return w;
    }

    private void RefreshList()
    {
        _prefabs.Clear();
        var guids = AssetDatabase.FindAssets("t:prefab");
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go != null) _prefabs.Add(go);
        }
        _prefabs.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70))) RefreshList();
        GUILayout.Space(6);
        GUILayout.Label("Search:", GUILayout.Width(48));
        string s = GUILayout.TextField(_search, EditorStyles.toolbarTextField, GUILayout.MinWidth(120));
        if (s != _search) { _search = s; Repaint(); }
        GUILayout.FlexibleSpace();
        GUILayout.Label("Thumb", GUILayout.Width(44));
        _thumbSize = GUILayout.HorizontalSlider(_thumbSize, 48, 192, GUILayout.Width(140));
        EditorGUILayout.EndHorizontal();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        float pad = 8f;
        float cell = _thumbSize + 40f;
        float width = position.width - 20f;
        int cols = Mathf.Max(1, Mathf.FloorToInt((width + pad) / (cell + pad)));

        int col = 0;
        EditorGUILayout.BeginHorizontal();
        foreach (var go in _prefabs)
        {
            if (!string.IsNullOrEmpty(_search) && !go.name.ToLower().Contains(_search.ToLower())) continue;

            EditorGUILayout.BeginVertical(GUILayout.Width(cell));
            var tex = AssetPreview.GetAssetPreview(go) ?? AssetPreview.GetMiniThumbnail(go);
            if (GUILayout.Button(tex, GUILayout.Width(_thumbSize), GUILayout.Height(_thumbSize)))
            {
                OnPick?.Invoke(go);
                FocusWindowIfItsOpen<PlaceholderSwitcher>();
            }
            GUILayout.Label(go.name, EditorStyles.miniLabel, GUILayout.Width(cell));
            EditorGUILayout.EndVertical();

            col++;
            if (col >= cols)
            {
                col = 0;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndScrollView();

        // Force repaints while previews stream in
        if (EditorApplication.timeSinceStartup > _nextRepaint)
        {
            Repaint();
            _nextRepaint = EditorApplication.timeSinceStartup + 0.5;
        }
    }
}
#endif
