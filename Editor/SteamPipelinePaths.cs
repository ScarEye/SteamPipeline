using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SteamPipeline
{
    public static class SteamPipelinePaths
    {
        /// <summary>Folder that contains Editor/ and sdk/ (this repo root when cloned under Assets).</summary>
        public static string ToolingRoot
        {
            get
            {
                var asmdef = AssetDatabase.FindAssets("SteamPipeline.Editor t:asmdef")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => p.Replace('\\', '/').EndsWith("SteamPipeline.Editor.asmdef"));

                if (string.IsNullOrEmpty(asmdef))
                {
                    asmdef = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset")
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .FirstOrDefault(p => p.Replace('\\', '/').EndsWith("SteamPipeline.Editor.asmdef"));
                }

                if (!string.IsNullOrEmpty(asmdef))
                {
                    var editorDir = Path.GetDirectoryName(asmdef);
                    return Path.GetFullPath(Path.Combine(editorDir ?? ".", ".."));
                }

                return Path.GetFullPath(Path.Combine(Application.dataPath, "SteamPipeline"));
            }
        }

        public static string ContentBuilderRoot =>
            Path.Combine(ToolingRoot, "sdk", "tools", "ContentBuilder");

        public static string SteamCmdExe =>
            Path.Combine(ContentBuilderRoot, "builder", "steamcmd.exe");

        public static string ContentFolder =>
            Path.Combine(ContentBuilderRoot, "content");

        public static string OutputFolder =>
            Path.Combine(ContentBuilderRoot, "output");

        public static string ScriptsFolder =>
            Path.Combine(ContentBuilderRoot, "scripts");

        public static string TemplatePath =>
            Path.Combine(ToolingRoot, "Editor", "Templates", "app_build.vdf.template");

        public static string GeneratedAppBuildVdf =>
            Path.Combine(ScriptsFolder, "app_build_generated.vdf");

        public static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static string ResolveUnityBuildFolder(SteamPipelineSettings settings)
        {
            var sub = string.IsNullOrWhiteSpace(settings.unityBuildSubfolder)
                ? "Builds/SteamWindows"
                : settings.unityBuildSubfolder.Trim().Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(ProjectRoot, sub);
        }
    }

    public static class SteamAppIdValidation
    {
        public static readonly HashSet<uint> SampleAppIds = new HashSet<uint> { 480u, 1000u };
        public static readonly HashSet<uint> SampleDepotIds = new HashSet<uint> { 1001u, 1002u };

        public static bool TryValidate(SteamPipelineSettings settings, out string error)
        {
            if (settings == null)
            {
                error = "No SteamPipelineSettings assigned.";
                return false;
            }

            if (settings.appId == 0)
            {
                error = "AppID is required. Get it from partner.steamgames.com (not SDK sample 1000 / Spacewar 480).";
                return false;
            }

            if (SampleAppIds.Contains(settings.appId))
            {
                error =
                    $"AppID {settings.appId} is a Steamworks sample/test ID (Spacewar 480 or SDK sample 1000). Set your real AppID from partner.steamgames.com.";
                return false;
            }

            if (settings.depotId == 0)
            {
                error = "DepotID is required. Get it from Steamworks → SteamPipe → Depots.";
                return false;
            }

            if (SampleDepotIds.Contains(settings.depotId))
            {
                error =
                    $"DepotID {settings.depotId} looks like an SDK sample depot (1001/1002). Confirm the real DepotID on partner.steamgames.com.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
