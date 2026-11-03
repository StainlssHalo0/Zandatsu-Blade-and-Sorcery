using System;
using UnityEngine.Rendering;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds zandatsu.shaders (a plain asset bundle with the burnt rim shader) for B&S PCVR. Batch mode:
/// Unity.exe -batchmode -nographics -quit -projectPath BasSDK -buildTarget Win64
///           -executeMethod ZandatsuBundleBuilder.Build -zandatsuOut &lt;folder&gt; -logFile &lt;log&gt;
/// </summary>
public static class ZandatsuBundleBuilder
{
    public const string BundleName = "zandatsu.shaders";
    private static readonly string[] Assets = { "Assets/Zandatsu/BurnEdge.shader", "Assets/Zandatsu/MeshCapture.shader" };

    [MenuItem("Zandatsu/Build Shader Bundle")]
    public static void Build()
    {
        string outDir = Arg("-zandatsuOut") ?? Path.Combine("BuildStaging", "Zandatsu");
        Directory.CreateDirectory(outDir);

        // As the SDK's own builder does for PC: single pass instanced, so the shaders get the stereo variants.
        PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;

        var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = Assets };
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.DisableWriteTypeTree,
            BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            Debug.LogError("[Zandatsu] bundle build failed");
            if (Application.isBatchMode) EditorApplication.Exit(1);
            return;
        }

        foreach (string asset in Assets)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(asset);
            Debug.Log($"[Zandatsu] built {Path.Combine(outDir, BundleName)}; shader {shader?.name}, supported {shader?.isSupported}, " +
                      $"errors {(shader ? ShaderUtil.GetShaderMessageCount(shader) : -1)}");
            if (shader)
                Debug.Log("[Zandatsu] keywords: " + string.Join(" ", shader.keywordSpace.keywordNames));
            if (shader)
                foreach (ShaderMessage m in ShaderUtil.GetShaderMessages(shader))
                    Debug.Log($"[Zandatsu] shader {m.severity}: {m.message} ({m.file}:{m.line}, {m.platform})");
        }
    }

    /// <summary>Logs how many of our variants are left before and after the other strippers (URP, the SDK).</summary>
    private class VariantLog : UnityEditor.Build.IPreprocessShaders
    {
        protected virtual string When => "before";
        public virtual int callbackOrder => -1000;

        public void OnProcessShader(Shader shader, UnityEditor.Rendering.ShaderSnippetData snippet,
            System.Collections.Generic.IList<UnityEditor.Rendering.ShaderCompilerData> data)
        {
            if (!shader || shader.name != "Zandatsu/BurnEdge") return;
            var stereo = new UnityEngine.Rendering.ShaderKeyword("STEREO_INSTANCING_ON");
            int withStereo = 0;
            foreach (var d in data) if (d.shaderKeywordSet.IsEnabled(stereo)) withStereo++;
            Debug.Log($"[Zandatsu] {snippet.shaderType} {When} stripping: {data.Count} variants, {withStereo} stereo instanced");
            if (When == "after" && snippet.shaderType == UnityEditor.Rendering.ShaderType.Fragment)
                foreach (var d in data)
                {
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var k in d.shaderKeywordSet.GetShaderKeywords()) names.Add(ShaderKeyword.GetKeywordName(shader, k));
                    Debug.Log("[Zandatsu]   variant: " + string.Join(" ", names));
                }
        }
    }

    private class VariantLogAfter : VariantLog
    {
        protected override string When => "after";
        public override int callbackOrder => 1000;
    }

    private static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
