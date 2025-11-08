#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
========================================================
Placeholder Switcher — Module 7: Save From Preview As…
File: PlaceholderTool.Module7.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08 (Module 7)
- Adds a dedicated "Load / Save" UI section with a primary
  button: "Save From Preview As…".
- Button enable rules (per your spec):
    • 0 preview items  → disabled + message:
      "Nothing to save, search for objects via a prefix to enable saving."
    • 1 preview item  → enabled (can save that root as a prefab).
    • >1 preview items → disabled + message:
      "Multiple placeholders detected — combine all objects first."
- Non-destructive save: the tool duplicates the single matched
  scene object into a temporary clone and saves that clone as a prefab,
  then destroys the clone.
- Uses a save dialog each time (no reliance on global savePath),
  so you can “Save As” freely.
- Helper methods to count and fetch the exact preview candidates,
  matching the prefix logic used elsewhere.

HOW TO WIRE
--------------------------------------------------------
1) In your UI (left column under the viewer, where you want "Load / Save"):
   call DrawLoadSaveSection().

2) Nothing else required. The section will:
   - Show the button + contextual message.
   - Handle the prefab save logic safely.
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // --- Public UI entry point for this module ---
    // Place this under your viewer (beneath the background controls).
    private void DrawLoadSaveSection()
    {
        // Section header (keep consistent with your styled headers)
        GUILayout.Label("Load / Save", EditorStyles.boldLabel);

        // Count previewable roots (prefix must be ≥3 chars to count)
        int count = GetPreviewRootCount();

        // Save button row
        using (new EditorGUI.DisabledScope(count != 1))
        {
            if (GUILayout.Button(new GUIContent("Save From Preview As…",
                    "Save the SINGLE object currently previewed into a new prefab."),
                GUILayout.Height(28)))
            {
                TrySaveSinglePreviewRootAsPrefab();
            }
        }

        // Contextual messages
        if (count == 0)
        {
            EditorGUILayout.HelpBox(
                "Nothing to save — search for objects via a prefix to enable saving.",
                MessageType.Info);
        }
        else if (count > 1)
        {
            EditorGUILayout.HelpBox(
                "Multiple placeholders detected — combine all objects first.",
                MessageType.Warning);
        }

        // (Optional) You can add your “Load Asset From File…” button near here later.
    }

    // --- Save logic for the single preview root ---
    private void TrySaveSinglePreviewRootAsPrefab()
    {
        var roots = GetPreviewCandidatesForCentering(); // from Module 6; exact same prefix logic
        // Filter to distinct root GameObjects (already roots of matches in that helper)
        roots = roots.Where(g => g != null).ToList();

        if (roots.Count != 1)
        {
            // Safety guard: UI should have disabled the button already
            EditorUtility.DisplayDialog("Cannot Save", 
                roots.Count == 0 
                    ? "Nothing to save. Enter a prefix (≥ 3 characters) that finds exactly one object." 
                    : "Multiple objects detected. Combine all objects first, then save.",
                "OK");
            return;
        }

        var src = roots[0];
        if (!src)
        {
            EditorUtility.DisplayDialog("Cannot Save", "The selected object is missing.", "OK");
            return;
        }

        // Pick a file path
        string suggested = SanitizeFileName(src.name);
        string path = EditorUtility.SaveFilePanelInProject(
            "Save From Preview As…",
            string.IsNullOrEmpty(suggested) ? "PreviewObject" : suggested,
            "prefab",
            "Choose where to save the prefab");

        if (string.IsNullOrEmpty(path)) return; // cancelled

        // Duplicate a temporary copy to save (non-destructive to scene)
        GameObject clone = null;
        try
        {
            clone = Instantiate(src);
            clone.name = src.name;

            // Ensure clone is at the scene root (prefab saver doesn’t need the original parent)
            clone.transform.SetParent(null, true);

            // Save as prefab asset
            var prefab = PrefabUtility.SaveAsPrefabAsset(clone, path);
            if (prefab != null)
                Debug.Log($"Saved prefab: {path}");
            else
                Debug.LogError("Failed to save prefab.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Save failed: {ex.Message}");
        }
        finally
        {
            if (clone) DestroyImmediate(clone);
        }
    }

    // --- Preview root count (0 / 1 / many) ---
    private int GetPreviewRootCount()
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3) return 0;
        var roots = GetPreviewCandidatesForCentering(); // reuses Module 6 helper
        return roots.Count(g => g != null);
    }

    // --- Utility: sanitize filename from GameObject name ---
    private static string SanitizeFileName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "PreviewObject";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        foreach (var ch in invalid) raw = raw.Replace(ch, '_');
        return raw.Trim();
    }
}
#endif
