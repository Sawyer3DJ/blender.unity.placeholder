#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// ============================================================
// PlaceholderTool.cs  (Pass A+B)
// Menu: Tools > Placeholder Tools > Placeholder Switcher
// ============================================================
public class PlaceholderSwitcher : EditorWindow
{
    // ----------------- Top / Inputs (left, above viewer) -----------------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject desiredAsset;          // “Desired Asset (Prefab)”
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false;

    // Count / status for placeholders
    private int _foundCount = 0;
    private string _foundStatus = "—";

    // ----------------- Viewer background mode -----------------
    private enum ViewerBgMode { CurrentSkybox, UnitySkybox }
    [SerializeField] private ViewerBgMode viewerBg = ViewerBgMode.CurrentSkybox;

    // ----------------- Transform Tools (right column) -----------------

    // Rotation
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero; // offset or absolute based on mode
    [SerializeField] private int rotationSeed = 1234567;                 // 10-digit capable but int32 storage

    // Scale
    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] private ScaleMode scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private Vector3 scaleXYZ = new Vector3(1,1,1);      // used in PlaceholderScale/NewScale
    // For SeedValue: uniform f in [min,max], then add XYZ offset below (adds-to-seeded-uniform)
    [SerializeField] private Vector3 scaleOffsetXYZ = Vector3.zero;
    [SerializeField] private int scaleSeed = 7654321;
    [SerializeField] private float scaleMin = 0.5f;
    [SerializeField] private float scaleMax = 1.5f;

    // Location offset
    private enum LocationSpace { Local, World }
    [SerializeField] private LocationSpace locationSpace = LocationSpace.Local;
    [SerializeField] private Vector3 locOffsetXYZ = Vector3.zero;
    [SerializeField] private bool useRandomLocationSeed = false;
    [SerializeField] private int locationSeed = 4567;
    // Axis influence for location
    [SerializeField] private bool locInfluenceX = true;
    [SerializeField] private bool locInfluenceY = true;
    [SerializeField] private bool locInfluenceZ = true;
    // Clamping for random location per-axis
    [SerializeField] private Vector2 locClampX = new Vector2(-1f, 1f);
    [SerializeField] private Vector2 locClampY = new Vector2(-1f, 1f);
    [SerializeField] private Vector2 locClampZ = new Vector2(-1f, 1f);

    // Randomize-all (affects seeds and clamp ranges, not parenting/pivot etc — those are in Pass C)
    private System.Random _uiRng = new System.Random(1337);

    // ----------------- Preview / camera -----------------
    private PreviewRenderUtility _preview;
    private float _yaw = -20f;
    private float _pitch = 15f;
    private float _distance = 10f;
    private bool _userAdjusted = false;

    private bool yOrbitInverted = true;   // default ON (as requested)
    private bool xOrbitInverted = false;

    // Panning offset (world-ish pivot offset inside preview)
    private Vector3 _pivotOffset = Vector3.zero;

    // Cached mesh/material for the desired asset
    private Mesh _mesh;              // first mesh we can find, used for all draws (representative)
    private Material[] _mats;        // corresponding materials (submeshes)
    private Material _fallbackMat;   // Standard fallback

    // Cached placeholder sample list for preview (positions etc)
    private List<Transform> _placeholderTs = new List<Transform>();

    // Right column scroll (vertical only)
    private Vector2 _rightScroll;

    // Styles
    private GUIStyle _titleStyle, _sectionHeader, _miniGray, _overlayCenter;

    // ------------------------------------------------------------
    // Entry
    // ------------------------------------------------------------
    [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
    public static void OpenWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(980, 640);
        w.Show();
    }

    private void OnEnable()
    {
        BuildStyles();
        InitPreview();
        RefreshDesiredAssetCache();
        RescanPlaceholders(); // also sets _foundStatus
    }

    private void OnDisable()
    {
        CleanupPreview();
    }

    // ------------------------------------------------------------
    // GUI
    // ------------------------------------------------------------
    private void OnGUI()
    {
        BuildStyles(); // safe guard in case domain reload styles reset

        // Title row (centered)
        GUILayout.Space(4);
        EditorGUILayout.LabelField("Placeholder Switcher", _titleStyle, GUILayout.ExpandWidth(true));
        GUILayout.Space(2);

        // Second row buttons (top right area aligned)
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Open GameObject Library", GUILayout.Height(22)))
        {
            // In Pass D this opens the real library window; in A+B we just ping the project search
            EditorGUIUtility.PingObject(desiredAsset);
            EditorUtility.DisplayDialog("Library", "Pass D will introduce a dedicated GameObject Library window.\\nFor now, use the 'Desired Asset (Prefab)' field or Project search.", "OK");
        }
        if (GUILayout.Button("Randomize All Parameters", GUILayout.Height(22)))
        {
            RandomizeAll();
            Repaint();
        }
        if (GUILayout.Button("Undo", GUILayout.Height(22)))
        {
            Undo.PerformUndo();
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(4);

        // Two-column split
        EditorGUILayout.BeginHorizontal();

        // ------------------------ LEFT: Inputs (top) + Viewer ------------------------
        EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(position.width * 0.56f, 560f)));

        DrawLeftInputs();
        DrawViewer();

        EditorGUILayout.EndVertical();

        // ------------------------ RIGHT: Transform Tools (scroll) --------------------
        EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll, false, false); // vertical only
        DrawTransformToolsRight();
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();
    }

    // ------------------------------------------------------------
    // LEFT — Inputs above viewer
    // ------------------------------------------------------------
    private void DrawLeftInputs()
    {
        DrawSectionHeader("Replace Object Placeholders");

        // Prefix + count status
        EditorGUILayout.BeginHorizontal();
        prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
        using (new EditorGUI.DisabledScope(true))
        {
            string chip = (_foundCount <= 0) ? "⚠ no assets found" : $"{_foundCount} object(s) found";
            GUILayout.Label(chip, EditorStyles.miniLabel, GUILayout.Width(140));
        }
        EditorGUILayout.EndHorizontal();

        // Desired asset
        var newAsset = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"), desiredAsset, typeof(GameObject), false);
        if (newAsset != desiredAsset)
        {
            desiredAsset = newAsset;
            RefreshDesiredAssetCache();
        }

        // Forced name / incremental
        forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
        useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);

        // Background row BELOW the viewer in older layout; here we show the selector label to signal modes.
        // (Actual buttons live directly under the viewer area to match your latest screenshot.)
    }

    // ------------------------------------------------------------
    // Viewer (mesh preview + overlay + controls)
    // ------------------------------------------------------------
    private void DrawViewer()
    {
        // Viewport rect
        var r = GUILayoutUtility.GetRect(10, 10, 400, 400, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        HandleViewerBackground();
        DrawPreview(r);

        // Overlay (prefix/asset guidance – shown when <3 chars or no asset)
        bool showOverlay = (string.IsNullOrEmpty(prefix) || prefix.Length < 3) || desiredAsset == null;
        if (showOverlay)
        {
            var overlay = new GUIContent(
                "Enter a prefix (≥ 3 chars) and choose a Desired Asset (Prefab)\\n— or open the GameObject Library — to view preview.\\n\\n" +
                "Tip: Use rotation/scale/location seeds & clamping to explore creative variations."
            );
            GUI.Label(r, overlay, _overlayCenter);
        }

        // Viewer background buttons row
        GUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Viewer Background", _miniGray, GUILayout.Width(110));
        if (GUILayout.Toggle(viewerBg == ViewerBgMode.CurrentSkybox, "Current Skybox", EditorStyles.miniButtonLeft))
        {
            if (viewerBg != ViewerBgMode.CurrentSkybox) { viewerBg = ViewerBgMode.CurrentSkybox; ApplyViewerBackground(); }
        }
        if (GUILayout.Toggle(viewerBg == ViewerBgMode.UnitySkybox, "Unity Skybox", EditorStyles.miniButtonRight))
        {
            if (viewerBg != ViewerBgMode.UnitySkybox) { viewerBg = ViewerBgMode.UnitySkybox; ApplyViewerBackground(); }
        }

        GUILayout.FlexibleSpace();

        // Controls chips
        GUILayout.Label("Controls", _miniGray, GUILayout.Width(60));
        if (GUILayout.Button("Re-center View", EditorStyles.miniButton, GUILayout.Width(120))) { RecenterView(true); }

        yOrbitInverted = GUILayout.Toggle(yOrbitInverted, "Y Inverted", EditorStyles.miniButton, GUILayout.Width(80));
        xOrbitInverted = GUILayout.Toggle(xOrbitInverted, "X Inverted", EditorStyles.miniButton, GUILayout.Width(80));

        EditorGUILayout.EndHorizontal();
        GUILayout.Space(4);
    }

    private void InitPreview()
    {
        _preview = new PreviewRenderUtility(true);
        _preview.cameraFieldOfView = 30f;
        _preview.lights[0].intensity = 1.2f;
        _preview.lights[1].intensity = 0.7f;
        _fallbackMat = new Material(Shader.Find("Standard"));
        ApplyViewerBackground();
    }

    private void CleanupPreview()
    {
        if (_fallbackMat != null) DestroyImmediate(_fallbackMat);
        _fallbackMat = null;

        if (_preview != null)
        {
            _preview.Cleanup();
            _preview = null;
        }
    }

    private void HandleViewerBackground()
    {
        // Nothing to do here; actual clears are in ApplyViewerBackground() and DrawPreview()
    }

    private void ApplyViewerBackground()
    {
        if (_preview == null) return;
        var cam = _preview.camera;

        if (viewerBg == ViewerBgMode.CurrentSkybox)
        {
            if (RenderSettings.skybox != null)
            {
                cam.clearFlags = CameraClearFlags.Skybox;
                // camera will render current RenderSettings.skybox
            }
            else
            {
                // If scene uses a sky sphere (mesh) instead of RenderSettings.skybox, we can’t draw it in Preview.
                // Fall back to a neutral color so it’s obvious; still “Current Skybox” mode.
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = new Color(0.18f, 0.22f, 0.25f, 1f);
            }
        }
        else // UnitySkybox
        {
            // Provide a basic unity procedural / color fallback
            cam.clearFlags = CameraClearFlags.Color;
            cam.backgroundColor = new Color(0.69f, 0.67f, 0.57f, 1f); // “ground-ish” beige like your baseline horizon
        }
    }

    private void RecenterView(bool resetUserAdjust)
    {
        _pivotOffset = Vector3.zero;
        _distance = 10f;
        if (resetUserAdjust) _userAdjusted = false;
        Repaint();
    }

    private void RefreshDesiredAssetCache()
    {
        _mesh = null;
        _mats = null;

        if (desiredAsset == null) return;
        // find first MeshFilter/MeshRenderer in prefab
        var mf = desiredAsset.GetComponentInChildren<MeshFilter>();
        var mr = desiredAsset.GetComponentInChildren<MeshRenderer>();
        if (mf != null && mf.sharedMesh != null) _mesh = mf.sharedMesh;
        if (mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) _mats = mr.sharedMaterials;
    }

    private void RescanPlaceholders()
    {
        _placeholderTs.Clear();
        if (!string.IsNullOrEmpty(prefix) && prefix.Length >= 3)
        {
            var transforms = Resources.FindObjectsOfTypeAll<Transform>();
            foreach (var t in transforms)
            {
                if (!t) continue;
                var go = t.gameObject;
                if (!go.scene.IsValid()) continue;        // ignore assets
                if (!go.activeInHierarchy) continue;      // preview active only
                if (go.name.StartsWith(prefix, StringComparison.Ordinal))
                    _placeholderTs.Add(t);
            }
        }
        _foundCount = _placeholderTs.Count;
        _foundStatus = (_foundCount <= 0) ? "⚠ no assets found" : $"{_foundCount} object(s) found";
    }

    private void DrawPreview(Rect r)
    {
        if (_preview == null) return;

        // Keep scan fresh-ish each repaint (cheap; filtered)
        if (Event.current.type == EventType.Repaint) RescanPlaceholders();

        var cam = _preview.camera;

        // Auto-fit on first draw if user hasn’t adjusted
        if (!_userAdjusted)
        {
            // estimate bounds using placeholders + unit mesh extents
            Bounds b;
            bool init = false;

            if (_placeholderTs.Count > 0)
            {
                foreach (var t in _placeholderTs)
                {
                    if (!t) continue;
                    var pos = t.position;
                    if (!init) { b = new Bounds(pos, Vector3.one); init = true; }
                    else b.Encapsulate(pos);
                }
            }
            else
            {
                b = new Bounds(Vector3.zero, new Vector3(10, 1, 10));
                init = true;
            }

            if (init)
            {
                var radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z) + 0.1f;
                var halfFov = _preview.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
                _distance = Mathf.Clamp(radius / Mathf.Tan(halfFov) + radius * 0.25f, 2f, 200f);
            }
        }

        var pivot = GetPreviewPivot() + _pivotOffset;
        var viewRot = Quaternion.Euler(_pitch, _yaw, 0f);
        cam.transform.position = pivot + viewRot * (Vector3.back * _distance);
        cam.transform.rotation  = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane  = 5000f;

        if (Event.current.type == EventType.Repaint)
        {
            _preview.BeginPreview(r, GUIStyle.none);

            // draw placeholders as if replaced with desiredAsset's mesh (or a cube fallback)
            Mesh drawMesh = _mesh ?? Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var mats = (_mats != null && _mats.Length > 0) ? _mats : new[] { _fallbackMat };

            if (_placeholderTs.Count == 0 || desiredAsset == null)
            {
                // Draw a single sample mesh at origin to give context
                var trs = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one);
                _preview.DrawMesh(drawMesh, trs, mats[0], 0);
            }
            else
            {
                foreach (var t in _placeholderTs)
                {
                    if (!t) continue;

                    // final rotation
                    Quaternion q;
                    switch (rotationMode)
                    {
                        case RotationMode.NewRotation:
                            q = Quaternion.Euler(rotationOffsetEuler);
                            break;
                        case RotationMode.SeedValueOnY:
                        {
                            int hash = StableHash(t, rotationSeed);
                            var rng = new System.Random(hash);
                            float y = (float)(rng.NextDouble() * 360.0);
                            q = Quaternion.Euler(rotationOffsetEuler) * Quaternion.Euler(0, y, 0);
                            break;
                        }
                        default: // PlaceholderRotation
                            q = t.rotation * Quaternion.Euler(rotationOffsetEuler);
                            break;
                    }

                    // final scale
                    Vector3 s = Vector3.one;
                    if (scaleMode == ScaleMode.PlaceholderScale)
                        s = Vector3.Scale(t.localScale, scaleXYZ);
                    else if (scaleMode == ScaleMode.NewScale)
                        s = SafeVec(scaleXYZ, 0.0001f);
                    else
                    {
                        // SeedValue: uniform factor + offset XYZ (adds to seed)
                        int hash = StableHash(t, scaleSeed);
                        var rng = new System.Random(hash);
                        float f = Mathf.Lerp(Mathf.Min(scaleMin, scaleMax), Mathf.Max(scaleMin, scaleMax), (float)rng.NextDouble());
                        s = new Vector3(f, f, f) + scaleOffsetXYZ;
                        s = SafeVec(s, 0.0001f);
                    }

                    // position with optional random offset (location stack)
                    Vector3 pos = t.position + ComputeLocationOffset(t);

                    var trs = Matrix4x4.TRS(pos, q, s);
                    int sm = Mathf.Min(drawMesh.subMeshCount, mats.Length);
                    for (int sub = 0; sub < sm; sub++)
                        _preview.DrawMesh(drawMesh, trs, mats[sub] ? mats[sub] : _fallbackMat, sub);
                }
            }

            cam.Render();
            var tex = _preview.EndPreview();
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
        }

        // Mouse controls (orbit/pan/zoom)
        if (r.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                _userAdjusted = true;
                if (Event.current.button == 0) // orbit
                {
                    float dx = Event.current.delta.x * (xOrbitInverted ? -0.5f : 0.5f);
                    float dy = Event.current.delta.y * (yOrbitInverted ?  0.5f : -0.5f);
                    _yaw   += dx;
                    _pitch = Mathf.Clamp(_pitch + dy, -80f, 80f);
                    Repaint();
                }
                else if (Event.current.button == 2 || (Event.current.button == 0 && (Event.current.modifiers & EventModifiers.Shift) != 0))
                {
                    // Pan (MMB or Shift+LMB)
                    float panScale = Mathf.Max(0.002f * _distance, 0.0001f);
                    var right = Quaternion.Euler(0, _yaw, 0) * Vector3.right;
                    var up = Vector3.up;
                    _pivotOffset += (-right * Event.current.delta.x + up * Event.current.delta.y) * panScale;
                    Repaint();
                }
            }
            else if (Event.current.type == EventType.ScrollWheel)
            {
                _userAdjusted = true;
                _distance = Mathf.Clamp(_distance * (1f + Event.current.delta.y * 0.05f), 1f, 1000f);
                Repaint();
            }
        }
    }

    private Vector3 ComputeLocationOffset(Transform t)
    {
        Vector3 add = locOffsetXYZ;

        if (useRandomLocationSeed)
        {
            int hash = StableHash(t, locationSeed);
            var rng = new System.Random(hash);
            float rx = Mathf.Lerp(Mathf.Min(locClampX.x, locClampX.y), Mathf.Max(locClampX.x, locClampX.y), (float)rng.NextDouble());
            float ry = Mathf.Lerp(Mathf.Min(locClampY.x, locClampY.y), Mathf.Max(locClampY.x, locClampY.y), (float)rng.NextDouble());
            float rz = Mathf.Lerp(Mathf.Min(locClampZ.x, locClampZ.y), Mathf.Max(locClampZ.x, locClampZ.y), (float)rng.NextDouble());
            if (locInfluenceX) add.x += rx;
            if (locInfluenceY) add.y += ry;
            if (locInfluenceZ) add.z += rz;
        }

        if (locationSpace == LocationSpace.Local)
            return t.TransformVector(add);
        return add; // world
    }

    private Vector3 GetPreviewPivot()
    {
        if (_placeholderTs.Count == 0) return Vector3.zero;
        var b = new Bounds(_placeholderTs[0].position, Vector3.zero);
        for (int i = 1; i < _placeholderTs.Count; i++)
        {
            var t = _placeholderTs[i];
            if (!t) continue;
            b.Encapsulate(t.position);
        }
        return b.center;
    }

    // ------------------------------------------------------------
    // RIGHT — Transform tools
    // ------------------------------------------------------------
    private void DrawTransformToolsRight()
    {
        // ---- Rotation Offset ----
        DrawSectionHeader("Transform Tools");

        EditorGUILayout.Space(4);
        DrawSubBox(() =>
        {
            GUILayout.Label("Rotation Offset", EditorStyles.boldLabel);

            rotationMode = (RotationMode)EditorGUILayout.EnumPopup(new GUIContent("Rotation Mode"), rotationMode);

            // Same row: XYZ fields
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Rotation (adds to placeholder)"), GUILayout.Width(180));
            rotationOffsetEuler.x = EditorGUILayout.FloatField("X", rotationOffsetEuler.x);
            rotationOffsetEuler.y = EditorGUILayout.FloatField("Y", rotationOffsetEuler.y);
            rotationOffsetEuler.z = EditorGUILayout.FloatField("Z", rotationOffsetEuler.z);
            EditorGUILayout.EndHorizontal();

            // Sliders row under the fields (neat)
            rotationOffsetEuler.x = EditorGUILayout.Slider(rotationOffsetEuler.x, -360f, 360f);
            rotationOffsetEuler.y = EditorGUILayout.Slider(rotationOffsetEuler.y, -360f, 360f);
            rotationOffsetEuler.z = EditorGUILayout.Slider(rotationOffsetEuler.z, -360f, 360f);

            if (rotationMode == RotationMode.SeedValueOnY)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                rotationSeed = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Random rotation seed (Y)"), rotationSeed), 1, int.MaxValue);
                if (GUILayout.Button("Randomize Seed", GUILayout.Width(130)))
                    rotationSeed = SafeRandSeed();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.HelpBox("Per-object deterministic Y rotation from seed; the XYZ offsets above are added on top.", MessageType.Info);
            }
        });

        // ---- Scale Offset ----
        EditorGUILayout.Space(6);
        DrawSubBox(() =>
        {
            GUILayout.Label("Scale Offset", EditorStyles.boldLabel);

            scaleMode = (ScaleMode)EditorGUILayout.EnumPopup(new GUIContent("Scaling Mode"), scaleMode);

            if (scaleMode == ScaleMode.PlaceholderScale || scaleMode == ScaleMode.NewScale)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent(scaleMode == ScaleMode.PlaceholderScale ? "Scale (multiplies placeholder scale)" : "Scale (new)"), GUILayout.Width(180));
                scaleXYZ.x = EditorGUILayout.FloatField("X", Mathf.Max(0.0001f, scaleXYZ.x));
                scaleXYZ.y = EditorGUILayout.FloatField("Y", Mathf.Max(0.0001f, scaleXYZ.y));
                scaleXYZ.z = EditorGUILayout.FloatField("Z", Mathf.Max(0.0001f, scaleXYZ.z));
                EditorGUILayout.EndHorizontal();

                scaleXYZ.x = Mathf.Max(0.0001f, EditorGUILayout.Slider(scaleXYZ.x, 0.001f, 10f));
                scaleXYZ.y = Mathf.Max(0.0001f, EditorGUILayout.Slider(scaleXYZ.y, 0.001f, 10f));
                scaleXYZ.z = Mathf.Max(0.0001f, EditorGUILayout.Slider(scaleXYZ.z, 0.001f, 10f));
            }
            else
            {
                // Seeded uniform + offset XYZ
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Scale (adds to seeded uniform)"), GUILayout.Width(180));
                scaleOffsetXYZ.x = EditorGUILayout.FloatField("X", scaleOffsetXYZ.x);
                scaleOffsetXYZ.y = EditorGUILayout.FloatField("Y", scaleOffsetXYZ.y);
                scaleOffsetXYZ.z = EditorGUILayout.FloatField("Z", scaleOffsetXYZ.z);
                EditorGUILayout.EndHorizontal();

                scaleOffsetXYZ.x = EditorGUILayout.Slider(scaleOffsetXYZ.x, -3f, 3f);
                scaleOffsetXYZ.y = EditorGUILayout.Slider(scaleOffsetXYZ.y, -3f, 3f);
                scaleOffsetXYZ.z = EditorGUILayout.Slider(scaleOffsetXYZ.z, -3f, 3f);

                EditorGUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                scaleSeed = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Random scaling seed"), scaleSeed), 1, int.MaxValue);
                GUILayout.Space(8);
                GUILayout.Label("Min", GUILayout.Width(28));
                scaleMin = EditorGUILayout.FloatField(scaleMin, GUILayout.Width(60));
                GUILayout.Label("Max", GUILayout.Width(28));
                scaleMax = EditorGUILayout.FloatField(scaleMax, GUILayout.Width(60));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Randomize Seed", GUILayout.Width(130))) scaleSeed = SafeRandSeed();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.HelpBox("Generates a uniform factor in [Min, Max] per object; the XYZ values above are added to that factor.", MessageType.Info);
            }
        });

        // ---- Location Offset ----
        EditorGUILayout.Space(6);
        DrawSubBox(() =>
        {
            GUILayout.Label("Location Offset", EditorStyles.boldLabel);

            locationSpace = (LocationSpace)EditorGUILayout.EnumPopup(new GUIContent("Location Transform Mode"), locationSpace);

            // Same row: fields
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Location Transform"), GUILayout.Width(180));
            locOffsetXYZ.x = EditorGUILayout.FloatField("X", locOffsetXYZ.x);
            locOffsetXYZ.y = EditorGUILayout.FloatField("Y", locOffsetXYZ.y);
            locOffsetXYZ.z = EditorGUILayout.FloatField("Z", locOffsetXYZ.z);
            EditorGUILayout.EndHorizontal();

            // Sliders underneath
            locOffsetXYZ.x = EditorGUILayout.Slider(locOffsetXYZ.x, -10f, 10f);
            locOffsetXYZ.y = EditorGUILayout.Slider(locOffsetXYZ.y, -10f, 10f);
            locOffsetXYZ.z = EditorGUILayout.Slider(locOffsetXYZ.z, -10f, 10f);

            // Seed + axis influence below
            useRandomLocationSeed = EditorGUILayout.Toggle(new GUIContent("Use random location seed"), useRandomLocationSeed);

            using (new EditorGUI.DisabledScope(!useRandomLocationSeed))
            {
                EditorGUILayout.BeginHorizontal();
                locationSeed = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Random location seed"), locationSeed), 1, int.MaxValue);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Randomize Seed", GUILayout.Width(130))) locationSeed = SafeRandSeed();
                EditorGUILayout.EndHorizontal();

                GUILayout.Space(2);
                GUILayout.Label("Influenced Axes");
                EditorGUILayout.BeginHorizontal();
                locInfluenceX = GUILayout.Toggle(locInfluenceX, "X", EditorStyles.miniButtonLeft, GUILayout.Width(40));
                locInfluenceY = GUILayout.Toggle(locInfluenceY, "Y", EditorStyles.miniButtonMid,  GUILayout.Width(40));
                locInfluenceZ = GUILayout.Toggle(locInfluenceZ, "Z", EditorStyles.miniButtonRight,GUILayout.Width(40));
                EditorGUILayout.EndHorizontal();

                GUILayout.Space(4);
                GUILayout.Label("Clamping");
                // X
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("X Min/Max", GUILayout.Width(80));
                locClampX.x = EditorGUILayout.FloatField("Min", locClampX.x);
                locClampX.y = EditorGUILayout.FloatField("Max", locClampX.y);
                EditorGUILayout.EndHorizontal();
                DrawMinMaxSlider(ref locClampX, -10f, 10f);

                // Y
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Y Min/Max", GUILayout.Width(80));
                locClampY.x = EditorGUILayout.FloatField("Min", locClampY.x);
                locClampY.y = EditorGUILayout.FloatField("Max", locClampY.y);
                EditorGUILayout.EndHorizontal();
                DrawMinMaxSlider(ref locClampY, -10f, 10f);

                // Z
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Z Min/Max", GUILayout.Width(80));
                locClampZ.x = EditorGUILayout.FloatField("Min", locClampZ.x);
                locClampZ.y = EditorGUILayout.FloatField("Max", locClampZ.y);
                EditorGUILayout.EndHorizontal();
                DrawMinMaxSlider(ref locClampZ, -10f, 10f);
            }
        });
    }

    // ------------------------------------------------------------
    // Helpers (UI + math)
    // ------------------------------------------------------------
    private void BuildStyles()
    {
        if (_titleStyle == null)
        {
            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18
            };
        }
        if (_sectionHeader == null)
        {
            _sectionHeader = new GUIStyle(EditorStyles.helpBox)
            {
                fontStyle = FontStyle.Bold
            };
        }
        if (_miniGray == null)
        {
            _miniGray = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
        }
        if (_overlayCenter == null)
        {
            _overlayCenter = new GUIStyle(EditorStyles.whiteLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                wordWrap = true
            };
        }
    }

    private void DrawSectionHeader(string title)
    {
        var rc = GUILayoutUtility.GetRect(10, 24, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rc, new Color(0.18f, 0.18f, 0.18f, 1f));
        var label = new Rect(rc.x + 6, rc.y + 4, rc.width - 12, rc.height - 8);
        EditorGUI.LabelField(label, title, EditorStyles.boldLabel);
    }

    private void DrawSubBox(Action contents)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        contents?.Invoke();
        EditorGUILayout.EndVertical();
    }

    private void DrawMinMaxSlider(ref Vector2 pair, float min, float max)
    {
        float a = pair.x, b = pair.y;
        EditorGUILayout.MinMaxSlider(ref a, ref b, min, max);
        pair = new Vector2(a, b);
    }

    private static Vector3 SafeVec(Vector3 v, float minComponent)
    {
        v.x = Mathf.Max(minComponent, float.IsFinite(v.x) ? v.x : minComponent);
        v.y = Mathf.Max(minComponent, float.IsFinite(v.y) ? v.y : minComponent);
        v.z = Mathf.Max(minComponent, float.IsFinite(v.z) ? v.z : minComponent);
        return v;
    }

    private static int StableHash(Transform t, int seed)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (t ? t.GetInstanceID() : 0);
            h = h * 31 + seed;
            h = h * 31 + t.name.GetHashCode();
            return h;
        }
    }

    private int SafeRandSeed()
    {
        // deterministic enough for UI: 1..int.Max
        int r = _uiRng.Next(1, int.MaxValue);
        if (r == 0) r = 1;
        return r;
    }

    private void RandomizeAll()
    {
        // Seeds
        rotationSeed = SafeRandSeed();
        scaleSeed    = SafeRandSeed();
        locationSeed = SafeRandSeed();

        // Rotation offsets
        rotationOffsetEuler.x = Mathf.Lerp(-180f, 180f, (float)_uiRng.NextDouble());
        rotationOffsetEuler.y = Mathf.Lerp(-180f, 180f, (float)_uiRng.NextDouble());
        rotationOffsetEuler.z = Mathf.Lerp(-180f, 180f, (float)_uiRng.NextDouble());

        // Scale: widen clamp then pick offsets
        scaleMin = 0.5f; scaleMax = 1.5f;
        scaleOffsetXYZ = new Vector3(
            Mathf.Lerp(-0.5f, 0.5f, (float)_uiRng.NextDouble()),
            Mathf.Lerp(-0.5f, 0.5f, (float)_uiRng.NextDouble()),
            Mathf.Lerp(-0.5f, 0.5f, (float)_uiRng.NextDouble())
        );

        // Location
        locOffsetXYZ = new Vector3(
            Mathf.Lerp(-2f, 2f, (float)_uiRng.NextDouble()),
            Mathf.Lerp(-1f, 1f, (float)_uiRng.NextDouble()),
            Mathf.Lerp(-2f, 2f, (float)_uiRng.NextDouble())
        );
        locClampX = new Vector2(-2, 2);
        locClampY = new Vector2(-1, 1);
        locClampZ = new Vector2(-2, 2);

        // keep influence enabled by default
        locInfluenceX = locInfluenceY = locInfluenceZ = true;
    }
}
#endif
