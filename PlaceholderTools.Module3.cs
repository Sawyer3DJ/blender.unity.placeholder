#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
========================================================
Placeholder Switcher — Module 3: Replace Header & Safety
File: PlaceholderTool.Module3.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08 (Module 3)
- Added "Replace Object Placeholders" header strip intended
  to be drawn ABOVE the 3D viewer:
  • Placeholder Prefix field (live count on the same row).
  • Desired Asset (Prefab) picker.
  • Forced Name (optional) + Use incremental naming toggle
    with mutual disable logic (as requested earlier).
  • "Automatically switch placeholders to scene" toggle with
    an inline warning (live when enabled).
- Implemented safe, debounced auto-switch:
  • Triggers when: prefix length ≥ 3, count > 0, prefab set.
  • Debounce timer avoids rapid thrashing during edits.
  • Shows a one-time info banner when enabling to remind about
    the 64-step undo guidance.
- Provided helper CountPlaceholdersMatchingPrefix() used by
  both UI counter and auto-switch condition.
- No changes to preview/transform/combining logic; this module
  only introduces the header UI + small scheduler to call your
  existing RunReplace() method.
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // ------- Integration note -------
    // This module expects these serialized fields to already exist in your main class (Module 1):
    //   [SerializeField] private string prefix;
    //   [SerializeField] private GameObject targetPrefab;
    //   [SerializeField] private string forcedName;
    //   [SerializeField] private bool useIncrementalNaming;
    //
    // If your Module 1 named them differently, either:
    //   - rename here to match, or
    //   - add forwarding properties in Module 1.

    // ------- Module 3 state -------
    [SerializeField] private bool autoSwitchEnabled = false;
    [SerializeField] private double _autoSwitchNextTime = 0d;
    [SerializeField] private int    _lastPrefixHash = 0;
    [SerializeField] private UnityEngine.Object _lastPrefabRef = null;
    [SerializeField] private int    _lastCount = -1;
    [SerializeField] private bool   _autoSwitchBannerShown = false;

    private const double AutoSwitchDebounceSec = 0.35d; // feel-good debounce

    // Expose a clean UI entry point for the header
    private void DrawReplaceHeaderPanel()
    {
        DrawDarkHeaderRow("Replace Object Placeholders");

        using (new EditorGUILayout.VerticalScope("box"))
        {
            // Row 1: Prefix + live counter
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                prefix = EditorGUILayout.TextField(new GUIContent("Placeholder Prefix", "Minimum 3 characters"), prefix);
                bool changed = EditorGUI.EndChangeCheck();

                // Live counter (computed with guard)
                var (statusText, statusColor) = GetPrefixStatusSummary(prefix);
                var c = GUI.color;
                GUI.color = statusColor;
                GUILayout.Label(statusText, EditorStyles.boldLabel, GUILayout.Width(160));
                GUI.color = c;

                if (changed)
                {
                    // schedule an auto-switch check if user edits the prefix
                    ScheduleAutoSwitch();
                }
            }

            // Row 2: Desired asset picker
            EditorGUI.BeginChangeCheck();
            targetPrefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Desired Asset (Prefab)", "Drag from Project or drop into the 3D viewer"),
                targetPrefab, typeof(GameObject), false);
            if (EditorGUI.EndChangeCheck())
            {
                ScheduleAutoSwitch();
            }

            // Row 3: Forced Name + incremental naming (mutually exclusive)
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(useIncrementalNaming))
                {
                    forcedName = EditorGUILayout.TextField(new GUIContent("Forced Name (optional)"), forcedName);
                }
                GUILayout.Space(10);
                bool newInc = EditorGUILayout.ToggleLeft(new GUIContent("Use incremental naming", "Names like Base_001, Base_002…"), useIncrementalNaming, GUILayout.Width(200));
                if (newInc != useIncrementalNaming)
                {
                    useIncrementalNaming = newInc;
                    if (useIncrementalNaming) forcedName = string.Empty; // clear forcedName if enabling increments
                }
            }

            // Row 4: Auto-switch + inline warning
            var canAutoNow = CanAutoSwitchNow(out int foundCount);
            using (new EditorGUILayout.HorizontalScope())
            {
                bool newAuto = EditorGUILayout.ToggleLeft(
                    new GUIContent("Automatically switch placeholders to scene",
                        "When enabled, the current Desired Asset will replace all placeholders matching the prefix in real time."),
                    autoSwitchEnabled);

                if (newAuto != autoSwitchEnabled)
                {
                    autoSwitchEnabled = newAuto;
                    if (autoSwitchEnabled && !_autoSwitchBannerShown)
                    {
                        _autoSwitchBannerShown = true;
                        ShowNotification(new GUIContent("Auto-switch ON: switching will happen in real-time.\nYou have 64 undos — use them wisely."));
                    }
                    ScheduleAutoSwitch(forceSoon:true);
                }

                // Inline state hint (compact; the big caution sits below)
                GUILayout.FlexibleSpace();
                GUI.enabled = false;
                GUILayout.TextField(canAutoNow ? "Ready" : "Waiting…", GUILayout.Width(90));
                GUI.enabled = true;
            }

            if (autoSwitchEnabled)
            {
                EditorGUILayout.HelpBox(
                    "Auto-switch is enabled. Any time the prefix finds objects and a Desired Asset is set, the tool will replace them automatically.\n" +
                    "⚠ This acts on your scene in real-time. You have up to ~64 undo steps in the editor.",
                    MessageType.Warning);
            }
        }

        // Throttle auto-switch from the editor update
        HandleAutoSwitchUpdateTick();
    }

    // ---------- Auto-switch scheduler & tick ----------

    private void ScheduleAutoSwitch(bool forceSoon = false)
    {
        _lastPrefixHash = (prefix ?? string.Empty).GetHashCode();
        _lastPrefabRef = targetPrefab;
        _lastCount = -1; // force a recount on next tick
        _autoSwitchNextTime = EditorApplication.timeSinceStartup + (forceSoon ? 0.05d : AutoSwitchDebounceSec);
    }

    private void HandleAutoSwitchUpdateTick()
    {
        // Only poll when the window is present and auto is on
        if (!autoSwitchEnabled) return;

        // Basic cooldown
        if (EditorApplication.timeSinceStartup < _autoSwitchNextTime) return;

        // If anything relevant changed, or count unknown, recompute
        bool anyChanged = _lastPrefixHash != (prefix ?? string.Empty).GetHashCode()
                          || _lastPrefabRef != (UnityEngine.Object)targetPrefab
                          || _lastCount < 0;

        if (!anyChanged) return;

        // Count now
        int count = CountPlaceholdersMatchingPrefix(prefix);
        _lastCount = count;
        _lastPrefixHash = (prefix ?? string.Empty).GetHashCode();
        _lastPrefabRef = targetPrefab;

        // Decide to run
        if (CanAutoSwitchNow(out _))
        {
            // Guard against missing method if Module1 renamed RunReplace
            var mi = GetType().GetMethod("RunReplace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (mi != null)
            {
                try
                {
                    mi.Invoke(this, null);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Auto-switch failed: {e.Message}");
                }
            }
            else
            {
                // If RunReplace doesn't exist yet (early wiring), just notify once
                ShowNotification(new GUIContent("Auto-switch ready (RunReplace unavailable in this build)."));
            }
        }

        // small cooldown after act/check
        _autoSwitchNextTime = EditorApplication.timeSinceStartup + AutoSwitchDebounceSec;
        Repaint();
    }

    private bool CanAutoSwitchNow(out int foundCount)
    {
        foundCount = 0;
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3) return false;
        if (targetPrefab == null || !IsPrefabAsset(targetPrefab)) return false;
        foundCount = CountPlaceholdersMatchingPrefix(prefix);
        return foundCount > 0;
    }

    private int CountPlaceholdersMatchingPrefix(string pfx)
    {
        if (string.IsNullOrEmpty(pfx) || pfx.Length < 3) return 0;

        int total = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (!s.IsValid() || !s.isLoaded) continue;
            var roots = s.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                total += CountRecursive(roots[r].transform, pfx);
            }
        }
        return total;

        static int CountRecursive(Transform t, string p)
        {
            if (t == null) return 0;
            int c = t.name.StartsWith(p, StringComparison.Ordinal) ? 1 : 0;
            for (int i = 0; i < t.childCount; i++) c += CountRecursive(t.GetChild(i), p);
            return c;
        }
    }

    private (string text, Color color) GetPrefixStatusSummary(string pfx)
    {
        if (string.IsNullOrEmpty(pfx) || pfx.Length < 3)
            return ("enter ≥3 chars", EditorGUIUtility.isProSkin ? new Color(1f, .65f, 0f) : new Color(.7f, .35f, 0f));

        int n = CountPlaceholdersMatchingPrefix(pfx);
        if (n <= 0)
            return ("⚠ no assets found", Color.red);

        return ($"{n} objects found", EditorGUIUtility.isProSkin ? new Color(0.6f, 1f, 0.6f) : new Color(0f, 0.5f, 0f));
    }
}
#endif
