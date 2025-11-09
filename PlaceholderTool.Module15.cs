#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

/*
========================================================
Placeholder Switcher — Module 15: Wiring Pass
File: PlaceholderTool.Module15.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08
- Provides small glue points to integrate Modules 9–14 without moving existing logic.
- Adds SetDesiredPrefab(GameObject) so Library/Drag-and-drop can assign the prefab.
- Adds a simple drag-and-drop handler over the viewer rect.
- Adds UI entry points for "Open Viewport", "Close Viewport", and "Open GameObject Library".
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // These fields must exist elsewhere; we re-declare as 'extern' style through partial
    // to avoid renaming. Keep them in the main partial that holds state.
    // Expected fields:
    // string prefix; GameObject targetPrefab;
    // float previewYaw, previewPitch, previewDistance; Vector3 previewPivot;

    // Allow Library / DnD to set the desired prefab
    public void SetDesiredPrefab(GameObject go)
    {
        if (go != null)
        {
            targetPrefab = go;
            Repaint();
        }
    }

    // Viewer toolbar helpers
    private void DrawViewerTopButtons()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        if (GUILayout.Button("Open GameObject Library", GUILayout.Height(22)))
        {
            PlaceholderLibraryWindow.OpenWindow();
        }
        if (GUILayout.Button("Open Viewport", GUILayout.Width(120)))
        {
            PlaceholderViewportWindow.OpenOrFocus(autoSyncDefault: true);
        }
        if (PlaceholderViewportWindow.Instance != null)
        {
            if (GUILayout.Button("Close Viewport", GUILayout.Width(120)))
                PlaceholderViewportWindow.Instance.Close();
        }
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    // Sync external viewport (call inside your preview repaint after camera settled)
    private void SyncExternalViewport(Vector3 pivot)
    {
        if (PlaceholderViewportWindow.Instance != null)
        {
            PlaceholderViewportWindow.Instance.SyncFromMain(
                pivot,
                previewYaw,
                previewPitch,
                previewDistance
            );
        }
    }

    // Accept drag-and-drop of prefabs over viewer rect
    private void HandleViewerDragAndDrop(Rect rect)
    {
        var e = Event.current;
        if (!rect.Contains(e.mousePosition)) return;

        if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
        {
            bool anyGO = DragAndDrop.objectReferences.Any(o => o is GameObject);
            if (anyGO)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        if (obj is GameObject go)
                        {
                            SetDesiredPrefab(go);
                            break;
                        }
                    }
                }
                e.Use();
            }
        }
    }
}
#endif
