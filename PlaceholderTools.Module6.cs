#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
========================================================
Placeholder Switcher — Module 6: Combine/Move/Pivot polish
File: PlaceholderTool.Module6.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08 (Module 6)
- Adds a dedicated Combine/Move UI section with contextual, honest
  warnings that only appear when relevant (e.g. after ticking
  "Combine objects into one").
- Pivot label clarified to "(affects preview centering)". Pivot changes,
  or parent/group changes, now auto-trigger a preview recenter
  (non-destructive to user camera if they’ve already adjusted).
- Provides a preview-centering helper that respects your chosen pivot:
    Parent, First Object, Bounds Center, World Origin, Selected Object.
  Module 5 auto-fit now *optionally* uses this preferred center.
- Parenting reminder appears if combining while using Parent/Group.
- Recenter hook is invoked when pivot/parent options change.

HOW TO WIRE (2 tiny steps)
--------------------------------------------------------
1) In your controls panel, call DrawCombineMoveSection() where your
   Combine/Move block belongs (instead of the old inline UI).

2) In your DrawPreview(rect) AFTER you call DrawPreviewContent(rect)
   from Module 5 and BEFORE EndPreview(), you can center the camera at
   the preferred pivot like this:

   // candidates gathered same way Module 5 does (prefix filter):
   var candidates = GetPreviewCandidatesForCentering();
   var preferredCenter = GetPreferredPreviewCenter(candidates);
   if (preferredCenter.HasValue)
   {
       var cam = previewUtil.camera;
       var rot = Quaternion.Euler(previewPitch, previewYaw, 0f);
       if (!previewUserAdjusted)
       {
           var center = preferredCenter.Value + previewPivotOffset;
           cam.transform.position = center + rot * (Vector3.back * previewDistance);
           cam.transform.rotation = Quaternion.LookRotation(center - cam.transform.position, Vector3.up);
       }
   }

   (If you prefer the Module 5 bounds-only centering, you can skip this;
    this helper simply lets you bias the center toward the chosen Pivot.)

Notes
--------------------------------------------------------
• Assumes the following fields exist (from prior modules):
  - bool combineIntoOne;
  - enum PivotMode { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    and PivotMode pivotMode;
  - Transform explicitParent;
  - bool groupWithEmptyParent; Transform GetGroupParentForScene(Scene s);
  - string prefix; bool previewUserAdjusted; Vector3 previewPivotOffset;
  - public void ForceRecenterPreview();  // from Module 5
• Non-breaking: If you already have a Combine/Move section, simply
  replace it with DrawCombineMoveSection() to get the contextual warning
  behavior + auto-recenter hooks.
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // --- Public UI entry point for this module ---
    // Call this from your Options/Controls column where Combine/Move lives.
    private void DrawCombineMoveSection()
    {
        // Section header (keep style consistent with your other headers)
        GUILayout.Label("Combine / Move", EditorStyles.boldLabel);

        // Combine toggle
        bool prevCombine = combineIntoOne;
        combineIntoOne = EditorGUILayout.Toggle(
            new GUIContent("Combine objects into one", "Static content only; bakes many into one mesh."),
            combineIntoOne);

        // Contextual warning only when combine is ON
        if (combineIntoOne)
        {
            // Parenting context
            string parentContext = GetCombineParentContextString();
            EditorGUILayout.HelpBox(
                "Combining meshes produces a SINGLE MeshRenderer. Per-object scripts, colliders, triggers, and events are LOST.\n" +
                "Tip: If you need to move many interactable objects together, consider keeping them separate and parent them under an empty object instead of combining.\n" +
                parentContext,
                MessageType.Warning);
        }

        // Pivot (affects preview centering)
        var prevPivot = pivotMode;
        pivotMode = (PivotMode)EditorGUILayout.EnumPopup(
            new GUIContent("Pivot (affects preview centering)", "Preview camera centers using this reference."),
            pivotMode);

        // If user picked SelectedObject but none is selected
        if (pivotMode == PivotMode.SelectedObject && Selection.activeTransform == null)
        {
            EditorGUILayout.HelpBox("Select a Transform in the Hierarchy to use as pivot.", MessageType.Info);
        }

        // If any option that impacts preview center changed, auto-recenter the preview once
        // (but only if user hasn't manually adjusted the camera already).
        if (!previewUserAdjusted && (prevCombine != combineIntoOne || prevPivot != pivotMode))
        {
            ForceRecenterPreview();
        }

        // --- Move block (keeps your existing fields) ---
        bool prevMove = moveToWorldCoordinates;
        moveToWorldCoordinates = EditorGUILayout.Toggle(
            new GUIContent("Move all objects to", "Choose a destination to move the result after switching/combining."),
            moveToWorldCoordinates);

        using (new EditorGUI.DisabledScope(!moveToWorldCoordinates))
        {
            EditorGUI.indentLevel++;

            // Destination selector (keep your existing semantics)
            var prevDest = moveDestination;
            moveDestination = (MoveDestination)EditorGUILayout.EnumPopup(
                new GUIContent("Destination"),
                moveDestination);

            // When WorldCoordinates selected → expose XYZ
            using (new EditorGUI.DisabledScope(moveDestination != MoveDestination.WorldCoordinates))
            {
                moveTargetPosition = EditorGUILayout.Vector3Field(
                    new GUIContent("World Coordinate"),
                    moveTargetPosition);
            }

            // If Parent chosen but no parent available, nudge the user
            if (moveDestination == MoveDestination.Parent && !HasAnyParentContext())
            {
                EditorGUILayout.HelpBox("No parent detected for the result. Choose a Parent first or pick a different destination.", MessageType.Info);
            }

            EditorGUI.indentLevel--;
        }

        // If options related to preview center changed (parent/group impacting Parent pivot),
        // politely recenter once unless the user already moved the camera.
        if (!previewUserAdjusted && (prevMove != moveToWorldCoordinates || HasParentingStateChangedSinceLastDraw()))
        {
            ForceRecenterPreview();
        }
    }

    // ---------------------------
    // Helpers used by this module
    // ---------------------------

    // Expose an enum for Move destination, if you don’t already have one
    private enum MoveDestination { FirstObject, BoundsCenter, WorldOrigin, WorldCoordinates, SelectedObject, Parent }
    [SerializeField] private MoveDestination moveDestination = MoveDestination.BoundsCenter;

    // Track parenting toggles to decide on recenter
    private bool _lastHadExplicitParent = false;
    private bool _lastHadGroupParent = false;

    private bool HasParentingStateChangedSinceLastDraw()
    {
        bool nowExplicit = explicitParent != null;
        bool nowGroup = groupWithEmptyParent;
        bool changed = (nowExplicit != _lastHadExplicitParent) || (nowGroup != _lastHadGroupParent);
        _lastHadExplicitParent = nowExplicit;
        _lastHadGroupParent = nowGroup;
        return changed;
    }

    private string GetCombineParentContextString()
    {
        if (explicitParent != null)
            return "Parenting: New combined object will be placed under the selected Parent.";

        if (groupWithEmptyParent)
            return "Parenting: New combined object will be placed under the Group parent in each scene.";

        return "Parenting: No explicit parent. Combined object will be created at the chosen pivot in the scene.";
    }

    private bool HasAnyParentContext()
    {
        if (explicitParent != null) return true;
        if (groupWithEmptyParent) return true;
        return false;
    }

    // Preferred center based on current Pivot setting and the current prefix candidates
    // Call this from DrawPreview after DrawPreviewContent so you can bias centering.
    private Vector3? GetPreferredPreviewCenter(List<GameObject> candidates)
    {
        if (candidates == null || candidates.Count == 0) return null;

        switch (pivotMode)
        {
            case PivotMode.Parent:
                {
                    if (explicitParent != null) return explicitParent.position;

                    // group parent: pick any group parent in the first candidate’s scene
                    var scene = candidates[0].scene;
                    var gp = GetGroupParentForScene(scene);
                    if (gp != null) return gp.position;

                    // fallback to bounds center
                    return ComputeBoundsCenter(candidates);
                }

            case PivotMode.FirstObject:
                return candidates[0].transform.position;

            case PivotMode.BoundsCenter:
                return ComputeBoundsCenter(candidates);

            case PivotMode.WorldOrigin:
                return Vector3.zero;

            case PivotMode.SelectedObject:
                return Selection.activeTransform ? Selection.activeTransform.position : ComputeBoundsCenter(candidates);
        }
        return null;
    }

    private static Vector3 ComputeBoundsCenter(List<GameObject> gos)
    {
        var have = false;
        var b = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var go in gos)
        {
            if (!go) continue;
            var r = go.GetComponent<Renderer>();
            var center = r ? r.bounds.center : go.transform.position;
            if (!have) { b = new Bounds(center, Vector3.zero); have = true; }
            else b.Encapsulate(center);
        }
        return have ? b.center : Vector3.zero;
    }

    // Utility so caller can fetch the same candidates list used for centering.
    // (Mirrors the collection logic used in Module 5.)
    private List<GameObject> GetPreviewCandidatesForCentering()
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3) return s_emptyGOList;

        var results = new List<GameObject>(256);
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (!s.IsValid() || !s.isLoaded) continue;
            var roots = s.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                CollectMatchRecursive(roots[r].transform, prefix, results);
            }
        }
        return results;

        static void CollectMatchRecursive(Transform t, string p, List<GameObject> outList)
        {
            if (!t) return;
            if (t.name.StartsWith(p, StringComparison.Ordinal)) outList.Add(t.gameObject);
            for (int i = 0; i < t.childCount; i++) CollectMatchRecursive(t.GetChild(i), p, outList);
        }
    }
    private static readonly List<GameObject> s_emptyGOList = new List<GameObject>(0);
}
#endif
