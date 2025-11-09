#if UNITY_EDITOR
/*
 * PlaceholderTool.PassEF.cs
 * Part of "Placeholder Switcher" EditorWindow (partial class).
 *
 * ========= CHANGELOG (Pass E + Pass F, single-file wired) =========
 * [EF-1] Added Parenting panel:
 *       - Parent(optional) field.
 *       - Group with New Empty Parent toggle.
 *       - Empty Parent Name, Empty Parent Location (FirstObject/BoundsCenter/WorldOrigin/Manual/SelectedObject).
 *       - Manual position vector (when Manual).
 *       - Per-scene parent creation with Undo. Reuses existing scene root by name.
 * [EF-2] Added Combine / Move / Convert / Collision panel:
 *       - "Combine objects into one" with pivot: Parent / FirstObject / BoundsCenter / WorldOrigin / SelectedObject.
 *       - "Move all objects to": None / Parent / FirstObject / BoundsCenter / WorldOrigin / WorldCoordinate / SelectedObject.
 *       - "Convert to Shrub" (reflection on type name "ConvertToShrub") with default Render Distance = 1000.
 *       - "Rebuild instanced collision" attempts to attach your project's custom collider; falls back to MeshCollider.
 * [EF-3] Runtime helpers:
 *       - PrepareGroupingParents_EF, ResolveGroupingParentFor_EF
 *       - CombineInstances_EF, MoveObjects_EF, GetWorldCenter_EF
 *       - TryConvertToShrub_EF, RebuildCollision_EF, FindTypeByNames_EF
 * [EF-4] Logging:
 *       - Lightweight action log (last 64 entries) to aid debugging.
 *
 * ========= NOTES =========
 * - All fields, enums and helpers here are *Pass-scoped* with the suffix "_EF" to avoid
 *   collisions with earlier modules you already have in your project.
 * - This file is a drop‑in partial; it does not replace your window. It only adds panels + logic.
 * - Hooking points are explained at the bottom of this file (see "Public entry points").
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public partial class PlaceholderSwitcher : EditorWindow
{
    // ================== EF: Serialized fields (scoped) ==================
    // --- Parenting ---
    [SerializeField] private Transform ef_explicitParent = null;
    [SerializeField] private bool      ef_groupWithEmptyParent = false;
    [SerializeField] private string    ef_groupParentName = "Imported Placeholders";

    private enum EmptyParentLocation_EF { FirstObject, BoundsCenter, WorldOrigin, Manual, SelectedObject }
    [SerializeField] private EmptyParentLocation_EF ef_emptyParentLocation = EmptyParentLocation_EF.FirstObject;
    [SerializeField] private Vector3   ef_manualParentPosition = Vector3.zero;

    // --- Combine / Move / Convert / Collision ---
    [SerializeField] private bool ef_combineIntoOne = false;

    private enum PivotMode_EF { Parent, FirstObject, BoundsCenter, WorldOrigin, SelectedObject }
    [SerializeField] private PivotMode_EF ef_pivotMode = PivotMode_EF.Parent;

    private enum MoveTarget_EF { None, Parent, FirstObject, BoundsCenter, WorldOrigin, WorldCoordinate, SelectedObject }
    [SerializeField] private MoveTarget_EF ef_moveTarget = MoveTarget_EF.None;
    [SerializeField] private Vector3 ef_moveWorldCoordinate = Vector3.zero;

    [SerializeField] private bool ef_convertToShrub = false;
    [SerializeField] private int  ef_shrubRenderDistance = 1000;

    [SerializeField] private bool ef_rebuildInstancedCollision = false;

    // EF state caches
    private readonly Dictionary<Scene, Transform> ef_groupParentByScene = new Dictionary<Scene, Transform>();
    private readonly List<string> ef_log = new List<string>(64);

    // ================== EF: UI ==================
    private static GUIStyle _efHeader;
    private static GUIStyle EF_Header
    {
        get
        {
            if (_efHeader == null)
            {
                _efHeader = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 11,
                };
            }
            return _efHeader;
        }
    }

    /// <summary>Draws the Parenting panel (right column).</summary>
    public void DrawParenting_EF(IList<GameObject> currentCandidates)
    {
        EditorGUILayout.LabelField("Parenting", EF_Header);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUI.DisabledScope(ef_groupWithEmptyParent))
            {
                var t = (Transform)EditorGUILayout.ObjectField(new GUIContent("Parent (optional)"), ef_explicitParent, typeof(Transform), true);
                if (t != ef_explicitParent)
                {
                    ef_explicitParent = t;
                    if (ef_explicitParent != null) ef_groupWithEmptyParent = false;
                }
            }

            ef_groupWithEmptyParent = EditorGUILayout.Toggle(new GUIContent("Group with New Empty Parent"), ef_groupWithEmptyParent);
            if (ef_groupWithEmptyParent && ef_explicitParent != null) ef_explicitParent = null;

            using (new EditorGUI.DisabledScope(!ef_groupWithEmptyParent))
            {
                EditorGUI.indentLevel++;
                ef_groupParentName    = EditorGUILayout.TextField(new GUIContent("Empty Parent Name"), ef_groupParentName);
                ef_emptyParentLocation = (EmptyParentLocation_EF)EditorGUILayout.EnumPopup(new GUIContent("Empty Parent Location"), ef_emptyParentLocation);
                using (new EditorGUI.DisabledScope(ef_emptyParentLocation != EmptyParentLocation_EF.Manual))
                {
                    ef_manualParentPosition = EditorGUILayout.Vector3Field(new GUIContent("Position (Manual)"), ef_manualParentPosition);
                }
                EditorGUI.indentLevel--;
            }

            // Status
            if (ef_groupWithEmptyParent)
            {
                string where = ef_emptyParentLocation.ToString();
                EditorGUILayout.HelpBox($"Will create/reuse a scene root named \"{ef_groupParentName}\" per scene at: {where}.", MessageType.Info);
            }
        }
    }

    /// <summary>Draws Combine / Move / Convert / Collision panel.</summary>
    public void DrawCombineMove_EF()
    {
        EditorGUILayout.LabelField("Combine / Move", EF_Header);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            ef_combineIntoOne = EditorGUILayout.Toggle(new GUIContent("Combine objects into one", "Static content only"), ef_combineIntoOne);
            using (new EditorGUI.DisabledScope(!ef_combineIntoOne))
            {
                EditorGUI.indentLevel++;
                ef_pivotMode = (PivotMode_EF)EditorGUILayout.EnumPopup(new GUIContent("Pivot (affects preview centering)"), ef_pivotMode);
                EditorGUILayout.HelpBox("⚠ Combining meshes creates ONE renderer/mesh. Per-object scripts, colliders, and triggers are lost.\nTip: If you need to move many interactive objects together, group under an empty parent instead.", MessageType.Warning);
                EditorGUI.indentLevel--;
            }

            ef_moveTarget = (MoveTarget_EF)EditorGUILayout.EnumPopup(new GUIContent("Move all objects to"), ef_moveTarget);
            using (new EditorGUI.DisabledScope(ef_moveTarget != MoveTarget_EF.WorldCoordinate))
            {
                EditorGUI.indentLevel++;
                ef_moveWorldCoordinate = EditorGUILayout.Vector3Field(new GUIContent("World Coordinate"), ef_moveWorldCoordinate);
                EditorGUI.indentLevel--;
            }
        }

        EditorGUILayout.LabelField("Convert / Collision", EF_Header);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            ef_convertToShrub = EditorGUILayout.Toggle(new GUIContent("Convert to Shrub", "Runs ConvertToShrub via reflection"), ef_convertToShrub);
            using (new EditorGUI.DisabledScope(!ef_convertToShrub))
            {
                EditorGUI.indentLevel++;
                ef_shrubRenderDistance = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent("Shrub Render Distance"), ef_shrubRenderDistance));
                EditorGUI.indentLevel--;
            }

            ef_rebuildInstancedCollision = EditorGUILayout.Toggle(new GUIContent("Rebuild instanced collision", "Attach custom instanced collider if available, else MeshCollider"), ef_rebuildInstancedCollision);
        }
    }

    // ================== EF: Public entry points (call from your main flow) ==================

    /// <summary>
    /// Prepare per-scene parents before spawning.
    /// </summary>
    public void PrepareGroupingParents_EF(Dictionary<Scene, List<GameObject>> byScene)
    {
        ef_groupParentByScene.Clear();
        if (ef_explicitParent == null && ef_groupWithEmptyParent && byScene != null)
        {
            foreach (var kv in byScene)
            {
                var scene = kv.Key;
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var desiredPos = GetEmptyParentPositionForScene_EF(kv.Value, ef_emptyParentLocation, ef_manualParentPosition);
                var parent = FindOrCreateGroupParentInScene_EF(scene, ef_groupParentName, desiredPos);
                ef_groupParentByScene[scene] = parent;
            }
        }
    }

    /// <summary>Resolve the parent to use for this source placeholder.</summary>
    public Transform ResolveGroupingParentFor_EF(GameObject source)
    {
        if (ef_explicitParent != null) return ef_explicitParent;

        if (ef_groupWithEmptyParent && source != null)
        {
            if (ef_groupParentByScene.TryGetValue(source.scene, out var t) && t) return t;
        }
        return null; // fallback to original parent by caller
    }

    /// <summary>Combine list into single GameObject (returns combined root or null).</summary>
    public GameObject CombineInstances_EF(List<GameObject> instances, Transform parentHint = null)
    {
        if (!ef_combineIntoOne || instances == null || instances.Count == 0) return null;

        // Gather meshes
        var filters   = new List<MeshFilter>();
        var renderers = new List<MeshRenderer>();
        foreach (var go in instances)
        {
            if (!go) continue;
            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            if (mf && mf.sharedMesh && mr) { filters.Add(mf); renderers.Add(mr); }
        }
        if (filters.Count == 0) { EF_Log("No MeshFilters found to combine."); return null; }

        // Pivot
        Vector3 pivotWS;
        switch (ef_pivotMode)
        {
            default:
            case PivotMode_EF.Parent:
                pivotWS = parentHint ? parentHint.position : Vector3.zero; break;
            case PivotMode_EF.FirstObject:
                pivotWS = filters[0].transform.position; break;
            case PivotMode_EF.BoundsCenter:
                var b = new Bounds(filters[0].transform.position, Vector3.zero);
                foreach (var mf in filters) { var r = mf.GetComponent<Renderer>(); if (r) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(mf.transform.position, Vector3.zero)); }
                pivotWS = b.center; break;
            case PivotMode_EF.WorldOrigin:
                pivotWS = Vector3.zero; break;
            case PivotMode_EF.SelectedObject:
                pivotWS = Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero; break;
        }
        var pivotToWorld = Matrix4x4.TRS(pivotWS, Quaternion.identity, Vector3.one);

        // Build submeshes/materials
        var combines  = new List<CombineInstance>();
        var materials = new List<Material>();
        for (int i = 0; i < filters.Count; i++)
        {
            var mf = filters[i];
            var mr = renderers[i];
            var mesh = mf.sharedMesh;
            var mats = mr.sharedMaterials;
            int subCount = Mathf.Min(mesh.subMeshCount, mats.Length);
            for (int s = 0; s < subCount; s++)
            {
                combines.Add(new CombineInstance{ mesh=mesh, subMeshIndex=s, transform=pivotToWorld.inverse * mf.transform.localToWorldMatrix });
                materials.Add(mats[s]);
            }
        }

        var finalMesh = new Mesh { name = "Combined_Mesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        finalMesh.CombineMeshes(combines.ToArray(), false, true, false);
        finalMesh.RecalculateBounds();
        if (!finalMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal)) finalMesh.RecalculateNormals();

        var result = new GameObject("Combined Object");
        Undo.RegisterCreatedObjectUndo(result, "Create combined object");
        if (parentHint) result.transform.SetParent(parentHint, false);
        result.transform.position = pivotWS;
        var mrf = result.AddComponent<MeshFilter>(); mrf.sharedMesh = finalMesh;
        var mrr = result.AddComponent<MeshRenderer>(); mrr.sharedMaterials = materials.ToArray();

        EF_Log($"Combined {filters.Count} MeshFilters into one.");
        return result;
    }

    /// <summary>Move all objects according to ef_moveTarget.</summary>
    public void MoveObjects_EF(List<GameObject> gos, Transform parentHint = null)
    {
        if (gos == null || gos.Count == 0) return;
        if (ef_moveTarget == MoveTarget_EF.None) return;

        Vector3 target;
        switch (ef_moveTarget)
        {
            case MoveTarget_EF.Parent:         target = parentHint ? parentHint.position : Vector3.zero; break;
            case MoveTarget_EF.FirstObject:     target = gos[0] ? gos[0].transform.position : Vector3.zero; break;
            case MoveTarget_EF.BoundsCenter:    target = GetWorldCenter_EF(gos); break;
            case MoveTarget_EF.WorldOrigin:     target = Vector3.zero; break;
            case MoveTarget_EF.WorldCoordinate: target = ef_moveWorldCoordinate; break;
            case MoveTarget_EF.SelectedObject:  target = Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero; break;
            default:                            target = Vector3.zero; break;
        }
        var center = GetWorldCenter_EF(gos);
        var delta = target - center;
        foreach (var go in gos) if (go) go.transform.position += delta;
        EF_Log($"Moved {gos.Count} object(s) to {ef_moveTarget}.");
    }

    /// <summary>Try run ConvertToShrub on go with optional Render Distance.</summary>
    public void TryConvertToShrub_EF(GameObject go, int renderDistance)
    {
        if (!go || !ef_convertToShrub) return;
        var t = FindTypeByNames_EF(new[]{ "ConvertToShrub" });
        if (t == null) { EF_Log("ConvertToShrub type not found."); return; }
        if (!typeof(Component).IsAssignableFrom(t)) { EF_Log("ConvertToShrub is not a Component."); return; }

        // Ensure only one
        foreach (var c in go.GetComponents(t)) if (c != null) Undo.DestroyObjectImmediate(c as Component);

        var comp = Undo.AddComponent(go, t);

        // Try set RenderDistance (field or property)
        try
        {
            var f = t.GetField("RenderDistance", BindingFlags.Public|BindingFlags.Instance);
            if (f != null && (f.FieldType == typeof(int) || f.FieldType == typeof(float)))
                f.SetValue(comp, f.FieldType == typeof(int) ? (object)renderDistance : (object)(float)renderDistance);
            else
            {
                var p = t.GetProperty("RenderDistance", BindingFlags.Public|BindingFlags.Instance);
                if (p != null && p.CanWrite && (p.PropertyType == typeof(int) || p.PropertyType == typeof(float)))
                    p.SetValue(comp, p.PropertyType == typeof(int) ? (object)renderDistance : (object)(float)renderDistance, null);
            }
        } catch { /* ignore */ }

        // Try invoke Convert/Build/Execute in that order
        var m = t.GetMethod("Convert", BindingFlags.Public|BindingFlags.Instance)
             ?? t.GetMethod("Build",   BindingFlags.Public|BindingFlags.Instance)
             ?? t.GetMethod("Execute", BindingFlags.Public|BindingFlags.Instance);
        if (m != null) { try { m.Invoke(comp, null); } catch { } }

        EF_Log($"ConvertToShrub executed (RenderDistance={renderDistance}).");
    }

    /// <summary>Rebuild instanced collision or MeshCollider.</summary>
    public void RebuildCollision_EF(GameObject go)
    {
        if (!go || !ef_rebuildInstancedCollision) return;

        var type = FindTypeByNames_EF(new[]
        {
            "Instanced Mesh Collider",
            "Instanced Mess Collider",
            "InstancedMeshCollider",
            "InstancedMeshCollision",
        });

        if (type != null && typeof(Component).IsAssignableFrom(type))
        {
            foreach (var c in go.GetComponents(type)) if (c) Undo.DestroyObjectImmediate(c as Component);
            var comp = Undo.AddComponent(go, type);
            var m = type.GetMethod("Rebuild", BindingFlags.Public|BindingFlags.Instance)
                 ?? type.GetMethod("Build",   BindingFlags.Public|BindingFlags.Instance)
                 ?? type.GetMethod("Setup",   BindingFlags.Public|BindingFlags.Instance);
            if (m != null) { try { m.Invoke(comp, null); } catch { } }
            EF_Log("Instanced collider rebuilt via custom component.");
        }
        else
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf && mf.sharedMesh)
            {
                var mc = go.GetComponent<MeshCollider>();
                if (!mc) mc = Undo.AddComponent<MeshCollider>(go);
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
                EF_Log("MeshCollider assigned as fallback.");
            }
        }
    }

    // ================== EF: Support helpers ==================
    private Transform FindOrCreateGroupParentInScene_EF(Scene scene, string name, Vector3 pos)
    {
        foreach (var root in scene.GetRootGameObjects())
            if (root && root.name == name) return root.transform;

        var go = new GameObject(name);
        go.transform.position = pos;
        Undo.RegisterCreatedObjectUndo(go, "Create Group Parent");
        SceneManager.MoveGameObjectToScene(go, scene);
        return go.transform;
    }

    private Vector3 GetEmptyParentPositionForScene_EF(List<GameObject> sceneCandidates, EmptyParentLocation_EF loc, Vector3 manual)
    {
        if (loc == EmptyParentLocation_EF.SelectedObject && Selection.activeTransform)
            return Selection.activeTransform.position;

        if (sceneCandidates == null || sceneCandidates.Count == 0)
            return loc == EmptyParentLocation_EF.Manual ? manual : Vector3.zero;

        switch (loc)
        {
            case EmptyParentLocation_EF.FirstObject:
                return sceneCandidates[0] ? sceneCandidates[0].transform.position : Vector3.zero;
            case EmptyParentLocation_EF.BoundsCenter:
                var b = new Bounds(sceneCandidates[0].transform.position, Vector3.zero);
                foreach (var go in sceneCandidates)
                {
                    if (!go) continue;
                    var r = go.GetComponent<Renderer>();
                    if (r != null) b.Encapsulate(r.bounds); else b.Encapsulate(new Bounds(go.transform.position, Vector3.zero));
                }
                return b.center;
            case EmptyParentLocation_EF.WorldOrigin:
                return Vector3.zero;
            case EmptyParentLocation_EF.Manual:
                return manual;
            case EmptyParentLocation_EF.SelectedObject:
                return Selection.activeTransform ? Selection.activeTransform.position : Vector3.zero;
        }
        return Vector3.zero;
    }

    private Vector3 GetWorldCenter_EF(List<GameObject> objects)
    {
        var b = new Bounds();
        bool init = false;
        foreach (var go in objects)
        {
            if (!go) continue;
            var r = go.GetComponent<Renderer>();
            var center = r ? r.bounds.center : go.transform.position;
            if (!init) { b = new Bounds(center, Vector3.zero); init = true; }
            else b.Encapsulate(center);
        }
        return init ? b.center : Vector3.zero;
    }

    private Type FindTypeByNames_EF(IEnumerable<string> names)
    {
        var guids = AssetDatabase.FindAssets("t:MonoScript");
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (!ms) continue;
            var n = ms.name;
            if (names.Contains(n))
            {
                var t = ms.GetClass();
                if (t != null) return t;
            }
        }
        foreach (var n in names)
        {
            var t = Type.GetType(n);
            if (t != null) return t;
        }
        return null;
    }

    private void EF_Log(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        if (ef_log.Count >= 64) ef_log.RemoveAt(0);
        ef_log.Add(msg);
        // Also surface to console for visibility
        Debug.Log($"[PlaceholderSwitcher.EF] {msg}");
    }
}
#endif
