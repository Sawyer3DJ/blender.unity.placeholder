#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/*
========================================================
Placeholder Switcher — Module 13: GameObject Library Window
File: PlaceholderTool.Module13.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08
- Adds a dedicated "GameObject Library" window that scans Project for prefabs.
- Large-by-default window sized to sit at the right side of screen.
- Search bar + thumbnail size slider + click-to-select behavior.
- Selection assigns back to PlaceholderSwitcher’s Desired Asset field.
========================================================
*/

public class PlaceholderLibraryWindow : EditorWindow
{
    public static PlaceholderLibraryWindow Instance;

    private string _search = "";
    private float _thumbSize = 96f;
    private Vector2 _scroll;
    private List<GameObject> _results = new List<GameObject>();

    [MenuItem("Window/Placeholder Tools/GameObject Library")]
    public static void OpenWindow()
    {
        var w = GetWindow<PlaceholderLibraryWindow>("GameObject Library");
        w.minSize = new Vector2(480, 420);
        w.position = new Rect(Screen.currentResolution.width * 0.55f, 120, Screen.currentResolution.width * 0.40f, Screen.currentResolution.height * 0.75f);
        w.Show();
    }

    private void OnEnable()
    {
        Instance = this;
        Refresh();
    }

    private void OnDisable()
    {
        Instance = null;
    }

    private void Refresh()
    {
        _results.Clear();
        var guids = AssetDatabase.FindAssets(string.IsNullOrEmpty(_search) ? "t:prefab" : $"t:prefab {_search}");
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go != null) _results.Add(go);
        }
    }

    private void OnGUI()
    {
        // Header
        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        GUILayout.Label("Prefab Library", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        GUILayout.Label("Thumb", GUILayout.Width(40));
        _thumbSize = GUILayout.HorizontalSlider(_thumbSize, 48f, 160f, GUILayout.Width(120));
        _thumbSize = Mathf.Round(_thumbSize / 4f) * 4f;
        EditorGUILayout.EndHorizontal();

        // Search
        EditorGUILayout.BeginHorizontal();
        var newSearch = EditorGUILayout.TextField(_search, "SearchTextField");
        if (GUILayout.Button("Clear", GUILayout.Width(60))) newSearch = "";
        if (newSearch != _search) { _search = newSearch; Refresh(); }
        EditorGUILayout.EndHorizontal();

        // Grid
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        int cols = Mathf.Max(1, Mathf.FloorToInt((position.width - 24f) / (_thumbSize + 16f)));
        int idx = 0;
        EditorGUILayout.BeginVertical();
        while (idx < _results.Count)
        {
            EditorGUILayout.BeginHorizontal();
            for (int c = 0; c < cols && idx < _results.Count; c++, idx++)
            {
                var go = _results[idx];
                EditorGUILayout.BeginVertical(GUILayout.Width(_thumbSize + 12f));
                var tex = AssetPreview.GetAssetPreview(go) ?? AssetPreview.GetMiniThumbnail(go);
                if (GUILayout.Button(tex, GUILayout.Width(_thumbSize), GUILayout.Height(_thumbSize)))
                {
                    // Assign back to PlaceholderSwitcher (active window if open)
                    var sw = Resources.FindObjectsOfTypeAll<PlaceholderSwitcher>().FirstOrDefault();
                    if (sw != null) sw.SetDesiredPrefab(go);
                    else EditorGUIUtility.PingObject(go);
                }
                GUILayout.Label(go.name, EditorStyles.miniLabel, GUILayout.Width(_thumbSize + 8f));
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();
    }
}
#endif
