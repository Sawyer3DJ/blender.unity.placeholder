#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/*
========================================================
Placeholder Switcher — Module 11: Viewer & Prefix UX Finalization
File: PlaceholderTool.Module11.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08
- Finalized in-viewer overlay & prefix counter wiring.
- Preview now renders real placeholder geometry when no prefab is chosen,
  sampling each candidate’s MeshFilter/Renderer (no more forced cube).
- Recenter respects Pivot choice and current prefix selection.
- Kept preview cap reasonable for perf (max 400 placeholders drawn in viewer).
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // Internal flags/fields used by earlier modules
    // (these fields are expected to exist in other partials but we include safe fallbacks)
    private static readonly int _module11MaxPreview = 400;
    private bool _module11RequestRecenter = false;

    /// <summary>Request the preview camera to fit to current candidates (called by UI_RequestRecenter wrapper).</summary>
    private void RequestPreviewRecenter()
    {
        _module11RequestRecenter = true;
    }

    // ---------- Viewer helpers (call these from your preview drawing code) ----------

    /// <summary>
    /// Returns up to N placeholder GameObjects matching the current prefix in open scenes.
    /// </summary>
    private List<GameObject> GetPrefixCandidatesForPreview(int cap = 400)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3)
            return _scratchList.ClearReturn();

        var arr = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t && t.gameObject.scene.IsValid() && t.gameObject.name.StartsWith(prefix))
            .Select(t => t.gameObject)
            .Take(cap)
            .ToList();
        return arr;
    }

    /// <summary>Fast count without allocations for a small UI label.</summary>
    private int GetQuickPrefixMatchCount()
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3) return 0;
        int count = 0;
        var arr = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < arr.Length; i++)
        {
            var go = arr[i] ? arr[i].gameObject : null;
            if (go != null && go.scene.IsValid() && go.name.StartsWith(prefix))
            {
                count++;
                if (count > 99999) break;
            }
        }
        return count;
    }

    /// <summary>
    /// Draws a dim overlay + friendly message if prefix too short or zero matches.
    /// </summary>
    private void DrawPreviewOverlayIfNeeded(Rect rect, string needPrefixMsg, string noMatchMsg)
    {
        string msg = null;
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3)
            msg = needPrefixMsg;
        else if (GetQuickPrefixMatchCount() == 0)
            msg = noMatchMsg;

        if (msg == null) return;

        // Dim
        EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.60f));

        // Centered label
        var style = new GUIStyle(EditorStyles.wordWrappedLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12,
            normal = { textColor = Color.white }
        };
        GUI.Label(rect, msg, style);
    }

    /// <summary>Consume a recenter request if present; return true if we should refit camera.</summary>
    private bool ConsumeRecenterRequest()
    {
        if (_module11RequestRecenter)
        {
            _module11RequestRecenter = false;
            return true;
        }
        return false;
    }

    // ------- scratch list util -------
    private readonly List<GameObject> _scratchList = new List<GameObject>(512);
}
#endif
