#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
========================================================
Placeholder Switcher — Module 9: UI Polish Pack
File: PlaceholderTool.Module9.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08 (Module 9)
- Adds dark "section header" row style to match your 20:59 look.
- Adds section/card helpers to reduce label clipping and keep
  consistent padding (BeginSection / EndSection / DrawHeader).
- Adds a preview overlay (dimmed, with centered instructional text)
  when either:
    • prefix is missing or < 3 chars, or
    • prefix is valid but no scene objects match.
- Adds vertical-only scroll helpers for the right column to avoid
  horizontal overflow. (BeginRightPaneScroll / EndRightPaneScroll)
- Adds compact label-width helpers to prevent “Pivot (affects …)”
  text from being cut off by the next column.
- Adds small “Recenter View” button utility for your viewer row.

HOW TO WIRE (non-destructive)
--------------------------------------------------------
You can adopt these helpers *incrementally*. Suggested spots:

1) Replace plain headers with:
   DrawHeader("Replace Object Placeholders");
   // …controls…
   // Use BeginSection/EndSection if you want a boxed body
   BeginSection();
   // controls…
   EndSection();

2) Under your preview surface:
   // var rect = GUILayoutUtility.GetRect(... existing viewer rect ...);
   DrawPreviewOverlayIfNeeded(rect,
       "Enter ≥ 3 letters for Placeholder Prefix to see objects.\n" +
       "Tip: You can also open the GameObject Library to pick a prefab.",
       "No matches found for the current prefix.");

3) Wrap the RIGHT column drawing with:
   BeginRightPaneScroll();
   // draw all right-column UI
   EndRightPaneScroll();

4) Around rows that were clipping:
   using (new ScopedLabelWidths(180f))  // tweak width as needed
   {
       // e.g. your Pivot row, longer labels, etc.
   }

5) Recenter viewer button next to your background row:
   if (GUILayout.Button("Recenter View", GUILayout.Width(110)))
       RequestPreviewRecenter();

No assumptions about your layout were changed; these are utilities you call
from your existing OnGUI/Draw* methods.
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // ---------- Styles / Layout ----------
    [SerializeField] private Vector2 _rightScrollPos = Vector2.zero;

    private GUIStyle _headerRowStyle;
    private GUIStyle _sectionBoxStyle;
    private GUIStyle _centerOverlayStyle;
    private GUIStyle _tinyLabelStyle;

    private Color _headerRowColor = new Color(0.18f, 0.18f, 0.18f, 1f); // dark grey row
    private const float _sectionPadding = 6f;

    private void EnsureUiStyles()
    {
        if (_headerRowStyle == null)
        {
            _headerRowStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 6, 6),
                normal = { textColor = EditorStyles.boldLabel.normal.textColor }
            };
        }
        if (_sectionBoxStyle == null)
        {
            _sectionBoxStyle = new GUIStyle("HelpBox")
            {
                padding = new RectOffset(10, 10, 8, 10),
                margin = new RectOffset(0, 0, 4, 8)
            };
        }
        if (_centerOverlayStyle == null)
        {
            _centerOverlayStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                wordWrap = true,
                richText = true,
                normal = { textColor = Color.white }
            };
        }
        if (_tinyLabelStyle == null)
        {
            _tinyLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false
            };
        }
    }

    // ---------- Section helpers ----------
    private void DrawHeader(string title)
    {
        EnsureUiStyles();
        var rect = GUILayoutUtility.GetRect(10, 24, GUILayout.ExpandWidth(true));
        // Dark background
        EditorGUI.DrawRect(rect, _headerRowColor);
        rect.x += 2; rect.width -= 4;
        GUI.Label(rect, title, _headerRowStyle);
    }

    private void BeginSection()
    {
        EnsureUiStyles();
        GUILayout.BeginVertical(_sectionBoxStyle);
        GUILayout.Space(_sectionPadding);
    }

    private void EndSection()
    {
        GUILayout.Space(2f);
        GUILayout.EndVertical();
    }

    // ---------- Right pane scroll (vertical only) ----------
    private void BeginRightPaneScroll()
    {
        // You can call this once before drawing the entire right pane.
        _rightScrollPos = EditorGUILayout.BeginScrollView(
            _rightScrollPos,    // state
            false,              // no horizontal scroll bar
            true,               // vertical scroll bar
            GUI.skin.horizontalScrollbar,
            GUI.skin.verticalScrollbar,
            GUILayout.ExpandHeight(true),
            GUILayout.ExpandWidth(true)
        );
    }

    private void EndRightPaneScroll()
    {
        EditorGUILayout.EndScrollView();
    }

    // ---------- Label width scoping ----------
    private float _savedLabelWidth = -1f;

    private void SetLabelWidth(float w)
    {
        if (_savedLabelWidth < 0f) _savedLabelWidth = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = w;
    }

    private void ResetLabelWidth()
    {
        if (_savedLabelWidth >= 0f)
        {
            EditorGUIUtility.labelWidth = _savedLabelWidth;
            _savedLabelWidth = -1f;
        }
    }

    private struct ScopedLabelWidths : System.IDisposable
    {
        private readonly PlaceholderSwitcher _w;
        public ScopedLabelWidths(float width)
        {
            _w = EditorWindow.focusedWindow as PlaceholderSwitcher;
            if (_w == null) _w = Resources.FindObjectsOfTypeAll<PlaceholderSwitcher>().FirstOrDefault();
            _w?.SetLabelWidth(width);
        }
        public void Dispose()
        {
            _w?.ResetLabelWidth();
        }
    }

    // ---------- Preview overlay ----------
    // Call this RIGHT AFTER you draw the preview texture in the viewer.
    private void DrawPreviewOverlayIfNeeded(Rect previewRect, string needPrefixMsg, string noMatchesMsg)
    {
        // Conditions:
        //  A) Prefix < 3 => show needPrefixMsg
        //  B) Prefix >= 3 but no matches => show noMatchesMsg
        //  Else => show nothing
        string overlayText = null;

        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3)
        {
            overlayText = needPrefixMsg;
        }
        else
        {
            // Count candidates in open scenes
            int count = Resources.FindObjectsOfTypeAll<Transform>()
                .Select(t => t ? t.gameObject : null)
                .Count(go => go != null && go.scene.IsValid() && go.name.StartsWith(prefix));

            if (count == 0)
                overlayText = noMatchesMsg;
        }

        if (string.IsNullOrEmpty(overlayText)) return;

        EnsureUiStyles();
        // Dim the viewer
        var dimColor = new Color(0f, 0f, 0f, 0.65f);
        EditorGUI.DrawRect(previewRect, dimColor);

        // Center message
        var padded = new Rect(previewRect.x + 12, previewRect.y + 12, previewRect.width - 24, previewRect.height - 24);
        GUI.Label(padded, overlayText, _centerOverlayStyle);

        // Small tip row at the bottom-left
        var tipRect = new Rect(previewRect.x + 8, previewRect.yMax - 18, previewRect.width - 16, 14);
        GUI.Label(tipRect, "Tip: Drag a prefab into the viewer to set Desired Asset.", _tinyLabelStyle);
    }

    // ---------- Viewer background row highlight ----------
    // Wrap your viewer background buttons row with this to give a dark header feel
    private void DrawViewerBackgroundRow(System.Action drawButtons)
    {
        EnsureUiStyles();
        var rowRect = GUILayoutUtility.GetRect(10, 26, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rowRect, _headerRowColor);
        GUILayout.BeginArea(rowRect);
        GUILayout.BeginHorizontal();
        GUILayout.Space(6);
        drawButtons?.Invoke();
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
        GUILayout.Space(2);
    }

    // ---------- Recenter request ----------
    // This just toggles a flag your preview loop can use to refit the camera.
    private bool _requestPreviewRecenter = false;
    private void RequestPreviewRecenter() => _requestPreviewRecenter = true;

    // Call from your preview drawing loop after you compute bounds & pivot:
    // if (_requestPreviewRecenter) { /* recompute distance/pivot */ _requestPreviewRecenter = false; }

    // ---------- Convenience: counts for "x objects found" inline hint ----------
    private int GetQuickPrefixMatchCount()
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3) return 0;
        return Resources.FindObjectsOfTypeAll<Transform>()
            .Select(t => t ? t.gameObject : null)
            .Count(go => go != null && go.scene.IsValid() && go.name.StartsWith(prefix));
    }
}
#endif
