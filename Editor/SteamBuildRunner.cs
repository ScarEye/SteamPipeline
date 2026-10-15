// Copyright (c) 2026 ScarEye
// Licensed under the MIT License. See LICENSE in the package root.

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SteamPipeline
{
    public static class SteamBuildRunner
    {
        static readonly string[] ExcludedDirectoryNameParts =
        {
            "BackUpThisFolder_ButDontShipItWithYourGame",
            "DoNotShip",
            "_BurstDebugInformation_DoNotShip"
        };

        static readonly string[] ExcludedFileNames =
        {
            "steam_appid.txt" // Steam warns: should not be in depots
        };

        static readonly string[] ExcludedFileExtensions =
        {
            ".pdb"
        };

        public static bool BuildWindows(SteamPipelineSettings settings, out string buildFolder, out string error)
        {
            buildFolder = null;
            error = null;

            if (!SteamAppIdValidation.TryValidate(settings, out error))
                return false;

            var scenes = ResolveScenes(settings);
            if (scenes.Length == 0)
            {
                error = "No scenes to build. Enable scenes in Build Settings, or assign them on the settings asset.";
                return false;
            }

            buildFolder = SteamPipelinePaths.ResolveUnityBuildFolder(settings);
            try
            {
                Directory.CreateDirectory(buildFolder);
            }
            catch (Exception ex)
            {
                error = "Could not create the build folder: " + ex.Message;
                return false;
            }

            var exeName = SanitizeFileName(Application.productName) + ".exe";
            var location = Path.Combine(buildFolder, exeName);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = location,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            Debug.Log($"[SteamPipeline] Building Windows 64 → {location}");
            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            catch (Exception ex)
            {
                error = "Unity build failed: " + ex.Message;
                return false;
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                error = FirstBuildError(report);
                return false;
            }

            return true;
        }

        public static bool StageToContentBuilder(SteamPipelineSettings settings, out string error)
        {
            return StageToContentBuilder(settings, SteamPipelinePaths.ResolveUnityBuildFolder(settings), out error);
        }

        public static bool StageToContentBuilder(SteamPipelineSettings settings, string buildFolder, out string error)
        {
            error = null;
            if (settings == null)
            {
                error = "No SteamPipelineSettings assigned.";
                return false;
            }

            if (string.IsNullOrEmpty(buildFolder) || !Directory.Exists(buildFolder))
            {
                error = "Build folder missing.";
                return false;
            }

            try
            {
                SteamPipelinePaths.EnsureStagingFolders();
                var content = SteamPipelinePaths.ContentFolder;
                var depotContent = SteamPipelinePaths.ResolveDepotContentFolder(settings);

                if (settings.cleanContentFolder)
                    CleanContentFolder(content);

                var skipped = 0;
                Directory.CreateDirectory(depotContent);
                CopyDirectory(buildFolder, depotContent, ref skipped);
                skipped += PurgeExcludedFromContent(content);

                // steam_appid.txt stays next to the local Unity build. It is not copied into the depot.
                if (settings.writeSteamAppIdTxt)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(buildFolder, "steam_appid.txt"), settings.appId.ToString());
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[SteamPipeline] Could not write local steam_appid.txt: " + ex.Message);
                    }
                }

                var stagedBytes = SteamPipelinePaths.GetContentFolderByteSize();
                Debug.Log(
                    $"[SteamPipeline] Staged build into {depotContent} " +
                    $"({SteamCmdProgressInfo.FormatSize(stagedBytes)}; skipped {skipped} do-not-ship items)");
                return true;
            }
            catch (Exception ex)
            {
                error = "Staging failed: " + ex.Message;
                return false;
            }
        }

        static void CleanContentFolder(string content)
        {
            if (!Directory.Exists(content))
                return;

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

        public static int PurgeExcludedFromContent(string contentRoot = null)
        {
            contentRoot = string.IsNullOrEmpty(contentRoot)
                ? SteamPipelinePaths.ContentFolder
                : contentRoot;

            if (!Directory.Exists(contentRoot))
                return 0;

            var removed = 0;

            // Deepest first so a parent match does not make the child delete throw.
            var dirs = Directory.GetDirectories(contentRoot, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length)
                .ToArray();

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir))
                    continue;

                var name = Path.GetFileName(dir);
                if (!ShouldExcludeDirectory(name))
                    continue;

                try
                {
                    Directory.Delete(dir, true);
                    removed++;
                    Debug.Log($"[SteamPipeline] Purged do-not-ship folder: {name}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SteamPipeline] Could not purge {dir}: {ex.Message}");
                }
            }

            foreach (var file in Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!ShouldExcludeFile(name))
                    continue;

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SteamPipeline] Could not purge {file}: {ex.Message}");
                }
            }

            return removed;
        }

        static string FirstBuildError(BuildReport report)
        {
            if (report != null)
            {
                var steps = report.steps;
                if (steps != null)
                {
                    foreach (var step in steps)
                    {
                        var messages = step.messages;
                        if (messages == null)
                            continue;

                        foreach (var msg in messages)
                        {
                            if (msg.type != LogType.Error && msg.type != LogType.Exception)
                                continue;

                            var text = (msg.content ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                            if (text.Length == 0)
                                continue;
                            if (text.Length > 500)
                                text = text.Substring(0, 500) + "…";
                            return "Unity build failed: " + text;
                        }
                    }
                }

                return $"Unity build failed: {report.summary.result} ({report.summary.totalErrors} errors)";
            }

            return "Unity build failed.";
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

        static void CopyDirectory(string sourceDir, string destDir, ref int skipped)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var name = Path.GetFileName(file);
                if (ShouldExcludeFile(name))
                {
                    skipped++;
                    continue;
                }

                File.Copy(file, Path.Combine(destDir, name), true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var name = Path.GetFileName(dir);
                if (ShouldExcludeDirectory(name))
                {
                    skipped++;
                    continue;
                }

                CopyDirectory(dir, Path.Combine(destDir, name), ref skipped);
            }
        }

        public static bool ShouldExcludeDirectory(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName))
                return false;

            foreach (var part in ExcludedDirectoryNameParts)
            {
                if (directoryName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        public static bool ShouldExcludeFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;

            foreach (var excluded in ExcludedFileNames)
            {
                if (string.Equals(fileName, excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var ext = Path.GetExtension(fileName);
            foreach (var excludedExt in ExcludedFileExtensions)
            {
                if (string.Equals(ext, excludedExt, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
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
