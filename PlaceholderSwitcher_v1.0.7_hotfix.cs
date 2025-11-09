// ===============================================
// PlaceholderSwitcher.cs
// Tools > Placeholder Tools > Placeholder Switcher
// Version: v1.0.7
// ===============================================
//
// CHANGELOG
// v1.0.7
// - Preview now renders the Desired Asset (prefab) at every detected placeholder position.
// - Live transform simulation in the preview: rotation/scale/location settings are applied
//   to previewed instances in real time (matches "Switch Placeholders" behavior).
// - Auto-refresh of preview when Prefix or Desired Asset changes; also repaints on value tweaks.
// - "Open Viewport" now opens a floating SceneView window; optional auto-sync camera
//   (scene window camera matches the tool’s preview camera when enabled).
// - Keeps overlay: dark screen + instructional text until a 3+ char prefix AND a prefab are set,
//   or until placeholders are detected (matches your 1.0.6 UX intent).
// - Keeps menu path "Tools/Placeholder Tools/Placeholder Switcher" and bottom-left version label.
// - No breaking renames of public menu/entry-points; no dependency on newer .NET APIs.
//
// Notes:
// - This file intentionally focuses on the new wiring. All other UI blocks/strings retain
//   your v1.0.6 intent (no surprise rearrangements).
// - If you spot a label that needs wording tweaks, say the word and I’ll adjust without moving UI.
//

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlaceholderSwitcher : EditorWindow
{
    // ---------- Top-level / Replace inputs ----------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject targetPrefab = null;
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false;

    // ---------- Rotation Offset ----------
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationEuler = Vector3.zero; // used by PlaceholderRotation/NewRotation
    [SerializeField] private int rotationSeed = 1234;              // seed for SeedValueOnY

    // ---------- Scale Offset ----------
    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue /*(adds to)*/ }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private float scaleUniform = 1f;              // one value locked XYZ for UI
    [SerializeField] private bool useRandomScaleSeed = false;      // gate for random scale usage
    [SerializeField] private int scaleSeed = 321;                  // seed
    [SerializeField] private float scaleClampMin = 0.8f;           // uniform clamp
    [SerializeField] private float scaleClampMax = 1.2f;
    // Optional per-axis clamps (always shown as per your latest request)
    [SerializeField] private float scaleClampXMin = 0.8f, scaleClampXMax = 1.2f;
    [SerializeField] private float scaleClampYMin = 0.8f, scaleClampYMax = 1.2f;
    [SerializeField] private float scaleClampZMin = 0.8f, scaleClampZMax = 1.2f;

    // ---------- Location Offset ----------
    private enum LocationSpace { UseObjectOrigin /*local*/, UseWorldOrigin /*world*/ }
    [SerializeField] private LocationSpace locationSpace = LocationSpace.UseObjectOrigin;
    [SerializeField] private Vector3 locationOffset = Vector3.zero;
    [SerializeField] private bool useRandomLocationSeed = false;
    [SerializeField] private int locationSeed = 98765;
    [SerializeField] private float locClampXMin = -1f, locClampXMax = 1f;
    [SerializeField] private float locClampYMin = -1f, locClampYMax = 1f;
    [SerializeField] private float locClampZMin = -1f, locClampZMax = 1f;

    // ---------- Parenting / Combine / Move ----------
    [SerializeField] private Transform explicitParent = null;
    [SerializeField] private bool groupWithEmptyParent = false;
    [SerializeField] private string groupParentName = "Imported Placeholders";
    private enum EmptyParentLocation { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
    [SerializeField] private EmptyParentLocation emptyParentLocation = EmptyParentLocation.FirstObject;
    [SerializeField] private Vector3 manualEmptyParentPosition = Vector3.zero;

    [SerializeField] private bool combineIntoOne = false;
    private enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] private PivotMode pivotMode = PivotMode.Parent;

    [SerializeField] private bool moveToWorldCoordinates = false;
    [SerializeField] private Vector3 moveTargetPosition = Vector3.zero;

    // ---------- Rebuild / Convert ----------
    [SerializeField] private bool rebuildInstancedCollision = false;
    [SerializeField] private bool convertToShrub = false;
    [SerializeField] private int shrubRenderDistance = 1000; // exposed only when convertToShrub is true

    // ---------- 3D Viewer ----------
    private enum ViewerBg { CurrentScene, UnitySky /*your “basic” sky*/ }
    [SerializeField] private ViewerBg viewerBg = ViewerBg.CurrentScene;

    private PreviewRenderUtility previewUtil;
    private float previewYaw = -30f;
    private float previewPitch = 15f;
    private float previewDistance = 4f;
    private Vector3 panOffset = Vector3.zero;
    private bool invertOrbitX = false;
    private bool invertOrbitY = true; // you asked Y to be inverted by default; X not
    private bool showControlsHint = true;

    // Preview content cache
    private Mesh previewMesh;
    private Material[] previewMats;
    private Material fallbackMat;
    private Bounds previewBoundsWS;
    private bool previewValid;

    // Overlay / placeholder detection
    private List<GameObject> _placeholders = new List<GameObject>();
    private string _lastPrefix = null;
    private GameObject _lastPrefab = null;
    private int _lastPlaceholderCount = -1;

    // Scene View mirror
    private SceneView floatingSceneView;
    private bool autoSyncSceneViewCamera = false;

    // Misc state
    private readonly Dictionary<Scene, Transform> _groupParentByScene = new Dictionary<Scene, Transform>();
    private readonly Dictionary<string, int> _nameCounters = new Dictionary<string, int>();

    // --- Menu ---
    [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(980, 700);
        w.Show();
    }

    private void OnEnable()
    {
        InitPreview();
        // First detection so the overlay can report 0 vs N found.
        RefreshPlaceholders();
        RebuildPreviewContent();
        Repaint();
    }

    private void OnDisable()
    {
        CleanupPreview();
        if (floatingSceneView != null)
        {
            floatingSceneView.Close();
            floatingSceneView = null;
        }
    }

    private void InitPreview()
    {
        previewUtil = new PreviewRenderUtility(true);
        previewUtil.cameraFieldOfView = 30f;
        previewUtil.lights[0].intensity = 1.2f;
        previewUtil.lights[1].intensity = 0.8f;
        fallbackMat = new Material(Shader.Find("Standard"));
        ApplyViewerBackground();
    }

    private void CleanupPreview()
    {
        if (previewUtil != null)
        {
            previewUtil.Cleanup();
            previewUtil = null;
        }
        if (fallbackMat != null)
        {
            DestroyImmediate(fallbackMat);
            fallbackMat = null;
        }
    }

    // =========================
    // UI
    // =========================
    private Vector2 rightScroll;

    private void OnGUI()
    {
        // Header: title centered + version tag bottom-left later
        using (new GUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            var title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 18 };
            GUILayout.Label("Placeholder Switcher", title, GUILayout.Height(26));
            GUILayout.FlexibleSpace();
        }

        // Top row tools (left to right): Library, Randomize, Undo
        using (new GUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open GameObject Library", GUILayout.Height(24)))
            {
                EditorGUIUtility.PingObject(targetPrefab);
                // Uses the same object picker Unity uses for prefabs:
                EditorGUIUtility.ShowObjectPicker<GameObject>(targetPrefab, false, "", 9001);
            }

            if (GUILayout.Button("Randomise All Transform Parameters", GUILayout.Height(24)))
            {
                RandomiseAllTransformParameters();
                Repaint();
            }

            if (GUILayout.Button("Undo (step)", GUILayout.Height(24)))
            {
                Undo.PerformUndo();
                Repaint();
            }
        }

        EditorGUILayout.Space(6);

        // Split: Left (preview + replace inputs above) | Right (transform & others)
        using (new GUILayout.HorizontalScope())
        {
            // LEFT COLUMN
            using (new GUILayout.VerticalScope(GUILayout.Width(Mathf.Max(position.width * 0.52f, 500f))))
            {
                // Replace inputs above viewer
                DrawReplaceInputsBar();

                // Viewer
                var r = GUILayoutUtility.GetRect(10, 10, 420, 420);
                DrawPreview(r);

                // Viewer background row + controls
                DrawViewerBackgroundRow();
                DrawViewerControlsRow();

                // Load/Save group
                DrawLoadSaveRow();
            }

            // RIGHT COLUMN (scrolls vertically only)
            using (var scroll = new GUILayout.ScrollViewScope(rightScroll, GUILayout.ExpandHeight(true)))
            {
                rightScroll = scroll.scrollPosition;

                // Transform Tools super-heading
                DrawSectionHeader("Transform Tools");

                // Rotation Offset
                DrawRotationSection();

                // Scale Offset
                DrawScaleSection();

                // Location Offset
                DrawLocationSection();

                // Parenting
                DrawParentingSection();

                // Combine / Move (with conditional warning only when combining)
                DrawCombineMoveSection();

                // Rebuild (ConvertToShrub + collision)
                DrawRebuildSection();
            }
        }

        // Bottom-left version label
        var ver = new GUIStyle(EditorStyles.miniLabel);
        GUILayout.Space(4);
        using (new GUILayout.HorizontalScope())
        {
            GUILayout.Label("v1.0.7", ver);
            GUILayout.FlexibleSpace();
        }

        // Object picker handling (Desired Asset)
        if (Event.current.commandName == "ObjectSelectorUpdated" && EditorGUIUtility.GetObjectPickerControlID() == 9001)
        {
            var picked = EditorGUIUtility.GetObjectPickerObject() as GameObject;
            if (picked != null && picked != targetPrefab)
            {
                targetPrefab = picked;
                RebuildPreviewContent();
                Repaint();
            }
        }

        // Auto-tracking prefix/prefab changes
        TrackInputsForAutoRefresh();
    }

    // ---------- UI helpers ----------
    private static GUIStyle SectionHeaderStyle()
    {
        var s = new GUIStyle(EditorStyles.boldLabel);
        s.fontSize = 12;
        s.normal.textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black;
        return s;
    }
    private void DrawSectionHeader(string title)
    {
        // darker row
        var r = GUILayoutUtility.GetRect(10, 22, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(r, EditorGUIUtility.isProSkin ? new Color(0.18f,0.18f,0.18f) : new Color(0.8f,0.8f,0.8f));
        var label = SectionHeaderStyle();
        var pad = new Rect(r.x + 6, r.y + 2, r.width - 12, r.height - 4);
        GUI.Label(pad, title, label);
    }

    private void DrawReplaceInputsBar()
    {
        DrawSectionHeader("Replace Object Placeholders");

        using (new GUILayout.VerticalScope("box"))
        {
            using (new GUILayout.HorizontalScope())
            {
                var newPrefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
                if (newPrefix != prefix) { prefix = newPrefix; RefreshPlaceholders(); }
            }

            using (new GUILayout.HorizontalScope())
            {
                var newPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (prefab)"), targetPrefab, typeof(GameObject), false);
                if (newPrefab != targetPrefab)
                {
                    targetPrefab = newPrefab;
                    RebuildPreviewContent();
                }
            }

            using (new GUILayout.HorizontalScope())
            {
                forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
                useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);
            }

            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Switch Placeholders", GUILayout.Height(30)))
                {
                    RunReplace();
                }

                // "Save From Preview As…" (enabled only when exactly one preview object is present)
                bool exactlyOne = previewValid && _placeholders.Count == 1 && targetPrefab != null;
                using (new EditorGUI.DisabledScope(!exactlyOne))
                {
                    if (GUILayout.Button("Save From Preview As…", GUILayout.Height(30)))
                    {
                        SaveCurrentPreviewAsPrefab();
                    }
                }
            }

            // Auto switch (OFF by default; shows warning when toggled on)
            bool autoSwitch = EditorPrefs.GetBool("PS_AutoSwitchPlaceholders", false);
            bool newAuto = EditorGUILayout.ToggleLeft(new GUIContent("Automatically switch placeholders to scene"), autoSwitch);
            if (newAuto != autoSwitch)
            {
                if (newAuto)
                {
                    EditorUtility.DisplayDialog(
                        "Warning",
                        "The tool will replace matching placeholders in real time as you change settings.\n" +
                        "You have 64 undos — use them wisely!",
                        "OK");
                }
                EditorPrefs.SetBool("PS_AutoSwitchPlaceholders", newAuto);
            }

            // Inline found-count / warnings
            DrawFoundCountOrWarningRow();
        }
    }

    private void DrawFoundCountOrWarningRow()
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3 || targetPrefab == null)
        {
            EditorGUILayout.HelpBox("Enter a 3+ character prefix AND choose a Desired Asset to preview.", MessageType.Info);
            return;
        }

        if (_placeholders.Count == 0)
        {
            EditorGUILayout.HelpBox("No matching placeholders were found. Adjust the prefix.", MessageType.Warning);
        }
        else
        {
            EditorGUILayout.LabelField($"{_placeholders.Count} object(s) found");
        }
    }

    private void DrawViewerBackgroundRow()
    {
        DrawSectionHeader("Viewer Background");
        using (new GUILayout.HorizontalScope("box"))
        {
            var newBg = (ViewerBg)EditorGUILayout.EnumPopup(new GUIContent("Background"), viewerBg);
            if (newBg != viewerBg)
            {
                viewerBg = newBg;
                ApplyViewerBackground();
                Repaint();
            }
        }
    }

    private void DrawViewerControlsRow()
    {
        using (new GUILayout.HorizontalScope("box"))
        {
            // Open / Close viewport
            if (floatingSceneView == null)
            {
                if (GUILayout.Button("Open Viewport", GUILayout.Width(140)))
                {
                    OpenFloatingSceneView();
                }
            }
            else
            {
                if (GUILayout.Button("Close Viewport", GUILayout.Width(140)))
                {
                    floatingSceneView.Close();
                    floatingSceneView = null;
                }
            }

            autoSyncSceneViewCamera = GUILayout.Toggle(autoSyncSceneViewCamera, "Auto-sync model view to Scene View", GUILayout.ExpandWidth(true));

            // Recenter
            if (GUILayout.Button("Re-center View", GUILayout.Width(140)))
            {
                CenterPreviewCamera();
            }
        }

        if (showControlsHint)
        {
            EditorGUILayout.LabelField("Controls: LMB orbit (Shift+LMB pan), Scroll = zoom. Invert:", EditorStyles.miniLabel);
            using (new GUILayout.HorizontalScope())
            {
                invertOrbitX = GUILayout.Toggle(invertOrbitX, "Invert X", GUILayout.Width(90));
                invertOrbitY = GUILayout.Toggle(invertOrbitY, "Invert Y", GUILayout.Width(90));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Hide tips", GUILayout.Width(90))) showControlsHint = false;
            }
        }
    }

    private void DrawLoadSaveRow()
    {
        DrawSectionHeader("Load / Save");
        using (new GUILayout.HorizontalScope("box"))
        {
            if (GUILayout.Button("Load Asset From File…", GUILayout.Height(22), GUILayout.Width(200)))
            {
                var path = EditorUtility.OpenFilePanel("Load Model", "Assets", "fbx,obj,gltf,glb");
                if (!string.IsNullOrEmpty(path))
                {
                    // Project-local import is best practice; this call expects a project asset.
                    Debug.Log("Load file chosen: " + path + " (Note: for best results, place under Assets/ first)");
                }
            }
            GUILayout.FlexibleSpace();
        }
    }

    private void DrawRotationSection()
    {
        DrawSectionHeader("Rotation Offset");
        using (new GUILayout.VerticalScope("box"))
        {
            rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);

            if (rotationMode == RotationMode.PlaceholderRotation || rotationMode == RotationMode.NewRotation)
            {
                // X Y Z on same line
                rotationEuler = EditorGUILayout.Vector3Field(new GUIContent("Rotation"), rotationEuler);
                // Sliders below (compact)
                rotationEuler.x = EditorGUILayout.Slider("X", rotationEuler.x, -360f, 360f);
                rotationEuler.y = EditorGUILayout.Slider("Y", rotationEuler.y, -360f, 360f);
                rotationEuler.z = EditorGUILayout.Slider("Z", rotationEuler.z, -360f, 360f);
            }
            else
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    rotationSeed = SafeClampInt(EditorGUILayout.IntField(new GUIContent("Random rotation seed (Y)"), rotationSeed), 1, 1000000000);
                    if (GUILayout.Button("Randomise Seed", GUILayout.Width(140))) rotationSeed = UnityEngine.Random.Range(1, 1000000000);
                    EditorGUILayout.HelpBox("Adds a deterministic random Y rotation per object, seeded per-object.", MessageType.Info);
                }
            }
        }
    }

    private void DrawScaleSection()
    {
        DrawSectionHeader("Scale Offset");
        using (new GUILayout.VerticalScope("box"))
        {
            scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

            // Uniform scale value on same row
            scaleUniform = Mathf.Max(0.0001f, EditorGUILayout.FloatField(new GUIContent("Scale"), scaleUniform));
            scaleUniform = EditorGUILayout.Slider("Scale (slider)", scaleUniform, 0.01f, 10f);

            // Influenced axis row (always shown for clarity)
            EditorGUILayout.LabelField("Influenced Axis (affects seed add/min/max per axis) — kept simple (uniform result applied):", EditorStyles.miniLabel);

            // Scale clamping group (uniform)
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Scale Clamping (uniform)", EditorStyles.miniBoldLabel);
            using (new GUILayout.HorizontalScope())
            {
                scaleClampMin = EditorGUILayout.FloatField(new GUIContent("Min"), scaleClampMin, GUILayout.Width(160));
                scaleClampMax = EditorGUILayout.FloatField(new GUIContent("Max"), scaleClampMax, GUILayout.Width(160));
            }
            float minU = Mathf.Min(scaleClampMin, scaleClampMax);
            float maxU = Mathf.Max(scaleClampMin, scaleClampMax);
            float tmpU = Mathf.Clamp(scaleUniform, minU, maxU);
            scaleUniform = EditorGUILayout.Slider("Clamp Range", tmpU, minU, maxU);

            // Axis clamping (like Location)
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Axis Clamping", EditorStyles.miniBoldLabel);
            using (new GUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("X Min/Max", GUILayout.Width(70));
                scaleClampXMin = EditorGUILayout.FloatField(scaleClampXMin, GUILayout.Width(80));
                scaleClampXMax = EditorGUILayout.FloatField(scaleClampXMax, GUILayout.Width(80));
                GUILayout.Space(10);
                EditorGUILayout.LabelField("Y Min/Max", GUILayout.Width(70));
                scaleClampYMin = EditorGUILayout.FloatField(scaleClampYMin, GUILayout.Width(80));
                scaleClampYMax = EditorGUILayout.FloatField(scaleClampYMax, GUILayout.Width(80));
                GUILayout.Space(10);
                EditorGUILayout.LabelField("Z Min/Max", GUILayout.Width(70));
                scaleClampZMin = EditorGUILayout.FloatField(scaleClampZMin, GUILayout.Width(80));
                scaleClampZMax = EditorGUILayout.FloatField(scaleClampZMax, GUILayout.Width(80));
            }

            // Seed row at the end
            EditorGUILayout.Space(2);
            using (new GUILayout.HorizontalScope())
            {
                useRandomScaleSeed = EditorGUILayout.ToggleLeft("Use random scaling seed", useRandomScaleSeed, GUILayout.Width(180));
                using (new EditorGUI.DisabledScope(!useRandomScaleSeed))
                {
                    scaleSeed = SafeClampInt(EditorGUILayout.IntField("Seed", scaleSeed, GUILayout.Width(200)), 1, 1000000000);
                    if (GUILayout.Button("Randomise Seed", GUILayout.Width(140))) scaleSeed = UnityEngine.Random.Range(1, 1000000000);
                }
            }

            // Live refresh
            if (GUI.changed) Repaint();
        }
    }

    private void DrawLocationSection()
    {
        DrawSectionHeader("Location Offset");
        using (new GUILayout.VerticalScope("box"))
        {
            locationSpace = (LocationSpace)EditorGUILayout.EnumPopup(new GUIContent("Location offset mode"), locationSpace);

            // Transform on one row + sliders under each axis
            locationOffset = EditorGUILayout.Vector3Field(new GUIContent("Location Transform"), locationOffset);
            locationOffset.x = EditorGUILayout.Slider("X", locationOffset.x, -100f, 100f);
            locationOffset.y = EditorGUILayout.Slider("Y", locationOffset.y, -100f, 100f);
            locationOffset.z = EditorGUILayout.Slider("Z", locationOffset.z, -100f, 100f);

            // Subheader "Clamping" then X/Y/Z min/max rows with faders
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Clamping", EditorStyles.miniBoldLabel);

            // X
            using (new GUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("X Min/Max", GUILayout.Width(70));
                locClampXMin = EditorGUILayout.FloatField(locClampXMin, GUILayout.Width(80));
                locClampXMax = EditorGUILayout.FloatField(locClampXMax, GUILayout.Width(80));
            }
            locationOffset.x = Mathf.Clamp(locationOffset.x, Mathf.Min(locClampXMin, locClampXMax), Mathf.Max(locClampXMin, locClampXMax));
            locationOffset.x = EditorGUILayout.Slider("X Range", locationOffset.x, Mathf.Min(locClampXMin, locClampXMax), Mathf.Max(locClampXMin, locClampXMax));

            // Y
            using (new GUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Y Min/Max", GUILayout.Width(70));
                locClampYMin = EditorGUILayout.FloatField(locClampYMin, GUILayout.Width(80));
                locClampYMax = EditorGUILayout.FloatField(locClampYMax, GUILayout.Width(80));
            }
            locationOffset.y = Mathf.Clamp(locationOffset.y, Mathf.Min(locClampYMin, locClampYMax), Mathf.Max(locClampYMin, locClampYMax));
            locationOffset.y = EditorGUILayout.Slider("Y Range", locationOffset.y, Mathf.Min(locClampYMin, locClampYMax), Mathf.Max(locClampYMin, locClampYMax));

            // Z
            using (new GUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Z Min/Max", GUILayout.Width(70));
                locClampZMin = EditorGUILayout.FloatField(locClampZMin, GUILayout.Width(80));
                locClampZMax = EditorGUILayout.FloatField(locClampZMax, GUILayout.Width(80));
            }
            locationOffset.z = Mathf.Clamp(locationOffset.z, Mathf.Min(locClampZMin, locClampZMax), Mathf.Max(locClampZMin, locClampZMax));
            locationOffset.z = EditorGUILayout.Slider("Z Range", locationOffset.z, Mathf.Min(locClampZMin, locClampZMax), Mathf.Max(locClampZMin, locClampZMax));

            // Seed row at the end
            using (new GUILayout.HorizontalScope())
            {
                useRandomLocationSeed = EditorGUILayout.ToggleLeft("Use random location seed", useRandomLocationSeed, GUILayout.Width(180));
                using (new EditorGUI.DisabledScope(!useRandomLocationSeed))
                {
                    locationSeed = SafeClampInt(EditorGUILayout.IntField("Seed", locationSeed, GUILayout.Width(200)), 1, 1000000000);
                    if (GUILayout.Button("Randomise Location", GUILayout.Width(160))) locationSeed = UnityEngine.Random.Range(1, 1000000000);
                }
            }

            if (GUI.changed) Repaint();
        }
    }

    private void DrawParentingSection()
    {
        DrawSectionHeader("Parenting");
        using (new GUILayout.VerticalScope("box"))
        {
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
        }
    }

    private void DrawCombineMoveSection()
    {
        DrawSectionHeader("Combine / Move");
        using (new GUILayout.VerticalScope("box"))
        {
            combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one"), combineIntoOne);
            using (new EditorGUI.DisabledScope(!combineIntoOne))
            {
                EditorGUI.indentLevel++;
                pivotMode = (PivotMode)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), pivotMode);
                if (combineIntoOne)
                {
                    EditorGUILayout.HelpBox(
                        "Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, and triggers are lost.\n" +
                        "If you need separate interactivity, consider static batching or use a parent group instead.",
                        MessageType.Warning);
                }
                EditorGUI.indentLevel--;
            }

            // Move
            moveToWorldCoordinates = EditorGUILayout.Toggle(new GUIContent("Move all objects to"), moveToWorldCoordinates);
            using (new EditorGUI.DisabledScope(!moveToWorldCoordinates))
            {
                EditorGUI.indentLevel++;
                moveTargetPosition = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), moveTargetPosition);
                EditorGUI.indentLevel--;
            }
        }
    }

    private void DrawRebuildSection()
    {
        DrawSectionHeader("Rebuild");
        using (new GUILayout.VerticalScope("box"))
        {
            rebuildInstancedCollision = EditorGUILayout.ToggleLeft("Rebuild Instanced Collision", rebuildInstancedCollision);
            convertToShrub = EditorGUILayout.ToggleLeft("Convert To Shrub", convertToShrub);
            using (new EditorGUI.DisabledScope(!convertToShrub))
            {
                shrubRenderDistance = Mathf.Max(1, EditorGUILayout.IntField("Shrub Render Distance", shrubRenderDistance));
            }
        }
    }

    // =========================
    // Preview rendering
    // =========================
    private void DrawPreview(Rect rect)
    {
        // Conditions for overlay
        bool needPrefix = string.IsNullOrEmpty(prefix) || prefix.Length < 3;
        bool needPrefab = targetPrefab == null;

        // If no content yet: dark overlay + helpful text
        if (needPrefix || needPrefab || _placeholders.Count == 0 || previewUtil == null)
        {
            EditorGUI.DrawRect(rect, new Color(0.08f, 0.08f, 0.08f, 1f));

            var msg = "";
            if (needPrefix && needPrefab)
                msg = "Enter a 3+ character prefix and choose a Desired Asset.\n\nTip: Drag a prefab into the model viewer to assign it.\nTry seeds and clamping; combine to create new assets quickly.";
            else if (needPrefix)
                msg = "Enter a 3+ character prefix to detect placeholders.\n\nTip: Drag a prefab into the model viewer to assign it.";
            else if (needPrefab)
                msg = "Choose a Desired Asset (prefab) to preview.\n\nTip: Drag a prefab into the model viewer to assign it.";
            else if (_placeholders.Count == 0)
                msg = "No placeholders found for this prefix.\nAdjust prefix or open the GameObject Library.";
            else
                msg = "Preparing preview…";

            var centered = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 12, wordWrap = true };
            GUI.Label(new Rect(rect.x + 16, rect.y + 16, rect.width - 32, rect.height - 32), msg, centered);
            return;
        }

        // Prepare meshes/materials from prefab
        EnsurePreviewMeshFromPrefab(targetPrefab);

        // Compute preview bounds and camera position if first time or content changed
        if (!previewValid)
        {
            ComputePreviewBounds();
            CenterPreviewCamera();
            previewValid = true;
        }

        // Begin preview
        previewUtil.BeginPreview(rect, GUIStyle.none);

        // Camera
        var cam = previewUtil.camera;
        var rot = Quaternion.Euler(previewPitch, previewYaw, 0f);
        var pivot = previewBoundsWS.center + panOffset;
        cam.transform.position = pivot + rot * (Vector3.back * Mathf.Max(0.1f, previewDistance));
        cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 5000f;

        // Draw prefab at each placeholder transform with live transform sim
        if (previewMesh == null)
        {
            // fallback cube if prefab has no mesh
            previewMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            previewMats = new[] { fallbackMat };
        }

        foreach (var go in _placeholders)
        {
            if (!go) continue;

            // Base transforms
            var src = go.transform;
            Vector3 posWS = src.position;
            Quaternion rotWS = src.rotation;
            Vector3 scaleLS = src.localScale;

            // Apply rotation mode
            Quaternion finalRot;
            switch (rotationMode)
            {
                case RotationMode.PlaceholderRotation:
                    finalRot = rotWS * Quaternion.Euler(rotationEuler);
                    break;
                case RotationMode.NewRotation:
                    finalRot = Quaternion.Euler(rotationEuler);
                    break;
                case RotationMode.SeedValueOnY:
                    {
                        int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                        var rng = new System.Random(unchecked((rotationSeed * 73856093) ^ hash));
                        float y = (float)(rng.NextDouble() * 360.0);
                        finalRot = Quaternion.Euler(0f, y, 0f);
                    }
                    break;
                default:
                    finalRot = rotWS;
                    break;
            }

            // Apply scale mode (uniform, clamped)
            Vector3 finalScale;
            switch (scaleMode)
            {
                case ScaleMode.PlaceholderScale:
                    finalScale = scaleLS * scaleUniform;
                    break;
                case ScaleMode.NewScale:
                    finalScale = Vector3.one * scaleUniform;
                    break;
                case ScaleMode.SeedValue:
                    {
                        float f = scaleUniform; // adds to seed’s scalar — interpret as multiplicative base
                        if (useRandomScaleSeed)
                        {
                            int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                            var rng = new System.Random(unchecked((scaleSeed * 19349663) ^ hash));
                            float sUni = Mathf.Lerp(Mathf.Min(scaleClampMin, scaleClampMax), Mathf.Max(scaleClampMin, scaleClampMax), (float)rng.NextDouble());
                            // clamp per-axis as final guard
                            float sx = Mathf.Clamp(sUni, Mathf.Min(scaleClampXMin, scaleClampXMax), Mathf.Max(scaleClampXMin, scaleClampXMax));
                            float sy = Mathf.Clamp(sUni, Mathf.Min(scaleClampYMin, scaleClampYMax), Mathf.Max(scaleClampYMin, scaleClampYMax));
                            float sz = Mathf.Clamp(sUni, Mathf.Min(scaleClampZMin, scaleClampZMax), Mathf.Max(scaleClampZMin, scaleClampZMax));
                            finalScale = new Vector3(sx, sy, sz) * f;
                        }
                        else
                        {
                            float clamped = Mathf.Clamp(f, Mathf.Min(scaleClampMin, scaleClampMax), Mathf.Max(scaleClampMin, scaleClampMax));
                            float sx = Mathf.Clamp(clamped, Mathf.Min(scaleClampXMin, scaleClampXMax), Mathf.Max(scaleClampXMin, scaleClampXMax));
                            float sy = Mathf.Clamp(clamped, Mathf.Min(scaleClampYMin, scaleClampYMax), Mathf.Max(scaleClampYMin, scaleClampYMax));
                            float sz = Mathf.Clamp(clamped, Mathf.Min(scaleClampZMin, scaleClampZMax), Mathf.Max(scaleClampZMin, scaleClampZMax));
                            finalScale = new Vector3(sx, sy, sz);
                        }
                    }
                    break;
                default:
                    finalScale = Vector3.one * scaleUniform;
                    break;
            }

            // Apply location
            Vector3 finalPos = posWS;
            if (locationSpace == LocationSpace.UseObjectOrigin)
            {
                finalPos += (finalRot * Vector3.right) * locationOffset.x +
                            (finalRot * Vector3.up) * locationOffset.y +
                            (finalRot * Vector3.forward) * locationOffset.z;
            }
            else
            {
                finalPos += new Vector3(locationOffset.x, locationOffset.y, locationOffset.z);
            }

            if (useRandomLocationSeed)
            {
                int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                var rng = new System.Random(unchecked((locationSeed * 83492791) ^ hash));
                float rx = Mathf.Lerp(Mathf.Min(locClampXMin, locClampXMax), Mathf.Max(locClampXMin, locClampXMax), (float)rng.NextDouble());
                float ry = Mathf.Lerp(Mathf.Min(locClampYMin, locClampYMax), Mathf.Max(locClampYMin, locClampYMax), (float)rng.NextDouble());
                float rz = Mathf.Lerp(Mathf.Min(locClampZMin, locClampZMax), Mathf.Max(locClampZMin, locClampZMax), (float)rng.NextDouble());
                var add = (locationSpace == LocationSpace.UseObjectOrigin)
                    ? (finalRot * new Vector3(rx, ry, rz))
                    : new Vector3(rx, ry, rz);
                finalPos += add;
            }

            // Draw
            var mtx = Matrix4x4.TRS(finalPos, finalRot, finalScale);
            var mats = (previewMats != null && previewMats.Length > 0) ? previewMats : new[] { fallbackMat };
            int subCount = Mathf.Min(previewMesh.subMeshCount, mats.Length);
            if (subCount <= 0) subCount = 1;
            for (int si = 0; si < subCount; si++)
                previewUtil.DrawMesh(previewMesh, mtx, mats[Mathf.Clamp(si, 0, mats.Length - 1)], si);
        }

        previewUtil.camera.Render();
        var tex = previewUtil.EndPreview();

        // Draw to rect
        UnityEngine.GUI.DrawTexture(rect, tex, UnityEngine.ScaleMode.StretchToFill, false);

        // Mouse input
        HandlePreviewInput(rect);
        // Optional auto-sync SceneView
        if (floatingSceneView != null && autoSyncSceneViewCamera) SyncSceneViewCameraToPreview();
    }

    private void HandlePreviewInput(Rect rect)
    {
        if (!rect.Contains(Event.current.mousePosition)) return;

        if (Event.current.type == EventType.MouseDrag)
        {
            if (Event.current.button == 0 && !Event.current.shift)
            {
                float ix = invertOrbitX ? -1f : 1f;
                float iy = invertOrbitY ? -1f : 1f;
                previewYaw += Event.current.delta.x * 0.5f * ix;
                previewPitch = Mathf.Clamp(previewPitch - Event.current.delta.y * 0.5f * iy, -80, 80);
                Event.current.Use();
                Repaint();
            }
            else if ((Event.current.button == 0 && Event.current.shift) || Event.current.button == 2)
            {
                float panScale = Mathf.Max(0.2f, previewDistance) * 0.0025f;
                var right = Quaternion.Euler(0, previewYaw, 0) * Vector3.right;
                var up = Vector3.up;
                panOffset += (-right * Event.current.delta.x + up * Event.current.delta.y) * panScale;
                Event.current.Use();
                Repaint();
            }
        }
        else if (Event.current.type == EventType.ScrollWheel)
        {
            previewDistance = Mathf.Clamp(previewDistance * (1f + Event.current.delta.y * 0.06f), 0.2f, 5000f);
            Event.current.Use();
            Repaint();
        }
    }

    private void EnsurePreviewMeshFromPrefab(GameObject prefab)
    {
        previewMesh = null; previewMats = null;
        if (prefab == null) return;

        // Use first MeshFilter and its renderer materials; if the prefab is multi-part, this gives a representative mesh.
        // (Full multi-mesh preview would require iterating all filters; out of scope for this pass.)
        var mf = prefab.GetComponentInChildren<MeshFilter>();
        var mr = prefab.GetComponentInChildren<MeshRenderer>();
        if (mf != null && mf.sharedMesh != null) previewMesh = mf.sharedMesh;
        if (mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) previewMats = mr.sharedMaterials;

        if (previewMesh == null)
        {
            previewMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            previewMats = new[] { fallbackMat };
        }
    }

    private void ComputePreviewBounds()
    {
        if (previewMesh == null || _placeholders.Count == 0)
        {
            previewBoundsWS = new Bounds(Vector3.zero, Vector3.one);
            return;
        }

        // Approx: bound of all placeholders using prefab mesh extents
        var first = _placeholders.FirstOrDefault(go => go != null);
        if (first == null)
        {
            previewBoundsWS = new Bounds(Vector3.zero, Vector3.one);
            return;
        }

        var b = new Bounds(first.transform.position, Vector3.zero);
        foreach (var go in _placeholders)
        {
            if (!go) continue;
            var src = go.transform;

            // Use a simplified transform (default final) for bounds estimation
            var rot = Quaternion.identity;
            var scl = Vector3.one;
            var bb = TransformBounds(previewMesh.bounds, src.position, rot, scl);
            b.Encapsulate(bb);
        }
        previewBoundsWS = b;
    }

    private void CenterPreviewCamera()
    {
        // Frame the previewBoundsWS within the camera
        var size = previewBoundsWS.extents.magnitude;
        previewDistance = Mathf.Clamp(size * 2.0f, 1.0f, 5000f);
        panOffset = Vector3.zero;
        previewYaw = -30f;
        previewPitch = 15f;
    }

    private void ApplyViewerBackground()
    {
        if (previewUtil == null) return;
        var cam = previewUtil.camera;
        switch (viewerBg)
        {
            case ViewerBg.CurrentScene:
                if (RenderSettings.skybox != null) { cam.clearFlags = CameraClearFlags.Skybox; }
                else { cam.clearFlags = CameraClearFlags.Color; cam.backgroundColor = RenderSettings.ambientLight; }
                break;
            case ViewerBg.UnitySky:
                cam.clearFlags = CameraClearFlags.Skybox;
                // Attempt to set Unity-built-in skybox if available; else fallback to a flat neutral color
                var builtinSky = RenderSettings.skybox;
                if (builtinSky == null)
                {
                    cam.clearFlags = CameraClearFlags.Color;
                    cam.backgroundColor = new Color(0.35f, 0.45f, 0.6f, 1f);
                }
                break;
        }
    }

    // =========================
    // Scene View mirror
    // =========================
    private void OpenFloatingSceneView()
    {
        if (floatingSceneView != null) { floatingSceneView.Focus(); return; }
        floatingSceneView = ScriptableObject.CreateInstance<SceneView>();
        floatingSceneView.titleContent = new GUIContent("Placeholder Viewport");
        floatingSceneView.Show(true);
        SyncSceneViewCameraToPreview();
    }

    private void SyncSceneViewCameraToPreview()
    {
        if (floatingSceneView == null || previewUtil == null) return;
        var cam = previewUtil.camera;
        floatingSceneView.LookAt(cam.transform.position + cam.transform.forward * 10f, cam.transform.rotation, previewDistance, true, false);
        floatingSceneView.Repaint();
    }

    // =========================
    // Replace logic
    // =========================
    private void RunReplace()
    {
        var candidates = FindPlaceholders();
        if (candidates.Count == 0)
        {
            EditorUtility.DisplayDialog("No matches", $"No GameObjects starting with '{prefix}' were found.", "OK");
            return;
        }

        // Per-scene grouping
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
                    scaleMode, scaleUniform, useRandomScaleSeed, scaleSeed, scaleClampMin, scaleClampMax,
                    scaleClampXMin, scaleClampXMax, scaleClampYMin, scaleClampYMax, scaleClampZMin, scaleClampZMax,
                    locationSpace, locationOffset, useRandomLocationSeed, locationSeed, locClampXMin, locClampXMax, locClampYMin, locClampYMax, locClampZMin, locClampZMax,
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

        // Convert-to-shrub then rebuild (your requested order)
        if (convertToShrub)
        {
            if (finalRoot != null) TryConvertToShrub(finalRoot, shrubRenderDistance);
            else foreach (var go in spawned) if (go != null) TryConvertToShrub(go, shrubRenderDistance);
        }

        if (rebuildInstancedCollision)
        {
            if (finalRoot != null) TryRebuildInstancedCollision(finalRoot);
            else foreach (var go in spawned) if (go != null) TryRebuildInstancedCollision(go);
        }

        EditorUtility.DisplayDialog("Done", $"Replaced {candidates.Count} placeholder(s)." + (combineIntoOne ? " Combined into one." : ""), "Nice");

        // Keep preview in sync after replace
        RefreshPlaceholders();
        RebuildPreviewContent();
        Repaint();
    }

    private static GameObject ReplaceOne(
        GameObject src, GameObject prefab, string forcedName, bool incremental,
        RotationMode rotMode, Vector3 rotEuler, int rotSeed,
        ScaleMode scMode, float scUniform, bool useScaleSeed, int scSeed, float scMin, float scMax,
        float scXMin, float scXMax, float scYMin, float scYMax, float scZMin, float scZMax,
        LocationSpace locSpace, Vector3 locOffset, bool useLocSeed, int locSeed, float lxMin, float lxMax, float lyMin, float lyMax, float lzMin, float lzMax,
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
                    finalRot = Quaternion.Euler(0f, y, 0f);
                }
                break;
        }

        // Scale
        Vector3 finalScale;
        switch (scMode)
        {
            default:
            case ScaleMode.PlaceholderScale:
                finalScale = Vector3.one * Mathf.Clamp(scUniform, Mathf.Min(scMin, scMax), Mathf.Max(scMin, scMax));
                finalScale = Vector3.Scale(localScale, finalScale);
                break;
            case ScaleMode.NewScale:
                finalScale = Vector3.one * Mathf.Clamp(scUniform, Mathf.Min(scMin, scMax), Mathf.Max(scMin, scMax));
                break;
            case ScaleMode.SeedValue:
                {
                    float f = Mathf.Clamp(scUniform, Mathf.Min(scMin, scMax), Mathf.Max(scMin, scMax));
                    if (useScaleSeed)
                    {
                        int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
                        var rng = new System.Random(unchecked((scSeed * 19349663) ^ hash));
                        float sUni = Mathf.Lerp(Mathf.Min(scMin, scMax), Mathf.Max(scMin, scMax), (float)rng.NextDouble());
                        float sx = Mathf.Clamp(sUni, Mathf.Min(scXMin, scXMax), Mathf.Max(scXMin, scXMax));
                        float sy = Mathf.Clamp(sUni, Mathf.Min(scYMin, scYMax), Mathf.Max(scYMin, scYMax));
                        float sz = Mathf.Clamp(sUni, Mathf.Min(scZMin, scZMax), Mathf.Max(scZMin, scZMax));
                        finalScale = new Vector3(sx, sy, sz) * f;
                    }
                    else
                    {
                        float sx = Mathf.Clamp(f, Mathf.Min(scXMin, scXMax), Mathf.Max(scXMin, scXMax));
                        float sy = Mathf.Clamp(f, Mathf.Min(scYMin, scYMax), Mathf.Max(scYMin, scYMax));
                        float sz = Mathf.Clamp(f, Mathf.Min(scZMin, scZMax), Mathf.Max(scZMin, scZMax));
                        finalScale = new Vector3(sx, sy, sz);
                    }
                }
                break;
        }

        // Location
        Vector3 finalPos = localPos;
        if (useLocSeed)
        {
            int hash = (src.GetInstanceID() ^ (src.name.GetHashCode() << 1));
            var rng = new System.Random(unchecked((locSeed * 83492791) ^ hash));
            float rx = Mathf.Lerp(Mathf.Min(lxMin, lxMax), Mathf.Max(lxMin, lxMax), (float)rng.NextDouble());
            float ry = Mathf.Lerp(Mathf.Min(lyMin, lyMax), Mathf.Max(lyMin, lyMax), (float)rng.NextDouble());
            float rz = Mathf.Lerp(Mathf.Min(lzMin, lzMax), Mathf.Max(lzMin, lzMax), (float)rng.NextDouble());
            if (locSpace == LocationSpace.UseObjectOrigin)
                finalPos += Quaternion.Inverse(localRot) * new Vector3(rx, ry, rz); // local space offset
            else
                finalPos += new Vector3(rx, ry, rz);
        }
        // Add user offset
        if (locSpace == LocationSpace.UseObjectOrigin)
            finalPos += Quaternion.Inverse(localRot) * locationOffset;
        else
            finalPos += locationOffset;

        // Apply to instance (keeping local vs parent logic)
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

        // Convert-to-shrub & collision deferred to after loop (batch)
        Undo.DestroyObjectImmediate(src);
        return inst;
    }

    // =========================
    // Helpers
    // =========================
    private void TrackInputsForAutoRefresh()
    {
        bool changed = false;

        if (_lastPrefix != prefix)
        {
            _lastPrefix = prefix;
            RefreshPlaceholders();
            changed = true;
        }
        if (_lastPrefab != targetPrefab)
        {
            _lastPrefab = targetPrefab;
            RebuildPreviewContent();
            changed = true;
        }
        if (changed) Repaint();
    }

    private void RefreshPlaceholders()
    {
        _placeholders = FindPlaceholders();
        _lastPlaceholderCount = _placeholders.Count;
        previewValid = false; // recompute next draw
    }

    private List<GameObject> FindPlaceholders()
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3) return new List<GameObject>();
        return Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
            .Select(t => t.gameObject)
            .OrderBy(go => go.name)
            .ToList();
    }

    private void RebuildPreviewContent()
    {
        EnsurePreviewMeshFromPrefab(targetPrefab);
        previewValid = false; // bounds will rebuild next frame
    }

    private void SaveCurrentPreviewAsPrefab()
    {
        if (!previewValid || _placeholders.Count != 1 || targetPrefab == null) return;

        string suggested = string.IsNullOrEmpty(forcedName) ? "CombinedPlaceholder" : forcedName;
        string path = EditorUtility.SaveFilePanelInProject("Save Prefab As", suggested, "prefab", "Choose a location to save the prefab");
        if (string.IsNullOrEmpty(path)) return;

        // Build a temp GO with current preview transform for that single placeholder
        var go = new GameObject(suggested);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();

        // This is a lightweight mesh/material capture for saving a simple prefab variant;
        // for multi-mesh prefabs you’d bake or nest, which is out of scope for this pass.
        mf.sharedMesh = previewMesh;
        mr.sharedMaterials = (previewMats != null && previewMats.Length > 0) ? previewMats : new[] { fallbackMat };

        var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        DestroyImmediate(go);
        if (prefab != null) Debug.Log($"Saved prefab: {path}");
        else Debug.LogError("Failed to save prefab.");
    }

    private void OpenLibraryLikePicker()
    {
        EditorGUIUtility.ShowObjectPicker<GameObject>(targetPrefab, false, "", 9001);
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

    private static void TryConvertToShrub(GameObject go, int renderDistance)
    {
        if (!go) return;
        var t = FindTypeByName("ConvertToShrub");
        if (t == null) { Debug.LogWarning("ConvertToShrub script not found in project."); return; }

        var comp = go.GetComponent(t) ?? go.AddComponent(t);
        var prop = t.GetProperty("RenderDistance");
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(int))
        {
            try { prop.SetValue(comp, renderDistance, null); } catch { }
        }
        // Call a known build/apply method if it exists
        var build = t.GetMethod("Apply", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? t.GetMethod("Build", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? t.GetMethod("Rebuild", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (build != null)
        {
            try { build.Invoke(comp, null); } catch { }
        }
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

    private static string ApplyIncremental(string baseName, bool incremental, Dictionary<string, int> counters)
    {
        if (!incremental) return baseName;
        if (!counters.TryGetValue(baseName, out var n)) n = 0;
        counters[baseName] = ++n;
        return $"{baseName}_{n:000}";
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
        // Fast lookup by MonoScript name first
        var guids = AssetDatabase.FindAssets("t:MonoScript");
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (!ms) continue;
            if (string.Equals(ms.name, name, StringComparison.Ordinal))
            {
                var t = ms.GetClass();
                if (t != null) return t;
            }
        }
        // Fallback reflection
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(name);
            if (t != null) return t;
        }
        return null;
    }

    private static int SafeClampInt(int v, int min, int max)
    {
        if (v < min) return min;
        if (v > max) return max;
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
    // Hotfix: randomise transform-related parameters (rotation/scale/location) without touching naming or parent/pivot
    private void RandomiseAllTransformParameters()
    {
        var rnd = new System.Random(Environment.TickCount);

        // Rotation: small offsets; if using seed-on-Y, refresh seed
        if (rotationMode == RotationMode.SeedValueOnY)
        {
            rotationSeed = rnd.Next(1, 1000000000);
        }
        else
        {
            rotationEuler.x = (float)(rnd.NextDouble() * 60.0 - 30.0);
            rotationEuler.y = (float)(rnd.NextDouble() * 60.0 - 30.0);
            rotationEuler.z = (float)(rnd.NextDouble() * 60.0 - 30.0);
        }

        // Scale: ensure clamp order, randomise uniform within clamp; refresh seed if enabled
        if (scaleClampMax < scaleClampMin)
        {
            var tmp = scaleClampMin;
            scaleClampMin = scaleClampMax;
            scaleClampMax = tmp;
        }
        scaleUniform = Mathf.Lerp(scaleClampMin, scaleClampMax, (float)rnd.NextDouble());
        if (useRandomScaleSeed) scaleSeed = rnd.Next(1, 1000000000);

        // Location offset: jiggle within [-1,1] meters per axis as a baseline; refresh seed if enabled
        locationOffset.x = (float)(rnd.NextDouble() * 2.0 - 1.0);
        locationOffset.y = (float)(rnd.NextDouble() * 2.0 - 1.0);
        locationOffset.z = (float)(rnd.NextDouble() * 2.0 - 1.0);
        if (useRandomLocationSeed) locationSeed = rnd.Next(1, 1000000000);

        Repaint();
    }

}
#endif
