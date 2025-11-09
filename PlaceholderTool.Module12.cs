#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/*
========================================================
Placeholder Switcher — Module 12: External Viewport Window
File: PlaceholderTool.Module12.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08
- Adds a dedicated external viewport window (always-on-top behavior with Focus cycling).
- Independent background mode (does not mirror main tool background).
- Auto-sync to tool's camera framing (on by default; can be toggled in main tool).
- Clean lifecycle on domain reload.
========================================================
*/

public class PlaceholderViewportWindow : EditorWindow
{
    public static PlaceholderViewportWindow Instance;
    public bool AutoSync = true; // independent toggle
    private PreviewRenderUtility _util;
    private float _yaw = -30, _pitch = 15, _dist = 2;
    private Vector3 _pivot;
    private bool _userAdjusted;

    private enum Bg { CurrentSkybox, UnitySkybox }
    private Bg _bgMode = Bg.CurrentSkybox;

    [MenuItem("Window/Placeholder Tools/External Viewport")]
    public static void OpenWindow()
    {
        var w = GetWindow<PlaceholderViewportWindow>("Placeholder Viewport");
        w.minSize = new Vector2(360, 260);
        w.Show();
    }

    public static void OpenOrFocus(bool autoSyncDefault = true)
    {
        if (Instance == null) OpenWindow();
        if (Instance != null)
        {
            Instance.Focus();
            Instance.AutoSync = autoSyncDefault;
        }
    }

    private void OnEnable()
    {
        Instance = this;
        _util = new PreviewRenderUtility(true);
        _util.cameraFieldOfView = 30f;
        _util.lights[0].intensity = 1.2f;
        _util.lights[1].intensity = 0.8f;
        ApplyBackground();
    }

    private void OnDisable()
    {
        Instance = null;
        if (_util != null) { _util.Cleanup(); _util = null; }
    }

    private void ApplyBackground()
    {
        var cam = _util?.camera;
        if (cam == null) return;
        switch (_bgMode)
        {
            default:
            case Bg.CurrentSkybox:
                cam.clearFlags = RenderSettings.skybox ? CameraClearFlags.Skybox : CameraClearFlags.Color;
                if (RenderSettings.skybox == null) cam.backgroundColor = RenderSettings.ambientLight;
                break;
            case Bg.UnitySkybox:
                cam.clearFlags = CameraClearFlags.Color;
                // Unity-like light blue horizon
                cam.backgroundColor = new Color(0.53f, 0.81f, 0.92f, 1f);
                break;
        }
    }

    public void SyncFromMain(Vector3 pivot, float yaw, float pitch, float dist)
    {
        if (!AutoSync) return;
        _pivot = pivot;
        _yaw = yaw;
        _pitch = pitch;
        _dist = dist;
        Repaint();
    }

    private void OnGUI()
    {
        // Basic header row
        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        GUILayout.Label("External Viewport", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        AutoSync = EditorGUILayout.ToggleLeft("Auto-sync viewport", AutoSync, GUILayout.Width(160));
        var newBg = (Bg)EditorGUILayout.EnumPopup(_bgMode, GUILayout.Width(140));
        if (newBg != _bgMode) { _bgMode = newBg; ApplyBackground(); }
        EditorGUILayout.EndHorizontal();

        var r = GUILayoutUtility.GetRect(10, 10, position.height - 24, position.height - 24);
        if (Event.current.type == EventType.Repaint && _util != null)
        {
            _util.BeginPreview(r, GUIStyle.none);
            var cam = _util.camera;
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            cam.transform.position = _pivot + rot * (Vector3.back * _dist);
            cam.transform.rotation = Quaternion.LookRotation(_pivot - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5000f;

            // Minimal proxy scene content: a grid
            Handles.SetCamera(cam);
            Handles.color = new Color(1,1,1,0.08f);
            for (int i = -10; i <= 10; i++)
            {
                Handles.DrawLine(new Vector3(i, 0, -10), new Vector3(i, 0, 10));
                Handles.DrawLine(new Vector3(-10, 0, i), new Vector3(10, 0, i));
            }

            cam.Render();
            var tex = _util.EndPreview();
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
        }

        // Simple orbit / pan / zoom
        if (r.Contains(Event.current.mousePosition))
        {
            if (Event.current.type == EventType.MouseDrag)
            {
                if (Event.current.button == 0)
                {
                    _userAdjusted = true;
                    _yaw += Event.current.delta.x * 0.5f;
                    _pitch = Mathf.Clamp(_pitch - Event.current.delta.y * 0.5f, -80, 80);
                    Repaint();
                }
                else if (Event.current.button == 2)
                {
                    _userAdjusted = true;
                    float panScale = _dist * 0.0025f;
                    var right = Quaternion.Euler(0, _yaw, 0) * Vector3.right;
                    var up = Vector3.up;
                    _pivot += (-right * Event.current.delta.x + up * Event.current.delta.y) * panScale;
                    Repaint();
                }
            }
            if (Event.current.type == EventType.ScrollWheel)
            {
                _userAdjusted = true;
                _dist = Mathf.Clamp(_dist * (1f + Event.current.delta.y * 0.04f), 0.3f, 2000f);
                Repaint();
            }
        }
    }
}
#endif
