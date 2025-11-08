#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
========================================================
Placeholder Switcher — Module 5: Real Placeholder Preview
File: PlaceholderTool.Module5.cs
Target: v1.0.0

CHANGE LOG
--------------------------------------------------------
2025-11-08 (Module 5)
- Preview now renders REAL meshes from placeholders that match
  the prefix (multi-part prefabs supported: all child MeshFilters
  and SkinnedMeshRenderers are previewed).
- Applies current Transform Tool settings (Rotation/Scale modes,
  including seeds & clamping) to preview only (non-destructive).
- Restored & improved the dark overlay with friendly guidance:
  • If prefix < 3 → "Enter ≥ 3 characters…"
  • If prefix ok but 0 matches → "⚠ no assets found"
  • Tip lines about dragging a prefab into the viewer, opening
    the GameObject Library, and using seeds/offsets creatively.
- Robust camera framing: auto fits bounds of ALL previewed items.
- Recenter API: ForceRecenterPreview() resets offset & fits again.

Notes
--------------------------------------------------------
• This module assumes your main partial class already defines:
  - Serialized fields: prefix, targetPrefab, rotation/scale modes.
  - The preview fields: previewUtil, previewYaw, previewPitch,
    previewDistance, previewUserAdjusted, previewPivotOffset,
    previewBackground, fallbackMat, etc. (from Module 1/2).
  - The helper methods from Module 2 (rotation/scale calculators).
• This module *overrides the internal preview content path* by
  introducing DrawPreviewContent(); call it inside your existing
  DrawPreviewArea()/DrawPreview() where you render content.
========================================================
*/

public partial class PlaceholderSwitcher : EditorWindow
{
    // ---------- Public recenter hook ----------
    // Call this to snap the viewer back to fit all current items.
    public void ForceRecenterPreview()
    {
        previewUserAdjusted = false;
        previewPivotOffset = Vector3.zero;
        Repaint();
    }

    // ---------- Core: draw preview content ----------
    // Call this from your existing DrawPreview(rect) right before EndPreview().
    // It returns the world-space bounds actually rendered so the caller can
    // set camera distance and/or show overlays if needed.
    private Bounds? DrawPreviewContent(Rect viewportRect)
    {
        // Decide what to show:
        // 1) Need a valid prefix length to even search
        if (string.IsNullOrEmpty(prefix) || prefix.Length < 3)
        {
            DrawDarkOverlay(viewportRect,
                "Enter ≥ 3 characters for Placeholder Prefix",
                "Tip: Drag a Prefab into this viewer to set Desired Asset.",
                "Tip: Open GameObject Library to browse prefabs quickly.",
                "Try Rotation/Scale/Location seeds to explore variations.");
            return null;
        }

        // 2) Collect placeholder roots matching prefix across open scenes
        var candidates = FindPlaceholders(prefix);
        if (candidates.Count == 0)
        {
            DrawDarkOverlay(viewportRect,
                "⚠ No objects found with the current prefix",
                $"Prefix: '{prefix}'",
                "Try a different prefix, or open GameObject Library.");
            return null;
        }

        // 3) Build preview items (multi-mesh) with current transform modes applied
        var items = BuildPreviewItemsFromPlaceholders(candidates);

        // 4) Compute bounds for camera fit
        Bounds? bounds = ComputeItemsBounds(items);
        if (bounds == null)
        {
            // No drawable meshes → fallback message
            DrawDarkOverlay(viewportRect,
                "Nothing renderable in matched objects",
                "Matched objects have no MeshRenderer/SkinnedMeshRenderer.");
            return null;
        }

        // 5) Draw everything
        foreach (var it in items)
        {
            if (it.mesh == null || it.materials == null || it.materials.Length == 0) continue;

            // Render each submesh with matching material index (safe clamp)
            int subCount = Mathf.Min(it.mesh.subMeshCount, it.materials.Length);
            for (int s = 0; s < subCount; s++)
            {
                var mat = it.materials[s] != null ? it.materials[s] : fallbackMat;
                previewUtil.DrawMesh(it.mesh, it.trs, mat, s);
            }
        }

        return bounds;
    }

    // ---------- Data for one preview draw ----------
    private struct PreviewItem
    {
        public Mesh mesh;
        public Material[] materials;
        public Matrix4x4 trs;
    }

    // ---------- Collect placeholders ----------
    private static List<GameObject> FindPlaceholders(string pfx)
    {
        var list = new List<GameObject>(256);
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (!s.IsValid() || !s.isLoaded) continue;
            var roots = s.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                CollectMatchRecursive(roots[r].transform, pfx, list);
            }
        }
        return list;

        static void CollectMatchRecursive(Transform t, string p, List<GameObject> outList)
        {
            if (!t) return;
            if (t.name.StartsWith(p, StringComparison.Ordinal)) outList.Add(t.gameObject);
            for (int i = 0; i < t.childCount; i++) CollectMatchRecursive(t.GetChild(i), p, outList);
        }
    }

    // ---------- Build preview items (multi-mesh support) ----------
    // Applies transform-preview logic consistent with your Transform Tools.
    private List<PreviewItem> BuildPreviewItemsFromPlaceholders(List<GameObject> candidates)
    {
        var items = new List<PreviewItem>(Mathf.Max(16, candidates.Count * 2));

        foreach (var go in candidates)
        {
            if (!go) continue;

            // Determine the preview transform per object,
            // using the same rules as your replacement (non-destructive preview).
            var worldPos = go.transform.position;
            var worldRot = GetPreviewObjectRotation(go.transform); // from Module 2
            var worldScale = GetPreviewObjectScale(go.transform);  // from Module 2

            // MeshFilters (static meshes)
            var mfs = go.GetComponentsInChildren<MeshFilter>(true);
            var mrs = go.GetComponentsInChildren<MeshRenderer>(true);
            if (mfs != null && mrs != null && mfs.Length > 0 && mrs.Length > 0)
            {
                // pair by transform
                var byTf = new Dictionary<Transform, (Mesh mesh, Material[] mats)>();
                foreach (var mr in mrs)
                {
                    if (!mr) continue;
                    byTf[mr.transform] = (null, mr.sharedMaterials ?? Array.Empty<Material>());
                }
                foreach (var mf in mfs)
                {
                    if (!mf || !mf.sharedMesh) continue;
                    if (byTf.TryGetValue(mf.transform, out var tuple))
                    {
                        tuple.mesh = mf.sharedMesh;
                        byTf[mf.transform] = tuple;
                    }
                    else
                    {
                        byTf[mf.transform] = (mf.sharedMesh, new[] { fallbackMat });
                    }
                }

                foreach (var kv in byTf)
                {
                    var mesh = kv.Value.mesh;
                    if (!mesh) continue;
                    var mats = kv.Value.mats?.Length > 0 ? kv.Value.mats : new[] { fallbackMat };

                    // Child local TRS relative to the placeholder root
                    var childLocal = kv.Key.localToWorldMatrix;
                    // We want: world from our *preview* transform, not the current scene rotation/scale
                    // Build a matrix from preview worldPos/worldRot/worldScale and then apply child’s local-to-root.
                    // The childLocal already contains the *actual* scene transforms;
                    // for preview we approximate: put child at root worldPos and bake
                    // our preview rotation/scale around the child pivot.
                    // Simpler & stable approach: TRS(worldPos, worldRot, worldScale) * rootInverse * childLocalScene
                    var rootToWorldScene = Matrix4x4.TRS(go.transform.position, go.transform.rotation, go.transform.lossyScale);
                    var rootWorldToScene = rootToWorldScene.inverse;
                    var previewRoot = Matrix4x4.TRS(worldPos, worldRot, worldScale);
                    var trs = previewRoot * rootWorldToScene * childLocal;

                    items.Add(new PreviewItem
                    {
                        mesh = mesh,
                        materials = mats,
                        trs = trs
                    });
                }
            }

            // Skinned meshes (use sharedMesh + renderer transform)
            var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs != null && smrs.Length > 0)
            {
                foreach (var smr in smrs)
                {
                    if (!smr || !smr.sharedMesh) continue;
                    var mats = smr.sharedMaterials != null && smr.sharedMaterials.Length > 0
                        ? smr.sharedMaterials
                        : new[] { fallbackMat };

                    var childLocal = smr.transform.localToWorldMatrix;
                    var rootToWorldScene = Matrix4x4.TRS(go.transform.position, go.transform.rotation, go.transform.lossyScale);
                    var rootWorldToScene = rootToWorldScene.inverse;
                    var previewRoot = Matrix4x4.TRS(worldPos, worldRot, worldScale);
                    var trs = previewRoot * rootWorldToScene * childLocal;

                    items.Add(new PreviewItem
                    {
                        mesh = smr.sharedMesh,
                        materials = mats,
                        trs = trs
                    });
                }
            }
        }

        // If nothing had meshes, return empty; caller shows overlay
        return items;
    }

    // ---------- Bound fitting ----------
    private static Bounds? ComputeItemsBounds(List<PreviewItem> items)
    {
        Bounds? b = null;
        foreach (var it in items)
        {
            if (it.mesh == null) continue;
            var mb = it.mesh.bounds;
            // Transform mesh bounds by its TRS
            var corners = GetBoundsCorners(mb);
            for (int i = 0; i < 8; i++) corners[i] = it.trs.MultiplyPoint3x4(corners[i]);
            var tb = new Bounds(corners[0], Vector3.zero);
            for (int i = 1; i < 8; i++) tb.Encapsulate(corners[i]);

            if (b == null) b = tb;
            else
            {
                var bb = b.Value;
                bb.Encapsulate(tb);
                b = bb;
            }
        }
        return b;
    }

    private static Vector3[] GetBoundsCorners(Bounds b)
    {
        var c = new Vector3[8];
        var min = b.min; var max = b.max;
        c[0] = new Vector3(min.x, min.y, min.z);
        c[1] = new Vector3(max.x, min.y, min.z);
        c[2] = new Vector3(min.x, max.y, min.z);
        c[3] = new Vector3(max.x, max.y, min.z);
        c[4] = new Vector3(min.x, min.y, max.z);
        c[5] = new Vector3(max.x, min.y, max.z);
        c[6] = new Vector3(min.x, max.y, max.z);
        c[7] = new Vector3(max.x, max.y, max.z);
        return c;
    }

    // ---------- Overlay ----------
    private void DrawDarkOverlay(Rect rect, params string[] lines)
    {
        // dark back
        EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(0, 0, 0, 0.5f) : new Color(0, 0, 0, 0.6f));

        // centered text block
        var style = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12,
            normal = { textColor = Color.white },
            wordWrap = true
        };

        float y = rect.y + rect.height * 0.5f - 40;
        var lineRect = new Rect(rect.x + 20, y, rect.width - 40, 22);
        foreach (var ln in lines)
        {
            GUI.Label(lineRect, ln, style);
            lineRect.y += 20;
        }
    }
}
#endif
