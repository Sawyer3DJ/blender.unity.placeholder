#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/*
========================================================
Placeholder Switcher — Module 2: Transform Tools (UI)
File: PlaceholderTool.Module2.cs
Target: v1.0.0 (UI-only; no switching/combining changes)

CHANGE LOG
--------------------------------------------------------
2025-11-08 (Module 2)
- Added polished right-column UI ("Transform Tools") as a partial
  without altering base logic:
  • Dark header rows for section titles.
  • Rotation Offset: XYZ fields on one row, sliders beneath. Modes:
      - Placeholder Rotation
      - New Rotation
      - Seed Value on Y (with seed + Randomise button)
  • Scale Offset: Single uniform scale (XYZ locked), slider beneath.
      - Modes: Placeholder Scale / New Scale / Seed Value
      - Seed + "Scale clamping" Min/Max (validated & auto-swap)
  • Location Offset: Local/World space, XYZ on one row with sliders
      beneath, optional random seed, "Influenced Axes" buttons,
      clamping per-axis (X/Y/Z) with Min/Max.
  • All numeric inputs are guarded; seeds support up to 10,000,000.
- No dependencies on Module 1 helpers (local clamp helper included).
- Pure UI: state is serialized fields; wiring to apply on replace/
  preview remains in Module 5+ per roadmap.

INTENDED INTEGRATION
--------------------------------------------------------
- Keep your existing PlaceholderTool window (now Module1).
- In your right-hand column, call:
      DrawTransformToolsPanel();
  This method is defined below.
- Do not remove your existing logic; this only replaces the ad-hoc
  UI stubs for Rotation/Scale/Location with a unified panel.

NEXT MODULES (per plan)
--------------------------------------------------------
3) Replace Header & Safety (auto-switch + 64-undo warning, counter)
4) Library picker (always-on-top, prefab-only, multi-mesh aware)
5) Placeholder mesh fidelity in preview
6) Combine/Move/Pivot logic & warnings
7) Save From Preview As… rules
8) ConvertToShrub + collision
9) Randomize All Parameters (excludes naming/parents/pivot)
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // ====== Serialized UI state (UI only; logic wires later) ======

    // --- Rotation Offset ---
    private enum RotationMode { PlaceholderRotation, NewRotation, SeedValueOnY }
    [SerializeField] private RotationMode ui_rotationMode = RotationMode.PlaceholderRotation;
    [SerializeField] private Vector3 ui_rotEuler = Vector3.zero;      // XYZ degrees
    [SerializeField] private int    ui_rotSeed  = 1234;               // 1..10_000_000
    [SerializeField] private bool   ui_rotX = true, ui_rotY = true, ui_rotZ = true; // reserved for per-axis enabling

    // --- Scale Offset ---
    private enum ScaleMode { PlaceholderScale, NewScale, SeedValue }
    [SerializeField] private ScaleMode ui_scaleMode = ScaleMode.PlaceholderScale;
    [SerializeField] private float     ui_scaleUniform = 1f;          // single value locks XYZ together
    [SerializeField] private int       ui_scaleSeed    = 321;         // 1..10_000_000
    [SerializeField] private float     ui_scaleClampMin = 0.1f;
    [SerializeField] private float     ui_scaleClampMax = 3.0f;

    // --- Location Offset ---
    private enum LocSpace { Local, World }
    [SerializeField] private LocSpace ui_locSpace = LocSpace.Local;
    [SerializeField] private Vector3  ui_locDelta = Vector3.zero;
    [SerializeField] private bool     ui_locUseSeed = false;
    [SerializeField] private int      ui_locSeed    = 4567;           // 1..10_000_000
    [SerializeField] private bool     ui_locX = true, ui_locY = true, ui_locZ = true; // influenced axes
    [SerializeField] private float     ui_locClampXMin = -1f, ui_locClampXMax = 1f;
    [SerializeField] private float     ui_locClampYMin = -1f, ui_locClampYMax = 1f;
    [SerializeField] private float     ui_locClampZMin = -1f, ui_locClampZMax = 1f;

    // ====== Styles ======
    private GUIStyle _miniRight;

    // ====== Public: draw the whole Transform Tools panel ======
    private void DrawTransformToolsPanel()
    {
        DrawDarkHeaderRow("Transform Tools");

        // --- Rotation Offset ---
        using (new EditorGUILayout.VerticalScope("box"))
        {
            DrawDarkHeaderRow("Rotation Offset");
            ui_rotationMode = (RotationMode)EditorGUILayout.EnumPopup("Rotation Mode", ui_rotationMode);

            // Row: XYZ numeric fields
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("Rotation (adds to placeholder)"), GUILayout.Width(180));
                ui_rotEuler.x = EditorGUILayout.FloatField(ui_rotEuler.x);
                ui_rotEuler.y = EditorGUILayout.FloatField(ui_rotEuler.y);
                ui_rotEuler.z = EditorGUILayout.FloatField(ui_rotEuler.z);
            }
            // Sliders beneath the row
            SliderTriple(ref ui_rotEuler, -180f, 180f);

            if (ui_rotationMode == RotationMode.SeedValueOnY)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    ui_rotSeed = ClampIntInclusive(EditorGUILayout.IntField(new GUIContent("Random rotation seed (Y)"), ui_rotSeed), 1, 10_000_000);
                    if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                        ui_rotSeed = Random.Range(1, 10_000_001);
                }
                EditorGUILayout.HelpBox("Per-object deterministic Y rotation from seed; the XYZ offset above is added on top.", MessageType.Info);
            }
        }

        // --- Scale Offset ---
        using (new EditorGUILayout.VerticalScope("box"))
        {
            DrawDarkHeaderRow("Scale Offset");
            ui_scaleMode = (ScaleMode)EditorGUILayout.EnumPopup("Scaling Mode", ui_scaleMode);

            // Row: single uniform field (XYZ locked together)
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("Scale (multiplies placeholder scale)"), GUILayout.Width(220));
                ui_scaleUniform = Mathf.Max(0.0001f, EditorGUILayout.FloatField(ui_scaleUniform));
            }
            // Slider directly beneath
            ui_scaleUniform = EditorGUILayout.Slider(ui_scaleUniform, 0.01f, 10f);

            if (ui_scaleMode == ScaleMode.SeedValue)
            {
                // Seed row
                using (new EditorGUILayout.HorizontalScope())
                {
                    ui_scaleSeed = ClampIntInclusive(EditorGUILayout.IntField(new GUIContent("Random scaling seed"), ui_scaleSeed), 1, 10_000_000);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                        ui_scaleSeed = Random.Range(1, 10_000_001);
                }

                // Clamping row
                EnsureMiniRight();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Scale clamping", GUILayout.Width(120));
                    GUILayout.Label("Min", _miniRight, GUILayout.Width(28));
                    ui_scaleClampMin = EditorGUILayout.FloatField(ui_scaleClampMin, GUILayout.Width(70));
                    GUILayout.Space(12);
                    GUILayout.Label("Max", _miniRight, GUILayout.Width(30));
                    ui_scaleClampMax = EditorGUILayout.FloatField(ui_scaleClampMax, GUILayout.Width(70));
                }
                if (ui_scaleClampMax < ui_scaleClampMin) (ui_scaleClampMin, ui_scaleClampMax) = (ui_scaleClampMax, ui_scaleClampMin);
            }
        }

        // --- Location Offset ---
        using (new EditorGUILayout.VerticalScope("box"))
        {
            DrawDarkHeaderRow("Location Offset");
            ui_locSpace = (LocSpace)EditorGUILayout.EnumPopup("Location Transform Mode", ui_locSpace);

            // Row: XYZ numbers
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("Location Transform"), GUILayout.Width(150));
                ui_locDelta.x = EditorGUILayout.FloatField(ui_locDelta.x);
                ui_locDelta.y = EditorGUILayout.FloatField(ui_locDelta.y);
                ui_locDelta.z = EditorGUILayout.FloatField(ui_locDelta.z);
            }
            // Sliders under row
            SliderTriple(ref ui_locDelta, -10f, 10f);

            // Seed enable + value
            ui_locUseSeed = EditorGUILayout.ToggleLeft("Use random location seed", ui_locUseSeed);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!ui_locUseSeed))
                {
                    ui_locSeed = ClampIntInclusive(EditorGUILayout.IntField(new GUIContent("Random location seed"), ui_locSeed), 1, 10_000_000);
                    if (GUILayout.Button("Randomise Seed", GUILayout.Width(130)))
                        ui_locSeed = Random.Range(1, 10_000_001);
                }
            }

            // Influenced axes row
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Influenced Axes", GUILayout.Width(120));
                AxisToggle(ref ui_locX, "X");
                AxisToggle(ref ui_locY, "Y");
                AxisToggle(ref ui_locZ, "Z");
            }

            // Clamping block
            GUILayout.Space(2);
            GUILayout.Label("Clamping");
            ClampRow("X Min/Max", ref ui_locClampXMin, ref ui_locClampXMax);
            ClampRow("Y Min/Max", ref ui_locClampYMin, ref ui_locClampYMax);
            ClampRow("Z Min/Max", ref ui_locClampZMin, ref ui_locClampZMax);
        }
    }

    // ====== Small UI helpers ======
    private void DrawDarkHeaderRow(string title)
    {
        var r = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
        var bg = EditorGUIUtility.isProSkin ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.82f, 0.82f, 0.82f);
        EditorGUI.DrawRect(new Rect(r.x, r.y + 2, r.width, r.height - 4), bg);
        var labelR = new Rect(r.x + 8, r.y, r.width - 16, r.height);
        GUI.Label(labelR, title, EditorStyles.boldLabel);
    }

    private void SliderTriple(ref Vector3 v, float min, float max)
    {
        v.x = EditorGUILayout.Slider(v.x, min, max);
        v.y = EditorGUILayout.Slider(v.y, min, max);
        v.z = EditorGUILayout.Slider(v.z, min, max);
    }

    private void AxisToggle(ref bool flag, string label)
    {
        var on = GUILayout.Toggle(flag, label, "Button", GUILayout.Width(30));
        flag = on;
    }

    private void ClampRow(string label, ref float min, ref float max)
    {
        EnsureMiniRight();
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(label, GUILayout.Width(120));
            GUILayout.Label("Min", _miniRight, GUILayout.Width(28));
            min = EditorGUILayout.FloatField(min, GUILayout.Width(70));
            GUILayout.Space(12);
            GUILayout.Label("Max", _miniRight, GUILayout.Width(30));
            max = EditorGUILayout.FloatField(max, GUILayout.Width(70));
        }
        if (max < min) (min, max) = (max, min);
    }

    private void EnsureMiniRight()
    {
        _miniRight ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
    }

    // local helper to avoid collisions with Module1 helpers
    private int ClampIntInclusive(int v, int min, int max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }
}
#endif
