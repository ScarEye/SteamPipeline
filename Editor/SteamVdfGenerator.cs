// Copyright (c) 2026 ScarEye
// Licensed under the MIT License. See LICENSE in the package root.

using System.IO;
using System.Text;
using UnityEngine;

namespace SteamPipeline
{
    public static class SteamVdfGenerator
    {
        public static string GenerateAppBuild(SteamPipelineSettings settings)
        {
            var templatePath = SteamPipelinePaths.TemplatePath;
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("Missing VDF template.", templatePath);

            var template = File.ReadAllText(templatePath);
            var contentRoot = Path.GetFullPath(SteamPipelinePaths.ContentFolder);
            var buildOutput = Path.GetFullPath(SteamPipelinePaths.OutputFolder);
            var branch = settings.releaseBranch?.Trim() ?? string.Empty;

            // Steam: omit SetLive when branch empty (upload only).
            var setLiveLine = string.IsNullOrEmpty(branch)
                ? string.Empty
                : $"\t\"SetLive\" \"{Escape(branch)}\"";

            var vdf = template
                .Replace("{{APP_ID}}", settings.appId.ToString())
                .Replace("{{DEPOT_ID}}", settings.depotId.ToString())
                .Replace("{{DESC}}", Escape(settings.ResolveBuildDescription()))
                .Replace("{{CONTENT_ROOT}}", Escape(contentRoot))
                .Replace("{{BUILD_OUTPUT}}", Escape(buildOutput))
                .Replace("\t\"SetLive\" \"{{BRANCH}}\"", setLiveLine)
                .Replace("{{BRANCH}}", Escape(branch));

            SteamPipelinePaths.EnsureStagingFolders();
            var outPath = SteamPipelinePaths.GeneratedAppBuildVdf;
            File.WriteAllText(outPath, vdf, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Debug.Log($"[SteamPipeline] Wrote {outPath}");
            return outPath;
        }

        static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
