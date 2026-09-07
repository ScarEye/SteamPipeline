using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SteamPipeline
{
    public static class SteamBuildRunner
    {
        public static bool BuildWindows(SteamPipelineSettings settings, out string buildFolder, out string error)
        {
            buildFolder = null;
            error = null;

            if (!SteamAppIdValidation.TryValidate(settings, out error))
                return false;

            var scenes = ResolveScenes(settings);
            if (scenes.Length == 0)
            {
                error = "No scenes to build. Enable scenes in Build Settings or assign them on SteamPipelineSettings.";
                return false;
            }

            buildFolder = SteamPipelinePaths.ResolveUnityBuildFolder(settings);
            Directory.CreateDirectory(buildFolder);

            var exeName = SanitizeFileName(Application.productName) + ".exe";
            var location = Path.Combine(buildFolder, exeName);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = location,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            Debug.Log($"[SteamPipeline] Building Windows64 → {location}");
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                error = $"Unity build failed: {report.summary.result}";
                return false;
            }

            return true;
        }

        public static bool StageToContentBuilder(SteamPipelineSettings settings, string buildFolder, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(buildFolder) || !Directory.Exists(buildFolder))
            {
                error = "Build folder missing.";
                return false;
            }

            SteamPipelinePaths.EnsureStagingFolders();
            var content = SteamPipelinePaths.ContentFolder;

            if (settings.cleanContentFolder)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(content))
                {
                    var name = Path.GetFileName(entry);
                    if (name == ".gitkeep")
                        continue;

                    if (Directory.Exists(entry))
                        Directory.Delete(entry, true);
                    else
                        File.Delete(entry);
                }
            }

            CopyDirectory(buildFolder, content);

            if (settings.writeSteamAppIdTxt)
            {
                File.WriteAllText(Path.Combine(content, "steam_appid.txt"), settings.appId.ToString());
            }

            Debug.Log($"[SteamPipeline] Staged build into {content}");
            return true;
        }

        static string[] ResolveScenes(SteamPipelineSettings settings)
        {
            if (settings.scenes != null && settings.scenes.Length > 0)
            {
                return settings.scenes
                    .Where(s => !string.IsNullOrWhiteSpace(s) && File.Exists(s))
                    .ToArray();
            }

            return EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
        }

        static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, dest);
            }
        }

        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Game";

            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
