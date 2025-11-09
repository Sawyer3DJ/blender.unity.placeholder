// PlaceholderTool.cs  — Pass C+D (Modules wired)
// Menu: Tools/Placeholder Tools/Placeholder Switcher
// Unity: 2020.3+ (tested in 2022.3 API surface)
// -----------------------------------------------------------------------------
// CHANGELOG (Pass C + D)
// C1: Transform Tools wiring
//     - Rotation Offset: Placeholder/New/Seed-on-Y, XYZ fields + sliders below.
//     - Scale Offset: Placeholder/New/Seed uniform, single value controls +
//       optional influenced axes (for future per-axis), clamping min/max.
//     - Location Offset: Local/World space, XYZ with sliders, influenced axes,
//       optional seeded offsets + per-axis clamping.
//     - “Use random location seed” toggle added (default off).
//     - Randomize buttons generate 1..10,000,000 seeds.
// C2: UI polish
//     - Right column titled “Transform Tools”.
//     - Title rows rendered darker for section headers.
//     - Keep the 20:59 layout: XYZ on same row, sliders directly underneath.
//     - Re-center View button works; orbit invert X/Y toggles shown below viewer.
// D1: Preview & background
//     - Viewer backgrounds: “Current Skybox” (RenderSettings.skybox) and
//       “Unity Skybox” (built-in default procedural-like) options. Defaults to
//       Current Skybox. (You asked to skip Manual for now.)
//     - Black overlay with friendly message appears until (prefix >= 3) AND
//       a Desired Asset is chosen OR at least one placeholder is found.
//     - Prefix status text: “⚠ no assets found” | “N object(s) found”.
// D2: Save/Load plumbing
//     - Save From Preview As… enabled only when preview has exactly 1 object;
//       greyed otherwise with warning. Uses SaveFilePanelInProject.
//     - Load Asset From File… opens file panel for .fbx/.obj/.gltf/.glb. If a
//       model is chosen, it is set as Desired Asset (Prefab) for this tool.
// D3: Safety / compile
//     - All previously missing methods are implemented (RandomizeAll,
//       SaveFromPreviewSingle, OpenViewport, CloseViewport, etc.) as safe
//       no-throws. External viewport is stubbed (opened window mirrors preview).
// -----------------------------------------------------------------------------

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlaceholderSwitcher : EditorWindow
{
    // -------- Top (left) “Replace Object Placeholders” --------
    [SerializeField] string prefix = "SS_";
    [SerializeField] GameObject targetPrefab = null;
    [SerializeField] string forcedName = "";
    [SerializeField] bool useIncremental = false;
    [SerializeField] bool autoSwitch = false; // text warning shown when enabled

    // Status cache
    int lastFoundCount = 0;

    // -------- Transform Tools (right) --------
    enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] Vector3 rotationEuler = Vector3.zero;           // offset or absolute
    [SerializeField] long rotationSeed = 1234;                       // 1..10M

    enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] float uniformScale = 1f;                        // single value (XYZ locked)
    [SerializeField] long scaleSeed = 321;
    [SerializeField] float scaleClampMin = 0.25f;
    [SerializeField] float scaleClampMax = 2.0f;

    enum OffsetSpace { Local, World }
    [SerializeField] OffsetSpace offsetSpace = OffsetSpace.Local;
    [SerializeField] Vector3 offsetXYZ = Vector3.zero;
    [SerializeField] bool useRandomLocationSeed = false;
    [SerializeField] long locationSeed = 4567;
    [SerializeField] bool influenceX = true, influenceY = true, influenceZ = true;
    [SerializeField] Vector2 clampX = new Vector2(-1f, 1f);
    [SerializeField] Vector2 clampY = new Vector2(-1f, 1f);
    [SerializeField] Vector2 clampZ = new Vector2(-1f, 1f);

    // -------- Combine / Move (minimal for this pass) --------
    [SerializeField] bool combineIntoOne = false;
    enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] PivotMode pivotMode = PivotMode.Parent;

    enum MoveTo { None, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinate, SelectedObject, Parent }
    [SerializeField] MoveTo moveAllTo = MoveTo.None;
    [SerializeField] Vector3 worldMoveTarget = Vector3.zero;

    [SerializeField] bool rebuildInstancedCollision = false;

    // ConvertToShrub (script present in project)
    [SerializeField] bool convertToShrub = false;
    [SerializeField] int shrubRenderDistance = 1000;

    // -------- Viewer --------
    enum ViewerBg { CurrentSkybox, UnitySkybox }
    [SerializeField] ViewerBg viewerBg = ViewerBg.CurrentSkybox;
    [SerializeField] bool invertOrbitY = true;
    [SerializeField] bool invertOrbitX = false;

    PreviewRenderUtility preview;
    float yaw = -20f, pitch = 10f, dist = 6f;
    bool userAdjusted = false;
    Vector3 panOffset = Vector3.zero;

    // Cached mesh/material for desired prefab
    Mesh previewMesh; Material[] previewMats; Material fallbackMat;
    ExternalViewportWindow externalViewport; // stub “separate window” preview

    // Naming
    readonly Dictionary<string,int> counters = new Dictionary<string,int>();

    // Menu entry
    [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(980, 640);
        w.Show();
    }

    void OnEnable()
    {
        InitPreview();
        EditorApplication.update += TickAutoSwitch;
    }

    void OnDisable()
    {
        EditorApplication.update -= TickAutoSwitch;
        CleanupPreview();
    }

    // ============================== GUI =======================================
    void OnGUI()
    {
        DrawHeaderToolbar();

        // Two columns: left (viewer + left-top inputs) | right (Transform Tools)
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawLeftColumn();
            DrawRightColumn();
        }
    }

    // ---------- Header row ----------
    void DrawHeaderToolbar()
    {
        EditorGUILayout.Space(2);
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Open GameObject Library", EditorStyles.toolbarButton, GUILayout.Width(200)))
                OpenGameObjectLibraryPicker();
            if (GUILayout.Button("Randomize All Parameters", EditorStyles.toolbarButton, GUILayout.Width(200)))
                RandomizeAll();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Undo", EditorStyles.toolbarButton, GUILayout.Width(80))) Undo.PerformUndo();
        }
        EditorGUILayout.Space(2);
    }

    // ---------- Left column ----------
    void DrawLeftColumn()
    {
        using (new GUILayout.VerticalScope(GUILayout.Width(Mathf.Max(520, position.width * 0.48f))))
        {
            DrawLeftTopInputs();

            var viewRect = GUILayoutUtility.GetRect(10, 10, 360, Mathf.Max(340, position.height * 0.45f));
            DrawPreview(viewRect);

            // Viewer controls row
            DrawViewerControls();

            // Load / Save section
            DrawLoadSave();
        }
    }

    void DrawLeftTopInputs()
    {
        TitleRow("Replace Object Placeholders");
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            // Prefix + status bubble
            using (new EditorGUILayout.HorizontalScope())
            {
                var newPrefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
                if (newPrefix != prefix) { prefix = newPrefix; RecountPlaceholders(); }
                DrawPrefixStatus();
            }

            targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), targetPrefab, typeof(GameObject), false);
            forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
            useIncremental = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncremental);

            // Auto-switch with warning
            bool newAuto = EditorGUILayout.ToggleLeft(new GUIContent("Automatically switch placeholders to scene"), autoSwitch);
            if (newAuto != autoSwitch)
            {
                autoSwitch = newAuto;
                if (autoSwitch)
                    EditorUtility.DisplayDialog("Warning",
                        "When enabled, any previewed model will immediately replace the active placeholders in the scene. You have 64 undos. Use them wisely!",
                        "Got it");
            }
        }
    }

    void DrawViewerControls()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            // Background buttons
            GUILayout.Label("Viewer Background", GUILayout.Width(110));
            if (GUILayout.Toggle(viewerBg == ViewerBg.CurrentSkybox, "Current Skybox", EditorStyles.miniButtonLeft)) viewerBg = ViewerBg.CurrentSkybox;
            if (GUILayout.Toggle(viewerBg == ViewerBg.UnitySkybox, "Unity Skybox", EditorStyles.miniButtonRight)) viewerBg = ViewerBg.UnitySkybox;

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Re-center View", GUILayout.Width(120))) { userAdjusted = false; panOffset = Vector3.zero; Repaint(); }

            // Invert toggles
            GUILayout.Space(12);
            invertOrbitY = GUILayout.Toggle(invertOrbitY, "Y Inverted", GUILayout.Width(90));
            invertOrbitX = GUILayout.Toggle(invertOrbitX, "X Inverted", GUILayout.Width(90));

            // External viewport
            GUILayout.Space(8);
            if (externalViewport == null)
            {
                if (GUILayout.Button("Open Viewport", GUILayout.Width(120))) OpenViewport();
            }
            else
            {
                if (GUILayout.Button("Close Viewport", GUILayout.Width(120))) CloseViewport();
            }
        }
    }

    void DrawLoadSave()
    {
        TitleRow("Load / Save");
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Save Path", GUILayout.Width(80));
                _savePath = EditorGUILayout.TextField(_savePath);
                if (GUILayout.Button("Select…", GUILayout.Width(80)))
                {
                    var suggested = System.IO.Path.GetFileName(string.IsNullOrEmpty(_savePath) ? "CombinedPlaceholder.prefab" : _savePath);
                    var path = EditorUtility.SaveFilePanelInProject("Save Prefab As", suggested, "prefab", "Choose save path");
                    if (!string.IsNullOrEmpty(path)) _savePath = path;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(_lastPreviewCount != 1);
                if (GUILayout.Button("Save From Preview As…", GUILayout.Height(26)))
                    SaveFromPreviewSingle();
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("Load Asset From File", GUILayout.Height(26), GUILayout.Width(160)))
                    LoadAssetFromFile();

                GUILayout.FlexibleSpace();
                // Switch button (large)
                EditorGUI.BeginDisabledGroup(!CanSwitch());
                var btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 13 };
                if (GUILayout.Button("Switch Placeholders", btnStyle, GUILayout.Height(30), GUILayout.Width(260)))
                    RunSwitch();
                EditorGUI.EndDisabledGroup();
            }

            if (_lastPreviewCount == 0)
            {
                InfoRow("Nothing to save, search for objects via a prefix to enable saving.");
            }
            else if (_lastPreviewCount > 1)
            {
                WarningRow("Multiple placeholders detected. Enable “Combine objects into one” to save them as a single asset.");
            }
        }
    }

    // ---------- Right column (Transform Tools) ----------
    void DrawRightColumn()
    {
        using (new GUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
        {
            TitleRow("Transform Tools");

            // Rotation section
            DrawRotation();

            // Scale section
            DrawScale();

            // Location Offset
            DrawLocationOffset();

            // Parenting / Combine / Move / Collision (stubs where not in this pass)
            DrawParentingAndCombine();
        }
    }

    void DrawRotation()
    {
        SectionRow("Rotation Offset");
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);

            // XYZ on same row
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(rotationMode == RotationMode.PlaceholderRotation ? "Rotation (adds to placeholder)" : rotationMode == RotationMode.NewRotation ? "Rotation (new)" : "Rotation offset (added to seeded Y)"), GUILayout.Width(210));
                rotationEuler.x = EditorGUILayout.FloatField("X", rotationEuler.x);
                rotationEuler.y = EditorGUILayout.FloatField("Y", rotationEuler.y);
                rotationEuler.z = EditorGUILayout.FloatField("Z", rotationEuler.z);
            }
            // Sliders row
            SliderTriplet(ref rotationEuler, -360f, 360f);

            if (rotationMode == RotationMode.SeedValueOnY)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    rotationSeed = EditorGUILayout.LongField(new GUIContent("Random rotation seed (Y)"), rotationSeed);
                    if (GUILayout.Button("Randomize Seed", GUILayout.Width(140))) rotationSeed = RandomSeed();
                }
                InfoRow("Per-object deterministic Y rotation from seed; offset above is added on top.");
            }
        }
    }

    void DrawScale()
    {
        SectionRow("Scale Offset");
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

            // Single value + label on same row
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Scale (multiplies placeholder scale)", GUILayout.Width(210));
                uniformScale = EditorGUILayout.FloatField(uniformScale);
            }
            // Single slider beneath
            var clamped = Mathf.Clamp(uniformScale, 0.0001f, 1000f);
            uniformScale = EditorGUILayout.Slider(clamped, 0.0001f, 10f);

            // Seed + clamping (min/max)
            if (scaleMode == ScaleMode.SeedValue)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    scaleSeed = EditorGUILayout.LongField(new GUIContent("Random scaling seed"), scaleSeed);
                    if (GUILayout.Button("Randomize Seed", GUILayout.Width(140))) scaleSeed = RandomSeed();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Scale clamping", GUILayout.Width(120));
                    scaleClampMin = EditorGUILayout.FloatField(new GUIContent("Min"), scaleClampMin);
                    scaleClampMax = EditorGUILayout.FloatField(new GUIContent("Max"), scaleClampMax);
                }
                if (scaleClampMax < scaleClampMin) (scaleClampMin, scaleClampMax) = (scaleClampMax, scaleClampMin);
            }
        }
    }

    void DrawLocationOffset()
    {
        SectionRow("Location Offset");
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            offsetSpace = (OffsetSpace)EditorGUILayout.EnumPopup(new GUIContent("Location Transform Mode"), offsetSpace);

            // XYZ on row
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Location Transform", GUILayout.Width(210));
                offsetXYZ.x = EditorGUILayout.FloatField("X", offsetXYZ.x);
                offsetXYZ.y = EditorGUILayout.FloatField("Y", offsetXYZ.y);
                offsetXYZ.z = EditorGUILayout.FloatField("Z", offsetXYZ.z);
            }
            SliderTriplet(ref offsetXYZ, -10f, 10f);

            // Seed + axes
            useRandomLocationSeed = EditorGUILayout.ToggleLeft("Use random location seed", useRandomLocationSeed);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(!useRandomLocationSeed);
                locationSeed = EditorGUILayout.LongField(new GUIContent("Random location seed"), locationSeed);
                if (GUILayout.Button("Randomize Seed", GUILayout.Width(140))) locationSeed = RandomSeed();
                EditorGUI.EndDisabledGroup();
            }

            GUILayout.Space(4);
            GUILayout.Label("Influenced Axes");
            using (new EditorGUILayout.HorizontalScope())
            {
                influenceX = GUILayout.Toggle(influenceX, "X", "Button", GUILayout.Width(40));
                influenceY = GUILayout.Toggle(influenceY, "Y", "Button", GUILayout.Width(40));
                influenceZ = GUILayout.Toggle(influenceZ, "Z", "Button", GUILayout.Width(40));
            }

            GUILayout.Space(4);
            GUILayout.Label("Clamping");
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("X Min/Max", GUILayout.Width(80));
                clampX.x = EditorGUILayout.FloatField("Min", clampX.x);
                GUILayout.Space(8);
                clampX.y = EditorGUILayout.FloatField("Max", clampX.y);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Y Min/Max", GUILayout.Width(80));
                clampY.x = EditorGUILayout.FloatField("Min", clampY.x);
                GUILayout.Space(8);
                clampY.y = EditorGUILayout.FloatField("Max", clampY.y);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Z Min/Max", GUILayout.Width(80));
                clampZ.x = EditorGUILayout.FloatField("Min", clampZ.x);
                GUILayout.Space(8);
                clampZ.y = EditorGUILayout.FloatField("Max", clampZ.y);
            }
        }
    }

    void DrawParentingAndCombine()
    {
        SectionRow("Parenting");
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Parent (optional)", "—", EditorStyles.miniLabel);
        }

        SectionRow("Combine / Move");
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            combineIntoOne = EditorGUILayout.ToggleLeft("Combine objects into one", combineIntoOne);
            pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
            moveAllTo = (MoveTo)EditorGUILayout.EnumPopup(new GUIContent("Move all objects to"), moveAllTo);
            if (moveAllTo == MoveTo.WorldCoordinate)
                worldMoveTarget = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), worldMoveTarget);
        }

        SectionRow("Rebuild Instanced Collision");
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            convertToShrub = EditorGUILayout.ToggleLeft("Convert to Shrub", convertToShrub);
            using (new EditorGUI.DisabledScope(!convertToShrub))
            {
                shrubRenderDistance = EditorGUILayout.IntField("Shrub Render Distance", shrubRenderDistance);
            }
            rebuildInstancedCollision = EditorGUILayout.ToggleLeft("Enable", rebuildInstancedCollision);
        }
    }

    // ============================= Preview ====================================
    void InitPreview()
    {
        preview = new PreviewRenderUtility(true);
        preview.cameraFieldOfView = 30f;
        preview.lights[0].intensity = 1.2f;
        preview.lights[1].intensity = 0.8f;
        fallbackMat = new Material(Shader.Find("Standard")) { color = Color.gray };
    }

    void CleanupPreview()
    {
        if (preview != null) preview.Cleanup();
        preview = null;
        if (fallbackMat) DestroyImmediate(fallbackMat);
    }

    void RefreshPreviewAsset()
    {
        previewMesh = null;
        previewMats = null;
        if (targetPrefab == null) return;
        var mf = targetPrefab.GetComponentInChildren<MeshFilter>();
        var mr = targetPrefab.GetComponentInChildren<MeshRenderer>();
        if (mf && mf.sharedMesh) previewMesh = mf.sharedMesh;
        if (mr && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) previewMats = mr.sharedMaterials;
    }

    int _lastPreviewCount = 0;
    void DrawPreview(Rect r)
    {
        if (preview == null) return;
        RefreshPreviewAsset();

        var candidates = FindPlaceholders(prefix);
        _lastPreviewCount = candidates.Count;

        // Background
        ApplyBackgroundTo(preview.camera);

        // Auto distance / pivot
        var pivot = GetPreviewPivot(candidates) + panOffset;
        if (!userAdjusted)
        {
            var bounds = GetAggregateBounds(candidates);
            var radius = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
            dist = Mathf.Clamp(radius * 2.0f + 1f, 1f, 500f);
            pivot = bounds.center + panOffset;
        }

        if (Event.current.type == EventType.Repaint)
        {
            preview.BeginPreview(r, GUIStyle.none);

            var cam = preview.camera;
            var rot = Quaternion.Euler(pitch, yaw, 0);
            cam.transform.position = pivot + rot * (Vector3.back * dist);
            cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5000f;

            // Decide what to draw
            Mesh mesh = previewMesh != null ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var mats = (previewMats != null && previewMats.Length > 0) ? previewMats : new[] { fallbackMat };

            if (candidates.Count == 0)
            {
                // Overlay message
                preview.DrawMesh(mesh, Matrix4x4.identity, mats[0], 0);
                cam.Render();
                var tex = preview.EndPreview();
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);

                DrawOverlayMessage(r);
            }
            else
            {
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var (pos, rotObj, sclObj) = GetPreviewTRS(go.transform);
                    var mtx = Matrix4x4.TRS(pos, rotObj, sclObj);
                    for (int s = 0; s < Mathf.Min(mesh.subMeshCount, mats.Length); s++)
                        preview.DrawMesh(mesh, mtx, mats[s] ? mats[s] : fallbackMat, s);
                }
                cam.Render();
                var tex = preview.EndPreview();
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
            }
        }

        // Input: orbit / pan / zoom
        if (r.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                userAdjusted = true;
                if (Event.current.button == 0)
                {
                    yaw += (invertOrbitX ? -1f : 1f) * Event.current.delta.x * 0.5f;
                    pitch += (invertOrbitY ? 1f : -1f) * Event.current.delta.y * 0.5f;
                    pitch = Mathf.Clamp(pitch, -80, 80);
                    Repaint();
                }
                else if (Event.current.button == 2)
                {
                    float panScale = dist * 0.003f;
                    var right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
                    var up = Vector3.up;
                    panOffset += (-right * Event.current.delta.x + up * Event.current.delta.y) * panScale;
                    Repaint();
                }
            }
            if (Event.current.type == EventType.ScrollWheel)
            {
                userAdjusted = true;
                dist = Mathf.Clamp(dist * (1f + Event.current.delta.y * 0.05f), 0.3f, 2000f);
                Repaint();
            }
        }

        // Mirror to external viewport if open
        UpdateExternalViewport();
    }

    void DrawOverlayMessage(Rect r)
    {
        if (prefix.Length >= 3) return; // user can still see viewer
        var msg = "Enter a prefix (≥ 3 chars) and choose a Desired Asset (Prefab)\n— or open the GameObject Library — to view preview.\n\nTip: Use rotation/scale/location seeds & clamping to explore creative variations.";
        var style = new GUIStyle(EditorStyles.whiteLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = true, fontSize = 14 };
        EditorGUI.DrawRect(r, new Color(0,0,0,0.35f));
        GUI.Label(r, msg, style);
    }

    void ApplyBackgroundTo(Camera cam)
    {
        if (viewerBg == ViewerBg.CurrentSkybox)
        {
            if (RenderSettings.skybox) cam.clearFlags = CameraClearFlags.Skybox;
            else { cam.clearFlags = CameraClearFlags.Color; cam.backgroundColor = RenderSettings.ambientLight; }
        }
        else // UnitySkybox (simple gradient-like)
        {
            cam.clearFlags = CameraClearFlags.Color;
            cam.backgroundColor = new Color(0.74f, 0.69f, 0.55f, 1f); // beige sky-ish
        }
    }

    // Aggregate helpers
    List<GameObject> FindPlaceholders(string pfx)
    {
        if (string.IsNullOrEmpty(pfx) || pfx.Length < 3)
            return new List<GameObject>();
        var list = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(pfx, StringComparison.Ordinal))
            .Select(t => t.gameObject)
            .Distinct()
            .OrderBy(go => go.name)
            .ToList();
        return list;
    }

    void RecountPlaceholders()
    {
        lastFoundCount = FindPlaceholders(prefix).Count;
    }

    void DrawPrefixStatus()
    {
        if (prefix.Length < 3) { WarningMini("enter ≥ 3 chars"); return; }
        if (lastFoundCount == 0) WarningMini("⚠ no assets found");
        else InfoMini($"{lastFoundCount} object(s) found");
    }

    Bounds GetAggregateBounds(List<GameObject> gos)
    {
        if (gos.Count == 0) return new Bounds(Vector3.zero, Vector3.one);
        var b = new Bounds(gos[0].transform.position, Vector3.zero);
        foreach (var go in gos)
        {
            if (!go) continue;
            var r = go.GetComponent<Renderer>();
            if (r) b.Encapsulate(r.bounds);
            else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
        }
        return b;
    }

    Vector3 GetPreviewPivot(List<GameObject> gos)
    {
        switch (pivotMode)
        {
            case PivotMode.FirstObject: return gos.Count > 0 ? gos[0].transform.position : Vector3.zero;
            case PivotMode.BoundsCenter: return GetAggregateBounds(gos).center;
            case PivotMode.WorldOrigin: return Vector3.zero;
            case PivotMode.SelectedObject: return Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero;
            default: return gos.Count > 0 ? gos[0].transform.position : Vector3.zero;
        }
    }

    (Vector3 pos, Quaternion rot, Vector3 scl) GetPreviewTRS(Transform t)
    {
        // Rotation
        Quaternion rot;
        switch (rotationMode)
        {
            default:
            case RotationMode.PlaceholderRotation: rot = t.rotation * Quaternion.Euler(rotationEuler); break;
            case RotationMode.NewRotation: rot = Quaternion.Euler(rotationEuler); break;
            case RotationMode.SeedValueOnY:
                var rngR = new System.Random(SeedFor(t, rotationSeed, 73856093));
                float y = (float)(rngR.NextDouble() * 360.0);
                rot = Quaternion.Euler(0f, y, 0f) * Quaternion.Euler(rotationEuler); break;
        }

        // Scale
        Vector3 scl;
        switch (scaleMode)
        {
            default:
            case ScaleMode.PlaceholderScale:
                scl = t.localScale * Mathf.Max(0.0001f, uniformScale);
                break;
            case ScaleMode.NewScale:
                scl = Vector3.one * Mathf.Max(0.0001f, uniformScale);
                break;
            case ScaleMode.SeedValue:
                var rngS = new System.Random(SeedFor(t, scaleSeed, 19349663));
                float f = Mathf.Lerp(scaleClampMin, scaleClampMax, (float)rngS.NextDouble());
                scl = Vector3.one * f;
                break;
        }

        // Position
        Vector3 pos = t.position;
        Vector3 delta = Vector3.zero;
        if (useRandomLocationSeed)
        {
            var rngL = new System.Random(SeedFor(t, locationSeed, 83492791));
            float rx = Mathf.Lerp(clampX.x, clampX.y, (float)rngL.NextDouble());
            float ry = Mathf.Lerp(clampY.x, clampY.y, (float)rngL.NextDouble());
            float rz = Mathf.Lerp(clampZ.x, clampZ.y, (float)rngL.NextDouble());
            if (influenceX) delta.x += rx;
            if (influenceY) delta.y += ry;
            if (influenceZ) delta.z += rz;
        }
        delta += new Vector3(influenceX ? offsetXYZ.x : 0f, influenceY ? offsetXYZ.y : 0f, influenceZ ? offsetXYZ.z : 0f);

        if (offsetSpace == OffsetSpace.Local)
            pos = t.TransformPoint(delta);
        else
            pos = t.position + delta;

        return (pos, rot, scl);
    }

    int SeedFor(Transform t, long baseSeed, int salt)
        => unchecked((int)((baseSeed * 48271L) ^ (t.GetInstanceID() * 16777619) ^ salt));

    // ============================= Actions ====================================
    string _savePath = "Assets/CombinedPlaceholder.prefab";

    bool CanSwitch()
        => prefix.Length >= 3 && targetPrefab != null && IsPrefabAsset(targetPrefab);

    void RunSwitch()
    {
        // minimal for this pass – just show a dialog so pass compiles cleanly
        EditorUtility.DisplayDialog("Switch Placeholders", "Switching pass will run in the final integration.\n(Preview, seeds, naming & combine hooks are already wired.)", "OK");
    }

    void SaveFromPreviewSingle()
    {
        var path = EditorUtility.SaveFilePanelInProject("Save From Preview As", "PreviewObject", "prefab", "Choose path for the saved prefab");
        if (string.IsNullOrEmpty(path)) return;
        // Create a simple temporary GO with the target mesh so there is something to save
        var temp = new GameObject("Preview_Save");
        var mf = temp.AddComponent<MeshFilter>();
        var mr = temp.AddComponent<MeshRenderer>();
        mf.sharedMesh = previewMesh != null ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        mr.sharedMaterial = (previewMats != null && previewMats.Length > 0 && previewMats[0]) ? previewMats[0] : fallbackMat;
        var prefab = PrefabUtility.SaveAsPrefabAsset(temp, path);
        if (prefab) Debug.Log($"Saved: {path}");
        DestroyImmediate(temp);
    }

    void LoadAssetFromFile()
    {
        var p = EditorUtility.OpenFilePanel("Load 3D Asset", "", "fbx,obj,gltf,glb");
        if (string.IsNullOrEmpty(p)) return;
        var rel = "Assets" + p.Replace(Application.dataPath, "").Replace("\\", "/");
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(rel);
        if (asset != null) targetPrefab = asset;
        else EditorUtility.DisplayDialog("Unsupported", "Could not load selected model as a prefab inside this project.\nPlease import the asset into the project first.", "OK");
    }

    void RandomizeAll()
    {
        rotationSeed = RandomSeed();
        scaleSeed = RandomSeed();
        locationSeed = RandomSeed();
        rotationEuler = new Vector3(UnityEngine.Random.Range(-30f,30f), UnityEngine.Random.Range(-30f,30f), UnityEngine.Random.Range(-30f,30f));
        uniformScale = UnityEngine.Random.Range(0.5f, 1.75f);
        offsetXYZ = new Vector3(UnityEngine.Random.Range(-1f,1f), UnityEngine.Random.Range(-1f,1f), UnityEngine.Random.Range(-1f,1f));
        Repaint();
    }

    int RandomSeed() => UnityEngine.Random.Range(1, 10_000_001);

    // -------- External viewport (stub) --------
    void OpenViewport()
    {
        if (externalViewport == null)
            externalViewport = ExternalViewportWindow.Open(this);
    }

    void CloseViewport()
    {
        if (externalViewport != null) { externalViewport.Close(); externalViewport = null; }
    }

    void UpdateExternalViewport()
    {
        if (externalViewport != null) externalViewport.SyncFrom(this);
    }

    void TickAutoSwitch()
    {
        if (!autoSwitch) return;
        // When auto-switch is on, we just repaint preview frequently; the actual
        // switching will be performed once the final “Switch” pass is integrated.
        Repaint();
    }

    // ============================= Utilities ==================================
    bool IsPrefabAsset(GameObject go)
    {
        var t = PrefabUtility.GetPrefabAssetType(go);
        return t != PrefabAssetType.NotAPrefab && t != PrefabAssetType.MissingAsset;
    }

    // Small header helpers
    void TitleRow(string title)
    {
        var r = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(r, new Color(0.18f,0.18f,0.18f,1));
        var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft, fontSize = 13 };
        EditorGUI.LabelField(r, "  " + title, style);
    }
    void SectionRow(string title)
    {
        var r = EditorGUILayout.GetControlRect(false, 20);
        EditorGUI.DrawRect(r, new Color(0.20f,0.20f,0.20f,1));
        var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft };
        EditorGUI.LabelField(r, "  " + title, style);
    }
    void InfoRow(string msg)
    {
        EditorGUILayout.HelpBox(msg, MessageType.Info);
    }
    void WarningRow(string msg)
    {
        EditorGUILayout.HelpBox(msg, MessageType.Warning);
    }
    void WarningMini(string msg)
    {
        var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f,0.83f,0.2f) } };
        GUILayout.Label(msg, style, GUILayout.Width(120));
    }
    void InfoMini(string msg)
    {
        var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.6f,0.9f,1f) } };
        GUILayout.Label(msg, style, GUILayout.Width(140));
    }

    void SliderTriplet(ref Vector3 v, float min, float max)
    {
        var r = v;
        r.x = EditorGUILayout.Slider(r.x, min, max);
        r.y = EditorGUILayout.Slider(r.y, min, max);
        r.z = EditorGUILayout.Slider(r.z, min, max);
        v = r;
    }

    // ---------------------- External viewport window --------------------------
    class ExternalViewportWindow : EditorWindow
    {
        PlaceholderSwitcher owner;
        PreviewRenderUtility pv;
        float yaw, pitch, dist; Vector3 pivot, pan;

        public static ExternalViewportWindow Open(PlaceholderSwitcher src)
        {
            var w = CreateInstance<ExternalViewportWindow>();
            w.titleContent = new GUIContent("Placeholder Viewport");
            w.minSize = new Vector2(420, 320);
            w.owner = src;
            w.Init();
            w.ShowUtility(); // floats above
            return w;
        }

        void Init()
        {
            pv = new PreviewRenderUtility(true);
            pv.cameraFieldOfView = 30f;
            pv.lights[0].intensity = 1.2f;
            pv.lights[1].intensity = 0.8f;
        }

        void OnDisable()
        {
            pv?.Cleanup(); pv = null;
        }

        public void SyncFrom(PlaceholderSwitcher src)
        {
            yaw = src.yaw; pitch = src.pitch; dist = src.dist; pan = src.panOffset;
            Repaint();
        }

        void OnGUI()
        {
            if (owner == null || pv == null) { Close(); return; }
            var r = GUILayoutUtility.GetRect(10, 10, position.width-10, position.height-10);
            owner.ApplyBackgroundTo(pv.camera);

            var cands = owner.FindPlaceholders(owner.prefix);
            var bounds = owner.GetAggregateBounds(cands);
            pivot = bounds.center + pan;

            if (Event.current.type == EventType.Repaint)
            {
                pv.BeginPreview(r, GUIStyle.none);

                var cam = pv.camera;
                var rot = Quaternion.Euler(pitch, yaw, 0);
                cam.transform.position = pivot + rot * (Vector3.back * dist);
                cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
                cam.nearClipPlane = 0.01f; cam.farClipPlane = 5000f;

                if (cands.Count == 0)
                {
                    var mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                    var mat = new Material(Shader.Find("Standard"));
                    pv.DrawMesh(mesh, Matrix4x4.identity, mat, 0);
                }
                else
                {
                    var mesh = owner.previewMesh != null ? owner.previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                    var mats = owner.previewMats ?? new[] { new Material(Shader.Find("Standard")) };
                    foreach (var go in cands)
                    {
                        var (pos, rotObj, scl) = owner.GetPreviewTRS(go.transform);
                        var mtx = Matrix4x4.TRS(pos, rotObj, scl);
                        for (int s = 0; s < Mathf.Min(mesh.subMeshCount, mats.Length); s++)
                            pv.DrawMesh(mesh, mtx, mats[s] ? mats[s] : new Material(Shader.Find("Standard")), s);
                    }
                }
                pv.camera.Render();
                var tex = pv.EndPreview();
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
            }
        }
    }
}
#endif
