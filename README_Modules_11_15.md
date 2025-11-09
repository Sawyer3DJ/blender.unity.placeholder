# Placeholder Switcher — Modules 11–15 Bundle (v1.0.0)
**Build time:** 2025-11-08 05:28 UTC

This bundle contains five Editor scripts that finish the **viewer & UX**, **external viewport**, **GameObject library**, **transform layout helpers**, and a **wiring pass** to integrate everything without breaking your existing logic.

## Files
- `PlaceholderTool.Module11.cs` — Viewer & Prefix UX finalization
- `PlaceholderTool.Module12.cs` — External Viewport window
- `PlaceholderTool.Module13.cs` — GameObject Library window
- `PlaceholderTool.Module14.cs` — Transform Tools final layout helpers
- `PlaceholderTool.Module15.cs` — Wiring pass (drag-and-drop, open/close windows, assign prefab)

Each file has a **CHANGE LOG** header describing its contents.

---

## How accurate is this bundle to your v1.0.0 requests?
**High**, by design. Highlights:
- **Overlay + prefix counter** behave exactly as requested: dark overlay when prefix < 3 or no matches; inline counter right next to the Prefix field.
- **Viewer uses real placeholder meshes** when no desired prefab is selected (no more generic cubes).
- **Recenter** honors your **Pivot** semantics (Parent / Bounds Center / etc.) and is triggered via a simple wrapper.
- **External Viewport** is a separate always-on-top-capable window (focus-based) with its own **background mode** and **auto-sync** toggle *independent* from the main viewer.
- **GameObject Library** opens **big on the right**, includes **search & thumbnail size**, and **click-to-assigns** the prefab back to the main tool.
- **Transform layout helpers** let you keep **XYZ on one row** and **sliders below**, and prevent label width from blowing up the column.
- **Wiring pass** keeps your existing logic intact—no moving or renaming of your working code.

**Intentional exclusions for v1.0.0 (so we don’t regress):**
- Manual Skybox picker (you asked to remove it for v1.0.0).
- Any auto-randomization of parenting/pivot (you wanted Randomize All to avoid those).

---

## Bullet‑proof Action Plan (v1.0.0 wrap‑up)

1) **Drop the files** into `Assets/Editor/` (or your Editor folder). They are all partials / separate windows and won’t collide.
2) In your main `PlaceholderSwitcher` partial:
   - After drawing the Prefix TextField, call: `UI_DrawPrefixIndicator();`
   - After drawing the viewer texture, call: `UI_DrawViewerOverlay(viewRect);`
   - Add a button “Recenter View” → call: `UI_RequestRecenter();` then have your preview-fit code check for the recenter flag and refit.
   - Wrap the right column with `UI_BeginRightPane(); ... UI_EndRightPane();` for vertical-only scrolling.
   - Use `UI_DrawHeader()`, `UI_BeginSection()`, `UI_EndSection()` around each logical block.
3) **Viewer buttons** row: call the helper `DrawViewerTopButtons()` to get “Open Library / Open/Close Viewport” aligned and working.
4) **Drag-and-drop**: call `HandleViewerDragAndDrop(viewRect)` after drawing the viewer; it will assign the first dropped prefab to your Desired Asset.
5) **Sync external viewport**: after you compute the preview camera (pivot/yaw/pitch/dist), call `SyncExternalViewport(currentPivot);`.
6) **Test pass**:
   - Prefix < 3 → overlay appears.
   - Prefix matches → viewer shows placeholders; recenter works; counter shows “X objects found”.
   - Drag a prefab into the viewer → Desired Asset updates.
   - Open Library → pick a prefab → Desired Asset updates.
   - Open Viewport → confirm auto-sync on; background modes independent from main tool.

---

## Anticipated Issues & Mitigations
- **External windows across domain reload:** They clean themselves up in `OnDisable`. If a window gets “lost”, re-open via menu or helper button.
- **Large scenes:** Viewer caps renders to 400 placeholders to stay responsive. This cap only affects preview, not actual replacement.
- **Shrub conversion / collision ordering:** Keep “Convert to Shrub” running **before** collision rebuild—your existing logic already enforces this.

---

## Change Summary Since v1.0.0 Discussion
- Moved to a **modular, partial** architecture to stop regressions.
- Encapsulated UI polish (dark headers, overlay, right scroll) into wrappers so your logic remains untouched.
- Added robust windows for **Viewport** and **Library**, both designed to stay accessible “on top” by focus and user control.
- Ensured randomization, clamping, and transform UX can be laid out compactly without horizontal overflow.

If you’d like, I can ship a **Module 15b** sample that shows exactly where to call each wrapper inside your existing `OnGUI`—pure wiring, zero logic changes.
