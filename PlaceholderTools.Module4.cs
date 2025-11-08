#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/*
========================================================
Placeholder Switcher — Module 4: GameObject Library
File: PlaceholderTool.Module4.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08 (Module 4)
- Added a separate prefab library window to browse/select
  Desired Assets:
  • Filters to Prefab assets that contain renderable meshes.
  • Large-by-default window; supports ShowUtility() to "float"
    above Editor windows (Unity’s best-approx for on-top).
  • Search bar (name contains), live filtering.
  • Thumbnail size slider (64–192 px).
  • Click to select; Double-click or "Use" button to set into
    Placeholder Switcher (Desired Asset).
  • Drag-and-drop: start a drag from any thumbnail to assign.
  • "Keep on top" toggle keeps it as a Utility window and
    repositions alongside the main tool.
- Owner integration:
  • Partial method DrawLibraryControlsRow() to render an
    "Open GameObject Library" button from your main window.
  • Callback OnLibraryPicked(GameObject) updates targetPrefab.
- Multi-mesh awareness:
  • The window only lists prefabs that have MeshRenderer or
    SkinnedMeshRenderer in children (so multi-part prefabs
    appear). The preview still uses AssetPreview thumbnails.
- No changes to switching/preview logic here.

USAGE
--------------------------------------------------------
1) In your main window OnGUI, call:
      DrawLibraryControlsRow();
   wherever you want the "Open GameObject Library" button.

2) The Library window opens docked to the right of your tool,
   can be resized, and supports search + size slider.

3) Double-click a prefab (or click "Use") to assign it as the
   Desired Asset in the Placeholder Switcher.

NOTES
--------------------------------------------------------
• Unity has no true "always-on-top"; ShowUtility() gets close.
• AssetPreview generation is async; thumbnails may appear a
  moment after the window opens as Unity bakes them.
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // Renders a compact row with a button to open the Library window.
    // Call this from your main layout (e.g., under your big title).
    private void DrawLibraryControlsRow()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open GameObject Library", GUILayout.Height(22)))
            {
                GameObjectLibraryWindow.ShowWindow(this);
            }
            GUILayout.FlexibleSpace();
        }
    }

    // Callback used by the library window when a prefab is picked.
    internal void OnLibraryPicked(GameObject picked)
    {
        if (picked == null) return;
        // Assign to your main field used elsewhere (Module 1/3).
        targetPrefab = picked;
        Repaint();
        ShowNotification(new GUIContent($"Desired Asset set: {picked.name}"));
    }
}


/// <summary>
/// A prefab library browser for picking Desired Asset.
/// </summary>
internal class GameObjectLibraryWindow : EditorWindow
{
    private const float DefaultWidth = 520f;
    private const float DefaultHeight = 700f;

    private PlaceholderSwitcher _owner;
    private Vector2 _scroll;
    private string _search = "";
    private float _thumbSize = 112f;
    private bool _keepOnTop = true;

    private List<GameObject> _allPrefabs = new List<GameObject>();
    private List<GameObject> _filtered = new List<GameObject>();

    private double _nextRescanTime;

    // --- Open/Show ---
    public static void ShowWindow(PlaceholderSwitcher owner)
    {
        // Try to position to the right of the owner
        var r = owner.position;
        var w = CreateInstance<GameObjectLibraryWindow>();
        w._owner = owner;
        w.titleContent = new GUIContent("GameObject Library");

        // Utility window floats above editor panes (closest to "always on top")
        w.ShowUtility();

        // Size + position
        var pos = new Rect(
            r.x + Mathf.Max(400f, r.width * 0.55f),
            r.y,
            DefaultWidth,
            DefaultHeight);
        w.position = pos;
        w.minSize = new Vector2(380f, 400f);

        w.Focus();
        w.Rescan();
    }

    private void OnEnable()
    {
        EditorApplication.update += OnEditorUpdate;
        // Initial lazy rescan
        _nextRescanTime = EditorApplication.timeSinceStartup + 0.2d;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnEditorUpdate()
    {
        // Lazy rescan to populate on first open or after domain reloads
        if (EditorApplication.timeSinceStartup >= _nextRescanTime)
        {
            _nextRescanTime = double.MaxValue;
            if (_allPrefabs.Count == 0) Rescan();
        }
        // Keep utility “on top” feel: refocus when clicked elsewhere (best-effort)
        if (_keepOnTop && focusedWindow != this && mouseOverWindow == this)
            Focus();
        Repaint();
    }

    private void Rescan()
    {
        _allPrefabs.Clear();
        var guids = AssetDatabase.FindAssets("t:Prefab");
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;

            // Multi-mesh awareness: only include if it renders something
            if (HasRenderable(go))
                _allPrefabs.Add(go);
        }
        ApplyFilter();
    }

    private static bool HasRenderable(GameObject prefab)
    {
        if (prefab == null) return false;
        // Keep prefabs that have at least one renderer & a mesh-bearing component
        var mr = prefab.GetComponentsInChildren<MeshRenderer>(true);
        var smr = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var mf = prefab.GetComponentsInChildren<MeshFilter>(true);
        bool anyRenderer = (mr != null && mr.Length > 0) || (smr != null && smr.Length > 0);
        bool anyMesh = (mf != null && mf.Any(m => m && m.sharedMesh))
                       || (smr != null && smr.Any(s => s && s.sharedMesh));
        return anyRenderer && anyMesh;
    }

    private void ApplyFilter()
    {
        string s = (_search ?? "").Trim();
        if (string.IsNullOrEmpty(s))
        {
            _filtered = _allPrefabs.ToList();
        }
        else
        {
            s = s.ToLowerInvariant();
            _filtered = _allPrefabs.Where(p => p && p.name.ToLowerInvariant().Contains(s)).ToList();
        }
    }

    private void OnGUI()
    {
        DrawToolbar();

        EditorGUILayout.Space(2);
        var rect = GUILayoutUtility.GetRect(10, 9999, 10, 9999, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
        {
            _scroll = scroll.scrollPosition;
            DrawGrid(rect);
        }

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            _keepOnTop = EditorGUILayout.ToggleLeft("Keep on top (Utility window)", _keepOnTop);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Rescan", GUILayout.Width(80))) Rescan();
        }
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("Search:", GUILayout.Width(50));
            EditorGUI.BeginChangeCheck();
            _search = GUILayout.TextField(_search, EditorStyles.toolbarTextField, GUILayout.MinWidth(160));
            if (EditorGUI.EndChangeCheck())
                ApplyFilter();

            GUILayout.FlexibleSpace();

            GUILayout.Label("Thumb", GUILayout.Width(44));
            _thumbSize = GUILayout.HorizontalSlider(_thumbSize, 64f, 192f, GUILayout.Width(120));
            _thumbSize = Mathf.Round(_thumbSize / 4f) * 4f;

            if (GUILayout.Button("Close", EditorStyles.toolbarButton, GUILayout.Width(60)))
                Close();
        }
    }

    private void DrawGrid(Rect contentRect)
    {
        if (_filtered == null || _filtered.Count == 0)
        {
            GUILayout.FlexibleSpace();
            var c = GUI.color;
            GUI.color = Color.gray;
            GUILayout.Label("No prefabs match your search.", EditorStyles.centeredGreyMiniLabel);
            GUI.color = c;
            GUILayout.FlexibleSpace();
            return;
        }

        float pad = 8f;
        float cell = _thumbSize + 40f; // thumbnail + label area
        int cols = Mathf.Max(1, Mathf.FloorToInt((position.width - pad * 2) / (cell)));
        int rowCount = Mathf.CeilToInt((float)_filtered.Count / cols);

        int index = 0;
        for (int r = 0; r < rowCount; r++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(pad);
                for (int c = 0; c < cols && index < _filtered.Count; c++, index++)
                {
                    DrawCell(_filtered[index], cell);
                }
                GUILayout.FlexibleSpace();
            }
            GUILayout.Space(6);
        }
    }

    private void DrawCell(GameObject prefab, float cellWidth)
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(cellWidth)))
        {
            // Thumbnail (request from AssetPreview; async)
            Texture2D tex = AssetPreview.GetAssetPreview(prefab);
            if (tex == null) tex = AssetPreview.GetMiniThumbnail(prefab);

            var rect = GUILayoutUtility.GetRect(_thumbSize, _thumbSize, GUILayout.Width(_thumbSize), GUILayout.Height(_thumbSize));
            EditorGUI.DrawPreviewTexture(rect, tex, null, ScaleMode.ScaleToFit);

            // Handle drag
            var evt = Event.current;
            if (evt.type == EventType.MouseDown && rect.Contains(evt.mousePosition))
            {
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = new UnityEngine.Object[] { prefab };
                DragAndDrop.StartDrag(prefab.name);
                evt.Use();
            }

            // Name
            var name = prefab.name;
            GUILayout.Label(name, EditorStyles.boldLabel, GUILayout.Height(18));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select", GUILayout.Width(60)))
                {
                    Selection.activeObject = prefab;
                    EditorGUIUtility.PingObject(prefab);
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Use", GUILayout.Width(48)))
                {
                    Pick(prefab);
                }
            }

            // Double-click to use
            if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
            {
                Pick(prefab);
                Event.current.Use();
            }
        }
    }

    private void Pick(GameObject prefab)
    {
        if (_owner != null)
        {
            _owner.OnLibraryPicked(prefab);
            _owner.Focus();
        }
        Close();
    }
}
#endif
