#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlaceholderTools
{
    public class PlaceholderSwitcher : EditorWindow
    {
        // ---------------- Inputs (left header) ----------------
        [SerializeField] private string prefix = "SS_";
        [SerializeField] private UnityEngine.Object desiredAsset = null; // Prefab/GameObject
        [SerializeField] private string forcedName = "";
        [SerializeField] private bool useIncremental = false;
        [SerializeField] private bool autoSwitchLive = false;

        // ---------------- Rotation ----------------
        private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
        [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
        [SerializeField] private Vector3 rotationEuler = Vector3.zero;
        [SerializeField] private int rotationSeed = 1234;

        // ---------------- Scale ----------------
        private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
        [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
        [SerializeField] private float scaleUnified = 1f;
        [SerializeField] private bool scaleAffectX = true, scaleAffectY = true, scaleAffectZ = true;
        [SerializeField] private int scaleSeed = 321;
        [SerializeField] private float scaleClampMinX = 0.8f, scaleClampMaxX = 1.2f;
        [SerializeField] private float scaleClampMinY = 0.8f, scaleClampMaxY = 1.2f;
        [SerializeField] private float scaleClampMinZ = 0.8f, scaleClampMaxZ = 1.2f;

        // ---------------- Location Offset ----------------
        private enum LocationSpace { Local, World }
        [SerializeField] private LocationSpace locationSpace = LocationSpace.Local;
        [SerializeField] private Vector3 locationOffset = Vector3.zero;
        [SerializeField] private bool locAffectX = true, locAffectY = true, locAffectZ = true;
        [SerializeField] private bool useRandomLocationSeed = false;
        [SerializeField] private int locationSeed = 4567;
        [SerializeField] private float locClampMinX = -1f, locClampMaxX = 1f;
        [SerializeField] private float locClampMinY = -1f, locClampMaxY = 1f;
        [SerializeField] private float locClampMinZ = -1f, locClampMaxZ = 1f;

        // ---------------- Parenting / Combine / Move / Collision / Shrub ----------------
        [SerializeField] private Transform explicitParent = null;
        [SerializeField] private bool groupWithEmptyParent = false;
        [SerializeField] private string groupParentName = "Imported Placeholders";
        private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
        [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.FirstObject;
        [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;

        [SerializeField] private bool combineIntoOne = false;
        private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
        [SerializeField] private PivotMode pivotMode = PivotMode.Parent;

        private enum MoveAllTarget { None, Parent, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinate, SelectedObject }
        [SerializeField] private MoveAllTarget moveAllTarget = MoveAllTarget.None;
        [SerializeField] private Vector3 worldMoveTarget = Vector3.zero;

        [SerializeField] private bool rebuildInstancedCollision = false;

        // Convert to Shrub
        [SerializeField] private bool convertToShrub = false;
        [SerializeField] private int shrubRenderDistance = 1000;

        // ---------------- Preview / viewport ----------------
        private enum BgMode { CurrentSkybox, UnitySkybox }
        [SerializeField] private BgMode bgMode = BgMode.CurrentSkybox;

        private PreviewRenderUtility previewUtil;
        private float previewYaw = -25f;
        private float previewPitch = 15f;
        private float previewDistance = 3.0f;
        private Vector3 previewPan = Vector3.zero;
        private bool orbitInvertY = true;
        private bool orbitInvertX = false;

        private Material proceduralSkybox;
        private Mesh previewMesh;
        private Material[] previewMats;
        private Material fallbackMat;

        // External viewport
        private static PlaceholderViewportWindow externalViewport;
        [SerializeField] private bool externalAutoSync = true;

        // Internal state
        private readonly Dictionary<Scene, Transform> groupParentByScene = new();
        private readonly Dictionary<string, int> nameCounters = new();

        [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
        public static void ShowWindow()
        {
            var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
            w.minSize = new Vector2(980, 640);
            w.Show();
        }

        private void OnEnable() => InitPreview();
        private void OnDisable() { CleanupPreview(); CloseViewport(); }

        private void InitPreview()
        {
            previewUtil = new PreviewRenderUtility(true);
            previewUtil.cameraFieldOfView = 35f;
            previewUtil.lights[0].intensity = 1.15f;
            previewUtil.lights[1].intensity = 0.8f;
            previewUtil.ambientColor = Color.gray * 0.35f;
            fallbackMat = new Material(Shader.Find("Standard")) { color = Color.gray };
            EnsureProceduralSkybox();
        }

        private void EnsureProceduralSkybox()
        {
            if (!proceduralSkybox)
            {
                var sh = Shader.Find("Skybox/Procedural");
                if (sh)
                {
                    proceduralSkybox = new Material(sh);
                    proceduralSkybox.SetColor("_SkyTint", new Color(0.3f, 0.45f, 0.8f));
                    proceduralSkybox.SetColor("_GroundColor", new Color(0.35f, 0.23f, 0.12f));
                    proceduralSkybox.SetFloat("_AtmosphereThickness", 0.85f);
                    proceduralSkybox.SetFloat("_Exposure", 1.1f);
                }
            }
        }

        private void CleanupPreview()
        {
            previewUtil?.Cleanup();
            if (proceduralSkybox) DestroyImmediate(proceduralSkybox);
            if (fallbackMat) DestroyImmediate(fallbackMat);
            previewUtil = null; proceduralSkybox = null; fallbackMat = null;
        }

        private Vector2 rightScroll;

        private void OnGUI()
        {
            DrawTitleAndTopButtons();
            EditorGUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();

            // LEFT
            EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(position.width * 0.52f, 520f)));
            DrawLeftColumn();
            EditorGUILayout.EndVertical();

            // RIGHT (vertical scroll only)
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            rightScroll = EditorGUILayout.BeginScrollView(rightScroll, GUIStyle.none, GUI.skin.verticalScrollbar);
            GUILayout.Space(2);
            DrawTransformTools();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            if (externalViewport && externalAutoSync) externalViewport.Repaint();
        }

        private void DrawTitleAndTopButtons()
        {
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            Rect r = EditorGUILayout.GetControlRect(false, 28);
            GUI.Label(r, "Placeholder Switcher", titleStyle);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open GameObject Library", GUILayout.Height(22))) OpenGameObjectLibrary();
            if (GUILayout.Button("Randomize All Parameters", GUILayout.Height(22))) RandomizeAll();

            // FIX: don't apply '!' to int
            using (new EditorGUI.DisabledScope(Undo.GetCurrentGroup() == 0))
            {
                if (GUILayout.Button("Undo", GUILayout.Width(70), GUILayout.Height(22)))
                    Undo.PerformUndo();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ---------------- LEFT ----------------
        private void DrawLeftColumn()
        {
            // Replace Object Placeholders
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var subStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            GUILayout.Label("Replace Object Placeholders", subStyle);

            EditorGUILayout.BeginHorizontal();
            prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
            int count = CountPlaceholders();
            if (!HasMinimumPrefix())
                EditorGUILayout.LabelField("⚠︎ enter ≥3 chars", GUILayout.Width(110));
            else if (count == 0)
                EditorGUILayout.LabelField("⚠︎ no assets found", GUILayout.Width(120));
            else
                EditorGUILayout.LabelField($"{count} object(s) found", GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            desiredAsset = EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), desiredAsset, typeof(GameObject), false);

            EditorGUILayout.BeginHorizontal();
            forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
            useIncremental = EditorGUILayout.ToggleLeft(new GUIContent("Use incremental naming"), useIncremental, GUILayout.Width(170));
            EditorGUILayout.EndHorizontal();

            bool prevAuto = autoSwitchLive;
            autoSwitchLive = EditorGUILayout.ToggleLeft(new GUIContent("Automatically switch placeholders to scene"), autoSwitchLive);
            if (autoSwitchLive && !prevAuto)
            {
                EditorGUILayout.HelpBox(
                    "Warning: when enabled, any time you pick/select a Desired Asset and a matching prefix is present, " +
                    "the placeholders will be switched in real-time. You have 64 undo steps—use them wisely!",
                    MessageType.Warning);
            }
            EditorGUILayout.EndVertical();

            // Viewer
            GUILayout.Space(6);
            DrawViewer();

            // Viewer background + controls
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Viewer Background", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(bgMode == BgMode.CurrentSkybox, "Current Skybox", "Button")) { bgMode = BgMode.CurrentSkybox; }
            if (GUILayout.Toggle(bgMode == BgMode.UnitySkybox, "Unity Skybox", "Button")) { bgMode = BgMode.UnitySkybox; }
            GUILayout.FlexibleSpace();
            bool invY = GUILayout.Toggle(orbitInvertY, "Y Inverted", "Button", GUILayout.Width(90));
            bool invX = GUILayout.Toggle(orbitInvertX, "X Inverted", "Button", GUILayout.Width(90));
            if (invY != orbitInvertY) orbitInvertY = invY;
            if (invX != orbitInvertX) orbitInvertX = invX;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Re-center View", GUILayout.Width(120))) RecenterView();
            GUILayout.FlexibleSpace();
            externalAutoSync = GUILayout.Toggle(externalAutoSync, " Auto-sync Model View to Viewport", GUILayout.Width(220));
            if (externalViewport == null)
            {
                if (GUILayout.Button("Open Viewport", GUILayout.Width(120))) OpenViewport();
            }
            else
            {
                if (GUILayout.Button("Close Viewport", GUILayout.Width(120))) CloseViewport();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            // Load / Save
            GUILayout.Space(6);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Load / Save", new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 });

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Load Asset From File", GUILayout.Width(160))) LoadAssetFromFile();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(CountPlaceholders() != 1))
            {
                if (GUILayout.Button("Save From Preview As…", GUILayout.Width(200)))
                    SaveFromPreviewSingle();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
            int c = CountPlaceholders();
            if (c == 0) EditorGUILayout.HelpBox("Nothing to save, search for objects via a prefix to enable saving.", MessageType.Info);
            else if (c > 1) EditorGUILayout.HelpBox("Multiple placeholders detected. Enable “Combine objects into one” to save them as a single asset.", MessageType.Warning);
            EditorGUILayout.EndVertical();

            // Big switch button
            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(!CanSwitch()))
            {
                if (GUILayout.Button("Switch Placeholders", GUILayout.Height(34)))
                    RunReplace();
            }
        }

        private void DrawTransformTools()
        {
            var hdr = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };

            // Transform Tools header
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Transform Tools", new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });
            EditorGUILayout.EndVertical();

            // Rotation Offset
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Rotation Offset", hdr);
            rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Rotation (adds to placeholder)", GUILayout.Width(180));
            rotationEuler.x = EditorGUILayout.FloatField(rotationEuler.x);
            rotationEuler.y = EditorGUILayout.FloatField(rotationEuler.y);
            rotationEuler.z = EditorGUILayout.FloatField(rotationEuler.z);
            EditorGUILayout.EndHorizontal();

            rotationEuler.x = EditorGUILayout.Slider(rotationEuler.x, -360f, 360f);
            rotationEuler.y = EditorGUILayout.Slider(rotationEuler.y, -360f, 360f);
            rotationEuler.z = EditorGUILayout.Slider(rotationEuler.z, -360f, 360f);

            if (rotationMode == RotationMode.SeedValueOnY)
            {
                EditorGUILayout.BeginHorizontal();
                rotationSeed = SafeClampInt(EditorGUILayout.IntField(new GUIContent("Random rotation seed (Y)"), rotationSeed), 1, int.MaxValue);
                if (GUILayout.Button("Randomize Seed", GUILayout.Width(130)))
                    rotationSeed = UnityEngine.Random.Range(1, int.MaxValue);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox("Per-object deterministic Y rotation from seed; the XYZ offset above is added on top.", MessageType.Info);
            }
            EditorGUILayout.EndVertical();

            // Scale Offset
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Scale Offset", hdr);
            scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Scale (multiplies placeholder scale)"), GUILayout.Width(220));
            scaleUnified = Mathf.Max(0.0001f, EditorGUILayout.FloatField(scaleUnified));
            scaleAffectX = GUILayout.Toggle(scaleAffectX, "X", "Button", GUILayout.Width(28));
            scaleAffectY = GUILayout.Toggle(scaleAffectY, "Y", "Button", GUILayout.Width(28));
            scaleAffectZ = GUILayout.Toggle(scaleAffectZ, "Z", "Button", GUILayout.Width(28));
            EditorGUILayout.EndHorizontal();
            scaleUnified = EditorGUILayout.Slider(scaleUnified, 0.0001f, 10f);

            if (scaleMode == ScaleMode.SeedValue)
            {
                scaleSeed = SafeClampInt(EditorGUILayout.IntField(new GUIContent("Random scaling seed"), scaleSeed), 1, int.MaxValue);
                if (GUILayout.Button("Randomize Seed", GUILayout.Width(130)))
                    scaleSeed = UnityEngine.Random.Range(1, int.MaxValue);

                GUILayout.Label("Scale clamping (Min / Max)");
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("X Min/Max", GUILayout.Width(80));
                scaleClampMinX = EditorGUILayout.FloatField(scaleClampMinX);
                GUILayout.Label("Max", GUILayout.Width(30));
                scaleClampMaxX = EditorGUILayout.FloatField(scaleClampMaxX);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Y Min/Max", GUILayout.Width(80));
                scaleClampMinY = EditorGUILayout.FloatField(scaleClampMinY);
                GUILayout.Label("Max", GUILayout.Width(30));
                scaleClampMaxY = EditorGUILayout.FloatField(scaleClampMaxY);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Z Min/Max", GUILayout.Width(80));
                scaleClampMinZ = EditorGUILayout.FloatField(scaleClampMinZ);
                GUILayout.Label("Max", GUILayout.Width(30));
                scaleClampMaxZ = EditorGUILayout.FloatField(scaleClampMaxZ);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();

            // Location Offset
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Location Offset", hdr);

            locationSpace = (LocationSpace)EditorGUILayout.EnumPopup(new GUIContent("Location Transform Mode"), locationSpace);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Location Transform", GUILayout.Width(160));
            locationOffset.x = EditorGUILayout.FloatField(locationOffset.x);
            locationOffset.y = EditorGUILayout.FloatField(locationOffset.y);
            locationOffset.z = EditorGUILayout.FloatField(locationOffset.z);
            EditorGUILayout.EndHorizontal();

            locationOffset.x = EditorGUILayout.Slider(locationOffset.x, -20f, 20f);
            locationOffset.y = EditorGUILayout.Slider(locationOffset.y, -20f, 20f);
            locationOffset.z = EditorGUILayout.Slider(locationOffset.z, -20f, 20f);

            useRandomLocationSeed = EditorGUILayout.ToggleLeft("Use random location seed", useRandomLocationSeed);
            using (new EditorGUI.DisabledScope(!useRandomLocationSeed))
            {
                EditorGUILayout.BeginHorizontal();
                locationSeed = SafeClampInt(EditorGUILayout.IntField(new GUIContent("Random location seed"), locationSeed), 1, int.MaxValue);
                if (GUILayout.Button("Randomize Seed", GUILayout.Width(130)))
                    locationSeed = UnityEngine.Random.Range(1, int.MaxValue);
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.Label("Influenced Axes");
            EditorGUILayout.BeginHorizontal();
            locAffectX = GUILayout.Toggle(locAffectX, "X", "Button", GUILayout.Width(28));
            locAffectY = GUILayout.Toggle(locAffectY, "Y", "Button", GUILayout.Width(28));
            locAffectZ = GUILayout.Toggle(locAffectZ, "Z", "Button", GUILayout.Width(28));
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label("Clamping", EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("X Min/Max", GUILayout.Width(80));
            locClampMinX = EditorGUILayout.FloatField(locClampMinX);
            GUILayout.Label("Max", GUILayout.Width(30));
            locClampMaxX = EditorGUILayout.FloatField(locClampMaxX);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Y Min/Max", GUILayout.Width(80));
            locClampMinY = EditorGUILayout.FloatField(locClampMinY);
            GUILayout.Label("Max", GUILayout.Width(30));
            locClampMaxY = EditorGUILayout.FloatField(locClampMaxY);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Z Min/Max", GUILayout.Width(80));
            locClampMinZ = EditorGUILayout.FloatField(locClampMinZ);
            GUILayout.Label("Max", GUILayout.Width(30));
            locClampMaxZ = EditorGUILayout.FloatField(locClampMaxZ);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            // Parenting
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Parenting", hdr);

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

            // Combine / Move
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Combine / Move", hdr);

            combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one"), combineIntoOne);
            using (new EditorGUI.DisabledScope(!combineIntoOne))
            {
                pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
                if (combineIntoOne)
                {
                    EditorGUILayout.HelpBox(
                        "Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, and triggers are lost.\n" +
                        "If you need to move multiple interactive objects together, parent them under an Empty and move the parent.",
                        MessageType.Warning);
                }
            }

            moveAllTarget = (MoveAllTarget)EditorGUILayout.EnumPopup(new GUIContent("Move all objects to"), moveAllTarget);
            using (new EditorGUI.DisabledScope(moveAllTarget != MoveAllTarget.WorldCoordinate))
            {
                worldMoveTarget = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), worldMoveTarget);
            }

            EditorGUILayout.EndVertical();

            // Rebuild / Shrub
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Rebuild Instanced Collision", hdr);
            rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Enable"), rebuildInstancedCollision);

            GUILayout.Space(2);
            convertToShrub = EditorGUILayout.Toggle(new GUIContent("Convert to Shrub"), convertToShrub);
            using (new EditorGUI.DisabledScope(!convertToShrub))
            {
                shrubRenderDistance = EditorGUILayout.IntField(new GUIContent("Shrub Render Distance"), Mathf.Max(1, shrubRenderDistance));
            }
            EditorGUILayout.EndVertical();
        }

        // ---------------- Viewer ----------------
        private void DrawViewer()
        {
            var rect = GUILayoutUtility.GetRect(10, 10, 380, 380, GUILayout.ExpandWidth(true));
            RefreshPreviewAsset();

            bool ready = HasMinimumPrefix() && desiredAsset && CountPlaceholders() > 0;

            if (Event.current.type == EventType.Repaint)
            {
                previewUtil.BeginPreview(rect, GUIStyle.none);

                var cam = previewUtil.camera;
                SetupCameraBackground(cam);
                var pivot = ComputePreviewPivot();
                var rot = Quaternion.Euler(previewPitch * (orbitInvertY ? -1f : 1f), previewYaw * (orbitInvertX ? -1f : 1f), 0f);
                cam.transform.position = pivot + rot * (Vector3.back * previewDistance) + previewPan;
                cam.transform.rotation = Quaternion.LookRotation((pivot + previewPan) - cam.transform.position, Vector3.up);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 5000f;

                if (ready)
                {
                    var mesh = previewMesh ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                    var mats = (previewMats != null && previewMats.Length > 0) ? previewMats : new[] { fallbackMat };

                    foreach (var go in GetPlaceholders())
                    {
                        if (!go) continue;
                        var r = ComputePreviewRotation(go.transform);
                        var s = ComputePreviewScale(go.transform);
                        var mtx = Matrix4x4.TRS(go.transform.position, r, s);
                        for (int si = 0; si < Mathf.Min(mesh.subMeshCount, mats.Length); si++)
                            previewUtil.DrawMesh(mesh, mtx, mats[si] ? mats[si] : fallbackMat, si);
                    }
                }

                cam.Render();
                var tex = previewUtil.EndPreview();
                // FIX: qualify to UnityEngine.ScaleMode
                GUI.DrawTexture(rect, tex, UnityEngine.ScaleMode.StretchToFill, false);
            }

            if (!ready)
            {
                EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.55f));
                var lab = new GUIStyle(EditorStyles.whiteLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = true, fontSize = 12 };
                GUI.Label(rect,
                    "Enter a prefix (≥ 3 chars) and choose a Desired Asset (Prefab) —\n" +
                    "or open the GameObject Library — to view preview.\n\n" +
                    "Tip: Use rotation/scale/location seeds & clamping to explore creative variations.",
                    lab);
            }

            if (rect.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    if (Event.current.button == 0) { previewYaw += Event.current.delta.x * 0.5f; previewPitch = Mathf.Clamp(previewPitch - Event.current.delta.y * 0.5f, -80, 80); Repaint(); }
                    else if (Event.current.button == 2 || (Event.current.button == 0 && Event.current.modifiers == EventModifiers.Shift))
                    {
                        float panScale = previewDistance * 0.0025f;
                        var right = Quaternion.Euler(0, previewYaw, 0) * Vector3.right;
                        var up = Vector3.up;
                        previewPan += (-right * Event.current.delta.x + up * Event.current.delta.y) * panScale;
                        Repaint();
                    }
                }
                if (Event.current.type == EventType.ScrollWheel)
                {
                    previewDistance = Mathf.Clamp(previewDistance * (1f + Event.current.delta.y * 0.04f), 0.3f, 2000f);
                    Repaint();
                }
            }
        }

        private void SetupCameraBackground(Camera cam)
        {
            if (bgMode == BgMode.CurrentSkybox)
            {
                if (RenderSettings.skybox) cam.clearFlags = CameraClearFlags.Skybox;
                else { cam.clearFlags = CameraClearFlags.Color; cam.backgroundColor = new Color(0.2f, 0.2f, 0.22f); }
            }
            else
            {
                if (proceduralSkybox)
                {
                    var prev = RenderSettings.skybox;
                    RenderSettings.skybox = proceduralSkybox;
                    cam.clearFlags = CameraClearFlags.Skybox;
                    RenderSettings.skybox = prev;
                }
                else { cam.clearFlags = CameraClearFlags.Color; cam.backgroundColor = new Color(0.3f, 0.45f, 0.8f); }
            }
        }

        private void RefreshPreviewAsset()
        {
            previewMesh = null; previewMats = null;
            var go = desiredAsset as GameObject;
            if (!go) return;
            var mf = go.GetComponentInChildren<MeshFilter>();
            var mr = go.GetComponentInChildren<MeshRenderer>();
            if (mf && mf.sharedMesh) previewMesh = mf.sharedMesh;
            if (mr && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) previewMats = mr.sharedMaterials;
        }

        private Vector3 ComputePreviewPivot()
        {
            var list = GetPlaceholders().ToList();
            if (list.Count == 0) return Vector3.zero;

            switch (pivotMode)
            {
                case PivotMode.FirstObject: return list[0] ? list[0].transform.position : Vector3.zero;
                case PivotMode.BoundsCenter:
                    var b = new Bounds(list[0].transform.position, Vector3.zero);
                    foreach (var go in list)
                    {
                        if (!go) continue;
                        var r = go.GetComponent<Renderer>();
                        if (r) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
                    }
                    return b.center;
                case PivotMode.WorldOrigin: return Vector3.zero;
                case PivotMode.SelectedObject: return Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero;
                case PivotMode.Parent:
                default:
                    if (explicitParent) return explicitParent.position;
                    return Vector3.zero;
            }
        }

        private Quaternion ComputePreviewRotation(Transform t)
        {
            switch (rotationMode)
            {
                case RotationMode.NewRotation: return Quaternion.Euler(rotationEuler);
                case RotationMode.SeedValueOnY:
                    int hash = (t.GetInstanceID() ^ (t.name.GetHashCode() << 1));
                    var rng = new System.Random(unchecked((rotationSeed * 73856093) ^ hash));
                    float y = (float)(rng.NextDouble() * 360.0);
                    return Quaternion.Euler(0f, y, 0f) * Quaternion.Euler(rotationEuler);
                default: return t.rotation * Quaternion.Euler(rotationEuler);
            }
        }

        private Vector3 ComputePreviewScale(Transform t)
        {
            if (scaleMode == ScaleMode.NewScale)
            {
                return new Vector3(scaleAffectX ? scaleUnified : 1f,
                                   scaleAffectY ? scaleUnified : 1f,
                                   scaleAffectZ ? scaleUnified : 1f);
            }
            if (scaleMode == ScaleMode.SeedValue)
            {
                int hash = (t.GetInstanceID() ^ (t.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                float rx = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxX - scaleClampMinX) + scaleClampMinX, scaleClampMinX, scaleClampMaxX);
                float ry = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxY - scaleClampMinY) + scaleClampMinY, scaleClampMinY, scaleClampMaxY);
                float rz = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxZ - scaleClampMinZ) + scaleClampMinZ, scaleClampMinZ, scaleClampMaxZ);
                if (!scaleAffectX) rx = 1f; if (!scaleAffectY) ry = 1f; if (!scaleAffectZ) rz = 1f;
                return new Vector3(rx, ry, rz);
            }
            var mul = new Vector3(scaleAffectX ? scaleUnified : 1f, scaleAffectY ? scaleUnified : 1f, scaleAffectZ ? scaleUnified : 1f);
            return Vector3.Scale(t.localScale, mul);
        }

        private void RecenterView()
        {
            previewYaw = -25f; previewPitch = 15f; previewPan = Vector3.zero; previewDistance = 3.0f; Repaint();
        }

        // ---------------- Save From Preview (single) ----------------
        private void SaveFromPreviewSingle()
        {
            if (!(desiredAsset is GameObject prefab))
            {
                EditorUtility.DisplayDialog("Pick a prefab", "Choose a Desired Asset (Prefab) first.", "OK");
                return;
            }
            if (!HasMinimumPrefix())
            {
                EditorUtility.DisplayDialog("Enter a prefix", "Enter at least 3 characters for Placeholder Prefix.", "OK");
                return;
            }

            var candidates = GetPlaceholders().OrderBy(g => g.name).ToList();
            if (candidates.Count != 1)
            {
                EditorUtility.DisplayDialog("Needs a single object", "This action is only available when exactly one placeholder is found.", "OK");
                return;
            }

            var suggested = System.IO.Path.GetFileName(prefab.name + "_Preview.prefab");
            var path = EditorUtility.SaveFilePanelInProject("Save From Preview As", suggested, "prefab", "Choose a location for the new prefab");
            if (string.IsNullOrEmpty(path)) return;

            var src = candidates[0];

            Undo.IncrementCurrentGroup();
            int grp = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Save From Preview");

            GameObject temp = null;
            try
            {
                temp = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (!temp) { EditorUtility.DisplayDialog("Error", "Failed to instantiate prefab.", "OK"); return; }
                temp.name = string.IsNullOrEmpty(forcedName) ? prefab.name : forcedName;

                var finalRot = ComputePreviewRotation(src.transform);
                var finalScale = ComputePreviewScale(src.transform);

                temp.transform.position = Vector3.zero;
                temp.transform.rotation = finalRot;
                temp.transform.localScale = finalScale;

                var saved = PrefabUtility.SaveAsPrefabAsset(temp, path);
                if (saved != null) Debug.Log($"Saved preview prefab: {path}");
                else EditorUtility.DisplayDialog("Failed", "Could not save prefab.", "OK");
            }
            finally
            {
                if (temp) DestroyImmediate(temp);
                Undo.CollapseUndoOperations(grp);
            }
        }

        // ---------------- Replace ----------------
        private bool CanSwitch() => HasMinimumPrefix() && desiredAsset && IsPrefabAsset(desiredAsset as GameObject);

        private void RunReplace()
        {
            if (!(desiredAsset is GameObject prefab))
            {
                EditorUtility.DisplayDialog("Pick a prefab", "Choose a Desired Asset (Prefab) first.", "OK");
                return;
            }
            var candidates = GetPlaceholders().OrderBy(go => go.name).ToList();
            if (candidates.Count == 0)
            {
                EditorUtility.DisplayDialog("No matches", $"No GameObjects starting with '{prefix}' were found.", "OK");
                return;
            }

            groupParentByScene.Clear();
            if (explicitParent == null && groupWithEmptyParent)
            {
                foreach (var scene in candidates.Select(c => c.scene).Distinct())
                {
                    if (!scene.IsValid() || !scene.isLoaded) continue;
                    var desiredPos = GetEmptyParentPositionForScene(candidates.Where(c => c.scene == scene).ToList(), emptyParentLocation, manualEmptyParentPosition);
                    var parent = FindOrCreateGroupParentInScene(scene, groupParentName, desiredPos);
                    groupParentByScene[scene] = parent;
                }
            }

            nameCounters.Clear();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Placeholder Switcher");

            var spawned = new List<GameObject>(candidates.Count);

            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    var src = candidates[i];
                    if (!src) continue;
                    if (EditorUtility.DisplayCancelableProgressBar("Switching Placeholders", $"Replacing {i + 1}/{candidates.Count}: {src.name}", (float)(i + 1) / candidates.Count))
                        break;

                    Transform groupingParent = explicitParent ? explicitParent : null;
                    if (groupingParent == null && groupWithEmptyParent)
                    {
                        if (groupParentByScene.TryGetValue(src.scene, out var gp) && gp) groupingParent = gp;
                    }

                    var inst = ReplaceOne(src, prefab, groupingParent);
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

            if (moveAllTarget != MoveAllTarget.None)
            {
                Vector3 target = Vector3.zero;
                switch (moveAllTarget)
                {
                    case MoveAllTarget.Parent: target = explicitParent ? explicitParent.position : Vector3.zero; break;
                    case MoveAllTarget.FirstObject: target = (spawned.Count > 0 && spawned[0]) ? spawned[0].transform.position : Vector3.zero; break;
                    case MoveAllTarget.BoundsCenter:
                        var b = new Bounds(spawned[0].transform.position, Vector3.zero);
                        foreach (var go in spawned) if (go) { var r = go.GetComponent<Renderer>(); if (r) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero)); }
                        target = b.center; break;
                    case MoveAllTarget.WorldOrigin: target = Vector3.zero; break;
                    case MoveAllTarget.WorldCoordinate: target = worldMoveTarget; break;
                    case MoveAllTarget.SelectedObject: target = Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero; break;
                }

                if (finalRoot) finalRoot.transform.position = target;
                else
                {
                    var center = GetWorldCenter(spawned);
                    var delta = target - center;
                    foreach (var go in spawned) if (go) go.transform.position += delta;
                }
            }

            if (convertToShrub)
            {
                var targetGO = finalRoot != null ? finalRoot : (spawned.Count == 1 ? spawned[0] : null);
                if (targetGO != null) TryConvertToShrub(targetGO, shrubRenderDistance);
            }

            if (rebuildInstancedCollision)
            {
                if (finalRoot) TryRebuildInstancedCollision(finalRoot);
                else foreach (var go in spawned) if (go) TryRebuildInstancedCollision(go);
            }

            Repaint();
        }

        private GameObject ReplaceOne(GameObject src, GameObject prefab, Transform groupingParent)
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

            var newParent = groupingParent ? groupingParent : parent;
            inst.transform.SetParent(newParent, false);

            Quaternion finalRot;
            switch (rotationMode)
            {
                default:
                case RotationMode.PlaceholderRotation: finalRot = localRot * Quaternion.Euler(rotationEuler); break;
                case RotationMode.NewRotation: finalRot = Quaternion.Euler(rotationEuler); break;
                case RotationMode.SeedValueOnY:
                    int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                    var rng = new System.Random(unchecked((rotationSeed * 73856093) ^ hash));
                    float y = (float)(rng.NextDouble() * 360.0);
                    finalRot = Quaternion.Euler(0f, y, 0f) * Quaternion.Euler(rotationEuler); break;
            }

            Vector3 finalScale;
            if (scaleMode == ScaleMode.NewScale)
            {
                finalScale = new Vector3(scaleAffectX ? scaleUnified : 1f, scaleAffectY ? scaleUnified : 1f, scaleAffectZ ? scaleUnified : 1f);
            }
            else if (scaleMode == ScaleMode.SeedValue)
            {
                int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                float rx = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxX - scaleClampMinX) + scaleClampMinX, scaleClampMinX, scaleClampMaxX);
                float ry = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxY - scaleClampMinY) + scaleClampMinY, scaleClampMinY, scaleClampMaxY);
                float rz = Mathf.Clamp((float)rng.NextDouble() * (scaleClampMaxZ - scaleClampMinZ) + scaleClampMinZ, scaleClampMinZ, scaleClampMaxZ);
                if (!scaleAffectX) rx = 1f; if (!scaleAffectY) ry = 1f; if (!scaleAffectZ) rz = 1f;
                finalScale = new Vector3(rx, ry, rz);
            }
            else
            {
                var mul = new Vector3(scaleAffectX ? scaleUnified : 1f, scaleAffectY ? scaleUnified : 1f, scaleAffectZ ? scaleUnified : 1f);
                finalScale = Vector3.Scale(localScale, mul);
            }

            Vector3 finalPos = localPos;
            if (useRandomLocationSeed)
            {
                int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((locationSeed * 83492791) ^ hash));
                float ox = Mathf.Clamp((float)rng.NextDouble() * (locClampMaxX - locClampMinX) + locClampMinX, locClampMinX, locClampMaxX);
                float oy = Mathf.Clamp((float)rng.NextDouble() * (locClampMaxY - locClampMinY) + locClampMinY, locClampMinY, locClampMaxY);
                float oz = Mathf.Clamp((float)rng.NextDouble() * (locClampMaxZ - locClampMinZ) + locClampMinZ, locClampMinZ, locClampMaxZ);
                if (!locAffectX) ox = 0f; if (!locAffectY) oy = 0f; if (!locAffectZ) oz = 0f;

                Vector3 seedOffset = new Vector3(ox, oy, oz);
                if (locationSpace == LocationSpace.Local) finalPos += seedOffset;
                else inst.transform.position = src.transform.position + seedOffset;
            }

            inst.layer = layer;
            try { inst.tag = tag; } catch { }
            GameObjectUtility.SetStaticEditorFlags(inst, staticFlags);
            inst.SetActive(active);

            if (locationSpace == LocationSpace.Local) inst.transform.localPosition = finalPos + locationOffset;
            else inst.transform.position = src.transform.position + locationOffset;

            inst.transform.localRotation = finalRot;
            inst.transform.localScale = finalScale;

            if (!string.IsNullOrEmpty(forcedName)) inst.name = ApplyIncremental(forcedName);
            else inst.name = ApplyIncremental(inst.name);

            Undo.DestroyObjectImmediate(src);
            return inst;
        }

        private string ApplyIncremental(string baseName)
        {
            if (!useIncremental) return baseName;
            if (!nameCounters.TryGetValue(baseName, out var n)) n = 0;
            nameCounters[baseName] = ++n;
            return $"{baseName}_{n:000}";
        }

        // ---------------- Helpers ----------------
        private IEnumerable<GameObject> GetPlaceholders()
        {
            if (!HasMinimumPrefix()) yield break;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (!t) continue;
                var go = t.gameObject;
                if (!go.scene.IsValid()) continue;
                if (go.name.StartsWith(prefix)) yield return go;
            }
        }
        private int CountPlaceholders() => HasMinimumPrefix() ? GetPlaceholders().Count() : 0;
        private bool HasMinimumPrefix() => !string.IsNullOrEmpty(prefix) && prefix.Length >= 3;

        private static bool IsPrefabAsset(GameObject go)
        {
            if (!go) return false;
            var t = PrefabUtility.GetPrefabAssetType(go);
            return t != PrefabAssetType.NotAPrefab && t != PrefabAssetType.MissingAsset;
        }

        private Transform GetGroupParentForScene(Scene scene) => groupParentByScene.TryGetValue(scene, out var t) ? t : null;

        private static Transform FindOrCreateGroupParentInScene(Scene scene, string parentName, Vector3 pos)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root && root.name == parentName) return root.transform;
            var go = new GameObject(parentName);
            go.transform.position = pos;
            Undo.RegisterCreatedObjectUndo(go, "Create Group Parent");
            SceneManager.MoveGameObjectToScene(go, scene);
            return go.transform;
        }

        private static Vector3 GetEmptyParentPositionForScene(List<GameObject> sceneCandidates, EmptyParentLocation loc, Vector3 manual)
        {
            if (loc == EmptyParentLocation.SelectedObject && Selection.activeTransform) return Selection.activeTransform.position;
            if (sceneCandidates == null || sceneCandidates.Count == 0) return loc == EmptyParentLocation.Manual ? manual : Vector3.zero;

            switch (loc)
            {
                case EmptyParentLocation.FirstObject: return sceneCandidates[0] ? sceneCandidates[0].transform.position : Vector3.zero;
                case EmptyParentLocation.BoundsCenter:
                    var b = new Bounds(sceneCandidates[0].transform.position, Vector3.zero);
                    foreach (var go in sceneCandidates)
                    {
                        if (!go) continue;
                        var r = go.GetComponent<Renderer>();
                        if (r) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
                    }
                    return b.center;
                case EmptyParentLocation.WorldOrigin: return Vector3.zero;
                case EmptyParentLocation.Manual: return manual;
                default: return Vector3.zero;
            }
        }

        private static Vector3 GetWorldCenter(List<GameObject> list)
        {
            if (list.Count == 0) return Vector3.zero;
            var b = new Bounds(list[0].transform.position, Vector3.zero);
            foreach (var go in list)
            {
                if (!go) continue;
                var r = go.GetComponent<Renderer>();
                if (r) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
            }
            return b.center;
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
                case PivotMode.Parent: pivotWS = explicitParent ? explicitParent.position : (groupParent ? groupParent.position : Vector3.zero); break;
                case PivotMode.FirstObject: pivotWS = filters[0].transform.position; break;
                case PivotMode.BoundsCenter:
                    var b = new Bounds(filters[0].transform.position, Vector3.zero);
                    foreach (var mf in filters) { var r = mf.GetComponent<Renderer>(); if (r) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(mf.transform.position, Vector3.zero)); }
                    pivotWS = b.center; break;
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
                var m = type.GetMethod("Rebuild", BindingFlags.Public | BindingFlags.Instance)
                        ?? type.GetMethod("Build", BindingFlags.Public | BindingFlags.Instance)
                        ?? type.GetMethod("Setup", BindingFlags.Public | BindingFlags.Instance);
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

        private static void TryConvertToShrub(GameObject go, int renderDistance)
        {
            var type = FindTypeByMonoScriptNames(new[] { "ConverterShrub", "ConvertToShrub" });
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                Debug.LogWarning("Convert to Shrub requested, but ConverterShrub script was not found.");
                return;
            }
            var comp = go.GetComponent(type) ?? Undo.AddComponent(go, type);

            var fi = type.GetField("RenderDistance", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                  ?? type.GetField("renderDistance", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi != null && fi.FieldType == typeof(int)) fi.SetValue(comp, renderDistance);
            var pi = type.GetProperty("RenderDistance", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (pi != null && pi.PropertyType == typeof(int) && pi.CanWrite) pi.SetValue(comp, renderDistance);

            var m = type.GetMethod("Convert", BindingFlags.Public | BindingFlags.Instance)
                 ?? type.GetMethod("Build", BindingFlags.Public | BindingFlags.Instance)
                 ?? type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance);
            if (m != null) { try { m.Invoke(comp, null); } catch { } }
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
                if (names.Contains(n)) { var t = ms.GetClass(); if (t != null) return t; }
            }
            foreach (var n in names) { var t = Type.GetType(n); if (t != null) return t; }
            return null;
        }

        // --------------- Library / Viewport / Randomize ---------------
        private void OpenGameObjectLibrary()
        {
            EditorGUIUtility.ShowObjectPicker<GameObject>(desiredAsset as GameObject, false, "", 0);
        }

        private void OpenViewport()
        {
            if (externalViewport == null)
            {
                externalViewport = ScriptableObject.CreateInstance<PlaceholderViewportWindow>();
                externalViewport.titleContent = new GUIContent("Model Viewport");
                externalViewport.owner = this;
                externalViewport.minSize = new Vector2(360, 280);
                externalViewport.ShowUtility();
                externalViewport.Focus();
                externalViewport.alwaysOnTop = true;
            }
        }

        private void CloseViewport()
        {
            if (externalViewport) { externalViewport.Close(); externalViewport = null; }
        }

        private void RandomizeAll()
        {
            rotationSeed = UnityEngine.Random.Range(1, int.MaxValue);
            scaleSeed = UnityEngine.Random.Range(1, int.MaxValue);
            locationSeed = UnityEngine.Random.Range(1, int.MaxValue);

            scaleClampMinX = 0.5f; scaleClampMaxX = 1.5f;
            scaleClampMinY = 0.5f; scaleClampMaxY = 1.5f;
            scaleClampMinZ = 0.5f; scaleClampMaxZ = 1.5f;

            locClampMinX = -2f; locClampMaxX = 2f;
            locClampMinY = -1f; locClampMaxY = 1f;
            locClampMinZ = -2f; locClampMaxZ = 2f;

            Repaint();
        }

        // NEW: implement missing method
        private void LoadAssetFromFile()
        {
            string[] filters = { "Unity Prefab", "prefab", "FBX", "fbx", "OBJ", "obj", "glTF", "gltf", "glb", "glb" };
            var path = EditorUtility.OpenFilePanelWithFilters("Load Model/Prefab (under Assets/)", Application.dataPath, filters);
            if (string.IsNullOrEmpty(path)) return;

            if (!path.StartsWith(Application.dataPath))
            {
                EditorUtility.DisplayDialog("Import first",
                    "Please place or import the file under your project's Assets/ folder, then try again.",
                    "OK");
                return;
            }

            var rel = "Assets" + path.Substring(Application.dataPath.Length);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(rel);
            if (go) desiredAsset = go;
            else EditorUtility.DisplayDialog("Not a prefab/model", "Could not load a GameObject at:\n" + rel, "OK");
            Repaint();
        }

        // Handle ObjectSelector updates + optional auto switch
        private void Update()
        {
            if (Event.current != null && Event.current.commandName == "ObjectSelectorUpdated")
            {
                var picked = EditorGUIUtility.GetObjectPickerObject();
                if (picked && picked is GameObject)
                {
                    desiredAsset = picked;
                    Repaint();
                    if (autoSwitchLive && CanSwitch()) RunReplace();
                }
            }
        }

        private static int SafeClampInt(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        // -------- External viewport --------
        public class PlaceholderViewportWindow : EditorWindow
        {
            public PlaceholderSwitcher owner;
            public bool alwaysOnTop;
            private void OnGUI()
            {
                if (!owner) { EditorGUILayout.HelpBox("Owner missing.", MessageType.Info); return; }
                var r = GUILayoutUtility.GetRect(10, 10, position.height - 10, position.height - 10, GUILayout.ExpandWidth(true));
                owner.DrawViewerIntoExternal(r);
            }
        }

        private void DrawViewerIntoExternal(Rect rect)
        {
            RefreshPreviewAsset();
            var cam = previewUtil.camera;
            SetupCameraBackground(cam);
            var pivot = ComputePreviewPivot();
            var rot = Quaternion.Euler(previewPitch * (orbitInvertY ? -1f : 1f), previewYaw * (orbitInvertX ? -1f : 1f), 0f);
            cam.transform.position = pivot + rot * (Vector3.back * previewDistance) + previewPan;
            cam.transform.rotation = Quaternion.LookRotation((pivot + previewPan) - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5000f;

            previewUtil.BeginPreview(rect, GUIStyle.none);

            var mesh = previewMesh ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var mats = (previewMats != null && previewMats.Length > 0) ? previewMats : new[] { fallbackMat };

            foreach (var go in GetPlaceholders())
            {
                if (!go) continue;
                var r = ComputePreviewRotation(go.transform);
                var s = ComputePreviewScale(go.transform);
                var mtx = Matrix4x4.TRS(go.transform.position, r, s);
                for (int si = 0; si < Mathf.Min(mesh.subMeshCount, mats.Length); si++)
                    previewUtil.DrawMesh(mesh, mtx, mats[si] ? mats[si] : fallbackMat, si);
            }

            cam.Render();
            var tex = previewUtil.EndPreview();
            GUI.DrawTexture(rect, tex, UnityEngine.ScaleMode.StretchToFill, false);
        }
    }
}
#endif
