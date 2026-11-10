// ============================================================
//  VRSceneSetupTools.cs  — Editor-only helper (keep in an "Editor" folder)
//  Menu: Tools > VR Logging > ...
//
//  1) Add Colliders To Selected Furniture
//     Select chairs, wardrobe, tables, shelves ... in the Hierarchy and run it.
//     Every mesh inside the selection that has NO collider gets a MeshCollider.
//     Objects with a Rigidbody (your grabbable bottles) are skipped.
//     Undo (Ctrl+Z) works.
//
//  2) Make Selected Bottles Collision-Safe
//     Select your bottles (beer AND water) and run it. Sets the Rigidbody to
//     Continuous Speculative collision detection + Interpolate, so dropped or
//     thrown bottles do not "tunnel" through thin chair seats or shelves.
// ============================================================
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class VRSceneSetupTools
{
    [MenuItem("Tools/VR Logging/Add Colliders To Selected Furniture")]
    private static void AddFurnitureColliders()
    {
        int added = 0, alreadyHad = 0, skippedGrabbable = 0;

        foreach (var root in Selection.gameObjects)
        {
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var go = mf.gameObject;
                if (mf.sharedMesh == null) continue;
                if (go.GetComponentInParent<Rigidbody>() != null) { skippedGrabbable++; continue; }
                if (go.GetComponent<Collider>() != null)          { alreadyHad++;       continue; }

                var mc = Undo.AddComponent<MeshCollider>(go);
                mc.sharedMesh = mf.sharedMesh;
                mc.convex     = false;   // exact shape; fine for objects that never move
                mc.isTrigger  = false;
                added++;
            }
        }
        Debug.Log($"[VRSceneSetupTools] Added {added} MeshCollider(s). " +
                  $"{alreadyHad} mesh(es) already had a collider, {skippedGrabbable} skipped (have a Rigidbody).");
    }

    [MenuItem("Tools/VR Logging/Make Selected Bottles Collision-Safe")]
    private static void MakeBottlesSafe()
    {
        int n = 0;
        foreach (var root in Selection.gameObjects)
        {
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
            {
                Undo.RecordObject(rb, "Collision-safe Rigidbody");
                // Speculative works for both kinematic (while held) and dynamic (after release)
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.interpolation          = RigidbodyInterpolation.Interpolate;
                n++;
            }
        }
        Debug.Log($"[VRSceneSetupTools] Updated {n} Rigidbody component(s).");
    }

    [MenuItem("Tools/VR Logging/Add Colliders To Selected Furniture", true)]
    [MenuItem("Tools/VR Logging/Make Selected Bottles Collision-Safe", true)]
    private static bool HasSelection() => Selection.gameObjects.Length > 0;
}
#endif
