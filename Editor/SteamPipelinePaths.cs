using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SteamPipeline
{
    public static class SteamPipelinePaths
    {
        /// <summary>
        /// Package / tooling root (contains Editor/ and SteamworksSDK~/).
        /// Works for UPM git install under Packages/ and for Assets/ clone.
        /// </summary>
        public static string PackageRoot
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

                // Fallbacks
                var packagesPath = Path.Combine(ProjectRoot, "Packages", "com.scareye.steampipeline");
                if (Directory.Exists(packagesPath))
                    return Path.GetFullPath(packagesPath);

                return Path.GetFullPath(Path.Combine(Application.dataPath, "SteamPipeline"));
            }
        }

        /// <summary>
        /// Steamworks SDK root. Folder name ends with ~ so Unity does not import C++/headers into the player.
        /// </summary>
        public static string SteamworksSdkRoot
        {
            get
            {
                var tilde = Path.Combine(PackageRoot, "SteamworksSDK~");
                if (Directory.Exists(tilde))
                    return tilde;

                // Legacy layout (pre-0.2.2)
                var legacy = Path.Combine(PackageRoot, "sdk");
                if (Directory.Exists(legacy))
                    return legacy;

                return tilde;
            }
        }

        /// <summary>Vendored Steamworks ContentBuilder inside the package (steamcmd lives here).</summary>
        public static string ContentBuilderRoot =>
            Path.Combine(SteamworksSdkRoot, "tools", "ContentBuilder");

        public static string SteamCmdExe =>
            Path.Combine(ContentBuilderRoot, "builder", "steamcmd.exe");

        /// <summary>
        /// Per-project staging (not inside the package). UPM packages can be read-only / replaced on update.
        /// </summary>
        public static string ProjectStagingRoot =>
            Path.Combine(ProjectRoot, "Library", "SteamPipeline");

        public static string ContentFolder =>
            Path.Combine(ProjectStagingRoot, "content");

        public static string OutputFolder =>
            Path.Combine(ProjectStagingRoot, "output");

        public static string ScriptsFolder =>
            Path.Combine(ProjectStagingRoot, "scripts");

        public static string TemplatePath =>
            Path.Combine(PackageRoot, "Editor", "Templates", "app_build.vdf.template");

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

        public static void EnsureStagingFolders()
        {
            Directory.CreateDirectory(ContentFolder);
            Directory.CreateDirectory(OutputFolder);
            Directory.CreateDirectory(ScriptsFolder);
        }

        public static long GetContentFolderByteSize()
        {
            try
            {
                if (!Directory.Exists(ContentFolder))
                    return 0;

                long total = 0;
                foreach (var file in Directory.EnumerateFiles(ContentFolder, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { /* ignored */ }
                }

                return total;
            }
            catch
            {
                return 0;
            }
        }

        static string _cachedPackageRoot;
        static string _cachedSteamCmd;
        static double _cacheAt;

        public static void InvalidatePathCache()
        {
            _cachedPackageRoot = null;
            _cachedSteamCmd = null;
            _cacheAt = 0;
        }

        public static string CachedPackageRootLabel()
        {
            RefreshPathCacheIfNeeded();
            return _cachedPackageRoot ?? PackageRoot;
        }

        public static string CachedSteamCmdLabel(out bool exists)
        {
            RefreshPathCacheIfNeeded();
            exists = !string.IsNullOrEmpty(_cachedSteamCmd) && File.Exists(_cachedSteamCmd);
            return exists ? _cachedSteamCmd : "MISSING — bootstrap builder";
        }

        static void RefreshPathCacheIfNeeded()
        {
            var now = EditorApplication.timeSinceStartup;
            if (_cachedPackageRoot != null && now - _cacheAt < 2.0)
                return;

            _cachedPackageRoot = PackageRoot;
            _cachedSteamCmd = SteamCmdExe;
            _cacheAt = now;
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
