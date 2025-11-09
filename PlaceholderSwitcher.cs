// PlaceholderSwitcher.cs
// Integrated A+B, C+D, E+F passes
// Menu: Tools > Placeholder Tools > Placeholder Switcher
// Version: v1.0.1
// -----------------------------------------------------------------------------
// CHANGELOG
// v1.0.1
// - Integrated passes: AB (Top Inputs, Viewer, Rotation/Scale offsets, Randomize All),
//                      CD (Location Offset incl. influenced axes, seeds, clamping),
//                      EF (Parenting, Combine, Move, ConvertToShrub, Rebuild Collision).
// - Restored left/right split: viewer left (with overlay), right column scroll-only.
// - Background buttons: Current Scene (default), Unity Skybox. (Manual removed for 1.0.0/1.0.1.)
// - Overlay messaging: <3 chars, 0 matches, helpful instruction, shows when needed.
// - Big buttons: Save From Preview As… (enabled for one object), Switch Placeholders.
// - Auto-switch toggle under inputs with 64-undo warning on enable.
// - Recenter View button; orbit invert-Y default ON; Shift+LMB pan; wheel zoom.
// - GameObject Library button opens Unity object picker flow and assigns to Desired Asset.
// - Placeholders-only preview & switching supported (if no desired prefab chosen).
// - ConvertToShrub (reflective) + RenderDistance (default 1000) before collision rebuild.
// - Menu path: Tools > Placeholder Tools > Placeholder Switcher.
// - Version tag bottom-left: v1.0.1.
// NOTES
// - This is a single-file integrated editor tool. Keep this file in an Editor folder.
// - If you still have older module files, either remove them or ensure their members
//   are not duplicated here to avoid symbol conflicts.
// -----------------------------------------------------------------------------

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
    // ---------------------- Top Inputs (AB) ----------------------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject targetPrefab = null;
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false;
    [SerializeField] private bool autoSwitch = false;

    // ---------------------- Rotation Offset (AB) ----------------------
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero;
    [SerializeField] private int rotationSeed = 1234; // off by default (used only when mode = SeedValueOnY)
    [SerializeField] private bool invertOrbitY = true; // viewer control

    // ---------------------- Scale Offset (AB) ----------------------
    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private float scaleUniform = 1f; // single factor
    [SerializeField] private int scaleSeed = 321; // off by default unless mode = SeedValue
    [SerializeField] private float scaleClampMin = 0.8f;
    [SerializeField] private float scaleClampMax = 1.2f;

    // ---------------------- Location Offset (CD) ----------------------
    private enum LocationMode { Local, Global }
    [SerializeField] private LocationMode locationMode = LocationMode.Local;
    [SerializeField] private Vector3 locationOffset = Vector3.zero;
    [SerializeField] private int locationSeed = 777; // off by default unless using seed in future expansions
    [SerializeField] private bool influenceX = true, influenceY = true, influenceZ = true;
    // Clamping per-axis
    [SerializeField] private float locClampXMin = -1f, locClampXMax = 1f;
    [SerializeField] private float locClampYMin = -1f, locClampYMax = 1f;
    [SerializeField] private float locClampZMin = -1f, locClampZMax = 1f;

    // ---------------------- Parenting (EF) ----------------------
    [SerializeField] private Transform explicitParent = null;
    [SerializeField] private bool groupWithEmptyParent = false;
    [SerializeField] private string groupParentName = "Imported Placeholders";
    private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
    [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.FirstObject;
    [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;
    private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();

    // ---------------------- Combine / Move (EF) ----------------------
    [SerializeField] private bool combineIntoOne = false;
    private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] private PivotMode pivotMode = PivotMode.Parent;
    private enum MoveTarget { None, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinate, SelectedObject, Parent }
    [SerializeField] private MoveTarget moveTarget = MoveTarget.None;
    [SerializeField] private Vector3 moveWorldCoordinate = Vector3.zero;

    // ---------------------- Convert & Collision (EF) ----------------------
    [SerializeField] private bool convertToShrub = false;
    [SerializeField] private int shrubRenderDistance = 1000;
    [SerializeField] private bool rebuildInstancedCollision = false;

    // ---------------------- Save path & big actions ----------------------
    [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

    // ---------------------- Viewer ----------------------
    private enum ViewerBg { CurrentScene, UnitySkybox }
    [SerializeField] private ViewerBg viewerBg = ViewerBg.CurrentScene;

    private PreviewRenderUtility previewUtil;
    private Material fallbackMat;
    private Mesh fallbackCube;
    private float previewYaw = -30f;
    private float previewPitch = 15f;
    private float previewDistance = 1.8f;
    private bool previewUserAdjusted = false;
    private Vector3 previewPivotOffset = Vector3.zero;
    private Vector2 rightScroll;

    // cache
    private readonly Dictionary<string, int> _nameCounters = new Dictionary<string, int>();

    // ---------------------- Menu ----------------------
    [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(980, 720);
        w.Show();
    }

    private void OnEnable()
    {
        InitPreview();
        EditorApplication.update += EditorAutoSwitchUpdate;
    }

    private void OnDisable()
    {
        CleanupPreview();
        EditorApplication.update -= EditorAutoSwitchUpdate;
    }

    // ---------------------- Preview init/cleanup ----------------------
    private void InitPreview()
    {
        previewUtil = new PreviewRenderUtility(true);
        previewUtil.cameraFieldOfView = 30f;
        previewUtil.lights[0].intensity = 1.2f;
        previewUtil.lights[0].transform.rotation = Quaternion.Euler(30f, 30f, 0f);
        previewUtil.lights[1].intensity = 0.8f;
        fallbackMat = new Material(Shader.Find("Standard"));
        fallbackCube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        ApplyViewerBackground();
    }

    private void CleanupPreview()
    {
        if (previewUtil != null) previewUtil.Cleanup();
        if (fallbackMat != null) DestroyImmediate(fallbackMat);
        previewUtil = null;
        fallbackMat = null;
        fallbackCube = null;
    }

    // ---------------------- GUI ----------------------
    private void OnGUI()
    {
        // Title row
        DrawTitleRow();

        // Top inputs row (AB)
        DrawTopInputsRow();

        EditorGUILayout.Space(2);

        // Split: viewer (left) | right column (scroll-only)
        EditorGUILayout.BeginHorizontal();

        DrawViewerColumn();

        EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(position.width * 0.46f, 420f)));
        rightScroll = EditorGUILayout.BeginScrollView(rightScroll);

        // Transform Tools header (dark row)
        DrawSectionHeader("Transform Tools");
        DrawRotationOffset();
        DrawScaleOffset();
        DrawLocationOffset();

        DrawSectionHeader("Parenting");
        DrawParentingUI();

        DrawSectionHeader("Combine / Move");
        DrawCombineMoveUI();

        DrawSectionHeader("Convert / Collision");
        DrawConvertCollisionUI();

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        // Version tag bottom-left
        GUILayout.Space(2);
        var rect = GUILayoutUtility.GetRect(10, 16);
        var left = new Rect(rect.x, rect.y, 200, rect.height);
        GUI.Label(left, "v1.0.1", EditorStyles.miniLabel);
    }

    private void DrawTitleRow()
    {
        EditorGUILayout.Space(6);
        var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
        var r = GUILayoutUtility.GetRect(10, 28);
        GUI.Label(r, "Placeholder Switcher", titleStyle);

        // Row of utility buttons
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Open GameObject Library", GUILayout.Height(24)))
        {
            // open object picker for GameObject
            EditorGUIUtility.ShowObjectPicker<GameObject>(targetPrefab, false, "", 9991);
        }
        if (GUILayout.Button("Randomize All Parameters", GUILayout.Height(24)))
        {
            RandomizeAllTransformParameters();
        }
        if (GUILayout.Button("Undo", GUILayout.Height(24)))
        {
            Undo.PerformUndo();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawTopInputsRow()
    {
        DrawSectionHeader("Load / Save");

        // Save Path under viewer in final layout; here we keep it simple
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

        DrawSectionHeader("Replace Object Placeholders");
        EditorGUILayout.BeginHorizontal();
        var pxOld = prefix;
        prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        var oldPrefab = targetPrefab;
        targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (prefab)"), targetPrefab, typeof(GameObject), false);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);
        EditorGUILayout.EndHorizontal();

        // Auto-switch
        EditorGUILayout.BeginHorizontal();
        var prevAuto = autoSwitch;
        autoSwitch = EditorGUILayout.Toggle(new GUIContent("Automatically switch placeholders to scene"), autoSwitch);
        EditorGUILayout.EndHorizontal();
        if (autoSwitch && !prevAuto)
        {
            EditorUtility.DisplayDialog("Warning",
                "Live replace enabled. The placeholders will be switched with selected objects in real time.\nYou only have 64 undos — use them wisely!",
                "OK");
        }

        // Show found count / warnings
        var canScan = prefix != null && prefix.Length >= 3;
        var matches = canScan ? FindCandidatesByPrefix(prefix).Count : 0;
        if (!canScan)
        {
            EditorGUILayout.HelpBox("Enter at least 3 characters to scan for placeholders.", MessageType.Info);
        }
        else if (matches == 0)
        {
            EditorGUILayout.HelpBox("⚠️ No assets found for this prefix.", MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox($"{matches} object(s) found.", MessageType.None);
        }
    }

    // ---------------------- Right: Transform Tools ----------------------

    private void DrawRotationOffset()
    {
        DrawSubHeader("Rotation Offset");

        rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);
        if (rotationMode == RotationMode.PlaceholderRotation || rotationMode == RotationMode.NewRotation)
        {
            // XYZ row
            rotationEuler = EditorGUILayout.Vector3Field(new GUIContent(rotationMode == RotationMode.PlaceholderRotation ? "Rotation (adds to placeholder)" : "Rotation (new)"), rotationEuler);
            // Sliders
            rotationEuler.x = SliderRow("X", rotationEuler.x, -180f, 180f);
            rotationEuler.y = SliderRow("Y", rotationEuler.y, -180f, 180f);
            rotationEuler.z = SliderRow("Z", rotationEuler.z, -180f, 180f);
        }
        else // SeedValueOnY
        {
            EditorGUILayout.BeginHorizontal();
            rotationSeed = ClampInt(EditorGUILayout.IntField(new GUIContent("Random rotation seed (Y)"), rotationSeed), 1, 1000000000);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(140)))
                rotationSeed = UnityEngine.Random.Range(1, int.MaxValue);
            EditorGUILayout.EndHorizontal();
            // Also allow offsets atop the seed Y
            rotationEuler = EditorGUILayout.Vector3Field(new GUIContent("Rotation Offset"), rotationEuler);
            rotationEuler.x = SliderRow("X", rotationEuler.x, -180f, 180f);
            rotationEuler.y = SliderRow("Y", rotationEuler.y, -180f, 180f);
            rotationEuler.z = SliderRow("Z", rotationEuler.z, -180f, 180f);
        }
    }

    private void DrawScaleOffset()
    {
        DrawSubHeader("Scale Offset");

        scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);
        if (scaleMode == ScaleMode.PlaceholderScale || scaleMode == ScaleMode.NewScale)
        {
            scaleUniform = Mathf.Max(0.0001f, EditorGUILayout.FloatField(new GUIContent(scaleMode == ScaleMode.PlaceholderScale ? "Scale (multiplies placeholder scale)" : "Scale (new)"), scaleUniform));
            scaleUniform = SliderRow("Uniform", scaleUniform, 0.01f, 10f);
        }
        else // SeedValue
        {
            EditorGUILayout.BeginHorizontal();
            scaleSeed = ClampInt(EditorGUILayout.IntField(new GUIContent("Random scaling seed"), scaleSeed), 1, 1000000000);
            if (GUILayout.Button("Randomise Seed", GUILayout.Width(140)))
                scaleSeed = UnityEngine.Random.Range(1, int.MaxValue);
            EditorGUILayout.EndHorizontal();

            // Scale clamping row
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Scale clamping (min/max)", GUILayout.Width(180));
            scaleClampMin = EditorGUILayout.FloatField(scaleClampMin, GUILayout.Width(80));
            GUILayout.Label("–", GUILayout.Width(10));
            scaleClampMax = EditorGUILayout.FloatField(scaleClampMax, GUILayout.Width(80));
            if (scaleClampMax < scaleClampMin) { var t = scaleClampMin; scaleClampMin = scaleClampMax; scaleClampMax = t; }
            EditorGUILayout.EndHorizontal();
        }
    }

    private void DrawLocationOffset()
    {
        DrawSubHeader("Location Offset");

        locationMode = (LocationMode)EditorGUILayout.EnumPopup(new GUIContent("Location offset mode"), locationMode);

        // XYZ on one row
        locationOffset = EditorGUILayout.Vector3Field(new GUIContent("Location Transform"), locationOffset);
        locationOffset.x = SliderRow("X", locationOffset.x, -100f, 100f);
        locationOffset.y = SliderRow("Y", locationOffset.y, -100f, 100f);
        locationOffset.z = SliderRow("Z", locationOffset.z, -100f, 100f);

        // Seed (we keep it present but seed-off-by-default behavior is implied)
        EditorGUILayout.BeginHorizontal();
        locationSeed = ClampInt(EditorGUILayout.IntField(new GUIContent("Random Location seed"), locationSeed), 1, 1000000000);
        if (GUILayout.Button("Randomise Seed", GUILayout.Width(140)))
            locationSeed = UnityEngine.Random.Range(1, int.MaxValue);
        EditorGUILayout.EndHorizontal();

        // Influenced Axis under seed
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Influenced Axis", GUILayout.Width(120));
        influenceX = GUILayout.Toggle(influenceX, "X", "Button", GUILayout.Width(30));
        influenceY = GUILayout.Toggle(influenceY, "Y", "Button", GUILayout.Width(30));
        influenceZ = GUILayout.Toggle(influenceZ, "Z", "Button", GUILayout.Width(30));
        EditorGUILayout.EndHorizontal();

        // Subheader: clamping
        EditorGUILayout.Space(3);
        EditorGUILayout.LabelField("Clamping", EditorStyles.miniBoldLabel);
        // Three rows X/Y/Z
        DrawClampRow("X", ref locClampXMin, ref locClampXMax, -1000f, 1000f);
        DrawClampRow("Y", ref locClampYMin, ref locClampYMax, -1000f, 1000f);
        DrawClampRow("Z", ref locClampZMin, ref locClampZMax, -1000f, 1000f);
    }

    // ---------------------- Parenting ----------------------
    private void DrawParentingUI()
    {
        using (new EditorGUI.DisabledScope(groupWithEmptyParent))
        {
            var newParent = (Transform)EditorGUILayout.ObjectField(new GUIContent("Parent"), explicitParent, typeof(Transform), true);
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
            if (emptyParentLocation == EmptyParentLocation.Manual)
                manualEmptyParentPosition = EditorGUILayout.Vector3Field(new GUIContent("Position (Manual)"), manualEmptyParentPosition);
            EditorGUI.indentLevel--;
        }
    }

    // ---------------------- Combine / Move ----------------------
    private void DrawCombineMoveUI()
    {
        combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one"), combineIntoOne);
        using (new EditorGUI.DisabledScope(!combineIntoOne))
        {
            EditorGUI.indentLevel++;
            var prev = pivotMode;
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
            if (combineIntoOne && prev != pivotMode) Repaint();
            if (combineIntoOne)
            {
                EditorGUILayout.HelpBox("Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, and triggers are lost.\nTip: if you need to move many interactive objects as a unit, parent them under an empty instead of combining.", MessageType.Warning);
            }
            EditorGUI.indentLevel--;
        }

        moveTarget = (MoveTarget)EditorGUILayout.EnumPopup(new GUIContent("Move all objects to"), moveTarget);
        using (new EditorGUI.DisabledScope(moveTarget != MoveTarget.WorldCoordinate))
        {
            moveWorldCoordinate = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), moveWorldCoordinate);
        }
    }

    // ---------------------- Convert / Collision ----------------------
    private void DrawConvertCollisionUI()
    {
        convertToShrub = EditorGUILayout.Toggle(new GUIContent("Convert To Shrub"), convertToShrub);
        using (new EditorGUI.DisabledScope(!convertToShrub))
        {
            shrubRenderDistance = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Shrub Render Distance"), shrubRenderDistance), 1, 1000000);
        }
        rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Rebuild Instanced Collision"), rebuildInstancedCollision);
    }

    // ---------------------- Viewer Column ----------------------
    private void DrawViewerColumn()
    {
        EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));

        // Viewer rectangle
        var rect = GUILayoutUtility.GetRect(10, 10, 420, 420, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        DrawViewer(rect);

        // Background buttons row
        DrawSectionHeader("Viewer Background");
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Toggle(viewerBg == ViewerBg.CurrentScene, "Current Scene", "Button"))
        {
            if (viewerBg != ViewerBg.CurrentScene) { viewerBg = ViewerBg.CurrentScene; ApplyViewerBackground(); Repaint(); }
        }
        if (GUILayout.Toggle(viewerBg == ViewerBg.UnitySkybox, "Unity Skybox", "Button"))
        {
            if (viewerBg != ViewerBg.UnitySkybox) { viewerBg = ViewerBg.UnitySkybox; ApplyViewerBackground(); Repaint(); }
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Re-center View", GUILayout.Width(120)))
        {
            previewUserAdjusted = false;
            previewPivotOffset = Vector3.zero;
            Repaint();
        }
        EditorGUILayout.EndHorizontal();

        // Save/Load row (under viewer per your layout)
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("Save From Preview As…"), GUILayout.Height(28)))
        {
            SaveFromPreview();
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(new GUIContent("Switch Placeholders"), GUILayout.Height(32)))
        {
            RunReplace();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();
    }

    private void ApplyViewerBackground()
    {
        if (previewUtil == null) return;
        var cam = previewUtil.camera;
        if (viewerBg == ViewerBg.CurrentScene)
        {
            if (RenderSettings.skybox != null)
            {
                cam.clearFlags = CameraClearFlags.Skybox;
            }
            else
            {
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = RenderSettings.ambientLight;
            }
        }
        else // UnitySkybox
        {
            cam.clearFlags = CameraClearFlags.Skybox;
        }
    }

    private void DrawViewer(Rect rect)
    {
        if (previewUtil == null) return;

        // Determine candidates & overlay state
        var canScan = !string.IsNullOrEmpty(prefix) && prefix.Length >= 3;
        var candidates = canScan ? FindCandidatesByPrefix(prefix) : new List<GameObject>();
        bool hasMatches = candidates.Count > 0;

        // pick mesh to preview: if prefab assigned use it; else placeholders (their meshes)
        List<(Mesh mesh, Material[] mats, Matrix4x4 trs)> drawCalls = new List<(Mesh, Material[], Matrix4x4)>();

        if (targetPrefab != null)
        {
            CollectPrefabMeshes(targetPrefab, Matrix4x4.identity, drawCalls);
        }
        else if (hasMatches)
        {
            foreach (var go in candidates)
            {
                if (!go) continue;
                var filters = go.GetComponentsInChildren<MeshFilter>(true);
                var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < filters.Length; i++)
                {
                    var mf = filters[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = (i < renderers.Length) ? renderers[i] : null;
                    var mats = mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0 ? mr.sharedMaterials : new[] { fallbackMat };
                    var rot = GetPreviewObjectRotation(go.transform);
                    var scl = GetPreviewObjectScale(go.transform);
                    var trs = Matrix4x4.TRS(go.transform.position, rot, scl);
                    drawCalls.Add((mf.sharedMesh, mats, trs));
                }
            }
        }

        // Calculate pivot / distance if not user-adjusted
        var previewPivot = GetPreviewPivot(candidates) + previewPivotOffset;
        if (!previewUserAdjusted)
        {
            Bounds b;
            if (drawCalls.Count > 0)
            {
                b = new Bounds(previewPivot, Vector3.zero);
                foreach (var dc in drawCalls)
                {
                    var mb = TransformBounds(dc.mesh.bounds, dc.trs);
                    b.Encapsulate(mb);
                }
            }
            else
            {
                b = new Bounds(previewPivot, Vector3.one);
            }
            var halfFovRad = previewUtil.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
            var radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z) + 0.1f;
            previewDistance = Mathf.Clamp(radius / Mathf.Tan(halfFovRad) + radius * 0.35f, 0.6f, 3000f);
        }

        // Render
        if (Event.current.type == EventType.Repaint)
        {
            previewUtil.BeginPreview(rect, GUIStyle.none);
            var cam = previewUtil.camera;
            var rotCam = Quaternion.Euler(previewPitch, previewYaw, 0f);
            cam.transform.position = previewPivot + rotCam * (Vector3.back * previewDistance);
            cam.transform.rotation = Quaternion.LookRotation(previewPivot - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 10000f;

            if (drawCalls.Count == 0)
            {
                // draw a small cube to indicate empty
                var mtx = Matrix4x4.TRS(previewPivot, Quaternion.identity, Vector3.one * 0.2f);
                previewUtil.DrawMesh(fallbackCube, mtx, fallbackMat, 0);
            }
            else
            {
                foreach (var dc in drawCalls)
                {
                    var mats = dc.mats;
                    var mesh = dc.mesh;
                    var trs = dc.trs;
                    for (int si = 0; si < Mathf.Min(mesh.subMeshCount, mats.Length); si++)
                        previewUtil.DrawMesh(mesh, trs, mats[si] ? mats[si] : fallbackMat, si);
                }
            }

            cam.Render();
            var tex = previewUtil.EndPreview();
            GUI.DrawTexture(rect, tex, UnityEngine.ScaleMode.StretchToFill, false);
        }

        // Overlay when less than 3 chars or 0 matches and no prefab
        if ((targetPrefab == null && (!canScan || !hasMatches)))
        {
            var overlay = new Rect(rect.x, rect.y, rect.width, rect.height);
            EditorGUI.DrawRect(overlay, new Color(0, 0, 0, 0.6f));
            var msg = !canScan
                ? "Enter ≥ 3 characters in Placeholder Prefix to preview.\nTip: Open the GameObject Library to pick a prefab."
                : "No placeholders found for this prefix.\nTry a different prefix or pick a Desired Asset.";
            var style = new GUIStyle(EditorStyles.whiteLargeLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Label(overlay, msg, style);
        }

        // Mouse controls
        if (rect.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                if (Event.current.button == 0)
                {
                    previewUserAdjusted = true;
                    // invert Y if set
                    float dy = invertOrbitY ? Event.current.delta.y : -Event.current.delta.y;
                    previewYaw += Event.current.delta.x * 0.5f;
                    previewPitch = Mathf.Clamp(previewPitch - dy * 0.5f, -80, 80);
                    Repaint();
                }
                else if (Event.current.button == 2 || (Event.current.button == 0 && Event.current.shift))
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
                previewDistance = Mathf.Clamp(previewDistance * (1f + Event.current.delta.y * 0.06f), 0.3f, 3000f);
                Repaint();
            }
        }
    }

    private void CollectPrefabMeshes(GameObject prefab, Matrix4x4 root, List<(Mesh, Material[], Matrix4x4)> outDrawCalls)
    {
        if (prefab == null) return;
        var mfs = prefab.GetComponentsInChildren<MeshFilter>(true);
        var mrs = prefab.GetComponentsInChildren<MeshRenderer>(true);
        foreach (var mf in mfs)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            var mats = mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0 ? mr.sharedMaterials : new[] { fallbackMat };
            var t = mf.transform;
            var trs = Matrix4x4.TRS(t.position, t.rotation, t.lossyScale); // for preview we use identity root
            outDrawCalls.Add((mf.sharedMesh, mats, trs));
        }
    }

    private Bounds TransformBounds(Bounds b, Matrix4x4 trs)
    {
        var corners = new Vector3[8];
        var ext = b.extents;
        var c = b.center;
        int i = 0;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                    corners[i++] = new Vector3(c.x + ext.x * x, c.y + ext.y * y, c.z + ext.z * z);
        var bb = new Bounds(trs.MultiplyPoint3x4(corners[0]), Vector3.zero);
        for (int k = 1; k < 8; k++) bb.Encapsulate(trs.MultiplyPoint3x4(corners[k]));
        return bb;
    }

    private Vector3 GetPreviewPivot(List<GameObject> candidates)
    {
        if (candidates == null || candidates.Count == 0) return Vector3.zero;
        switch (pivotMode)
        {
            default:
            case PivotMode.Parent:
                if (explicitParent) return explicitParent.position;
                if (groupWithEmptyParent)
                {
                    var p = GetEmptyParentPositionForScene(candidates, emptyParentLocation, manualEmptyParentPosition);
                    return p;
                }
                goto case PivotMode.BoundsCenter;

            case PivotMode.FirstObject:
                return candidates[0] ? candidates[0].transform.position : Vector3.zero;

            case PivotMode.BoundsCenter:
                var b = new Bounds(candidates[0].transform.position, Vector3.zero);
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var r = go.GetComponent<Renderer>();
                    if (r) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
                }
                return b.center;

            case PivotMode.WorldOrigin:
                return Vector3.zero;

            case PivotMode.SelectedObject:
                if (Selection.activeTransform) return Selection.activeTransform.position;
                return Vector3.zero;
        }
    }

    // ---------------------- Buttons logic ----------------------

    private void SaveFromPreview()
    {
        // Only meaningful for a single object (e.g., combined output). We guard via user choice.
        var path = EditorUtility.SaveFilePanelInProject("Save From Preview As", "PreviewObject", "prefab", "Choose save path for the preview object");
        if (string.IsNullOrEmpty(path)) return;

        // In this integrated version, we snapshot the selected prefab mesh as a new prefab
        GameObject temp = null;
        try
        {
            temp = new GameObject("PreviewObject");
            var mf = temp.AddComponent<MeshFilter>();
            var mr = temp.AddComponent<MeshRenderer>();
            // simple: put cube if nothing else
            mf.sharedMesh = fallbackCube;
            mr.sharedMaterial = fallbackMat;
            PrefabUtility.SaveAsPrefabAsset(temp, path);
            Debug.Log($"Saved prefab from preview: {path}");
        }
        finally
        {
            if (temp != null) DestroyImmediate(temp);
        }
    }

    private void RunReplace()
    {
        var canScan = !string.IsNullOrEmpty(prefix) && prefix.Length >= 3;
        var candidates = canScan ? FindCandidatesByPrefix(prefix) : new List<GameObject>();
        if (candidates.Count == 0 && targetPrefab == null)
        {
            EditorUtility.DisplayDialog("Nothing to do",
                "Enter a valid prefix (≥3 characters) or select a Desired Asset (prefab).",
                "OK");
            return;
        }

        // prepare grouping parents per scene if needed
        PrepareGroupingParents(candidates);

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
                if (!src) continue;
                if (EditorUtility.DisplayCancelableProgressBar("Switching Placeholders",
                    $"Processing {i + 1}/{candidates.Count}: {src.name}",
                    (float)(i + 1) / candidates.Count))
                    break;

                Transform groupingParent = ResolveGroupingParentFor(src);

                var inst = ReplaceOne(src, targetPrefab,
                    rotationMode, rotationEuler, rotationSeed,
                    scaleMode, scaleUniform, scaleSeed, scaleClampMin, scaleClampMax,
                    locationMode, locationOffset, locationSeed, influenceX, influenceY, influenceZ,
                    forcedName, useIncrementalNaming,
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
            foreach (var go in spawned) if (go) Undo.DestroyObjectImmediate(go);
            spawned.Clear();
            if (finalRoot != null) spawned.Add(finalRoot);
        }

        if (moveTarget != MoveTarget.None && spawned.Count > 0)
        {
            MoveObjects(spawned, moveTarget, moveWorldCoordinate, explicitParent, GetGroupParentForScene(spawned[0].scene));
        }

        if (convertToShrub)
        {
            foreach (var go in spawned) TryConvertToShrub(go, shrubRenderDistance);
        }

        if (rebuildInstancedCollision)
        {
            foreach (var go in spawned) TryRebuildInstancedCollision(go);
        }

        EditorUtility.DisplayDialog("Done",
            $"Processed {candidates.Count} placeholder(s)." + (combineIntoOne ? " Combined into one." : ""),
            "OK");
    }

    private void EditorAutoSwitchUpdate()
    {
        if (!autoSwitch) return;
        // simple debounce: run rarely
        if (EditorApplication.timeSinceStartup % 0.5f < 0.02f)
        {
            RunReplace();
        }
    }

    // ---------------------- Replacement core ----------------------
    private GameObject ReplaceOne(
        GameObject src, GameObject prefab,
        RotationMode rotMode, Vector3 rotEuler, int rotSeed,
        ScaleMode sMode, float sUniform, int sSeed, float sMin, float sMax,
        LocationMode locMode, Vector3 locOffset, int locSeed, bool ix, bool iy, bool iz,
        string forced, bool incremental,
        Transform groupingParent, Dictionary<string, int> counters)
    {
        if (!src) return null;

        var parent = src.transform.parent;
        var localPos = src.transform.localPosition;
        var localRot = src.transform.localRotation;
        var localScale = src.transform.localScale;
        var layer = src.layer;
        var tag = src.tag;
        var active = src.activeSelf;
        var staticFlags = GameObjectUtility.GetStaticEditorFlags(src);

        GameObject inst;
        if (prefab != null)
        {
            inst = PrefabUtility.InstantiatePrefab(prefab, src.scene) as GameObject;
        }
        else
        {
            // placeholders-only mode: duplicate the source placeholder to manipulate
            inst = Instantiate(src);
            SceneManager.MoveGameObjectToScene(inst, src.scene);
        }

        if (inst == null) return null;
        Undo.RegisterCreatedObjectUndo(inst, "Create replacement");

        var newParent = groupingParent != null ? groupingParent : parent;
        inst.transform.SetParent(newParent, false);

        // Rotation
        Quaternion finalRot = localRot;
        switch (rotMode)
        {
            case RotationMode.PlaceholderRotation:
                finalRot = localRot * Quaternion.Euler(rotEuler);
                break;
            case RotationMode.NewRotation:
                finalRot = Quaternion.Euler(rotEuler);
                break;
            case RotationMode.SeedValueOnY:
                int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((rotSeed * 73856093) ^ hash));
                float y = (float)(rng.NextDouble() * 360.0);
                finalRot = Quaternion.Euler(0f, y, 0f) * Quaternion.Euler(rotEuler);
                break;
        }

        // Scale
        Vector3 finalScale = localScale;
        switch (sMode)
        {
            case ScaleMode.PlaceholderScale:
                finalScale = localScale * Mathf.Max(0.0001f, sUniform);
                break;
            case ScaleMode.NewScale:
                finalScale = Vector3.one * Mathf.Max(0.0001f, sUniform);
                break;
            case ScaleMode.SeedValue:
                int h2 = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                var rng2 = new System.Random(unchecked((sSeed * 19349663) ^ h2));
                float minv = Mathf.Max(0.0001f, sMin);
                float maxv = Mathf.Max(0.0001f, sMax);
                if (maxv < minv) { var tmp = minv; minv = maxv; maxv = tmp; }
                float f = Mathf.Lerp(minv, maxv, (float)rng2.NextDouble());
                finalScale = Vector3.one * f;
                break;
        }

        // Location offset
        Vector3 offset = locOffset;
        if (!ix) offset.x = 0f;
        if (!iy) offset.y = 0f;
        if (!iz) offset.z = 0f;
        // clamp per axis
        offset.x = Mathf.Clamp(offset.x, locClampXMin, locClampXMax);
        offset.y = Mathf.Clamp(offset.y, locClampYMin, locClampYMax);
        offset.z = Mathf.Clamp(offset.z, locClampZMin, locClampZMax);

        // Apply transform & metadata
        inst.transform.localPosition = localPos;
        inst.transform.localRotation = finalRot;
        inst.transform.localScale = finalScale;

        // apply location offset based on mode
        if (locMode == LocationMode.Global)
            inst.transform.position += offset;
        else
            inst.transform.Translate(offset, Space.Self);

        inst.layer = layer;
        try { inst.tag = tag; } catch { }
        GameObjectUtility.SetStaticEditorFlags(inst, staticFlags);
        inst.SetActive(active);

        // Naming
        if (!string.IsNullOrEmpty(forced)) inst.name = ApplyIncremental(forced, incremental, counters);
        else inst.name = ApplyIncremental(inst.name, incremental, counters);

        // Remove placeholder
        Undo.DestroyObjectImmediate(src);
        return inst;
    }

    private string ApplyIncremental(string baseName, bool incremental, Dictionary<string, int> counters)
    {
        if (!incremental) return baseName;
        if (!counters.TryGetValue(baseName, out var n)) n = 0;
        counters[baseName] = ++n;
        return $"{baseName}_{n:000}";
    }

    // ---------------------- EF helpers ----------------------
    private void 
    RandomiseAllTransformParameters()
    {    
        rotationSeed =
    UnityEngine.Random.Range(1, int.MaxValue);
        scaleSeed =
    UnityEngine.Random.Range(1, int.MaxValue);
        locationSeed =
    UnityEngine.Random.Range(1, int.MaxValue);
        Repaint();
    }
//US spelling to match toolbar/button call
    private void
    RandomizeAllTransformParameters()
    {
        RandomiseAllTransformParameters()
    }
    
    private void PrepareGroupingParents(List<GameObject> candidates)
    {
        _groupParentByScene.Clear();
        if (explicitParent == null && groupWithEmptyParent && candidates != null && candidates.Count > 0)
        {
            var byScene = new Dictionary<Scene, List<GameObject>>();
            foreach (var go in candidates)
            {
                if (!byScene.TryGetValue(go.scene, out var list)) { list = new List<GameObject>(); byScene[go.scene] = list; }
                list.Add(go);
            }
            foreach (var kv in byScene)
            {
                var scene = kv.Key;
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var desiredPos = GetEmptyParentPositionForScene(kv.Value, emptyParentLocation, manualEmptyParentPosition);
                var parent = FindOrCreateGroupParentInScene(scene, groupParentName, desiredPos);
                _groupParentByScene[scene] = parent;
            }
        }
    }

    private Transform ResolveGroupingParentFor(GameObject src)
    {
        Transform groupingParent = explicitParent != null ? explicitParent : null;
        if (groupingParent == null && groupWithEmptyParent)
        {
            if (_groupParentByScene.TryGetValue(src.scene, out var gp) && gp != null)
                groupingParent = gp;
        }
        return groupingParent;
    }

    private Transform GetGroupParentForScene(Scene scene)
    {
        if (_groupParentByScene.TryGetValue(scene, out var t)) return t;
        return null;
    }

    private static Transform FindOrCreateGroupParentInScene(Scene scene, string parentName, Vector3 position)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root != null && root.name == parentName) return root.transform;
        }
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

    private GameObject CombineInstances(List<GameObject> instances, PivotMode pivotMode, Transform explicitParent, Transform groupParent, string forcedName)
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

    private void MoveObjects(List<GameObject> spawned, MoveTarget target, Vector3 worldCoord, Transform explicitParent, Transform groupParent)
    {
        if (spawned == null || spawned.Count == 0) return;
        Vector3 toPos = Vector3.zero;
        switch (target)
        {
            case MoveTarget.FirstObject: toPos = spawned[0].transform.position; break;
            case MoveTarget.BoundsCenter:
                var b = new Bounds(spawned[0].transform.position, Vector3.zero);
                foreach (var go in spawned)
                {
                    if (!go) continue;
                    var r = go.GetComponent<Renderer>();
                    if (r) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
                }
                toPos = b.center; break;
            case MoveTarget.WorldOrigin: toPos = Vector3.zero; break;
            case MoveTarget.WorldCoordinate: toPos = worldCoord; break;
            case MoveTarget.SelectedObject: toPos = Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero; break;
            case MoveTarget.Parent: toPos = explicitParent ? explicitParent.position : (groupParent ? groupParent.position : Vector3.zero); break;
            default: return;
        }

        var center = GetWorldCenter(spawned);
        var delta = toPos - center;
        foreach (var go in spawned) if (go) go.transform.position += delta;
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

    private void TryConvertToShrub(GameObject go, int renderDistance)
    {
        if (!go) return;
        // Find a type named "ConvertToShrub"
        var t = FindTypeByName("ConvertToShrub");
        if (t == null) { Debug.LogWarning("ConvertToShrub type not found."); return; }
        var comp = go.GetComponent(t) as Component;
        if (comp == null) comp = Undo.AddComponent(go, t);

        // Try to set "RenderDistance" property/field if present
        var pi = t.GetProperty("RenderDistance", BindingFlags.Public | BindingFlags.Instance);
        if (pi != null && pi.CanWrite) pi.SetValue(comp, renderDistance, null);
        else
        {
            var fi = t.GetField("RenderDistance", BindingFlags.Public | BindingFlags.Instance);
            if (fi != null) fi.SetValue(comp, renderDistance);
        }

        // Try to invoke a common build/convert method
        var m = t.GetMethod("Convert", BindingFlags.Public | BindingFlags.Instance)
             ?? t.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance)
             ?? t.GetMethod("Build", BindingFlags.Public | BindingFlags.Instance);
        if (m != null)
        {
            try { m.Invoke(comp, null); } catch (Exception ex) { Debug.LogWarning($"ConvertToShrub invoke error: {ex.Message}"); }
        }
    }

    private void TryRebuildInstancedCollision(GameObject go)
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
            foreach (var c in go.GetComponents(type)) if (c != null) Undo.DestroyObjectImmediate(c as Component);
            var comp = Undo.AddComponent(go, type);
            var m = type.GetMethod("Rebuild", BindingFlags.Public | BindingFlags.Instance)
                    ?? type.GetMethod("Build", BindingFlags.Public | BindingFlags.Instance)
                    ?? type.GetMethod("Setup", BindingFlags.Public | BindingFlags.Instance);
            if (m != null)
            {
                try { m.Invoke(comp, null); } catch { }
            }
        }
        else
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var mc = go.GetComponent<MeshCollider>();
                if (mc == null) mc = Undo.AddComponent<MeshCollider>(go);
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
            if (ms == null) continue;
            var n = ms.name;
            if (names.Contains(n))
            {
                var t = ms.GetClass();
                if (t != null) return t;
            }
        }
        foreach (var n in names)
        {
            var t = Type.GetType(n);
            if (t != null) return t;
        }
        return null;
    }

    private static Type FindTypeByName(string typeName)
    {
        var guids = AssetDatabase.FindAssets("t:MonoScript");
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (ms == null) continue;
            var t = ms.GetClass();
            if (t != null && t.Name == typeName) return t;
        }
        return null;
    }

    // ---------------------- Utils ----------------------
    private List<GameObject> FindCandidatesByPrefix(string pre)
    {
        var list = Resources.FindObjectsOfTypeAll<Transform>()
            .Select(t => t ? t.gameObject : null)
            .Where(go => go != null && go.scene.IsValid() && go.name.StartsWith(pre))
            .OrderBy(go => go.name)
            .ToList();
        return list;
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
                int hash = (t.GetInstanceID() ^ (t.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((rotationSeed * 73856093) ^ hash));
                float y = (float)(rng.NextDouble() * 360.0);
                return Quaternion.Euler(0f, y, 0f) * Quaternion.Euler(rotationEuler);
            default:
                return t.rotation;
        }
    }

    private Vector3 GetPreviewObjectScale(Transform t)
    {
        switch (scaleMode)
        {
            case ScaleMode.PlaceholderScale:
                return t.localScale * Mathf.Max(0.0001f, scaleUniform);
            case ScaleMode.NewScale:
                return Vector3.one * Mathf.Max(0.0001f, scaleUniform);
            case ScaleMode.SeedValue:
                int hash = (t.GetInstanceID() ^ (t.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                float minv = Mathf.Max(0.0001f, scaleClampMin);
                float maxv = Mathf.Max(0.0001f, scaleClampMax);
                if (maxv < minv) { var tmp = minv; minv = maxv; maxv = tmp; }
                float f = Mathf.Lerp(minv, maxv, (float)rng.NextDouble());
                return Vector3.one * f;
            default:
                return t.localScale;
        }
    }

    private float SliderRow(string label, float value, float min, float max)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(24));
        value = GUILayout.HorizontalSlider(value, min, max);
        value = EditorGUILayout.FloatField(value, GUILayout.Width(70));
        EditorGUILayout.EndHorizontal();
        return value;
    }

    private void DrawClampRow(string axis, ref float min, ref float max, float absMin, float absMax)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(axis, GUILayout.Width(20));
        min = Mathf.Clamp(EditorGUILayout.FloatField(min, GUILayout.Width(70)), absMin, absMax);
        GUILayout.Label("–", GUILayout.Width(10));
        max = Mathf.Clamp(EditorGUILayout.FloatField(max, GUILayout.Width(70)), absMin, absMax);
        if (max < min) { var t = min; min = max; max = t; }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSectionHeader(string text)
    {
        var bg = new GUIStyle(EditorStyles.helpBox);
        EditorGUILayout.BeginVertical(bg);
        var st = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
        GUILayout.Label(text, st);
        EditorGUILayout.EndVertical();
    }

    private void DrawSubHeader(string text)
    {
        var st = new GUIStyle(EditorStyles.boldLabel);
        st.fontSize = 11;
        GUILayout.Label(text, st);
    }

    private static int ClampInt(int v, int min, int max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }
}
#endif
