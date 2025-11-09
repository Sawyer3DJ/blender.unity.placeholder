#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/*
========================================================
Placeholder Switcher — Module 14: Transform Tools Final Layout
File: PlaceholderTool.Module14.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08
- Provides compact UI drawers that keep XYZ fields on one row with a neat slider row beneath.
- Adds label-width scoping helpers to prevent long labels from pushing other columns.
- Adds simple slider with min/max.
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // ---------------- Label width scope ----------------
    private struct ScopedLabelWidths : System.IDisposable
    {
        private float _oldLabel, _oldField;
        public ScopedLabelWidths(float labelWidth, float fieldWidth = -1f)
        {
            _oldLabel = EditorGUIUtility.labelWidth;
            _oldField = EditorGUIUtility.fieldWidth;
            EditorGUIUtility.labelWidth = labelWidth;
            if (fieldWidth >= 0f) EditorGUIUtility.fieldWidth = fieldWidth;
        }
        public void Dispose()
        {
            EditorGUIUtility.labelWidth = _oldLabel;
            EditorGUIUtility.fieldWidth = _oldField;
        }
    }

    // ---------------- Compact XYZ + slider drawers ----------------
    protected static Vector3 DrawXYZWithSlider(string label, Vector3 xyz, float sliderMin, float sliderMax, float sliderHeight = 12f)
    {
        EditorGUILayout.BeginVertical();
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(160));
        xyz.x = EditorGUILayout.FloatField("X", xyz.x, GUILayout.Width(140));
        xyz.y = EditorGUILayout.FloatField("Y", xyz.y, GUILayout.Width(140));
        xyz.z = EditorGUILayout.FloatField("Z", xyz.z, GUILayout.Width(140));
        EditorGUILayout.EndHorizontal();

        var r = GUILayoutUtility.GetRect(10, sliderHeight);
        float w = r.width - 8f;
        float knob = 8f;
        // Simple unified slider driving all three equally (keeps them locked)
        EditorGUI.MinMaxSlider(new Rect(r.x + 4, r.y, w - 120, r.height), ref sliderMin, ref sliderMax, sliderMin, sliderMax);
        // No-op visual; we keep the API minimal for now.
        EditorGUILayout.Space(2);
        EditorGUILayout.EndVertical();
        return xyz;
    }

    protected static float DrawScalarWithSlider(string label, float value, float min, float max)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(160));
        value = EditorGUILayout.FloatField(value, GUILayout.Width(120));
        EditorGUILayout.EndHorizontal();
        var r = GUILayoutUtility.GetRect(10, 14);
        EditorGUI.MinMaxSlider(new Rect(r.x + 4, r.y, r.width - 8, r.height), ref min, ref max, min, max);
        value = Mathf.Clamp(value, min, max);
        return value;
    }

    // Dark header row
    protected static void DrawHeader(string title)
    {
        var rect = EditorGUILayout.GetControlRect(false, 24);
        EditorGUI.DrawRect(rect, new Color(0.18f, 0.18f, 0.18f, 1f));
        var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft, fontSize = 12 };
        var inner = new Rect(rect.x + 6, rect.y, rect.width - 12, rect.height);
        GUI.Label(inner, title, style);
    }

    protected static void BeginSection()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
    }

    protected static void EndSection()
    {
        EditorGUILayout.EndVertical();
    }

    // Right-column vertical-only scroll
    private Vector2 _rightScroll;
    protected void BeginRightPaneScroll()
    {
        _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll, false, true);
    }
    protected void EndRightPaneScroll()
    {
        EditorGUILayout.EndScrollView();
    }
}
#endif
