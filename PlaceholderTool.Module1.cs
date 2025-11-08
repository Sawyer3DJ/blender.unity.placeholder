/*
========================================================
 PlaceholderTool.cs  —  "Placeholder Switcher" (Editor)
========================================================
Menu:  Tools > Placeholder Tools > Placeholder Switcher

Author: (you + assistant)
Unity:  2020+ (tested with PreviewRenderUtility)

--------------------
CHANGE LOG (extract)
--------------------
v1.0.0-ovr1  (2025-11-08)
- Added state-driven overlay in the embedded 3D viewer:
  • PrefixTooShort (needs ≥3 chars)
  • NoMatches
  • ExternalViewportOwnsPreview (dims when external window controls camera)
  • PlaceholdersOnly (when no Desired Asset selected)
- Always-on HUD counter: "N object(s) found" (top-right of viewer).
- Drag & Drop: drop a Prefab (GameObject asset) onto the viewer to set
  "Desired Asset (Prefab)".
- Soft fade for overlay (no allocations).
- External viewport stubs: Open/Close now just toggle a dedicated window
  and sync overlay state—no missing-method errors.
- Compact, isolated implementation so it won’t disturb other modules while
  you continue integrating features.

NOTE:
This file focuses on the preview/overlay plumbing you asked to lock down.
It leaves your transformation / switching logic placeholders in place so you
can wire them back as you finalize v1.0.0. Keep this file as a safe reference.
*/

#if UNITY_EDITOR
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlaceholderSwitcher : EditorWindow
{
    // ------------------- Inputs (kept minimal) -------------------
    [SerializeField] private string prefix = "SS_";
    [SerializeField] private GameObject targetPrefab = null; // Desired Asset (Prefab)
    [SerializeField] private string forcedName = "";
    [SerializeField] private bool useIncrementalNaming = false;

    // ------------------- Preview / camera -------------------
    private PreviewRenderUtility previewUtil;
    private float previewYaw = -30f;
    private float previewPitch = 15f;
    private float previewDistance = 2.0f;
    private bool previewUserAdjusted = false;
    private Vector3 previewPivotOffset = Vector3.zero;
    private Mesh previewMesh;
    private Material[] previewMats;
    private Material fallbackMat;

    // ------------------- Overlay / HUD states (NEW) -------------------
    [SerializeField] private bool externalViewportOpen = false;  // set by external window
    [SerializeField] private bool autoSyncViewport = true;       // kept for wording
    private enum OverlayState { None, PrefixTooShort, NoMatches, ExternalViewportOwnsPreview, PlaceholdersOnly }
    private OverlayState overlayState = OverlayState.PrefixTooShort;
    private string overlayCachedText = null;
    private int overlayHash = int.MinValue;
    private float overlayFade = 1f;        // 0..1
    private double overlayLastTime = 0.0;

    // HUD/overlay GUI styles
    private GUIStyle _hudRightMini;
    private GUIStyle _overlayTitle;
    private GUIStyle _overlayBody;

    // ------------------- Menu & lifecycle -------------------
    [MenuItem("Tools/Placeholder Tools/Placeholder Switcher")]
    public static void ShowWindow()
    {
        var w = GetWindow<PlaceholderSwitcher>(true, "Placeholder Switcher");
        w.minSize = new Vector2(900, 560);
        w.Show();
    }

    private void OnEnable()
    {
        previewUtil = new PreviewRenderUtility(true);
        previewUtil.cameraFieldOfView = 30f;
        previewUtil.lights[0].intensity = 1.2f;
        previewUtil.lights[1].intensity = 0.8f;
        fallbackMat = new Material(Shader.Find("Standard"));
        ApplyPreviewBackground_CurrentSkybox();
        overlayLastTime = EditorApplication.timeSinceStartup;
    }

    private void OnDisable()
    {
        previewUtil?.Cleanup();
        previewUtil = null;
        if (fallbackMat) DestroyImmediate(fallbackMat);
        fallbackMat = null;
    }

    // ------------------- UI -------------------
    private Vector2 _scrollR;

    private void OnGUI()
    {
        // Title row + toolbar
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Open GameObject Library", EditorStyles.toolbarButton))
                OpenGameObjectLibrary();

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Randomize All Parameters", EditorStyles.toolbarButton))
                RandomizeAll(); // simple stub (does nothing destructive)

            if (GUILayout.Button("Undo", EditorStyles.toolbarButton))
                EditorApplication.ExecuteMenuItem("Edit/Undo");
        }

        EditorGUILayout.Space(4);

        // Two columns: Left = Preview, Right = Options (scroll)
        EditorGUILayout.BeginHorizontal();

        // ------------ Left: Preview & top inputs ------------
        EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(position.width * 0.52f, 460f)));

        // Replace Object Placeholders (compact)
        DrawReplaceHeader();

        // Viewer
        var rect = GUILayoutUtility.GetRect(10, 10, 380, 420);
        DrawPreview(rect);

        // Viewer footer (background & small controls)
        DrawViewerFooter(rect);

        EditorGUILayout.EndVertical();

        // ------------ Right: Options (scroll only vertical) ------------
        _scrollR = EditorGUILayout.BeginScrollView(_scrollR, GUILayout.ExpandWidth(true));
        GUILayout.Label("Transform Tools", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);

        // Minimal transform stubs (so file compiles safely)
        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label("Rotation Offset", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• This reference build focuses on the viewer overlay.\n• Wire your rotation/scale/location logic here.", EditorStyles.wordWrappedMiniLabel);
        }

        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label("Scale Offset", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Keep your XYZ + clamping + seed UI here.", EditorStyles.wordWrappedMiniLabel);
        }

        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label("Location Offset", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Keep your local/global, influenced axes, seed & clamping here.", EditorStyles.wordWrappedMiniLabel);
        }

        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label("Parenting / Combine / Move", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Combine, pivot mode, move-to targets UI stays here.", EditorStyles.wordWrappedMiniLabel);
        }

        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label("Rebuild Instanced Collision", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Collision options live here.", EditorStyles.wordWrappedMiniLabel);
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndHorizontal();

        // Bottom action row
        EditorGUILayout.Space(6);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Switch Placeholders", GUILayout.Height(32), GUILayout.Width(280)))
            {
                RunReplace(); // stubbed minimal
            }
            GUILayout.FlexibleSpace();
        }
        EditorGUILayout.Space(4);
    }

    private void DrawReplaceHeader()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label("Replace Object Placeholders", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix"), prefix);
            // Live found counter (mirrors HUD)
            int cnt = CountMatches(prefix);
            var s = prefix != null && prefix.Length >= 3 ? $"{cnt} object(s) found" : "no assets found";
            EditorGUILayout.LabelField(new GUIContent("", s), GUILayout.Width(22)); // subtle icon slot
            EditorGUILayout.EndHorizontal();

            targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Desired Asset (Prefab)"),
                targetPrefab, typeof(GameObject), false);

            forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
            useIncrementalNaming = EditorGUILayout.Toggle(new GUIContent("Use incremental naming"), useIncrementalNaming);
        }
    }

    private void DrawViewerFooter(Rect rect)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Current Skybox", GUILayout.Width(120)))
                ApplyPreviewBackground_CurrentSkybox();
            if (GUILayout.Button("Unity Skybox", GUILayout.Width(120)))
                ApplyPreviewBackground_UnitySkybox();

            GUILayout.FlexibleSpace();

            if (!externalViewportOpen)
            {
                if (GUILayout.Button("Open Viewport", GUILayout.Width(120)))
                    OpenViewport();
            }
            else
            {
                if (GUILayout.Button("Close Viewport", GUILayout.Width(120)))
                    CloseViewport();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Re-center View", GUILayout.Width(120)))
            {
                previewUserAdjusted = false;
                previewYaw = -30f; previewPitch = 15f; previewDistance = 2.0f;
                previewPivotOffset = Vector3.zero;
                Repaint();
            }

            GUILayout.Space(12);
            autoSyncViewport = EditorGUILayout.ToggleLeft("Auto-sync Model View to Viewport", autoSyncViewport, GUILayout.Width(260));
        }
    }

    // ------------------- Background modes -------------------
    private void ApplyPreviewBackground_CurrentSkybox()
    {
        if (previewUtil == null) return;
        var cam = previewUtil.camera;
        if (RenderSettings.skybox != null)
        {
            cam.clearFlags = CameraClearFlags.Skybox;
        }
        else
        {
            cam.clearFlags = CameraClearFlags.Color;
            cam.backgroundColor = new Color(0.35f, 0.45f, 0.55f);
        }
    }

    // A simple, neutral "Unity sky-like" gradient look
    private void ApplyPreviewBackground_UnitySkybox()
    {
        if (previewUtil == null) return;
        var cam = previewUtil.camera;
        cam.clearFlags = CameraClearFlags.Color;
        cam.backgroundColor = new Color(0.62f, 0.74f, 0.86f); // light sky tint
    }

    // ------------------- Preview core -------------------
    private void RefreshPreviewMesh()
    {
        previewMesh = null; previewMats = null;
        if (targetPrefab == null) return;
        var mf = targetPrefab.GetComponentInChildren<MeshFilter>();
        var mr = targetPrefab.GetComponentInChildren<MeshRenderer>();
        if (mf != null && mf.sharedMesh != null) previewMesh = mf.sharedMesh;
        if (mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) previewMats = mr.sharedMaterials;
    }

    private void DrawPreview(Rect rect)
    {
        if (previewUtil == null) return;

        RefreshPreviewMesh();

        // Collect placeholders by prefix (cap to avoid heavy scenes)
        var candidates = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && !string.IsNullOrEmpty(prefix) && t.gameObject.name.StartsWith(prefix))
            .Select(t => t.gameObject)
            .Take(400)
            .ToList();

        var cam = previewUtil.camera;
        var rot = Quaternion.Euler(previewPitch, previewYaw, 0f);

        // Auto-fit once if user hasn't touched camera
        if (!previewUserAdjusted)
        {
            var mesh = previewMesh != null ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (candidates.Count > 0 && mesh != null)
            {
                var boundsWS = new Bounds(candidates[0].transform.position, Vector3.zero);
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    boundsWS.Encapsulate(go.GetComponent<Renderer>() ? go.GetComponent<Renderer>().bounds
                        : new Bounds(go.transform.position, Vector3.one * 0.1f));
                }
                var halfFovRad = previewUtil.cameraFieldOfView * 0.5f * Mathf.Deg2Rad;
                var radius = Mathf.Max(boundsWS.extents.x, boundsWS.extents.y, boundsWS.extents.z);
                previewDistance = Mathf.Clamp(radius / Mathf.Tan(halfFovRad) + radius * 0.25f, 0.4f, 2000f);
            }
            else previewDistance = 1.6f;
        }

        if (Event.current.type == EventType.Repaint)
        {
            previewUtil.BeginPreview(rect, GUIStyle.none);

            var pivot = Vector3.zero + previewPivotOffset;
            cam.transform.position = pivot + rot * (Vector3.back * previewDistance);
            cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5000f;

            Mesh mesh = previewMesh != null ? previewMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var mats = (previewMats != null && previewMats.Length > 0) ? previewMats : new[] { fallbackMat };

            if (candidates.Count == 0)
            {
                previewUtil.DrawMesh(mesh, Matrix4x4.identity, mats[0], 0);
            }
            else
            {
                foreach (var go in candidates)
                {
                    if (!go) continue;
                    var trs = Matrix4x4.TRS(go.transform.position, go.transform.rotation, go.transform.lossyScale);
                    for (int si = 0; si < Mathf.Min(mesh.subMeshCount, mats.Length); si++)
                        previewUtil.DrawMesh(mesh, trs, mats[si] ? mats[si] : fallbackMat, si);
                }
            }

            cam.Render();
            var tex = previewUtil.EndPreview();
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        // Orbit / Zoom / Pan
        if (rect.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                if (Event.current.button == 0) // orbit
                {
                    previewUserAdjusted = true;
                    previewYaw += Event.current.delta.x * 0.5f;
                    previewPitch = Mathf.Clamp(previewPitch - Event.current.delta.y * 0.5f, -80, 80);
                    Repaint();
                }
                else if (Event.current.button == 2) // pan
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

        // >>> NEW: overlay + HUD + drag & drop
        DrawPreviewOverlayAndHud(rect, candidates.Count);
    }

    // ------------------- Overlay / HUD implementation -------------------
    private static int SafeCount<T>(IList<T> list) => list == null ? 0 : list.Count;

    private static void DrawOverlayPanel(Rect r)
    {
        var old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.58f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    private void EnsureOverlayStyles()
    {
        if (_overlayTitle == null)
        {
            _overlayTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 15,
                normal = { textColor = Color.white }
            };
        }
        if (_overlayBody == null)
        {
            _overlayBody = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = Color.white }
            };
        }
        if (_hudRightMini == null)
        {
            _hudRightMini = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.UpperRight,
                normal = { textColor = new Color(1f, 1f, 1f, 0.92f) }
            };
        }
    }

    private OverlayState ComputeOverlayState(int matchCount)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3)
            return OverlayState.PrefixTooShort;
        if (externalViewportOpen)
            return OverlayState.ExternalViewportOwnsPreview;
        if (matchCount <= 0)
            return OverlayState.NoMatches;
        if (targetPrefab == null)
            return OverlayState.PlaceholdersOnly;
        return OverlayState.None;
    }

    private string GetOverlayText(OverlayState st, int matchCount)
    {
        switch (st)
        {
            case OverlayState.PrefixTooShort:
                return "Enter a prefix (≥ 3 chars) and choose a Desired Asset (Prefab) —\n" +
                       "or open the GameObject Library — to view preview.\n\n" +
                       "Tip: Use rotation/scale/location seeds & clamping to explore creative variations.";
            case OverlayState.NoMatches:
                return $"No objects found for prefix '{prefix}'.\nTry another prefix or open the GameObject Library.";
            case OverlayState.ExternalViewportOwnsPreview:
                return "Preview is controlled by the external Viewport window.\n" +
                       "Close it or disable Auto-Sync to resume here.";
            case OverlayState.PlaceholdersOnly:
                return "Previewing placeholders only.\n" +
                       "You can transform, combine, and save them.\n" +
                       "Select a Desired Asset to switch.";
            default:
                return string.Empty;
        }
    }

    private void DrawPreviewOverlayAndHud(Rect rect, int matchCount)
    {
        EnsureOverlayStyles();

        // HUD counter
        if (!string.IsNullOrEmpty(prefix) && prefix.Length >= 3)
        {
            var hudRect = new Rect(rect.x + rect.width - 200f, rect.y + 6f, 194f, 18f);
            GUI.Label(hudRect, $"{matchCount} object(s) found", _hudRightMini);
        }

        // State
        var st = ComputeOverlayState(matchCount);
        overlayState = st;

        // Smooth fade
        float target = (st == OverlayState.None) ? 0f : 1f;
        double t = EditorApplication.timeSinceStartup;
        float dt = (float)Mathf.Clamp01((float)(t - overlayLastTime));
        overlayLastTime = t;
        overlayFade = Mathf.MoveTowards(overlayFade, target, dt * 8f);
        if (overlayFade <= 0.001f && target < 0.5f) return;

        // Cache text
        int h = (st.GetHashCode() * 397) ^ (prefix?.GetHashCode() ?? 0) ^ matchCount;
        if (h != overlayHash)
        {
            overlayCachedText = GetOverlayText(st, matchCount);
            overlayHash = h;
        }

        // Draw dim panel + text
        var old = GUI.color;
        GUI.color = new Color(1, 1, 1, overlayFade);
        DrawOverlayPanel(rect);

        var mid = new Rect(rect.x + 24f, rect.center.y - 36f, rect.width - 48f, 72f);
        GUI.Label(mid, overlayCachedText, _overlayBody);

        // Drag & Drop prefab -> Desired Asset
        if (rect.Contains(Event.current.mousePosition))
        {
            var evt = Event.current;
            if ((evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform) &&
                DragAndDrop.objectReferences != null)
            {
                var go = DragAndDrop.objectReferences.FirstOrDefault(o => o is GameObject) as GameObject;
                if (go != null)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        if (IsPrefabAsset(go))
                        {
                            targetPrefab = go;
                            Repaint();
                        }
                    }
                    evt.Use();
                }
            }
        }

        GUI.color = old;
    }

    // ------------------- External viewport stubs -------------------
    private ExternalPreviewWindow childWindow;

    private void OpenViewport()
    {
        if (childWindow == null)
        {
            childWindow = ExternalPreviewWindow.Open(this);
        }
        externalViewportOpen = true;
        Repaint();
    }

    private void CloseViewport()
    {
        if (childWindow != null)
        {
            childWindow.Close();
            childWindow = null;
        }
        externalViewportOpen = false;
        Repaint();
    }

    public void NotifyChildViewportClosed()
    {
        externalViewportOpen = false;
        Repaint();
    }

    // ------------------- Minimal actions -------------------
    private static bool IsPrefabAsset(GameObject go)
    {
        var t = PrefabUtility.GetPrefabAssetType(go);
        return t != PrefabAssetType.NotAPrefab && t != PrefabAssetType.MissingAsset;
    }

    private int CountMatches(string pref)
    {
        if (string.IsNullOrEmpty(pref) || pref.Length < 3) return 0;
        return Resources.FindObjectsOfTypeAll<Transform>()
            .Count(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(pref));
    }

    private void RandomizeAll()
    {
        // Stub: leave parameter randomization to your main tool logic.
        // This exists so the toolbar button is wired and safe.
        ShowNotification(new GUIContent("RandomizeAll() placeholder"));
    }

    private void RunReplace()
    {
        // Stub: call your real replacement & transform pipeline here.
        ShowNotification(new GUIContent("Switch Placeholders (stub in reference build)"));
    }
}

// ------------------- External viewport window (simple stub) -------------------
public class ExternalPreviewWindow : EditorWindow
{
    private PlaceholderSwitcher owner;

    public static ExternalPreviewWindow Open(PlaceholderSwitcher owner)
    {
        var w = CreateInstance<ExternalPreviewWindow>();
        w.owner = owner;
        w.titleContent = new GUIContent("Viewport");
        w.minSize = new Vector2(480, 320);
        w.ShowUtility(); // floats above; easy to move/close
        return w;
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("External Viewport (stub)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("This window stands in for your real viewport.\n" +
                                "Close it to return control to the embedded preview.", MessageType.Info);
        if (GUILayout.Button("Close"))
            Close();
    }

    private void OnDestroy()
    {
        if (owner != null) owner.NotifyChildViewportClosed();
    }
}

#endif
