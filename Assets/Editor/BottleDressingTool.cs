// ============================================================
//  BottleDressingTool.cs — Editor-only (keep in Assets/Editor)
//  Menu: Tools > VR Logging > Add Water + Label To Selected Bottle
//
//  Select the water bottle (the Building Block or its WaterBottle child)
//  and run the menu item. The tool:
//    1. finds the bottle mesh (largest visible mesh in the selection),
//    2. measures its shape and builds a WATER volume inside it and a
//       LABEL band on its smooth section (see BottleDressingGeometry.cs),
//    3. creates the materials Water_Mat and Label_Mat once, in
//       Assets/Models/WaterBottle/Dressing/,
//    4. adds two children "Water" and "Label" to the bottle mesh object.
//  Running it again replaces the old Water/Label. Ctrl+Z works.
//  Neither child gets a collider, so grabbing, physics and gaze logging
//  are unchanged.
// ============================================================
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using G = BottleDressingGeometry;

public static class BottleDressingTool
{
    private const string Folder   = "Assets/Models/WaterBottle/Dressing";
    private const string LabelTex = Folder + "/Label_Quellara.png";

    [MenuItem("Tools/VR Logging/Add Water + Label To Selected Bottle")]
    private static void Run()
    {
        var sel = Selection.activeGameObject;
        var bottle = FindBottleMesh(sel);
        if (bottle == null)
        {
            EditorUtility.DisplayDialog("Add Water + Label",
                "No visible bottle mesh found in the selection.\n\nSelect the water bottle object " +
                "(the Building Block or the WaterBottle model inside it).", "OK");
            return;
        }

        Transform tr = bottle.transform;
        var src = bottle.sharedMesh.vertices;
        var verts = new List<G.V3>(src.Length);
        foreach (var v in src) verts.Add(new G.V3(v.x, v.y, v.z));

        var profile = G.Analyze(verts);

        // Decide the label's U direction in WORLD space so the text is never mirrored,
        // whatever axis conversion or negative scale the imported model carries.
        bool reverse = G.TextNeedsReverse(ToWorld(tr, profile.up), ToWorld(tr, profile.e1), ToWorld(tr, profile.e2));

        var water = G.BuildWater(profile);
        var label = G.BuildLabel(profile, reverseU: reverse);

        EnsureFolder(Folder);
        string key = Sanitize(bottle.sharedMesh.name);
        var waterMesh = SaveMesh(ToMesh(water, "Water_" + key), $"{Folder}/Water_{key}.asset");
        var labelMesh = SaveMesh(ToMesh(label, "Label_" + key), $"{Folder}/Label_{key}.asset");

        var waterMat = GetOrCreateMaterial($"{Folder}/Water_Mat.mat", SetupWaterMaterial);
        var labelMat = GetOrCreateMaterial($"{Folder}/Label_Mat.mat", SetupLabelMaterial);

        RemoveChild(tr, "Water");
        RemoveChild(tr, "Label");
        AddChild(tr, "Water", waterMesh, waterMat, ShadowCastingMode.Off);
        AddChild(tr, "Label", labelMesh, labelMat, ShadowCastingMode.On);

        EditorUtility.SetDirty(tr.gameObject);
        Debug.Log($"[BottleDressingTool] Added Water + Label to '{tr.name}'. {water.info}; {label.info}" +
                  (reverse ? " (label direction flipped for readable text)" : ""));
    }

    [MenuItem("Tools/VR Logging/Add Water + Label To Selected Bottle", true)]
    private static bool Validate() => Selection.activeGameObject != null;

    // ── helpers ──────────────────────────────────────────────

    /// <summary>Largest mesh with an ENABLED renderer (skips the hidden beer mesh and the small cap).</summary>
    private static MeshFilter FindBottleMesh(GameObject root)
    {
        if (root == null) return null;
        MeshFilter best = null; float bestSize = 0f;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            if (mf.name == "Water" || mf.name == "Label") continue;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || !mr.enabled || !mf.gameObject.activeInHierarchy) continue;
            Vector3 s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
            float size = Mathf.Abs(s.x * s.y * s.z);
            if (size > bestSize) { bestSize = size; best = mf; }
        }
        return best;
    }

    private static G.V3 ToWorld(Transform t, G.V3 v)
    {
        Vector3 w = t.TransformVector(new Vector3((float)v.x, (float)v.y, (float)v.z));
        return new G.V3(w.x, w.y, w.z);
    }

    private static Mesh ToMesh(G.MeshData d, string name)
    {
        var m = new Mesh { name = name };
        var v = new List<Vector3>(d.v.Count);
        foreach (var p in d.v) v.Add(new Vector3((float)p.x, (float)p.y, (float)p.z));
        var uv = new List<Vector2>(d.uv.Count);
        foreach (var p in d.uv) uv.Add(new Vector2((float)p[0], (float)p[1]));
        m.SetVertices(v);
        m.SetUVs(0, uv);
        m.SetTriangles(d.tri, 0);
        m.RecalculateNormals();
        m.RecalculateTangents();
        m.RecalculateBounds();
        return m;
    }

    private static Mesh SaveMesh(Mesh mesh, string path)
    {
        if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    private static Material GetOrCreateMaterial(string path, System.Action<Material> setup)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;      // keep any changes made by hand
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        mat = new Material(shader);
        setup(mat);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    private static void SetupWaterMaterial(Material m)
    {
        m.SetColor("_BaseColor", new Color(0.62f, 0.80f, 0.93f, 0.42f));
        m.SetFloat("_Smoothness", 0.92f);
        m.SetFloat("_Metallic", 0f);
        // URP transparent surface
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetFloat("_QueueOffset", -1f);                 // draw before the bottle -> no flicker
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent - 1;
        m.SetShaderPassEnabled("ShadowCaster", false);
    }

    private static void SetupLabelMaterial(Material m)
    {
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(LabelTex);
        if (tex == null)
            Debug.LogWarning($"[BottleDressingTool] {LabelTex} not found - label will be plain white.");
        else
        {
            var imp = AssetImporter.GetAtPath(LabelTex) as TextureImporter;
            if (imp != null)
            {
                imp.maxTextureSize = 4096;     // keep the text sharp
                imp.wrapModeU = TextureWrapMode.Repeat;
                imp.wrapModeV = TextureWrapMode.Clamp;
                imp.SaveAndReimport();
            }
            m.SetTexture("_BaseMap", tex);
            m.SetTexture("_MainTex", tex);
        }
        m.SetColor("_BaseColor", Color.white);
        m.SetFloat("_Smoothness", 0.35f);
    }

    private static void RemoveChild(Transform parent, string name)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var c = parent.GetChild(i);
            if (c.name == name) Undo.DestroyObjectImmediate(c.gameObject);
        }
    }

    private static void AddChild(Transform parent, string name, Mesh mesh, Material mat, ShadowCastingMode shadows)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Add " + name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = shadows;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrEmpty(s) ? "Bottle" : s.Replace(' ', '_');
    }
}
#endif
