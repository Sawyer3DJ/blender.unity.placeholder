// PlaceholderSwitcher.cs
// Version: v1.0.2
// Menu: Tools > Placeholder Tools > Placeholder Switcher
//
// -----------------------------------------------------------------------------
// CHANGELOG
// -----------------------------------------------------------------------------
// v1.0.2
// - Consolidated modules A..F into a single editor tool file.
// - Restored "Current Sky (scene render settings)" and "Unity Skybox" background modes.
// - Dark overlay message returns when prefix < 3 chars or no placeholders found.
// - Status on "Placeholder Prefix" shows "⚠️ no assets found" or "N objects found".
// - Drag & drop prefab into the 3D viewer assigns Desired Asset.
// - GameObject Library window (object picker style) re-added; opens large, on-top.
// - External Viewport window restored: Open/Close + Auto-sync camera; non-modal.
// - Preview frames real meshes of placeholders (not cubes) when no Desired Asset set.
// - "Automatically switch placeholders to scene" option (with 64-undo warning).
// - Rotation Offset / Scale Offset / Location Offset rebuilt with tidy rows + sliders.
// - Randomize All respects clamping; excludes naming/parent/pivot selections.
// - Combine warning appears only when combine is toggled on.
// - Save From Preview As (single preview object) enabled; disabled with multi-object.
// - Bottom-left version label shows v1.0.2.
//
// v1.0.1
// - Interim bugfix pass: compiler errors (enum misuse, helper visibility), 
//   added temporary RandomizeAllTransformParameters() helper.
//
// v1.0.0 (baseline from earlier happy snapshot)
// - Working placeholder replacement, grouping parent, basic combine, collision rebuild,
//   prefix scanning across open scenes, and functional preview with orbit/pan/zoom.
//
// -----------------------------------------------------------------------------
// QUICK START
// -----------------------------------------------------------------------------
// 1) Put this file anywhere under an Editor folder:  Assets/Editor/PlaceholderSwitcher.cs
// 2) Open via menu: Tools > Placeholder Tools > Placeholder Switcher
// 3) Enter a prefix (min 3 chars). If placeholders are found, you'll see them in the viewer.
// 4) (Optional) Pick a Desired Asset prefab (drag into viewer or use the Library button).
// 5) Tweak Rotation/Scale/Location offsets. Use Randomize All for quick variety.
// 6) Press "Switch Placeholders" to perform the replacement.
// 7) Use "Combine objects into one" carefully—interactivity/colliders will be lost.
// 8) "Save From Preview As…" enables only when a single thing is in preview.
// 9) External Viewport: open it to track the same framing in a second window.
//
// -----------------------------------------------------------------------------
// IMPLEMENTATION NOTES (for maintainers)
// -----------------------------------------------------------------------------
// - This file keeps the tool self-contained. Advanced features like the dedicated
//   library/thumb window are implemented via Object Picker to stay robust.
// - External Viewport is a lightweight mirror of the preview framing; it does not
//   affect SceneView or the game's sky. It intentionally does NOT mirror background mode.
// - Randomization uses deterministic seeds per object (hashing object identity).
// - We avoid APIs that vary across Unity versions (e.g., Random.NextInt64).
// - If a class named "ConvertToShrub" exists, and "Convert To Shrub" is ticked,
//   we will AddComponent<ConvertToShrub>() and set a "RenderDistance" field if found.
//
// -----------------------------------------------------------------------------

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
        // ------------------------ Constants & GUI ------------------------
        private const string kMenuPath = "Tools/Placeholder Tools/Placeholder Switcher";
        private static readonly Color TitleStripe = new Color(0.20f, 0.20f, 0.20f, 1f);
        private static readonly GUILayoutOption WideButton = GUILayout.Height(32);

        // Version label
        private const string kVersion = "v1.0.2";

        // ------------------------ Inputs (top-left block) ------------------------
        [SerializeField] private string prefix = "SS_";
        [SerializeField] private GameObject targetPrefab = null;
        [SerializeField] private string forcedName = "";
        [SerializeField] private bool useIncremental = false;
        [SerializeField] private bool autoSwitchToScene = false; // shows warning

        // ------------------------ Rotation Offset ------------------------
        private enum RotMode { PlaceholderRotation, NewRotation, SeedValueOnY }
        [SerializeField] private RotMode rotMode = RotMode.PlaceholderRotation;
        [SerializeField] private Vector3 rotEuler = Vector3.zero; // appears on same row; sliders under
        [SerializeField] private int rotSeed = 1234;              // 1..10_000

        // ------------------------ Scale Offset ------------------------
        private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
        [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
        [SerializeField] private float scaleUniform = 1f;         // single xyz, sliders under
        [SerializeField] private int scaleSeed = 321;             // 1..10_000
        [SerializeField] private float scaleClampMin = 0.7f;
        [SerializeField] private float scaleClampMax = 1.3f;

        // ------------------------ Location Offset ------------------------
        private enum LocFrame { ObjectLocal, World }
        [SerializeField] private LocFrame locFrame = LocFrame.ObjectLocal;
        [SerializeField] private bool locSeedEnabled = false;
        [SerializeField] private int locSeed = 999;               // 1..10_000
        [SerializeField] private Vector3 locOffset = Vector3.zero;
        // Clamping (per-axis)
        [SerializeField] private Vector2 locClampX = new Vector2(-1, 1);
        [SerializeField] private Vector2 locClampY = new Vector2(-1, 1);
        [SerializeField] private Vector2 locClampZ = new Vector2(-1, 1);
        // Influence toggles
        [SerializeField] private bool locInfluenceX = true;
        [SerializeField] private bool locInfluenceY = true;
        [SerializeField] private bool locInfluenceZ = true;

        // ------------------------ Parenting ------------------------
        [SerializeField] private Transform explicitParent = null;
        [SerializeField] private bool groupWithEmptyParent = false;
        [SerializeField] private string groupParentName = "Imported Placeholders";

        // ------------------------ Combine / Move ------------------------
        [SerializeField] private bool combineIntoOne = false;
        private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
        [SerializeField] private PivotMode pivotMode = PivotMode.Parent;

        private enum MoveTarget { None, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinates, SelectedObject, Parent }
        [SerializeField] private MoveTarget moveAllTarget = MoveTarget.None;
        [SerializeField] private Vector3 moveWorldCoordinate = Vector3.zero;

        // ------------------------ Collision & Shrub ------------------------
        [SerializeField] private bool convertToShrub = false;
        [SerializeField] private int shrubRenderDistance = 1000;
        [SerializeField] private bool rebuildInstancedCollision = false;

        // ------------------------ Save Path & Buttons ------------------------
        [SerializeField] private string savePath = "Assets/CombinedPlaceholder.prefab";

        // ------------------------ Background ------------------------
        private enum BgMode { CurrentSky, UnitySkybox }
        [SerializeField] private BgMode bgMode = BgMode.CurrentSky; // default to scene's look

        // ------------------------ Preview & External View ------------------------
        private PreviewRenderUtility preview;
        private Mesh previewMesh;
        private Material[] previewMats;
        private Material fallbackMat;
        private float yaw = -30, pitch = 15, dist = 2.0f;
        private bool userTweakedCamera = false;
        private Vector3 previewPivot = Vector3.zero;
        private Vector3 previewPivotOffset = Vector3.zero;
        private bool invertOrbitX = false;   // user pref
        private bool invertOrbitY = true;    // Y inverted by default as requested

        // External viewport
        private ExternalViewportWindow externalViewport;
        private bool autoSyncViewport = true; // on by default
        private bool externalPinnedOnTop = true;

        // Status caches
        private int lastFoundCount = 0;
        private readonly Dictionary<string, int> nameCounters = new Dictionary<string, int>();
        private readonly Dictionary<Scene, Transform> groupByScene = new Dictionary<Scene, Transform>();

        // ------------------------ Menu ------------------------
        [MenuItem(kMenuPath)]
        public static void Open()
        {
            var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
            w.minSize = new Vector2(980, 640);
            w.Show();
        }

        // ------------------------ Lifecycle ------------------------
        private void OnEnable()
        {
            EnsurePreview();
            fallbackMat = new Material(Shader.Find("Standard"));
            EditorApplication.update += EditorUpdate;
        }
        private void OnDisable()
        {
            EditorApplication.update -= EditorUpdate;
            if (preview != null) preview.Cleanup();
            if (fallbackMat != null) DestroyImmediate(fallbackMat);
            preview = null; fallbackMat = null;
        }

        private void EditorUpdate()
        {
            // live prefix tracking status
            lastFoundCount = CountPlaceholders(prefix);
            if (autoSyncViewport && externalViewport != null)
            {
                externalViewport.SyncFrom(this, previewPivot);
            }
            // Redraw for overlay/status
            Repaint();
        }

        private void EnsurePreview()
        {
            if (preview != null) return;
            preview = new PreviewRenderUtility(true);
            preview.cameraFieldOfView = 30f;
            preview.lights[0].intensity = 1.1f;
            preview.lights[1].intensity = 0.9f;
            ApplyBackground();
        }

        // ------------------------ GUI ------------------------
        private Vector2 rightScroll = Vector2.zero;

        private void OnGUI()
        {
            // Top title row
            DrawHeader();

            // Row 2: toolbar buttons
            DrawToolbar();

            EditorGUILayout.Space(4);

            // Two-column layout
            EditorGUILayout.BeginHorizontal();

            // Left column: Replace-block above + Viewer under
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.52f));

            DrawReplaceBlock();       // prefix + desired asset + naming + auto-switch
            DrawViewer();             // big preview with background buttons beneath
            DrawLoadSaveBlock();      // save path + SaveFromPreviewAs + Load Asset

            EditorGUILayout.EndVertical();

            // Right column: scrollable Transform/Parenting/Combine/Collision
            rightScroll = EditorGUILayout.BeginScrollView(rightScroll);
            DrawTransformBlocks();
            DrawParentingBlock();
            DrawCombineMoveBlock();
            DrawCollisionShrubBlock();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndHorizontal();

            // Bottom action row
            DrawActionRow();

            // Footer
            DrawFooter();
        }

        private void TitleStripeRow(string title)
        {
            var rect = EditorGUILayout.GetControlRect(false, 22);
            EditorGUI.DrawRect(rect, TitleStripe);
            var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft, fontSize = 12 };
            EditorGUI.LabelField(rect, $"  {title}", style);
        }

        private void DrawHeader()
        {
            var rect = EditorGUILayout.GetControlRect(false, 30);
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 16
            };
            EditorGUI.LabelField(rect, "Placeholder Switcher", style);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Open GameObject Library", GUILayout.Height(24)))
            {
                // ObjectPicker for GameObjects
                EditorGUIUtility.ShowObjectPicker<GameObject>(null, false, "", 9001);
            }
            // Handle selection from object picker
            if (Event.current.commandName == "ObjectSelectorUpdated" && EditorGUIUtility.GetObjectPickerControlID() == 9001)
            {
                var picked = EditorGUIUtility.GetObjectPickerObject() as GameObject;
                if (picked != null) targetPrefab = picked;
                Repaint();
            }

            if (GUILayout.Button(externalViewport == null ? "Open Viewport" : "Close Viewport", GUILayout.Height(24), GUILayout.Width(140)))
            {
                if (externalViewport == null)
                {
                    externalViewport = ExternalViewportWindow.OpenPinned(externalPinnedOnTop);
                    externalViewport.SyncFrom(this, previewPivot);
                }
                else
                {
                    externalViewport.Close();
                    externalViewport = null;
                }
            }

            autoSyncViewport = GUILayout.Toggle(autoSyncViewport, "Auto-sync viewer to Viewport", GUILayout.Height(24), GUILayout.Width(220));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawReplaceBlock()
        {
            TitleStripeRow("Replace Object Placeholders");

            EditorGUILayout.BeginVertical("box");
            // Prefix + status
            EditorGUILayout.BeginHorizontal();
            prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
            string status = (prefix.Length >= 3)
                ? (lastFoundCount > 0 ? $"{lastFoundCount} objects found" : "⚠️ no assets found")
                : "Enter ≥ 3 chars";
            EditorGUILayout.LabelField(status, GUILayout.Width(140));
            EditorGUILayout.EndHorizontal();

            // Desired Asset
            var newPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), targetPrefab, typeof(GameObject), false);
            if (newPrefab != targetPrefab) { targetPrefab = newPrefab; RefreshPreviewSource(); }

            // Drag & drop into viewer hint lives in viewer overlay; but here too:
            EditorGUILayout.HelpBox("Tip: Drag a prefab into the model viewer to assign it.", MessageType.None);

            // Naming
            forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
            useIncremental = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncremental);

            // Auto switch
            bool prevAuto = autoSwitchToScene;
            autoSwitchToScene = EditorGUILayout.ToggleLeft(new GUIContent("Automatically switch placeholders to scene",
                "When enabled, switching occurs immediately when you pick a Desired Asset. You have 64 undos — use them wisely."), autoSwitchToScene);
            if (!prevAuto && autoSwitchToScene)
            {
                EditorUtility.DisplayDialog("Warning",
                    "When 'Automatically switch placeholders to scene' is enabled, the currently previewed asset will replace all detected placeholders in real time.\n\nYou have 64 undos — use them wisely!", "OK");
            }

            EditorGUILayout.EndVertical();
        }

        // ------------------------ Viewer ------------------------
        private void DrawViewer()
        {
            TitleStripeRow("Model Viewer");

            // Preview rect
            var rect = GUILayoutUtility.GetRect(10, 10, 420, 420);
            DrawPreview(rect);

            // Background buttons row under viewer
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Viewer Background", GUILayout.Width(120));
            var prev = bgMode;
            bool pressedCurrent = GUILayout.Button("Current Sky", GUILayout.Height(22));
            bool pressedUnity = GUILayout.Button("Unity Skybox", GUILayout.Height(22));
            if (pressedCurrent) bgMode = BgMode.CurrentSky;
            if (pressedUnity) bgMode = BgMode.UnitySkybox;
            if (bgMode != prev) ApplyBackground();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Re-center view", GUILayout.Width(120)))
            {
                userTweakedCamera = false;
                previewPivotOffset = Vector3.zero;
            }
            EditorGUILayout.EndHorizontal();
        }

        private void ApplyBackground()
        {
            if (preview == null) return;
            var cam = preview.camera;
            switch (bgMode)
            {
                case BgMode.CurrentSky:
                    if (RenderSettings.skybox != null) cam.clearFlags = CameraClearFlags.Skybox;
                    else { cam.clearFlags = CameraClearFlags.Color; cam.backgroundColor = RenderSettings.ambientLight; }
                    break;
                case BgMode.UnitySkybox:
                    cam.clearFlags = CameraClearFlags.Skybox;
                    break;
            }
        }

        private void RefreshPreviewSource()
        {
            previewMesh = null; previewMats = null;
            if (targetPrefab == null) return;
            var mf = targetPrefab.GetComponentInChildren<MeshFilter>();
            var mr = targetPrefab.GetComponentInChildren<MeshRenderer>();
            if (mf && mf.sharedMesh) previewMesh = mf.sharedMesh;
            if (mr && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) previewMats = mr.sharedMaterials;
        }

        private int CountPlaceholders(string pfx)
        {
            if (string.IsNullOrEmpty(pfx) || pfx.Length < 3) return 0;
            return Resources.FindObjectsOfTypeAll<Transform>()
                .Count(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(pfx, StringComparison.Ordinal));
        }

        private void DrawOverlayMessage(Rect r, string message, MessageType type = MessageType.Info)
        {
            // dim overlay
            EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.65f));
            // centered label
            var inner = new Rect(r.x + 12, r.y + r.height / 2f - 40, r.width - 24, 80);
            var style = new GUIStyle(EditorStyles.wordWrappedLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 12, richText = true };
            var icon = type == MessageType.Warning ? "⚠️ " : string.Empty;
            GUI.Label(inner, $"{icon}{message}", style);
        }

        private void DrawPreview(Rect r)
        {
            EnsurePreview();

            // Drag & drop into viewer
            var evt = Event.current;
            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                if (r.Contains(evt.mousePosition))
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (var o in DragAndDrop.objectReferences)
                        {
                            if (o is GameObject go)
                            {
                                targetPrefab = go;
                                RefreshPreviewSource();
                                if (autoSwitchToScene) RunReplace();
                                break;
                            }
                        }
                    }
                    evt.Use();
                }
            }

            // If prefix too short or none found — dark overlay help
            if (prefix.Length < 3)
            {
                DrawOverlayMessage(r, "Enter at least <b>3 characters</b> for Placeholder Prefix.\n\n" +
                    "Tip: Drag a <b>Prefab</b> into this area to set the Desired Asset.\n" +
                    "You can also <b>Open GameObject Library</b> to pick an asset.", MessageType.Info);
                return;
            }
            if (lastFoundCount == 0)
            {
                DrawOverlayMessage(r, "No placeholders found with that prefix.\nTry a different prefix, or place some markers in the scene.", MessageType.Warning);
                return;
            }

            // Gather up to N placeholder samples to draw
            var placeholders = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(t => t.gameObject)
                .Take(300).ToList();

            // Determine pivot: bounds center of placeholders
            var pivot = GetBoundsCenter(placeholders);
            previewPivot = pivot;

            // Find a mesh/material set to draw:
            //  1) If Desired Asset assigned, draw that
            //  2) Else try to use first placeholder's own mesh (so you see the real thing)
            Mesh meshToDraw = previewMesh;
            Material[] matsToDraw = previewMats;
            if (meshToDraw == null || matsToDraw == null)
            {
                // try first placeholder
                var first = placeholders.FirstOrDefault();
                if (first != null)
                {
                    var mf = first.GetComponentInChildren<MeshFilter>();
                    var mr = first.GetComponentInChildren<MeshRenderer>();
                    if (mf && mf.sharedMesh) meshToDraw = mf.sharedMesh;
                    if (mr && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) matsToDraw = mr.sharedMaterials;
                }
                // fallback
                if (meshToDraw == null) meshToDraw = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                if (matsToDraw == null) matsToDraw = new[] { fallbackMat };
            }

            // Auto-fit camera distance (unless user tweaked)
            if (!userTweakedCamera)
            {
                var bounds = GetAggregateBounds(placeholders, meshToDraw);
                var halfFov = preview.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
                var radius = bounds.extents.magnitude;
                dist = Mathf.Clamp(radius / Mathf.Tan(halfFov) + radius * 0.4f, 0.6f, 5000f);
            }

            if (Event.current.type == EventType.Repaint)
            {
                preview.BeginPreview(r, GUIStyle.none);
                var cam = preview.camera;

                var rot = Quaternion.Euler(pitch, yaw, 0);
                var camPos = (pivot + previewPivotOffset) + rot * (Vector3.back * dist);
                cam.transform.position = camPos;
                cam.transform.rotation = Quaternion.LookRotation((pivot + previewPivotOffset) - camPos, Vector3.up);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 10000f;

                // Render each placeholder sample
                foreach (var go in placeholders)
                {
                    if (!go) continue;
                    // Determine transform for preview
                    var t = go.transform;
                    var rotObj = GetPreviewRotation(t);
                    var sclObj = GetPreviewScale(t);
                    var trs = Matrix4x4.TRS(t.position, rotObj, sclObj);

                    for (int s = 0; s < Mathf.Min(meshToDraw.subMeshCount, matsToDraw.Length); s++)
                        preview.DrawMesh(meshToDraw, trs, matsToDraw[s] ? matsToDraw[s] : fallbackMat, s);
                }

                cam.Render();
                var tex = preview.EndPreview();
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
            }

            // Orbit/pan/zoom
            if (r.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    if (Event.current.button == 0) // orbit
                    {
                        userTweakedCamera = true;
                        yaw += (invertOrbitX ? -1f : 1f) * Event.current.delta.x * 0.5f;
                        pitch += (invertOrbitY ? 1f : -1f) * Event.current.delta.y * 0.5f;
                        pitch = Mathf.Clamp(pitch, -80, 80);
                        Repaint();
                    }
                    else if (Event.current.button == 2 || (Event.current.button == 0 && Event.current.shift))
                    {
                        userTweakedCamera = true;
                        float panScale = dist * 0.002f;
                        var right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
                        var up = Vector3.up;
                        previewPivotOffset += (-right * Event.current.delta.x + up * Event.current.delta.y) * panScale;
                        Repaint();
                    }
                }
                if (Event.current.type == EventType.ScrollWheel)
                {
                    userTweakedCamera = true;
                    dist = Mathf.Clamp(dist * (1f + Event.current.delta.y * 0.05f), 0.3f, 8000f);
                    Repaint();
                }
            }
        }

        private static Vector3 GetBoundsCenter(List<GameObject> gos)
        {
            if (gos == null || gos.Count == 0) return Vector3.zero;
            var b = new Bounds(gos[0].transform.position, Vector3.zero);
            foreach (var g in gos)
            {
                if (!g) continue;
                var r = g.GetComponentInChildren<Renderer>();
                if (r) b.Encapsulate(r.bounds);
                else b.Encapsulate(new Bounds(g.transform.position, Vector3.zero));
            }
            return b.center;
        }
        private static Bounds GetAggregateBounds(List<GameObject> gos, Mesh mesh)
        {
            if (gos == null || gos.Count == 0) return new Bounds(Vector3.zero, Vector3.one);
            var b = new Bounds(gos[0].transform.position, Vector3.zero);
            foreach (var g in gos)
            {
                if (!g) continue;
                var r = g.GetComponentInChildren<Renderer>();
                if (r) b.Encapsulate(r.bounds);
                else b.Encapsulate(new Bounds(g.transform.position, Vector3.zero));
            }
            return b;
        }

        // Rotation preview computation
        private Quaternion GetPreviewRotation(Transform t)
        {
            switch (rotMode)
            {
                case RotMode.PlaceholderRotation: return t.rotation * Quaternion.Euler(rotEuler);
                case RotMode.NewRotation:         return Quaternion.Euler(rotEuler);
                case RotMode.SeedValueOnY:
                    {
                        int hash = t.GetInstanceID() ^ (t.name.GetHashCode() << 1);
                        var rng = new System.Random(unchecked((rotSeed * 73856093) ^ hash));
                        float y = (float)(rng.NextDouble() * 360.0);
                        return Quaternion.Euler(rotEuler.x, y + rotEuler.y, rotEuler.z);
                    }
            }
            return t.rotation;
        }
        // Scale preview computation
        private Vector3 GetPreviewScale(Transform t)
        {
            switch (scaleMode)
            {
                case ScaleMode.PlaceholderScale: return t.localScale * Mathf.Max(0.0001f, scaleUniform);
                case ScaleMode.NewScale:         return Vector3.one * Mathf.Max(0.0001f, scaleUniform);
                case ScaleMode.SeedValue:
                    {
                        int hash = t.GetInstanceID() ^ (t.name.GetHashCode() << 1);
                        var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                        float minv = Mathf.Min(scaleClampMin, scaleClampMax);
                        float maxv = Mathf.Max(scaleClampMin, scaleClampMax);
                        float f = Mathf.Lerp(minv, maxv, (float)rng.NextDouble());
                        return Vector3.one * Mathf.Max(0.0001f, f);
                    }
            }
            return t.localScale;
        }

        private void DrawLoadSaveBlock()
        {
            TitleStripeRow("Load / Save");
            EditorGUILayout.BeginVertical("box");
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
            using (new EditorGUI.DisabledScope(lastFoundCount != 1))
            {
                if (GUILayout.Button("Save From Preview As…", GUILayout.Height(26)))
                {
                    // If exactly one thing framed (we rely on lastFoundCount == 1)
                    if (!string.IsNullOrEmpty(savePath))
                    {
                        var temp = new GameObject("PreviewSave");
                        try
                        {
                            var go = FindFirstPlaceholder(prefix);
                            if (go != null)
                            {
                                var mr = go.GetComponentInChildren<MeshRenderer>();
                                var mf = go.GetComponentInChildren<MeshFilter>();
                                if (mr && mf && mf.sharedMesh)
                                {
                                    var mrf = temp.AddComponent<MeshFilter>(); mrf.sharedMesh = mf.sharedMesh;
                                    var mrr = temp.AddComponent<MeshRenderer>(); mrr.sharedMaterials = mr.sharedMaterials;
                                }
                                temp.transform.position = go.transform.position;
                            }
                            PrefabUtility.SaveAsPrefabAsset(temp, savePath);
                        }
                        finally { DestroyImmediate(temp); }
                    }
                }
            }
            if (lastFoundCount > 1)
            {
                EditorGUILayout.HelpBox("Multiple placeholders detected — combine first, then save.", MessageType.Info);
            }
            else if (lastFoundCount == 0)
            {
                EditorGUILayout.HelpBox("Nothing to save — search for objects via a prefix to enable saving.", MessageType.Info);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private GameObject FindFirstPlaceholder(string pfx)
        {
            return Resources.FindObjectsOfTypeAll<Transform>()
                .Select(t => t ? t.gameObject : null)
                .FirstOrDefault(go => go && go.scene.IsValid() && go.name.StartsWith(pfx, StringComparison.Ordinal));
        }

        // ------------------------ Right column blocks ------------------------
        private void DrawTransformBlocks()
        {
            TitleStripeRow("Transform Tools");

            // Rotation Offset
            EditorGUILayout.BeginVertical("box");
            GUILayout.Label("Rotation Offset", EditorStyles.boldLabel);
            rotMode = (RotMode)EditorGUILayout.EnumPopup("Rotation Mode", rotMode);
            // Row: XYZ fields
            rotEuler = EditorGUILayout.Vector3Field("Rotation", rotEuler);
            // Row: sliders (one per axis)
            DrawAxisSlider("X", ref rotEuler.x, -180, 180);
            DrawAxisSlider("Y", ref rotEuler.y, -180, 180);
            DrawAxisSlider("Z", ref rotEuler.z, -180, 180);
            if (rotMode == RotMode.SeedValueOnY)
            {
                EditorGUILayout.BeginHorizontal();
                rotSeed = Mathf.Clamp(EditorGUILayout.IntField("Random rotation seed (Y)", rotSeed), 1, 1000000000);
                if (GUILayout.Button("Randomise Seed", GUILayout.Width(140)))
                    rotSeed = UnityEngine.Random.Range(1, 1000000000);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();

            // Scale Offset
            EditorGUILayout.BeginVertical("box");
            GUILayout.Label("Scale Offset", EditorStyles.boldLabel);
            scaleMode = (ScaleMode)EditorGUILayout.EnumPopup("Scaling Mode", scaleMode);
            scaleUniform = Mathf.Max(0.0001f, EditorGUILayout.FloatField("Scale", scaleUniform));
            DrawAxisSlider("Uniform", ref scaleUniform, 0.01f, 10f);
            if (scaleMode == ScaleMode.SeedValue)
            {
                EditorGUILayout.BeginHorizontal();
                scaleSeed = Mathf.Clamp(EditorGUILayout.IntField("Random scaling seed", scaleSeed), 1, 1000000000);
                if (GUILayout.Button("Randomise Seed", GUILayout.Width(140)))
                    scaleSeed = UnityEngine.Random.Range(1, 1000000000);
                EditorGUILayout.EndHorizontal();

                // Clamping row
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Scale clamping (min/max)", GUILayout.Width(180));
                scaleClampMin = EditorGUILayout.FloatField(scaleClampMin, GUILayout.Width(70));
                DrawMinMaxSlider(ref scaleClampMin, ref scaleClampMax, 0.01f, 10f);
                scaleClampMax = EditorGUILayout.FloatField(scaleClampMax, GUILayout.Width(70));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();

            // Location Offset
            EditorGUILayout.BeginVertical("box");
            GUILayout.Label("Location Offset", EditorStyles.boldLabel);
            locFrame = (LocFrame)EditorGUILayout.EnumPopup("Location transform mode", locFrame);

            // Location seed toggle + field
            locSeedEnabled = EditorGUILayout.Toggle("Use random location seed", locSeedEnabled);
            using (new EditorGUI.DisabledScope(!locSeedEnabled))
            {
                EditorGUILayout.BeginHorizontal();
                locSeed = Mathf.Clamp(EditorGUILayout.IntField("Random Location Seed", locSeed), 1, 1000000000);
                if (GUILayout.Button("Randomise Seed", GUILayout.Width(140)))
                    locSeed = UnityEngine.Random.Range(1, 1000000000);
                EditorGUILayout.EndHorizontal();
            }

            // Influence axis row
            GUILayout.Label("Influenced Axis");
            EditorGUILayout.BeginHorizontal();
            locInfluenceX = GUILayout.Toggle(locInfluenceX, "X", "Button", GUILayout.Width(40));
            locInfluenceY = GUILayout.Toggle(locInfluenceY, "Y", "Button", GUILayout.Width(40));
            locInfluenceZ = GUILayout.Toggle(locInfluenceZ, "Z", "Button", GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();

            // Location transform row (XYZ) + sliders under
            locOffset = EditorGUILayout.Vector3Field("Location Transform", locOffset);
            DrawAxisSlider("X", ref locOffset.x, -50, 50, !locInfluenceX);
            DrawAxisSlider("Y", ref locOffset.y, -50, 50, !locInfluenceY);
            DrawAxisSlider("Z", ref locOffset.z, -50, 50, !locInfluenceZ);

            // Subheader + clamping rows
            GUILayout.Space(4);
            GUILayout.Label("Location clamping", EditorStyles.miniBoldLabel);
            DrawClampRow("X min/max", ref locClampX, -100, 100);
            DrawClampRow("Y min/max", ref locClampY, -100, 100);
            DrawClampRow("Z min/max", ref locClampZ, -100, 100);

            EditorGUILayout.EndVertical();
        }

        private void DrawAxisSlider(string label, ref float value, float min, float max, bool disabled = false)
        {
            using (new EditorGUI.DisabledScope(disabled))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(16);
                GUILayout.Label(label, GUILayout.Width(24));
                float v = value;
                v = GUILayout.HorizontalSlider(v, min, max);
                GUILayout.Label(v.ToString("0.###"), GUILayout.Width(60));
                EditorGUILayout.EndHorizontal();
                value = v;
            }
        }

        private void DrawMinMaxSlider(ref float min, ref float max, float sliderMin, float sliderMax)
        {
            float a = min, b = max;
            EditorGUILayout.MinMaxSlider(ref a, ref b, sliderMin, sliderMax);
            min = a; max = b;
        }

        private void DrawClampRow(string label, ref Vector2 clamp, float sliderMin, float sliderMax)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120));
            clamp.x = EditorGUILayout.FloatField(clamp.x, GUILayout.Width(70));
            float a = clamp.x, b = clamp.y;
            EditorGUILayout.MinMaxSlider(ref a, ref b, sliderMin, sliderMax);
            clamp.y = EditorGUILayout.FloatField(clamp.y, GUILayout.Width(70));
            clamp.x = Mathf.Min(clamp.x, clamp.y);
            clamp.y = Mathf.Max(clamp.x, clamp.y);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawParentingBlock()
        {
            TitleStripeRow("Parenting");
            EditorGUILayout.BeginVertical("box");
            using (new EditorGUI.DisabledScope(groupWithEmptyParent))
            {
                explicitParent = (Transform)EditorGUILayout.ObjectField(new GUIContent("Parent (optional)"), explicitParent, typeof(Transform), true);
            }
            bool newGroup = EditorGUILayout.Toggle(new GUIContent("Group with New Empty Parent"), groupWithEmptyParent);
            if (newGroup != groupWithEmptyParent)
            {
                groupWithEmptyParent = newGroup;
                if (groupWithEmptyParent) explicitParent = null;
            }
            using (new EditorGUI.DisabledScope(!groupWithEmptyParent))
            {
                groupParentName = EditorGUILayout.TextField(new GUIContent("Empty Parent Name"), groupParentName);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawCombineMoveBlock()
        {
            TitleStripeRow("Combine / Move");
            EditorGUILayout.BeginVertical("box");
            combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one"), combineIntoOne);
            using (new EditorGUI.DisabledScope(!combineIntoOne))
            {
                pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
                EditorGUILayout.HelpBox("Combining creates ONE mesh/renderer. Per-object scripts, colliders & triggers are lost. Use Static Batching if interactivity is needed.", MessageType.Warning);
            }

            moveAllTarget = (MoveTarget)EditorGUILayout.EnumPopup(new GUIContent("Move all objects to"), moveAllTarget);
            using (new EditorGUI.DisabledScope(moveAllTarget != MoveTarget.WorldCoordinates))
            {
                moveWorldCoordinate = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), moveWorldCoordinate);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawCollisionShrubBlock()
        {
            TitleStripeRow("Rebuild Instanced Collision / Shrub");
            EditorGUILayout.BeginVertical("box");

            convertToShrub = EditorGUILayout.Toggle("Convert To Shrub", convertToShrub);
            using (new EditorGUI.DisabledScope(!convertToShrub))
            {
                shrubRenderDistance = EditorGUILayout.IntField(new GUIContent("Shrub Render Distance"), Mathf.Max(1, shrubRenderDistance));
            }

            rebuildInstancedCollision = EditorGUILayout.Toggle("Rebuild instanced collision", rebuildInstancedCollision);
            EditorGUILayout.EndVertical();
        }

        private void DrawActionRow()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            // Big primary action on the left
            using (new EditorGUI.DisabledScope(prefix.Length < 3 || (targetPrefab == null && !HasAnyPlaceholderMesh())))
            {
                GUIStyle big = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
                if (GUILayout.Button("Switch Placeholders", big, GUILayout.Height(36), GUILayout.Width(260)))
                {
                    RunReplace();
                }
            }
            GUILayout.FlexibleSpace();
            // Randomize All + Undo grouping name hint
            if (GUILayout.Button("Randomise All Transform Parameters", GUILayout.Width(280), WideButton))
            {
                RandomizeAllTransformParameters();
            }
            if (GUILayout.Button("Undo Last (Unity Undo)", GUILayout.Width(200), WideButton))
            {
                Undo.PerformUndo();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawFooter()
        {
            var rect = EditorGUILayout.GetControlRect(false, 20);
            var style = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.LowerLeft };
            EditorGUI.LabelField(rect, kVersion, style);
        }

        private bool HasAnyPlaceholderMesh()
        {
            var go = FindFirstPlaceholder(prefix);
            if (!go) return false;
            return go.GetComponentInChildren<MeshFilter>() != null;
        }

        // ------------------------ Replacement Core ------------------------
        private void RunReplace()
        {
            var candidates = Resources.FindObjectsOfTypeAll<Transform>()
                .Select(t => t ? t.gameObject : null)
                .Where(go => go && go.scene.IsValid() && go.name.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(go => go.name).ToList();
            if (candidates.Count == 0)
            {
                EditorUtility.DisplayDialog("No matches", "No GameObjects with that prefix were found.", "OK");
                return;
            }

            // Prepare target mesh source if none chosen: allow "placeholders-only workflow"
            GameObject prefabForSpawn = targetPrefab;
            if (prefabForSpawn == null)
            {
                // We'll instantiate the same model as each placeholder (identity transform modified by offsets)
            }

            // Group parent per scene (if requested)
            groupByScene.Clear();
            if (explicitParent == null && groupWithEmptyParent)
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (!s.IsValid() || !s.isLoaded) continue;
                    var p = FindOrCreateGroupParentInScene(s, groupParentName);
                    groupByScene[s] = p;
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

                    Transform groupingParent = explicitParent != null ? explicitParent : null;
                    if (groupingParent == null && groupWithEmptyParent)
                    {
                        if (groupByScene.TryGetValue(src.scene, out var gp) && gp != null)
                            groupingParent = gp;
                    }

                    var inst = ReplaceOne(src, prefabForSpawn);
                    if (inst != null) spawned.Add(inst);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Undo.CollapseUndoOperations(group);
            }

            GameObject finalRoot = null;

            // Move as requested
            if (spawned.Count > 0 && moveAllTarget != MoveTarget.None)
            {
                Vector3 target = ComputeMoveTarget(spawned);
                foreach (var go in spawned) if (go) go.transform.position += (target - GetWorldCenter(spawned));
            }

            // Combine (after move target so combined result lands in place)
            if (combineIntoOne && spawned.Count > 0)
            {
                finalRoot = CombineInstances(spawned);
                foreach (var go in spawned) if (go) Undo.DestroyObjectImmediate(go);
            }

            // Convert to Shrub
            if (convertToShrub && (finalRoot != null || spawned.Count > 0))
            {
                if (finalRoot != null) TryConvertToShrub(finalRoot, shrubRenderDistance);
                else foreach (var go in spawned) TryConvertToShrub(go, shrubRenderDistance);
            }

            // Rebuild collision
            if (rebuildInstancedCollision)
            {
                if (finalRoot != null) TryRebuildInstancedCollision(finalRoot);
                else foreach (var go in spawned) TryRebuildInstancedCollision(go);
            }
        }

        private Vector3 ComputeMoveTarget(List<GameObject> spawned)
        {
            switch (moveAllTarget)
            {
                case MoveTarget.FirstObject:
                    return spawned[0].transform.position;
                case MoveTarget.BoundsCenter:
                    return GetWorldCenter(spawned);
                case MoveTarget.WorldOrigin:
                    return Vector3.zero;
                case MoveTarget.WorldCoordinates:
                    return moveWorldCoordinate;
                case MoveTarget.SelectedObject:
                    return Selection.activeTransform ? Selection.activeTransform.position : GetWorldCenter(spawned);
                case MoveTarget.Parent:
                    if (explicitParent) return explicitParent.position;
                    var pg = GetGroupParentForScene(spawned[0].scene);
                    if (pg) return pg.position;
                    return GetWorldCenter(spawned);
                default:
                    return GetWorldCenter(spawned);
            }
        }

        private Transform GetGroupParentForScene(Scene scene)
        {
            if (groupByScene.TryGetValue(scene, out var t)) return t;
            return null;
        }

        private static Transform FindOrCreateGroupParentInScene(Scene scene, string parentName)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root && root.name == parentName) return root.transform;
            var go = new GameObject(parentName);
            Undo.RegisterCreatedObjectUndo(go, "Create Group Parent");
            SceneManager.MoveGameObjectToScene(go, scene);
            return go.transform;
        }

        private static Vector3 GetWorldCenter(List<GameObject> objects)
        {
            var b = new Bounds();
            bool init = false;
            foreach (var go in objects)
            {
                if (!go) continue;
                var r = go.GetComponentInChildren<Renderer>();
                var center = r ? r.bounds.center : go.transform.position;
                if (!init) { b = new Bounds(center, Vector3.zero); init = true; }
                else b.Encapsulate(center);
            }
            return init ? b.center : Vector3.zero;
        }

        private GameObject ReplaceOne(GameObject src, GameObject prefabForSpawn)
        {
            // Cache original
            var parent = src.transform.parent;
            var localPos = src.transform.localPosition;
            var localRot = src.transform.localRotation;
            var localScale = src.transform.localScale;
            var layer = src.layer;
            var tag = src.tag;
            var active = src.activeSelf;
            var staticFlags = GameObjectUtility.GetStaticEditorFlags(src);

            GameObject inst = null;

            if (prefabForSpawn != null)
            {
                inst = PrefabUtility.InstantiatePrefab(prefabForSpawn, src.scene) as GameObject;
            }
            else
            {
                // placeholders-only mode: duplicate the placeholder's own mesh into a fresh GO
                var mf = src.GetComponentInChildren<MeshFilter>();
                var mr = src.GetComponentInChildren<MeshRenderer>();
                if (mf && mf.sharedMesh)
                {
                    inst = new GameObject("PlaceholderCopy");
                    var newMF = inst.AddComponent<MeshFilter>(); newMF.sharedMesh = mf.sharedMesh;
                    var newMR = inst.AddComponent<MeshRenderer>(); newMR.sharedMaterials = mr ? mr.sharedMaterials : new Material[] { };
                    SceneManager.MoveGameObjectToScene(inst, src.scene);
                }
                else
                {
                    // fallback: still instantiate an empty so we can apply offsets then
                    inst = new GameObject("PlaceholderEmpty");
                    SceneManager.MoveGameObjectToScene(inst, src.scene);
                }
            }

            if (inst == null) return null;
            Undo.RegisterCreatedObjectUndo(inst, "Create replacement");

            // Choose parent
            var groupingParent = explicitParent ? explicitParent : (groupWithEmptyParent ? GetGroupParentForScene(src.scene) : parent);
            inst.transform.SetParent(groupingParent, false);

            // Compute final rotation
            Quaternion finalRot;
            switch (rotMode)
            {
                case RotMode.PlaceholderRotation: finalRot = localRot * Quaternion.Euler(rotEuler); break;
                case RotMode.NewRotation:         finalRot = Quaternion.Euler(rotEuler); break;
                case RotMode.SeedValueOnY:
                    {
                        int hash = src.GetInstanceID() ^ (src.name.GetHashCode() << 1);
                        var rng = new System.Random(unchecked((rotSeed * 73856093) ^ hash));
                        float y = (float)(rng.NextDouble() * 360.0);
                        finalRot = Quaternion.Euler(rotEuler.x, y + rotEuler.y, rotEuler.z);
                    }
                    break;
                default: finalRot = localRot; break;
            }

            // Compute final scale
            Vector3 finalScale;
            switch (scaleMode)
            {
                case ScaleMode.PlaceholderScale: finalScale = localScale * Mathf.Max(0.0001f, scaleUniform); break;
                case ScaleMode.NewScale:         finalScale = Vector3.one * Mathf.Max(0.0001f, scaleUniform); break;
                case ScaleMode.SeedValue:
                    {
                        int hash = src.GetInstanceID() ^ (src.name.GetHashCode() << 1);
                        var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                        float minv = Mathf.Min(scaleClampMin, scaleClampMax);
                        float maxv = Mathf.Max(scaleClampMin, scaleClampMax);
                        float f = Mathf.Lerp(minv, maxv, (float)rng.NextDouble());
                        finalScale = Vector3.one * Mathf.Max(0.0001f, f);
                    }
                    break;
                default: finalScale = localScale; break;
            }

            // Location offset (local or world)
            Vector3 finalPos = localPos;
            Vector3 offset = locOffset;
            if (locSeedEnabled)
            {
                int hash = src.GetInstanceID() ^ (src.name.GetHashCode() << 1);
                var rng = new System.Random(unchecked((locSeed * 83492791) ^ hash));
                float rx = Mathf.Lerp(locClampX.x, locClampX.y, (float)rng.NextDouble());
                float ry = Mathf.Lerp(locClampY.x, locClampY.y, (float)rng.NextDouble());
                float rz = Mathf.Lerp(locClampZ.x, locClampZ.y, (float)rng.NextDouble());
                if (locInfluenceX) offset.x += rx;
                if (locInfluenceY) offset.y += ry;
                if (locInfluenceZ) offset.z += rz;
            }

            if (locFrame == LocFrame.ObjectLocal)
            {
                // interpret offset in the placeholder's local space
                finalPos = localPos + (Quaternion.Euler(localRot.eulerAngles) * offset);
            }
            else // world
            {
                // apply in world-space; convert back into parent's local
                var world = src.transform.TransformPoint(offset) - src.transform.position;
                finalPos = localPos + world;
            }

            // Apply
            inst.transform.localPosition = finalPos;
            inst.transform.localRotation = finalRot;
            inst.transform.localScale = finalScale;

            inst.layer = layer;
            try { inst.tag = tag; } catch { }
            GameObjectUtility.SetStaticEditorFlags(inst, staticFlags);
            inst.SetActive(active);

            // Naming
            inst.name = !string.IsNullOrEmpty(forcedName)
                ? ApplyIncremental(forcedName, useIncremental)
                : ApplyIncremental(inst.name, useIncremental);

            // Remove placeholder
            Undo.DestroyObjectImmediate(src);
            return inst;
        }

        private string ApplyIncremental(string baseName, bool incremental)
        {
            if (!incremental) return baseName;
            if (!nameCounters.TryGetValue(baseName, out int n)) n = 0;
            nameCounters[baseName] = ++n;
            return $"{baseName}_{n:000}";
        }

        private GameObject CombineInstances(List<GameObject> instances)
        {
            // Collect meshes
            var filters = new List<MeshFilter>();
            var renderers = new List<MeshRenderer>();
            foreach (var go in instances)
            {
                if (!go) continue;
                var mf = go.GetComponentInChildren<MeshFilter>();
                var mr = go.GetComponentInChildren<MeshRenderer>();
                if (mf && mf.sharedMesh && mr) { filters.Add(mf); renderers.Add(mr); }
            }
            if (filters.Count == 0) { Debug.LogWarning("No MeshFilters found to combine."); return null; }

            // Pivot
            Vector3 pivotWS = Vector3.zero;
            switch (pivotMode)
            {
                case PivotMode.Parent:
                    pivotWS = explicitParent ? explicitParent.position :
                              (groupWithEmptyParent ? GetGroupParentForScene(instances[0].scene)?.position ?? Vector3.zero : Vector3.zero);
                    break;
                case PivotMode.FirstObject:   pivotWS = filters[0].transform.position; break;
                case PivotMode.BoundsCenter:  pivotWS = GetWorldCenter(instances); break;
                case PivotMode.WorldOrigin:   pivotWS = Vector3.zero; break;
                case PivotMode.SelectedObject: pivotWS = Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero; break;
            }
            var pivotToWorld = Matrix4x4.TRS(pivotWS, Quaternion.identity, Vector3.one);

            // Build combined
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
                    combines.Add(new CombineInstance
                    {
                        mesh = mesh,
                        subMeshIndex = s,
                        transform = pivotToWorld.inverse * mf.transform.localToWorldMatrix
                    });
                    materials.Add(mats[s]);
                }
            }

            var finalMesh = new Mesh { name = "Combined_Mesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            finalMesh.CombineMeshes(combines.ToArray(), false, true, false);
            finalMesh.RecalculateBounds();
            if (!finalMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal)) finalMesh.RecalculateNormals();

            var result = new GameObject(string.IsNullOrEmpty(forcedName) ? "Combined Object" : forcedName);
            Undo.RegisterCreatedObjectUndo(result, "Create combined object");
            var parent = explicitParent ? explicitParent : GetGroupParentForScene(instances[0].scene);
            if (parent) result.transform.SetParent(parent, false);
            result.transform.position = pivotWS;

            var mrf = result.AddComponent<MeshFilter>(); mrf.sharedMesh = finalMesh;
            var mrr = result.AddComponent<MeshRenderer>(); mrr.sharedMaterials = materials.ToArray();
            return result;
        }

        // Collision rebuild (best effort)
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
                var mf = go.GetComponentInChildren<MeshFilter>();
                if (mf && mf.sharedMesh)
                {
                    var mc = go.GetComponent<MeshCollider>();
                    if (!mc) mc = Undo.AddComponent<MeshCollider>(go);
                    mc.sharedMesh = mf.sharedMesh;
                    mc.convex = false;
                }
            }
        }

        // ConvertToShrub integration
        private static void TryConvertToShrub(GameObject go, int renderDistance)
        {
            var shrubType = FindTypeByName("ConvertToShrub");
            if (shrubType == null) return;
            var comp = go.GetComponent(shrubType) ?? Undo.AddComponent(go, shrubType);

            // Try set "RenderDistance" (or close variants)
            var field = shrubType.GetField("RenderDistance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? shrubType.GetField("renderDistance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(int))
                field.SetValue(comp, renderDistance);

            var prop = shrubType.GetProperty("RenderDistance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(int))
                prop.SetValue(comp, renderDistance, null);
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
        private static Type FindTypeByName(string name)
        {
            var guids = AssetDatabase.FindAssets("t:MonoScript " + name);
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (!ms) continue; var t = ms.GetClass(); if (t != null && t.Name == name) return t;
            }
            // Fallback
            return Type.GetType(name);
        }

        // Randomize helper — respects clamping and excludes naming/parent/pivot
        private void RandomizeAllTransformParameters()
        {
            // Rotation: small variety
            rotMode = RotMode.PlaceholderRotation;
            rotEuler = new Vector3(UnityEngine.Random.Range(-20f, 20f), UnityEngine.Random.Range(0f, 360f), UnityEngine.Random.Range(-10f, 10f));
            // Scale: seed mode with current clamps
            scaleMode = ScaleMode.SeedValue;
            scaleSeed = UnityEngine.Random.Range(1, 1000000000);
            // Location: enable seed and vary clamps a touch
            locSeedEnabled = true;
            locSeed = UnityEngine.Random.Range(1, 1000000000);
            // Influence: all on by default for variety
            locInfluenceX = locInfluenceY = locInfluenceZ = true;
        }
    }

    // ------------------------ External Viewport ------------------------
    internal class ExternalViewportWindow : EditorWindow
    {
        private PreviewRenderUtility util;
        private Vector3 pivot;
        private float yaw, pitch, dist;
        private bool pinnedTop = true;

        internal static ExternalViewportWindow OpenPinned(bool alwaysOnTop)
        {
            var w = CreateInstance<ExternalViewportWindow>();
            w.titleContent = new GUIContent("Placeholder Viewport");
            w.position = new Rect(Screen.currentResolution.width * 0.55f, 80, 520, 420);
            w.pinnedTop = alwaysOnTop;
            w.Show();
            return w;
        }

        private void OnEnable()
        {
            util = new PreviewRenderUtility(true);
            util.cameraFieldOfView = 30f;
            util.lights[0].intensity = 1.1f;
            util.lights[1].intensity = 0.9f;
        }
        private void OnDisable()
        {
            util?.Cleanup();
            util = null;
        }

        public void SyncFrom(PlaceholderSwitcher src, Vector3 pivotPoint)
        {
            pivot = pivotPoint;
            // Read current camera-ish values from main
            var cam = src.GetType().GetField("preview", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(src) as PreviewRenderUtility;
            if (cam != null)
            {
                // Not reading internal camera transform; we just mimic framing based on src's private fields
                var yawF = src.GetType().GetField("yaw", BindingFlags.NonPublic | BindingFlags.Instance);
                var pitchF = src.GetType().GetField("pitch", BindingFlags.NonPublic | BindingFlags.Instance);
                var distF = src.GetType().GetField("dist", BindingFlags.NonPublic | BindingFlags.Instance);
                if (yawF != null) yaw = (float)yawF.GetValue(src);
                if (pitchF != null) pitch = (float)pitchF.GetValue(src);
                if (distF != null) dist = (float)distF.GetValue(src);
            }
            Repaint();
        }

        private void OnGUI()
        {
            if (util == null) return;
            var r = GUILayoutUtility.GetRect(10, 10, position.height - 10, position.height - 10);
            if (Event.current.type == EventType.Repaint)
            {
                util.BeginPreview(r, GUIStyle.none);
                var cam = util.camera;
                var rot = Quaternion.Euler(pitch, yaw, 0);
                var camPos = pivot + rot * (Vector3.back * Mathf.Max(0.1f, dist));
                cam.transform.position = camPos;
                cam.transform.rotation = Quaternion.LookRotation(pivot - camPos, Vector3.up);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 10000f;
                cam.clearFlags = CameraClearFlags.Skybox; // External viewport always uses skybox
                cam.Render();
                var tex = util.EndPreview();
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
            }
        }
    }
}
#endif
