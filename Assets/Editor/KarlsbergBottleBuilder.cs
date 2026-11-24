// Builds the grabbable Karlsberg beer bottle prefab from the Blender export.
//
//   Assets/Models/KarlsbergBottle/KarlsbergBottle.fbx            (meshes, metres, Y-up)
//   Assets/Models/KarlsbergBottle/Textures/T_KarlsbergLabel.png  (label, 2048x1024)
//   -> Assets/Models/KarlsbergBottle/Materials/*.mat              (URP Lit)
//   -> Assets/Prefabs/KarlsbergBottle.prefab
//
// The prefab follows the project's grabbable-stimulus pattern ([BuildingBlock] BeerBottle):
// Rigidbody + convex MeshCollider + Grabbable + GrabTracker + StimulusObject on the root,
// HandGrabInteractable + GrabInteractable on a child, tag "gazeTrack".
//
// Runs once automatically when the prefab is missing; rebuild any time with
// Tools > VR Logging > Build Karlsberg Bottle Prefab.

using System.IO;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[InitializeOnLoad]
public static class KarlsbergBottleBuilder
{
    const string Root      = "Assets/Models/KarlsbergBottle";
    const string FbxPath   = Root + "/KarlsbergBottle.fbx";
    const string TexPath   = Root + "/Textures/T_KarlsbergLabel.png";
    const string MatDir    = Root + "/Materials";
    const string PrefabPath = "Assets/Prefabs/KarlsbergBottle.prefab";

    static KarlsbergBottleBuilder()
    {
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (File.Exists(PrefabPath) || !File.Exists(FbxPath)) return;
            Build();
        };
    }

    [MenuItem("Tools/VR Logging/Build Karlsberg Bottle Prefab")]
    public static void Build()
    {
        ConfigureImporters();

        var mGlass = Lit("M_BottleGlass", new Color(0.43f, 0.20f, 0.05f, 0.45f), 0.95f, 0f, transparent: true);
        var mBeer  = Lit("M_Beer",        new Color(0.78f, 0.47f, 0.08f, 1f),     0.70f, 0f);
        var mLabel = Lit("M_Label",       Color.white,                             0.30f, 0f,
                         tex: AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath));
        var mNeck  = Lit("M_NeckFoil",    new Color(0f, 0.25f, 0.49f, 1f),         0.70f, 0.6f);
        var mCap   = Lit("M_Cap",         new Color(0f, 0.25f, 0.49f, 1f),         0.75f, 0.9f);

        var meshes = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<Mesh>().ToDictionary(m => m.name);
        Mesh M(string n)
        {
            if (!meshes.TryGetValue(n, out var m))
                throw new System.Exception($"Mesh '{n}' not found in {FbxPath}. Found: {string.Join(", ", meshes.Keys)}");
            return m;
        }

        var root = new GameObject("KarlsbergBottle");
        try
        {
            root.tag = "gazeTrack";

            // Visual parts. Draw order: beer + labels opaque, glass transparent on top.
            Part(root, "Beer_Liquid",  M("Beer_Liquid"),  mBeer,  ShadowCastingMode.Off);
            Part(root, "Label_Body",   M("Label_Body"),   mLabel, ShadowCastingMode.On);
            Part(root, "Label_Neck",   M("Label_Neck"),   mNeck,  ShadowCastingMode.On);
            Part(root, "Bottle_Cap",   M("Bottle_Cap"),   mCap,   ShadowCastingMode.On);
            var glass = Part(root, "Bottle_Glass", M("Bottle_Glass"), mGlass, ShadowCastingMode.On);

            // Physics: a full 0.5 l bottle weighs ~0.85 kg.
            var col = root.AddComponent<MeshCollider>();
            col.sharedMesh = glass.GetComponent<MeshFilter>().sharedMesh;
            col.convex = true;

            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 0.85f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Grabbing (Meta Interaction SDK), same wiring as the BuildingBlock bottle.
            var grabbable = root.AddComponent<Grabbable>();
            Set(grabbable, "_targetTransform", root.transform);
            Set(grabbable, "_rigidbody", rb);
            SetBool(grabbable, "_kinematicWhileSelected", true);
            SetBool(grabbable, "_throwWhenUnselected", true);

            var grabTracker = root.AddComponent<GrabTracker>();
            grabTracker.objectLabel = "KarlsbergBottle";
            grabTracker.isTriggerObject = true;

            var stim = root.AddComponent<StimulusObject>();
            stim.stimulusID = "KarlsbergBottle";
            stim.category = StimulusCategory.Trigger;

            var grabGo = new GameObject("HandGrabInteraction");
            grabGo.transform.SetParent(root.transform, false);
            var hand = grabGo.AddComponent<HandGrabInteractable>();
            Set(hand, "_pointableElement", grabbable);
            Set(hand, "_rigidbody", rb);
            var ctrl = grabGo.AddComponent<GrabInteractable>();
            Set(ctrl, "_pointableElement", grabbable);
            Set(ctrl, "_rigidbody", rb);

            Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool ok);
            if (!ok) throw new System.Exception("SaveAsPrefabAsset failed");

            var b = glass.GetComponent<MeshFilter>().sharedMesh.bounds;
            Debug.Log($"[KarlsbergBottle] Prefab built: {PrefabPath}  (glass bounds size {b.size.x:F3} x {b.size.y:F3} x {b.size.z:F3} m)", prefab);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    static void ConfigureImporters()
    {
        if (AssetImporter.GetAtPath(FbxPath) is ModelImporter mi)
        {
            bool dirty = false;
            if (mi.materialImportMode != ModelImporterMaterialImportMode.None) { mi.materialImportMode = ModelImporterMaterialImportMode.None; dirty = true; }
            if (mi.importAnimation) { mi.importAnimation = false; dirty = true; }
            if (mi.animationType != ModelImporterAnimationType.None) { mi.animationType = ModelImporterAnimationType.None; dirty = true; }
            if (!mi.bakeAxisConversion) { mi.bakeAxisConversion = true; dirty = true; }
            if (mi.importTangents != ModelImporterTangents.CalculateMikk) { mi.importTangents = ModelImporterTangents.CalculateMikk; dirty = true; }
            if (dirty) mi.SaveAndReimport();
        }
        if (AssetImporter.GetAtPath(TexPath) is TextureImporter ti)
        {
            bool dirty = false;
            if (ti.maxTextureSize != 2048) { ti.maxTextureSize = 2048; dirty = true; }
            if (!ti.sRGBTexture) { ti.sRGBTexture = true; dirty = true; }
            if (ti.anisoLevel < 4) { ti.anisoLevel = 4; dirty = true; }
            if (ti.wrapModeU != TextureWrapMode.Repeat) { ti.wrapModeU = TextureWrapMode.Repeat; dirty = true; }
            if (ti.wrapModeV != TextureWrapMode.Clamp) { ti.wrapModeV = TextureWrapMode.Clamp; dirty = true; }
            if (dirty) ti.SaveAndReimport();
        }
    }

    static Material Lit(string name, Color color, float smoothness, float metallic,
                        bool transparent = false, Texture2D tex = null)
    {
        Directory.CreateDirectory(MatDir);
        string path = $"{MatDir}/{name}.mat";
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
        m.shader = shader;
        m.SetColor("_BaseColor", color);
        m.SetColor("_Color", color);
        m.SetFloat("_Smoothness", smoothness);
        m.SetFloat("_Metallic", metallic);
        if (tex != null) { m.SetTexture("_BaseMap", tex); m.SetTexture("_MainTex", tex); }

        if (transparent)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetShaderPassEnabled("DepthOnly", false);
        }
        else
        {
            m.SetFloat("_Surface", 0f);
            m.SetFloat("_ZWrite", 1f);
            m.SetFloat("_SrcBlend", (float)BlendMode.One);
            m.SetFloat("_DstBlend", (float)BlendMode.Zero);
            m.SetOverrideTag("RenderType", "Opaque");
            m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = -1;
            m.SetShaderPassEnabled("DepthOnly", true);
        }
        EditorUtility.SetDirty(m);
        AssetDatabase.SaveAssets();
        return m;
    }

    static GameObject Part(GameObject parent, string name, Mesh mesh, Material mat, ShadowCastingMode shadows)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = shadows;
        return go;
    }

    static void Set(Object target, string field, Object value)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p == null) { Debug.LogWarning($"[KarlsbergBottle] {target.GetType().Name}.{field} not found"); return; }
        p.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetBool(Object target, string field, bool value)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p == null) { Debug.LogWarning($"[KarlsbergBottle] {target.GetType().Name}.{field} not found"); return; }
        p.boolValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
