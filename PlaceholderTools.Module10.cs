#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

/*
========================================================
Placeholder Switcher — Module 10: Glue & Wiring Helpers
File: PlaceholderTool.Module10.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08 (Module 10)
- Adds small, focused wrapper methods so you can use Module 9
  (UI polish) without rewriting your existing layout.
- Gives you:
    • UI_BeginRightPane() / UI_EndRightPane() for vertical-only scroll
    • UI_DrawHeader(title) + UI_BeginSection()/UI_EndSection() wrappers
    • UI_DrawPrefixIndicator() — shows "⚠ no assets found" / "X objects found"
      inline immediately after your prefix TextField
    • UI_DrawViewerOverlay(viewRect) — dim + message when prefix < 3 or no matches
    • UI_DrawViewerBackgroundRow(buttons) — dark header row for viewer background
    • UI_RequestRecenter() flag you can trigger from a button
- These call Module 9 internals (BeginRightPaneScroll, DrawHeader, etc.)
  so you don’t need to call those directly.

HOW TO USE (minimal touch)
--------------------------------------------------------
1) RIGHT COLUMN SCROLL
   UI_BeginRightPane();
   // ... your entire right-column UI ...
   UI_EndRightPane();

2) SECTION TITLES & BOXES
   UI_DrawHeader("Rotation Offset");
   UI_BeginSection();
   // rotation controls...
   UI_EndSection();

3) PREFIX STATUS (call this immediately after your prefix TextField)
   prefix = EditorGUILayout.TextField("Placeholder Prefix", prefix);
   UI_DrawPrefixIndicator(); // shows count or "⚠ no assets found"

4) VIEW OVERLAY (call right after you draw the viewer texture)
   var r = /* your viewer rect */;
   UI_DrawViewerOverlay(r);

5) VIEWER BACKGROUND ROW (dark row with your background buttons)
   UI_DrawViewerBackgroundRow(() =>
   {
       // your 3 buttons here (e.g. Current Skybox / Unity Skybox / Open Viewport)
   });

6) RECENTER (wire to a button)
   if (GUILayout.Button("Recenter View")) UI_RequestRecenter();
   // In your preview camera fit code:
   // if (_requestPreviewRecenter) { /* recompute fit */ _requestPreviewRecenter = false; }

Notes
--------------------------------------------------------
- This module is UI-only. It does not move or rename controls.
- If you don’t call these, nothing changes visually.
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // ------ PUBLIC WRAPPERS (call these from your OnGUI) ------

    public void UI_BeginRightPane()
    {
        BeginRightPaneScroll(); // from Module 9
    }

    public void UI_EndRightPane()
    {
        EndRightPaneScroll();   // from Module 9
    }

    public void UI_DrawHeader(string title)
    {
        DrawHeader(title);      // from Module 9
    }

    public void UI_BeginSection()
    {
        BeginSection();         // from Module 9
    }

    public void UI_EndSection()
    {
        EndSection();           // from Module 9
    }

    /// <summary>
    /// Call immediately AFTER you draw the Prefix TextField.
    /// Draws a right-aligned inline status: "X objects found" or "⚠ no assets found".
    /// Requires: 'prefix' field exists on this partial (it does).
    /// </summary>
    public void UI_DrawPrefixIndicator()
    {
        // Get the rect of the last drawn control (the TextField line)
        var last = GUILayoutUtility.GetLastRect();
        if (Event.current.type != EventType.Repaint) return;

        // Decide status text
        string text;
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3)
        {
            text = "⚠ enter ≥ 3 letters";
        }
        else
        {
            int count = GetQuickPrefixMatchCount(); // from Module 9
            text = count == 0 ? "⚠ no assets found" : $"{count} object{(count == 1 ? "" : "s")} found";
        }

        // Right-align inside the same line
        var content = new GUIContent(text);
        var size = EditorStyles.miniLabel.CalcSize(content);
        var r = new Rect(last.xMax - size.x - 4f, last.y + (last.height - size.y) * 0.5f, size.x + 2f, size.y + 2f);
        GUI.Label(r, content, EditorStyles.miniLabel);
    }

    /// <summary>
    /// Call right after you draw the viewer’s texture. Will dim and show instructions if needed.
    /// </summary>
    public void UI_DrawViewerOverlay(Rect viewRect)
    {
        DrawPreviewOverlayIfNeeded(
            viewRect,
            "Enter <b>≥ 3 letters</b> for <i>Placeholder Prefix</i> to preview.\n" +
            "Tip: <b>Open GameObject Library</b> to pick a prefab, or drag a prefab here.",
            "No placeholders match the current prefix.");
    }

    /// <summary>
    /// Surround your viewer background buttons with a dark header row.
    /// </summary>
    public void UI_DrawViewerBackgroundRow(System.Action drawButtons)
    {
        DrawViewerBackgroundRow(drawButtons);
    }

    /// <summary>
    /// Trigger the preview camera to refit (you must consume the flag in your viewer code).
    /// </summary>
    public void UI_RequestRecenter()
    {
        RequestPreviewRecenter();
    }
}
#endif
