using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SteamPipeline
{
    public sealed class SteamPipelineWindow : EditorWindow
    {
        SteamPipelineSettings _settings;
        string _username;
        string _password = string.Empty;
        Vector2 _logScroll;
        readonly StringBuilder _log = new StringBuilder();
        bool _busy;
        SteamPipelineProgressWindow _progress;

        [MenuItem("SteamPipeline/Open")]
        public static void Open()
        {
            var window = GetWindow<SteamPipelineWindow>("SteamPipeline");
            window.minSize = new Vector2(420, 520);
            window.Show();
        }

        [MenuItem("SteamPipeline/Create Settings Asset")]
        public static void CreateSettingsAsset()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "Create SteamPipeline Settings",
                SteamPipelineSettings.DefaultAssetName,
                "asset",
                "Choose where to save SteamPipeline settings (project-specific AppID/DepotID).");

            if (string.IsNullOrEmpty(path))
                return;

            var asset = CreateInstance<SteamPipelineSettings>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        void OnEnable()
        {
            _username = SteamCredentialsStore.Username;
            if (_settings == null)
            {
                var guids = AssetDatabase.FindAssets("t:SteamPipelineSettings");
                if (guids.Length > 0)
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                    _settings = AssetDatabase.LoadAssetAtPath<SteamPipelineSettings>(assetPath);
                }
            }
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("SteamPipeline", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Set your real AppID and DepotID from partner.steamgames.com. " +
                "Do not use Spacewar 480 or SDK sample AppID 1000 / depots 1001–1002.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(_busy))
            {
                DrawProjectSection();
                EditorGUILayout.Space(8);
                DrawSteamSection();
                EditorGUILayout.Space(8);
                DrawActions();
            }

            EditorGUILayout.Space(8);
            DrawLog();
        }

        void DrawProjectSection()
        {
            EditorGUILayout.LabelField("Project", EditorStyles.boldLabel);
            _settings = (SteamPipelineSettings)EditorGUILayout.ObjectField(
                "Settings", _settings, typeof(SteamPipelineSettings), false);

            if (_settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Create a settings asset via SteamPipeline > Create Settings Asset.",
                    MessageType.Warning);
                return;
            }

            EditorGUI.BeginChangeCheck();
            _settings.appId = (uint)System.Math.Max(0, EditorGUILayout.LongField("App ID", _settings.appId));
            _settings.depotId = (uint)System.Math.Max(0, EditorGUILayout.LongField("Depot ID", _settings.depotId));
            _settings.releaseBranch = EditorGUILayout.TextField("Release Branch", _settings.releaseBranch ?? string.Empty);
            _settings.unityBuildSubfolder = EditorGUILayout.TextField("Unity Build Folder", _settings.unityBuildSubfolder);
            _settings.buildDescription = EditorGUILayout.TextField("Build Description", _settings.buildDescription);
            _settings.cleanContentFolder = EditorGUILayout.Toggle("Clean Content Folder", _settings.cleanContentFolder);
            _settings.writeSteamAppIdTxt = EditorGUILayout.Toggle("Write steam_appid.txt", _settings.writeSteamAppIdTxt);

            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(_settings);

            if (!SteamAppIdValidation.TryValidate(_settings, out var err))
                EditorGUILayout.HelpBox(err, MessageType.Error);

            EditorGUILayout.LabelField("Package", SteamPipelinePaths.PackageRoot);
            EditorGUILayout.LabelField("steamcmd",
                SteamCmdUploader.SteamCmdExists ? SteamPipelinePaths.SteamCmdExe : "MISSING — bootstrap builder");
            EditorGUILayout.LabelField("Staging", SteamPipelinePaths.ProjectStagingRoot);
        }

        void DrawSteamSection()
        {
            EditorGUILayout.LabelField("Steam Login", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _username = EditorGUILayout.TextField("Username", _username ?? string.Empty);
            if (EditorGUI.EndChangeCheck())
                SteamCredentialsStore.Username = _username;

            _password = EditorGUILayout.PasswordField("Password (not saved)", _password ?? string.Empty);
            EditorGUILayout.HelpBox(
                "Password is kept in memory for this Editor session only. " +
                "First login may require Steam Guard — use Login (opens steamcmd).",
                MessageType.None);
        }

        void DrawActions()
        {
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Login / Refresh Guard", GUILayout.Height(28)))
                    _ = RunAsync(LoginAsync);

                if (GUILayout.Button("Build Only", GUILayout.Height(28)))
                    _ = RunAsync(BuildOnlyAsync);

                if (GUILayout.Button("Upload Only", GUILayout.Height(28)))
                    _ = RunAsync(UploadOnlyAsync);
            }

            var canBuildUpload = _settings != null &&
                                 SteamAppIdValidation.TryValidate(_settings, out _) &&
                                 !string.IsNullOrWhiteSpace(_username);

            using (new EditorGUI.DisabledScope(!canBuildUpload))
            {
                var old = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.45f, 0.85f, 0.55f);
                if (GUILayout.Button("Build & Upload", GUILayout.Height(36)))
                    _ = RunAsync(BuildAndUploadAsync);
                GUI.backgroundColor = old;
            }
        }

        void DrawLog()
        {
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.ExpandHeight(true));
            EditorGUILayout.TextArea(_log.ToString(), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("Clear Log"))
                _log.Length = 0;
        }

        async Task RunAsync(System.Func<Task> action)
        {
            if (_busy) return;
            _busy = true;
            Repaint();
            try
            {
                await action();
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }

        async Task LoginAsync()
        {
            AppendLog("Starting steamcmd login (Steam Guard may prompt in the console window)...");
            var result = await SteamCmdUploader.LoginInteractiveAsync(_username, _password, AppendLog);
            AppendLog(result.Success ? "Login finished." : $"Login failed: {result.Error}");
        }

        async Task BuildOnlyAsync()
        {
            OpenProgress("Build");
            try
            {
                if (!TryBuildAndStage(out _))
                {
                    FailProgress("Build failed. See the SteamPipeline log.");
                    return;
                }

                CompleteProgress(true, "Build and staging complete.");
                AppendLog("Build + stage complete.");
            }
            finally
            {
                await Task.CompletedTask;
            }
        }

        async Task UploadOnlyAsync()
        {
            OpenProgress("Upload to Steam");
            SetProgress("Uploading to Steam…", 0.55f, "steamcmd is running (hidden).", indeterminate: true);
            AppendLog("Uploading to Steam...");

            var result = await SteamCmdUploader.UploadAsync(_settings, _username, _password, OnUploadLogLine);
            if (result.Success)
            {
                CompleteProgress(true, "Upload succeeded.");
                AppendLog("Upload succeeded.");
            }
            else
            {
                FailProgress(result.Error ?? "Upload failed.");
                AppendLog($"Upload failed: {result.Error}");
            }
        }

        async Task BuildAndUploadAsync()
        {
            OpenProgress("Build & Upload");
            try
            {
                if (!TryBuildAndStage(out _))
                {
                    FailProgress("Build failed. See the SteamPipeline log.");
                    return;
                }

                SetProgress("Uploading to Steam…", 0.55f, "Build done. steamcmd is running (hidden).", indeterminate: true);
                AppendLog("Uploading to Steam...");

                var result = await SteamCmdUploader.UploadAsync(_settings, _username, _password, OnUploadLogLine);
                if (result.Success)
                {
                    CompleteProgress(true, "Build & Upload succeeded.");
                    AppendLog("Build & Upload succeeded.");
                }
                else
                {
                    FailProgress(result.Error ?? "Upload failed.");
                    AppendLog($"Upload failed: {result.Error}");
                }
            }
            finally
            {
                await Task.Yield();
            }
        }

        bool TryBuildAndStage(out string buildFolder)
        {
            buildFolder = null;

            SetProgress("Building Windows 64…", 0.1f, "Unity BuildPipeline is running.", indeterminate: true);
            AppendLog("Building Windows64...");
            if (!SteamBuildRunner.BuildWindows(_settings, out buildFolder, out var error))
            {
                AppendLog("Build failed: " + error);
                return false;
            }

            SetProgress("Staging build…", 0.45f, "Copying player into Library/SteamPipeline/content.", indeterminate: false);
            AppendLog("Staging to ContentBuilder/content...");
            if (!SteamBuildRunner.StageToContentBuilder(_settings, buildFolder, out error))
            {
                AppendLog("Stage failed: " + error);
                return false;
            }

            SetProgress("Ready to upload", 0.5f, "Staging complete.");
            return true;
        }

        void OpenProgress(string title)
        {
            if (_progress != null)
            {
                try { _progress.Close(); } catch { /* ignored */ }
                _progress = null;
            }

            _progress = SteamPipelineProgressWindow.ShowProgress(title);
            SetProgress("Starting…", 0.02f, null, indeterminate: true);
        }

        void SetProgress(string phase, float progress, string detail, bool indeterminate = false)
        {
            if (_progress == null)
                return;
            _progress.SetPhase(phase, progress, detail, indeterminate);
        }

        void OnUploadLogLine(string line)
        {
            AppendLog(line);
            _progress?.ReportLine(line);
        }

        void CompleteProgress(bool success, string message)
        {
            _progress?.Complete(success, message);
        }

        void FailProgress(string message)
        {
            _progress?.Complete(false, message);
        }

        void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            _log.AppendLine(line);
            Debug.Log("[SteamPipeline] " + line);
            Repaint();
        }
    }
}
