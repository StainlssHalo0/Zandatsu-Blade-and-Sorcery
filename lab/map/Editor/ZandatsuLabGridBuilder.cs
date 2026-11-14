using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ThunderRoad;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class ZandatsuLabGridBuilder
{
    private const string AssetFolder = "Assets/ZandatsuLabGrid";
    private const string ScenePath = AssetFolder + "/Grid.unity";
    private const string SceneAddress = "ZandatsuLab.Level.Grid";
    private const float Width = 24f;

    public static void Build()
    {
        var args = Environment.GetCommandLineArgs();
        int outputArg = Array.IndexOf(args, "-zandatsuLabOut");
        if (outputArg < 0 || outputArg == args.Length - 1)
            throw new ArgumentException("Pass -zandatsuLabOut with an output directory.");

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            throw new InvalidOperationException("Build this level with -buildTarget Win64.");

        CreateScene();

        var settings = AddressableAssetSettingsDefaultObject.Settings;
        var group = settings.FindGroup("ZandatsuLabGrid") ??
            settings.CreateGroup("ZandatsuLabGrid", false, false, false, null,
                typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
        var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(ScenePath), group);
        entry.address = SceneAddress;
        entry.SetLabel("Windows", true, true);
        AssetDatabase.SaveAssets();

        var bundle = ScriptableObject.CreateInstance<AssetBundleGroup>();
        bundle.folderName = "ZandatsuLab";
        bundle.addressableAssetGroups = new List<AddressableAssetGroup> { group };
        if (!AssetBundleBuilder.Build(bundle, false))
            throw new InvalidOperationException("Could not build the ZandatsuLab map.");

        string output = args[outputArg + 1];
        Directory.CreateDirectory(output);
        var files = Directory.EnumerateFiles(AssetBundleBuilder.assetsLocalPath)
            .Where(file => Path.GetExtension(file) == ".bundle" ||
                Path.GetFileName(file).StartsWith("catalog_ZandatsuLab", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!files.Any(file => file.EndsWith(".bundle")) || !files.Any(file => file.EndsWith(".json")))
            throw new InvalidOperationException("The SDK did not produce the scene bundle and its catalog.");
        foreach (string file in files)
            File.Copy(file, Path.Combine(output, Path.GetFileName(file)), true);
        Debug.Log("[ZandatsuLabGrid] Exported level assets to " + output);
    }

    private static void CreateScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var level = new GameObject("Level");
        level.AddComponent<Level>();
        var spawner = new GameObject("PlayerSpawner");
        spawner.transform.SetParent(level.transform, false);
        spawner.transform.localPosition = new Vector3(0f, 0.03f, -2.5f);
        spawner.AddComponent<PlayerSpawner>();

        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        if (!unlit) throw new InvalidOperationException("The URP Unlit shader is missing from the SDK.");

        var floor = new Material(unlit) { name = "Lab floor" };
        floor.SetTexture("_BaseMap", CreateGrid());
        floor.SetTextureScale("_BaseMap", Vector2.one * (Width / 2f));
        floor = SaveAsset(floor, "Floor.mat");
        var wall = new Material(unlit) { name = "Lab backdrop" };
        wall.SetColor("_BaseColor", new Color32(69, 87, 106, 255));
        wall = SaveAsset(wall, "Backdrop.mat");

        var stone = AssetDatabase.LoadAssetAtPath<PhysicMaterial>("Assets/SDK/PhysicMaterials/Stone.physicMaterial");
        if (!stone) throw new InvalidOperationException("The Stone physics material is missing from the SDK.");
        Block("Grid floor", new Vector3(0f, -0.25f, 0f), new Vector3(Width, 0.5f, Width), floor, stone);
        float edge = Width / 2f;
        for (int side = -1; side <= 1; side += 2)
        {
            Block("End wall", new Vector3(0f, 3f, side * edge), new Vector3(Width, 6f, 0.3f), wall, stone);
            Block("Side wall", new Vector3(side * edge, 3f, 0f), new Vector3(0.3f, 6f, Width), wall, stone);
        }
        Block("Ceiling", new Vector3(0f, 6f, 0f), new Vector3(Width, 0.3f, Width), wall, stone)
            .shadowCastingMode = ShadowCastingMode.Off;

        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
        // The game's LitMoss materials expect the linear fog variant. Keep it beyond the room.
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogStartDistance = 40f;
        RenderSettings.fogEndDistance = 70f;
        RenderSettings.sun = AddLight("Key light", new Vector3(52f, -32f, 0f), 1.05f, LightShadows.Soft);
        AddLight("Fill light", new Vector3(32f, 145f, 0f), 0.45f, LightShadows.None);

        var surface = level.AddComponent<NavMeshSurface>();
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.BuildNavMesh();
        if (!surface.navMeshData) throw new InvalidOperationException("No navigation mesh was baked for the floor.");
        surface.navMeshData = SaveAsset(surface.navMeshData, "Navigation.asset");
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
    }

    private static Texture2D CreateGrid()
    {
        const int size = 256;
        const int tile = size / 2;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = "One meter grid",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,
        };
        var pixels = new Color32[size * size];
        var light = new Color32(161, 186, 205, 255);
        var dark = new Color32(131, 156, 177, 255);
        var line = new Color32(103, 130, 154, 255);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            bool grid = x % tile < 2 || y % tile < 2;
            pixels[y * size + x] = grid ? line : (x / tile + y / tile) % 2 == 0 ? light : dark;
        }
        texture.SetPixels32(pixels);
        texture.Apply();
        // Make the mipmapped texture a Unity asset so its filter settings ship with the scene.
        string path = AssetFolder + "/Grid.asset";
        AssetDatabase.CreateAsset(texture, path);
        return texture;
    }

    private static T SaveAsset<T>(T asset, string name) where T : UnityEngine.Object
    {
        string path = AssetFolder + "/" + name;
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    private static Renderer Block(string name, Vector3 position, Vector3 scale, Material material, PhysicMaterial surface)
    {
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = name;
        block.isStatic = true;
        block.transform.position = position;
        block.transform.localScale = scale;
        block.GetComponent<Collider>().sharedMaterial = surface;
        var renderer = block.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        return renderer;
    }

    private static Light AddLight(string name, Vector3 angles, float intensity, LightShadows shadows)
    {
        var light = new GameObject(name).AddComponent<Light>();
        light.transform.eulerAngles = angles;
        light.type = LightType.Directional;
        light.intensity = intensity;
        light.shadows = shadows;
        return light;
    }
}
