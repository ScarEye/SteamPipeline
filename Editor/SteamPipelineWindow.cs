using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SteamPipeline
{
    public sealed class SteamPipelineWindow : EditorWindow
    {
        SteamPipelineSettings _settings;
        string _username = string.Empty;
        string _password = string.Empty;
        bool _showPassword;
        Vector2 _logScroll;
        readonly StringBuilder _log = new StringBuilder();
        bool _busy;
        SteamPipelineProgressWindow _progress;
        CancellationTokenSource _cts;
        double _lastLogRepaintAt;
        string _lastAppended;

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
            _username = SteamCredentialsStore.Username ?? string.Empty;
            _password = string.Empty;
            _showPassword = false;
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

        void OnDisable()
        {
            // Persist username only when the window closes — not on every keystroke.
            SteamCredentialsStore.Username = _username ?? string.Empty;
            _password = string.Empty;
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

            if (_busy)
            {
                EditorGUILayout.HelpBox(
                    "A build/upload is running. Use Cancel on the progress window (or close it) to stop.",
                    MessageType.Warning);
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

            EditorGUILayout.LabelField("Package", SteamPipelinePaths.CachedPackageRootLabel());
            EditorGUILayout.LabelField("steamcmd", SteamPipelinePaths.CachedSteamCmdLabel(out _));
            EditorGUILayout.LabelField("Staging", SteamPipelinePaths.ProjectStagingRoot);
        }

        void DrawSteamSection()
        {
            EditorGUILayout.LabelField("Steam Login", EditorStyles.boldLabel);

            // Username is local field only while typing; saved on window close / action start.
            _username = EditorGUILayout.TextField("Username", _username ?? string.Empty);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (_showPassword)
                    _password = EditorGUILayout.TextField("Password", _password ?? string.Empty);
                else
                    _password = EditorGUILayout.PasswordField("Password", _password ?? string.Empty);

                _showPassword = GUILayout.Toggle(_showPassword, "Show", GUILayout.Width(55));
            }

            EditorGUILayout.HelpBox(
                "Password is not saved to disk and is cleared when this window closes or after an action starts. " +
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
            SteamCredentialsStore.Username = _username ?? string.Empty;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            Repaint();
            try
            {
                await action();
            }
            finally
            {
                _busy = false;
                _password = string.Empty; // do not keep password around
                _cts?.Dispose();
                _cts = null;
                Repaint();
            }
        }

        string TakePassword()
        {
            var p = _password ?? string.Empty;
            _password = string.Empty; // clear UI field immediately
            return p;
        }

        async Task LoginAsync()
        {
            var password = TakePassword();
            AppendLog("Starting steamcmd login (Steam Guard may prompt in the console window)...", important: true);
            var result = await SteamCmdUploader.LoginInteractiveAsync(_username, password, line => AppendLog(line), _cts.Token);
            AppendLog(result.Cancelled ? "Login cancelled." : result.Success ? "Login finished." : $"Login failed: {result.Error}", important: true);
        }

        async Task BuildOnlyAsync()
        {
            OpenProgress("Build");
            try
            {
                if (!TryBuildAndStage(out _))
                {
                    if (_cts.IsCancellationRequested)
                    {
                        FailProgress("Cancelled.");
                        AppendLog("Build cancelled.", important: true);
                    }
                    else
                    {
                        FailProgress("Build failed. See the SteamPipeline log.");
                    }

                    return;
                }

                CompleteProgress(true, "Build and staging complete.");
                AppendLog("Build + stage complete.", important: true);
            }
            finally
            {
                await Task.CompletedTask;
            }
        }

        async Task UploadOnlyAsync()
        {
            var password = TakePassword();
            OpenProgress("Upload to Steam");
            var contentBytes = SteamPipelinePaths.GetContentFolderByteSize();
            _progress?.SetContentBytes(contentBytes);
            SetProgress("Uploading to Steam…", 0.55f,
                contentBytes > 0
                    ? $"Staged {SteamCmdProgressInfo.FormatSize(contentBytes)}. steamcmd running (hidden)."
                    : "steamcmd is running (hidden).",
                indeterminate: true);
            AppendLog("Uploading to Steam...", important: true);

            var result = await SteamCmdUploader.UploadAsync(
                _settings, _username, password,
                OnUploadLogLine, OnUploadProgress, _cts.Token);

            HandleUploadResult(result, "Upload");
        }

        async Task BuildAndUploadAsync()
        {
            var password = TakePassword();
            OpenProgress("Build & Upload");
            try
            {
                if (!TryBuildAndStage(out _))
                {
                    if (_cts.IsCancellationRequested)
                    {
                        FailProgress("Cancelled.");
                        AppendLog("Cancelled before upload.", important: true);
                    }
                    else
                    {
                        FailProgress("Build failed. See the SteamPipeline log.");
                    }

                    return;
                }

                if (_cts.IsCancellationRequested)
                {
                    FailProgress("Cancelled.");
                    AppendLog("Cancelled before upload.", important: true);
                    return;
                }

                var contentBytes = SteamPipelinePaths.GetContentFolderByteSize();
                _progress?.SetContentBytes(contentBytes);
                SetProgress("Uploading to Steam…", 0.55f,
                    contentBytes > 0
                        ? $"Build done. Staged {SteamCmdProgressInfo.FormatSize(contentBytes)}. Uploading…"
                        : "Build done. steamcmd is running (hidden).",
                    indeterminate: true);
                AppendLog("Uploading to Steam...", important: true);

                var result = await SteamCmdUploader.UploadAsync(
                    _settings, _username, password,
                    OnUploadLogLine, OnUploadProgress, _cts.Token);

                HandleUploadResult(result, "Build & Upload");
            }
            finally
            {
                await Task.Yield();
            }
        }

        void HandleUploadResult(SteamCmdUploadResult result, string label)
        {
            if (result.Cancelled || _cts.IsCancellationRequested)
            {
                FailProgress("Cancelled by user.");
                AppendLog($"{label} cancelled.", important: true);
                return;
            }

            if (result.Success)
            {
                CompleteProgress(true, $"{label} succeeded.");
                AppendLog($"{label} succeeded.", important: true);
            }
            else
            {
                FailProgress(result.Error ?? $"{label} failed.");
                AppendLog($"{label} failed: {result.Error}", important: true);
            }
        }

        bool TryBuildAndStage(out string buildFolder)
        {
            buildFolder = null;

            if (_cts.IsCancellationRequested)
                return false;

            SetProgress("Building Windows 64…", 0.1f, "Unity BuildPipeline is running (cannot interrupt mid-compile).", indeterminate: true);
            AppendLog("Building Windows64...", important: true);
            if (!SteamBuildRunner.BuildWindows(_settings, out buildFolder, out var error))
            {
                AppendLog("Build failed: " + error, important: true);
                return false;
            }

            if (_cts.IsCancellationRequested)
                return false;

            SetProgress("Staging build…", 0.45f, "Copying player into Library/SteamPipeline/content.", indeterminate: false);
            AppendLog("Staging to ContentBuilder/content...", important: true);
            if (!SteamBuildRunner.StageToContentBuilder(_settings, buildFolder, out error))
            {
                AppendLog("Stage failed: " + error, important: true);
                return false;
            }

            if (_cts.IsCancellationRequested)
                return false;

            var bytes = SteamPipelinePaths.GetContentFolderByteSize();
            _progress?.SetContentBytes(bytes);
            SetProgress("Ready to upload", 0.5f,
                bytes > 0 ? $"Staging complete — {SteamCmdProgressInfo.FormatSize(bytes)}" : "Staging complete.");
            return true;
        }

        void OpenProgress(string title)
        {
            if (_progress != null)
            {
                try
                {
                    _progress.OnCancelRequested = null;
                    _progress.Close();
                }
                catch { /* ignored */ }

                _progress = null;
            }

            _progress = SteamPipelineProgressWindow.ShowProgress(title);
            _progress.OnCancelRequested = OnProgressCancel;
            SetProgress("Starting…", 0.02f, null, indeterminate: true);
        }

        void OnProgressCancel()
        {
            AppendLog("Cancel requested — stopping…", important: true);
            try { _cts?.Cancel(); } catch { /* ignored */ }
            SteamCmdUploader.CancelActive();
            Repaint();
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

        void OnUploadProgress(SteamCmdProgressInfo info)
        {
            _progress?.ReportProgress(info);
            if (info != null && !string.IsNullOrEmpty(info.Status) && info.HasPercent)
                AppendLog(info.Status);
        }

        void CompleteProgress(bool success, string message)
        {
            _progress?.Complete(success, message);
        }

        void FailProgress(string message)
        {
            _progress?.Complete(false, message);
        }

        void AppendLog(string line, bool important = false)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (line == _lastAppended)
                return;
            _lastAppended = line;
            _log.AppendLine(line);

            // Avoid spamming Console (major Editor lag during upload).
            if (important)
                Debug.Log("[SteamPipeline] " + line);

            var now = EditorApplication.timeSinceStartup;
            if (important || now - _lastLogRepaintAt > 0.25)
            {
                _lastLogRepaintAt = now;
                Repaint();
            }
        }
    }
}
