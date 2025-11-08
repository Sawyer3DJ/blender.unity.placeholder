#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/*
========================================================
Placeholder Switcher — Module 8: Convert-to-Shrub (pre-collision)
File: PlaceholderTool.Module8.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08 (Module 8)
- Adds a dedicated "Convert To Shrub" UI block (sits ABOVE
  "Rebuild Instanced Collision" in the right column).
- Adds options:
    • [✓] Convert To Shrub
    • Shrub Render Distance (default 1000)
- Implements reflection-based attachment/invocation for your
  project's shrub script. Tries class names in this order:
    "ConvertToShrub", "ConverterShrub", "Converter Shrub"
  (so you’re covered if the class name differs slightly).
- Sets a render-distance member if it exists (supports:
    public field:  RenderDistance / renderDistance / Distance
    public prop :  RenderDistance / renderDistance / Distance )
- Provides helpers to apply conversion to a single GameObject or
  a batch (list). This runs *before* collision rebuild, per spec.
- Non-destructive: logs clear messages; silently skips when the
  shrub script can’t be found (so your pipeline never breaks).

HOW TO WIRE (two tiny hooks)
--------------------------------------------------------
1) UI — in your Options/Controls column, where you show
   "Rebuild instanced collision", call:

   DrawConvertToShrubSection();   // place this *above* your collision UI

2) Pipeline — after you have either:
     a) a combined 'finalRoot', or
     b) a 'spawned' list of instances,
   but *before* your collision rebuild runs, call:

   // If you have a combined object:
   if (finalRoot) ApplyShrubIfRequested(finalRoot);

   // If NOT combined (per-instance pass):
   else ApplyShrubIfRequested(spawned);

   That’s it. Keep your collision rebuild exactly as it is; this
   module does nothing unless the checkbox is enabled.

Notes
--------------------------------------------------------
• The module uses reflection so you don’t need a hard compile-time
  reference to the shrub script. It will log a single warning the
  first time if nothing is found and then quietly continue.
• Works with either int or float render distance members.
• Order is guaranteed: Convert→(optionally) Collision (your call).
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // -----------------------
    // UI State (serialized)
    // -----------------------
    [SerializeField] private bool  convertToShrub = false;
    [SerializeField] private int   shrubRenderDistance = 1000; // default you asked for

    // -----------------------
    // UI Section
    // -----------------------
    private void DrawConvertToShrubSection()
    {
        GUILayout.Label("Convert To Shrub", EditorStyles.boldLabel);

        convertToShrub = EditorGUILayout.Toggle(
            new GUIContent("Convert to Shrub", "Attach and run your project's ConvertToShrub (or equivalent) before collision rebuild."),
            convertToShrub);

        using (new EditorGUI.DisabledScope(!convertToShrub))
        {
            shrubRenderDistance = Mathf.Clamp(
                EditorGUILayout.IntField(new GUIContent("Shrub Render Distance", "Overrides the shrub script's render distance if available."), shrubRenderDistance),
                1, 2_000_000);

            EditorGUILayout.HelpBox(
                "Order: Convert to Shrub runs BEFORE 'Rebuild Instanced Collision'. " +
                "If the shrub script can’t be located, the tool will skip conversion and continue.",
                MessageType.Info);
        }
    }

    // -----------------------
    // Public pipeline helpers
    // -----------------------

    // Apply shrub conversion to a single object (combined result)
    private void ApplyShrubIfRequested(GameObject root)
    {
        if (!convertToShrub || !root) return;
        EnsureShrubInitialized();
        TryConvertOneToShrub(root);
    }

    // Apply shrub conversion to many objects (non-combined instances)
    private void ApplyShrubIfRequested(List<GameObject> objects)
    {
        if (!convertToShrub || objects == null || objects.Count == 0) return;
        EnsureShrubInitialized();
        foreach (var go in objects) if (go) TryConvertOneToShrub(go);
    }

    // -----------------------
    // Internal conversion impl
    // -----------------------

    // Cached reflection targets
    private static Type   _shrubType;
    private static FieldInfo   _fiRenderDistance;
    private static PropertyInfo _piRenderDistance;
    private static FieldInfo   _fiRenderDistanceLower;  // 'renderDistance'
    private static PropertyInfo _piRenderDistanceLower;
    private static FieldInfo   _fiDistance;
    private static PropertyInfo _piDistance;

    private static bool _shrubSearched = false;
    private static bool _logMissingShrubOnce = true;

    private void EnsureShrubInitialized()
    {
        if (_shrubSearched) return;
        _shrubSearched = true;

        // Try the likely class names in order
        var candidateNames = new[]
        {
            "ConvertToShrub",   // per your clarification (C, T, S capitals)
            "ConverterShrub",   // alternate you mentioned
            "Converter Shrub"   // extremely unlikely as a class name (space), but we’ll try
        };

        foreach (var n in candidateNames)
        {
            var t = Type.GetType(n);
            if (t != null) { _shrubType = t; break; }
        }

        // If not found by Type.GetType, scan loaded assemblies (Editor domain)
        if (_shrubType == null)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in asms)
            {
                try
                {
                    var t = asm.GetType("ConvertToShrub") ??
                            asm.GetType("ConverterShrub") ??
                            asm.GetType("Converter Shrub");
                    if (t != null) { _shrubType = t; break; }
                }
                catch { /* ignore assembly load issues */ }
            }
        }

        if (_shrubType != null)
        {
            // Cache likely render-distance members
            _fiRenderDistance      = _shrubType.GetField("RenderDistance", BindingFlags.Instance | BindingFlags.Public);
            _piRenderDistance      = _shrubType.GetProperty("RenderDistance", BindingFlags.Instance | BindingFlags.Public);
            _fiRenderDistanceLower = _shrubType.GetField("renderDistance", BindingFlags.Instance | BindingFlags.Public);
            _piRenderDistanceLower = _shrubType.GetProperty("renderDistance", BindingFlags.Instance | BindingFlags.Public);
            _fiDistance            = _shrubType.GetField("Distance", BindingFlags.Instance | BindingFlags.Public);
            _piDistance            = _shrubType.GetProperty("Distance", BindingFlags.Instance | BindingFlags.Public);
        }
        else
        {
            if (_logMissingShrubOnce)
            {
                _logMissingShrubOnce = false;
                Debug.LogWarning("ConvertToShrub: Could not locate shrub script type (tried ConvertToShrub / ConverterShrub). Conversion will be skipped.");
            }
        }
    }

    private void TryConvertOneToShrub(GameObject go)
    {
        if (_shrubType == null || go == null) return;

        // If it already exists, remove and re-add for a clean setup (optional, but helps when re-running)
        var existing = go.GetComponent(_shrubType);
        if (existing != null) Undo.DestroyObjectImmediate(existing as Component);

        var comp = Undo.AddComponent(go, _shrubType);

        // Assign render distance if such a member exists
        TrySetRenderDistance(comp, shrubRenderDistance);

        // If the shrub script exposes a known setup method, call it
        var mSetup =
            _shrubType.GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public) ??
            _shrubType.GetMethod("Build", BindingFlags.Instance | BindingFlags.Public)  ??
            _shrubType.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.Public);

        if (mSetup != null)
        {
            try { mSetup.Invoke(comp, null); }
            catch (Exception ex) { Debug.LogWarning($"ConvertToShrub: Setup invoke failed on {go.name}: {ex.Message}"); }
        }
    }

    private void TrySetRenderDistance(object shrubComponent, int value)
    {
        if (shrubComponent == null) return;

        // Try fields first (int or float)
        if (_fiRenderDistance != null)
        {
            SetIntOrFloatField(_fiRenderDistance, shrubComponent, value);
            return;
        }
        if (_fiRenderDistanceLower != null)
        {
            SetIntOrFloatField(_fiRenderDistanceLower, shrubComponent, value);
            return;
        }
        if (_fiDistance != null)
        {
            SetIntOrFloatField(_fiDistance, shrubComponent, value);
            return;
        }

        // Try properties next (int or float)
        if (_piRenderDistance != null)
        {
            SetIntOrFloatProperty(_piRenderDistance, shrubComponent, value);
            return;
        }
        if (_piRenderDistanceLower != null)
        {
            SetIntOrFloatProperty(_piRenderDistanceLower, shrubComponent, value);
            return;
        }
        if (_piDistance != null)
        {
            SetIntOrFloatProperty(_piDistance, shrubComponent, value);
            return;
        }

        // Nothing matched — silently ignore
    }

    private static void SetIntOrFloatField(FieldInfo fi, object target, int value)
    {
        try
        {
            if (fi.FieldType == typeof(int))      fi.SetValue(target, value);
            else if (fi.FieldType == typeof(float)) fi.SetValue(target, (float)value);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"ConvertToShrub: Failed to set field '{fi.Name}': {ex.Message}");
        }
    }

    private static void SetIntOrFloatProperty(PropertyInfo pi, object target, int value)
    {
        try
        {
            if (!pi.CanWrite) return;
            if (pi.PropertyType == typeof(int))        pi.SetValue(target, value, null);
            else if (pi.PropertyType == typeof(float)) pi.SetValue(target, (float)value, null);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"ConvertToShrub: Failed to set property '{pi.Name}': {ex.Message}");
        }
    }
}
#endif
