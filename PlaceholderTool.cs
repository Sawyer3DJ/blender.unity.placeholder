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
// Tools > Placeholders > Placeholder Switcher
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
    private readonly Dictionary<string, int> _nameCounters = new Dictionary<string, int>();

    // ---------- Rotation ----------
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero;
    [SerializeField] private int rotationSeed = 1234;      // 1..10000 (seeded Y)

    // ---------- Scale ----------
    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private Vector3 scaleXYZ = Vector3.one;  // always visible; in Seed mode adds to the random uniform
    [SerializeField] private int scaleSeed = 321;             // 1..10000 (seeded uniform)
    [SerializeField] private float scaleRandomMin = 0.8f;     // clamp min
    [SerializeField] private float scaleRandomMax = 1.2f;     // clamp max

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

    // ---------- Collision ----------
    [SerializeField] private bool rebuildInstancedCollision = false;

    // ---------- Save (under viewer) ----------
    [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

    // ---------- Preview ----------
    private enum PreviewBg { CurrentSkybox, BasicScene, Gray, White }
    [SerializeField] private PreviewBg previewBackground = PreviewBg.CurrentSkybox;

    private PreviewRenderUtility previewUtil;
    private float previewYaw = -30f;
    private float previewPitch = 15f;
    private float previewDistance = 2.4f;     // slightly farther default
    private bool previewUserAdjusted = false;
    private Vector3 previewPivotOffset = Vector3.zero;

    private struct PrefabDraw
    {
        public Mesh mesh;
        public Material[] mats;
        public Matrix4x4 localMatrix;
        public Bounds localBounds;
    }
    private List<PrefabDraw> prefabMeshes = new List<PrefabDraw>();
    private Material fallbackMat;

    // ---------- State ----------
    private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();

    [MenuItem("Tools/Placeholders/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(1180, 780);
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

        if (!previewUtil.camera.TryGetComponent<Skybox>(out var _))
            previewUtil.camera.gameObject.AddComponent<Skybox>();

        fallbackMat = new Material(Shader.Find("Standard"));
        ApplyPreviewBackground();
        RebuildPrefabMeshCache();
    }

    private void CleanupPreview()
    {
        previewUtil?.Cleanup();
        if (fallbackMat != null) DestroyImmediate(fallbackMat);
        previewUtil = null; fallbackMat = null;
        prefabMeshes.Clear();
    }

    // ------------------------------------------------------
    // UI
    // ------------------------------------------------------
    private void OnGUI()
    {
        // Title row
        var big = new GUIStyle(EditorStyles.largeLabel) { fontStyle = FontStyle.Bold, fontSize = 20 };
        GUILayout.Label("Placeholder Switcher", big);
        EditorGUILayout.Space(4);

        // Split: Preview (left large) | Controls (right)
        EditorGUILayout.BeginHorizontal();

        // -------- Left: Preview column --------
        var leftWidth = Mathf.Max(position.width * 0.60f, 600f);
        EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
        DrawPreviewArea(leftWidth);
        EditorGUILayout.EndVertical();

        // -------- Right: Controls column --------
        EditorGUILayout.BeginVertical();
        GUILayout.BeginHorizontal();
        GUILayout.Space(10); // extra padding to move column right
        GUILayout.BeginVertical();

        float oldLW = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 240f; // widen to avoid cramped labels

        DrawControls();

        EditorGUIUtility.labelWidth = oldLW;
        GUILayout.EndVertical();
        GUILayout.Space(6);
        GUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();
    }

    private void DrawControls()
    {
        GUILayout.Label("Replace Object Placeholders", EditorStyles.boldLabel);

        // Inputs
        prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix", "e.g. 'SS_'"), prefix);
        targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), targetPrefab, typeof(GameObject), false);
        if (GUI.changed) RebuildPrefabMeshCache();

        // Naming
        using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(forcedName)))
        {
            originalNameSource = (OriginalNameSource)EditorGUILayout.EnumPopup(new GUIContent("Original name source"), originalNameSource);
        }
        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);

        EditorGUILayout.Space(6);

        // Rotation
        GUILayout.Label("Rotation", EditorStyles.boldLabel);
        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);
        rotationEuler = EditorGUILayout.Vector3Field(
            new GUIContent(rotationMode == RotationMode.NewRotation ? "Rotation (new)" :
                           rotationMode == RotationMode.PlaceholderRotation ? "Rotation (adds to placeholder)" :
                           "Rotation (offset added to seeded Y)"),
            rotationEuler);
        if (rotationMode == RotationMode.SeedValueOnY)
        {
            EditorGUILayout.BeginHorizontal();
            rotationSeed = SafeClampInt(EditorGUILayout.IntField(new GUIContent("Random rotation seed (Y)"), rotationSeed), 1, 10000);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                rotationSeed = UnityEngine.Random.Range(1, 10001);
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(6);

        // Scale
        GUILayout.Label("Scale", EditorStyles.boldLabel);
        scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

        // Always show XYZ (in Seed mode it's an additive offset)
        scaleXYZ = SafeVector3(EditorGUILayout.Vector3Field(
            new GUIContent(scaleMode == ScaleMode.PlaceholderScale ? "Scale (multiplies placeholder)" :
                           scaleMode == ScaleMode.NewScale ? "Scale (new)" :
                           "Scale (adds to seeded uniform)"),
            scaleXYZ), 0.0001f);

        if (scaleMode == ScaleMode.SeedValue)
        {
            // Seed + clamping row with Randomise
            EditorGUILayout.BeginHorizontal();
            scaleSeed = SafeClampInt(EditorGUILayout.IntField(new GUIContent("Random scaling seed"), scaleSeed), 1, 10000);
            GUILayout.Space(8);
            GUILayout.Label("Clamping", GUILayout.Width(70));
            scaleRandomMin = SafePositive(EditorGUILayout.FloatField(new GUIContent("Min", "Minimum uniform factor"), scaleRandomMin), 0.0001f);
            scaleRandomMax = SafePositive(EditorGUILayout.FloatField(new GUIContent("Max", "Maximum uniform factor"), scaleRandomMax), 0.0001f);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                scaleSeed = UnityEngine.Random.Range(1, 10001);
            EditorGUILayout.EndHorizontal();
            if (scaleRandomMax < scaleRandomMin) (scaleRandomMin, scaleRandomMax) = (scaleRandomMax, scaleRandomMin);
        }

        EditorGUILayout.Space(6);

        // Parenting
        GUILayout.Label("Parenting", EditorStyles.boldLabel);
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

        // Combine / Move / Collision
        GUILayout.Label("Combine / Move", EditorStyles.boldLabel);
        combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one", "Static content only"), combineIntoOne);
        using (new EditorGUI.DisabledScope(!combineIntoOne))
        {
            EditorGUI.indentLevel++;
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (also affects preview centering)"), pivotMode);

            if (combineIntoOne)
            {
                EditorGUILayout.HelpBox(
                    "Combining bakes all spawned instances into ONE mesh/renderer. Parent choices affect placement. " +
                    "The selected Pivot determines the combined origin. Per-object behaviours (scripts/colliders/triggers) are lost.",
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

        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Rebuild instanced collision"), rebuildInstancedCollision);

        EditorGUILayout.Space(10);
    }

    // ------------------------------------------------------
    // Preview Area (left)
    // ------------------------------------------------------
    private void DrawPreviewArea(float leftWidth)
    {
        float viewerHeight = Mathf.Max(position.height * 0.60f, 460f);
        var rect = GUILayoutUtility.GetRect(leftWidth - 24, viewerHeight);
        DrawPreview(rect);

        // Viewer footer: background, controls, actions
        EditorGUILayout.Space(4);
        var newBg = (PreviewBg)EditorGUILayout.EnumPopup(new GUIContent("Viewer Background"), previewBackground);
        if (newBg != previewBackground)
        {
            previewBackground = newBg;
            ApplyPreviewBackground();
        }
        EditorGUILayout.LabelField("Controls", "LMB: Orbit (Y inverted)   Shift+LMB: Pan   Wheel: Zoom", EditorStyles.miniLabel);

        // Re-center view
        if (GUILayout.Button("Re-center View", GUILayout.Height(22)))
        {
            previewUserAdjusted = false;
            previewPivotOffset = Vector3.zero;
            Repaint();
        }

        EditorGUILayout.Space(6);

        // Save path + buttons
        EditorGUILayout.BeginHorizontal();
        savePath = EditorGUILayout.TextField("Save Path", savePath);
        if (GUILayout.Button("Select…", GUILayout.Width(90)))
        {
            var suggested = System.IO.Path.GetFileName(savePath);
            var path = EditorUtility.SaveFilePanelInProject("Save Prefab As",
                string.IsNullOrEmpty(suggested) ? "CombinedPlaceholder" : suggested,
                "prefab", "Choose save path");
            if (!string.IsNullOrEmpty(path)) savePath = path;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(8);

        // Two buttons: Load Asset From File | Save From Preview As…
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Load Asset From File", GUILayout.Height(30)))
        {
            LoadAssetFromFile();
        }

        // Save From Preview As… (rules: enabled if 1 placeholder OR (many & combine checked))
        var canPreviewSave = CanSaveFromPreview(out string previewSaveHint);
        using (new EditorGUI.DisabledScope(!canPreviewSave || string.IsNullOrEmpty(savePath)))
        {
            if (GUILayout.Button(new GUIContent("Save From Preview As…", previewSaveHint), GUILayout.Height(30)))
                SaveFromPreview();
        }
        EditorGUILayout.EndHorizontal();

        if (!canPreviewSave)
        {
            EditorGUILayout.HelpBox("Multiple placeholders detected. Enable “Combine objects into one” to save them as a single asset.", MessageType.Info);
        }

        EditorGUILayout.Space(8);

        // Big execute button
        bool canSwitch = prefix != null && prefix.Length >= 3 && targetPrefab != null && IsPrefabAsset(targetPrefab);
        using (new EditorGUI.DisabledScope(!canSwitch))
        {
            if (GUILayout.Button("Switch Placeholders", GUILayout.Height(38)))
                RunReplace();
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
                if (sky) sky.enabled = true;
                break;
            case PreviewBg.BasicScene:
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = new Color(0.58f, 0.63f, 0.70f, 1f);
                if (sky) sky.enabled = false;
                break;
            case PreviewBg.White:
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = Color.white;
                if (sky) sky.enabled = false;
                break;
            case PreviewBg.Gray:
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 1f);
                if (sky) sky.enabled = false;
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

        // Drag & drop to set target prefab
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

        var previewPivot = GetPreviewPivot(candidates) + previewPivotOffset;

        if (!previewUserAdjusted)
        {
            var boundsWS = new Bounds(candidates[0].transform.position, Vector3.zero);

            foreach (var go in candidates)
            {
                if (!go) continue;

                var rotObj = GetPreviewObjectRotation(go.transform);
                var sclObj = GetPreviewObjectScale(go.transform);

                foreach (var pd in prefabMeshes)
                {
                    var world = Matrix4x4.TRS(go.transform.position, rotObj, sclObj) * pd.localMatrix;
                    var bb = TransformBounds(pd.localBounds, world);
                    boundsWS.Encapsulate(bb);
                }
            }

            var halfFovRad = previewUtil.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
            var radius = Mathf.Max(boundsWS.extents.x, boundsWS.extents.y, boundsWS.extents.z);
            previewDistance = Mathf.Clamp(radius / Mathf.Tan(halfFovRad) + radius * 0.35f, 0.4f, 3000f);
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

                foreach (var pd in prefabMeshes)
                {
                    if (!pd.mesh) continue;
                    var mats = (pd.mats != null && pd.mats.Length > 0) ? pd.mats : new[] { fallbackMat };
                    var world = Matrix4x4.TRS(go.transform.position, rotObj, sclObj) * pd.localMatrix;
                    for (int si = 0; si < Mathf.Min(pd.mesh.subMeshCount, mats.Length); si++)
                    {
                        var mat = mats[si] ? mats[si] : fallbackMat;
                        previewUtil.DrawMesh(pd.mesh, world, mat, si);
                    }
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
                if (Event.current.button == 0 && !Event.current.shift) // orbit
                {
                    previewUserAdjusted = true;
                    previewYaw += Event.current.delta.x * 0.5f;
                    previewPitch = Mathf.Clamp(previewPitch + Event.current.delta.y * 0.5f, -80, 80); // inverted Y
                    Repaint();
                }
                else if (Event.current.button == 0 && Event.current.shift) // pan
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

    // ======= MISSING HELPERS (now added) =======

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
                int hash = (t.GetInstanceID() ^ (t.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((rotationSeed * 73856093) ^ hash));
                float y = (float)(rng.NextDouble() * 360.0);
                return Quaternion.Euler(rotationEuler.x, y + rotationEuler.y, rotationEuler.z);
            }
            default:
                return t.rotation;
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
                int hash = (t.GetInstanceID() ^ (t.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                float minv = SafePositive(scaleRandomMin, 0.0001f);
                float maxv = SafePositive(scaleRandomMax, 0.0001f);
                if (maxv < minv) { var tmp = minv; minv = maxv; maxv = tmp; }
                float f = Mathf.Lerp(minv, maxv, (float)rng.NextDouble());
                return new Vector3(f, f, f) + scaleXYZ; // seed uniform + XYZ offset
            }
            default:
                return t.localScale;
        }
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
                if (Selection.activeTransform) return Selection.activeTransform.position;
                return Vector3.zero;
        }
        return Vector3.zero;
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
        var count = Resources.FindObjectsOfTypeAll<Transform>()
            .Count(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix));

        if (count == 1) return true;
        if (count > 1 && combineIntoOne) return true;

        if (count > 1) hint = "Multiple placeholders found. Enable “Combine objects into one”.";
        return false;
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

                inst.transform.SetParent(tempRoot.transform, false);

                // Rotation
                Quaternion finalRot;
                switch (rotationMode)
                {
                    default:
                    case RotationMode.PlaceholderRotation:
                        finalRot = src.transform.localRotation * Quaternion.Euler(rotationEuler);
                        break;
                    case RotationMode.NewRotation:
                        finalRot = Quaternion.Euler(rotationEuler);
                        break;
                    case RotationMode.SeedValueOnY:
                    {
                        int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                        var rng = new System.Random(unchecked((rotationSeed * 73856093) ^ hash));
                        float y = (float)(rng.NextDouble() * 360.0);
                        finalRot = Quaternion.Euler(rotationEuler.x, y + rotationEuler.y, rotationEuler.z);
                        break;
                    }
                }

                // Scale
                Vector3 finalScale;
                switch (scaleMode)
                {
                    default:
                    case ScaleMode.PlaceholderScale:
                        finalScale = Vector3.Scale(src.transform.localScale, SafeVector3(scaleXYZ, 0.0001f));
                        break;
                    case ScaleMode.NewScale:
                        finalScale = SafeVector3(scaleXYZ, 0.0001f);
                        break;
                    case ScaleMode.SeedValue:
                    {
                        int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                        var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                        float minv = SafePositive(scaleRandomMin, 0.0001f);
                        float maxv = SafePositive(scaleRandomMax, 0.0001f);
                        if (maxv < minv) { var tmp = minv; minv = maxv; maxv = tmp; }
                        float f = Mathf.Lerp(minv, maxv, (float)rng.NextDouble());
                        finalScale = new Vector3(f, f, f) + scaleXYZ;
                        break;
                    }
                }

                inst.transform.localPosition = src.transform.localPosition;
                inst.transform.localRotation = finalRot;
                inst.transform.localScale = finalScale;

                temps.Add(inst);
            }

            GameObject toSave;
            if (candidates.Count == 1 && !combineIntoOne)
            {
                toSave = temps[0];
            }
            else
            {
                toSave = CombineInstances(temps, pivotMode, explicitParent, GetGroupParentForScene(temps[0].scene),
                    string.IsNullOrEmpty(forcedName) ? "Combined Object" : forcedName);
            }

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
        {
            AssetDatabase.CreateFolder("Assets", "ImportedByPlaceholderSwitcher");
        }

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

                var inst = ReplaceOne(
                    src, targetPrefab,
                    originalNameSource,
                    forcedName, useIncrementalNaming,
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
            finalRoot = CombineInstances(spawned, pivotMode, explicitParent, GetGroupParentForScene(spawned[0].scene),
                string.IsNullOrEmpty(forcedName) ? null : forcedName);
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
        GameObject src, GameObject prefab,
        OriginalNameSource nameSource,
        string forced, bool incremental,
        RotationMode rotMode, Vector3 rotEuler, int rotSeed,
        ScaleMode scMode, Vector3 scXYZ, int scSeed, float scMin, float scMax,
        Transform groupingParent, Dictionary<string, int> counters)
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
                finalRot = Quaternion.Euler(rotEuler.x, y + rotEuler.y, rotEuler.z);
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
                var rng = new System.Random(unchecked((scSeed * 19349663) ^ hash));
                float minv = SafePositive(scMin, 0.0001f);
                float maxv = SafePositive(scMax, 0.0001f);
                if (maxv < minv) { var tmp = minv; minv = maxv; maxv = tmp; }
                float f = Mathf.Lerp(minv, maxv, (float)rng.NextDouble());
                finalScale = new Vector3(f, f, f) + scXYZ; // seed uniform + XYZ offset
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
        string baseName = string.IsNullOrEmpty(forced)
            ? (nameSource == OriginalNameSource.Placeholder ? src.name : prefab.name)
            : forced;

        inst.name = ApplyIncremental(baseName, incremental, counters);

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

    // -------- Helpers --------
    private static int SafeClampInt(int v, int min, int max) => v < min ? min : (v > max ? max : v);
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
    private static Bounds TransformBounds(Bounds b, Vector3 pos, Quaternion rot, Vector3 scl) =>
        TransformBounds(b, Matrix4x4.TRS(pos, rot, scl));
}
#endif
