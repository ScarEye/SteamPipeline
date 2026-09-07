using System;
using UnityEngine;

namespace SteamPipeline
{
    /// <summary>
    /// Per-project SteamPipe settings. Store AppID/DepotID here — never commit Steam passwords.
    /// </summary>
    public sealed class SteamPipelineSettings : ScriptableObject
    {
        public const string DefaultAssetName = "SteamPipelineSettings";

        [Header("Steam App")]
        [Tooltip("Your real AppID from partner.steamgames.com — not Spacewar 480 or SDK sample 1000.")]
        public uint appId;

        [Tooltip("Windows (or primary) DepotID from SteamPipe > Depots.")]
        public uint depotId;

        [Tooltip("Steam branch to set live after upload (e.g. beta). Leave empty to upload without setting a branch live.")]
        public string releaseBranch = "beta";

        [Header("Build")]
        [Tooltip("If empty, uses scenes enabled in File > Build Settings.")]
        public string[] scenes;

        [Tooltip("Relative folder (under project) for the Unity player before staging to ContentBuilder.")]
        public string unityBuildSubfolder = "Builds/SteamWindows";

        [Tooltip("Optional description shown in Steamworks builds list. Empty = auto from version + timestamp.")]
        public string buildDescription;

        [Header("Staging")]
        [Tooltip("Clear ContentBuilder/content before copying the new build.")]
        public bool cleanContentFolder = true;

        [Tooltip("Write steam_appid.txt into staged content using AppID (useful for local runs).")]
        public bool writeSteamAppIdTxt = true;

        public string ResolveBuildDescription()
        {
            if (!string.IsNullOrWhiteSpace(buildDescription))
                return buildDescription.Trim();

            return $"Unity {Application.version} {DateTime.Now:yyyy-MM-dd HH:mm}";
        }
    }
}
