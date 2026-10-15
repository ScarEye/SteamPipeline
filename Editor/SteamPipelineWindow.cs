// Copyright (c) 2026 ScarEye
// Licensed under the MIT License. See LICENSE in the package root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SteamPipeline
{
    public sealed class SteamPipelineWindow : EditorWindow
    {
        const string SettingsGuidKey = "SteamPipeline.SettingsGuid";
        const int MaxLogLines = 400;

        SteamPipelineSettings _settings;
        SerializedObject _serialized;
        string _username = string.Empty;
        string _password = string.Empty;
        bool _showPassword;
        Vector2 _logScroll;
        readonly List<string> _logLines = new List<string>(128);
        string _logText = string.Empty;
        int _logVersion;
        int _logTextVersion;
        bool _showConsole;
        bool _followLog = true;
        bool _deferScheduled;
        Vector2 _bodyScroll;
        bool _busy;
        SteamPipelineProgressWindow _progress;
        CancellationTokenSource _cts;
        double _lastLogRepaintAt;
        string _lastAppended;

        static GUIStyle _logStyle;

        [MenuItem("SteamPipeline/Open")]
        public static void Open()
        {
            var window = GetWindow<SteamPipelineWindow>("SteamPipeline");
            window.minSize = new Vector2(560, 480);
            window.Show();
        }

        [MenuItem("SteamPipeline/Create Settings Asset")]
        public static void CreateSettingsAsset()
        {
            CreateSettingsAssetAndReturn();
        }

        static SteamPipelineSettings CreateSettingsAssetAndReturn()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "Create SteamPipeline Settings",
                SteamPipelineSettings.DefaultAssetName,
                "asset",
                "Choose where to save SteamPipeline settings (project App ID and Depot ID).");

            if (string.IsNullOrEmpty(path))
                return null;

            var asset = CreateInstance<SteamPipelineSettings>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            return asset;
        }

        void OnEnable()
        {
            _username = SteamCredentialsStore.Username ?? string.Empty;
            _password = string.Empty;
            _showPassword = false;
            RestoreSettings();
        }

        void OnDisable()
        {
            SteamCredentialsStore.Username = _username ?? string.Empty;
            _password = string.Empty;
            if (SteamCmdUploader.AssemblyReloading || !_busy)
                return;

            try { _cts?.Cancel(); }
            catch { }
            SteamCmdUploader.CancelActive();
        }

        void RestoreSettings()
        {
            var guid = SessionState.GetString(SettingsGuidKey, string.Empty);
            if (!string.IsNullOrEmpty(guid))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var saved = AssetDatabase.LoadAssetAtPath<SteamPipelineSettings>(path);
                if (saved != null)
                {
                    BindSettings(saved);
                    return;
                }
            }

            if (_settings != null)
            {
                BindSettings(_settings);
                return;
            }

            var guids = AssetDatabase.FindAssets("t:SteamPipelineSettings");
            if (guids.Length == 0)
                return;

            var assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            BindSettings(AssetDatabase.LoadAssetAtPath<SteamPipelineSettings>(assetPath));
        }

        void BindSettings(SteamPipelineSettings settings)
        {
            _settings = settings;
            _serialized = settings != null ? new SerializedObject(settings) : null;
            if (settings == null)
            {
                SessionState.EraseString(SettingsGuidKey);
                return;
            }

            var path = AssetDatabase.GetAssetPath(settings);
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!string.IsNullOrEmpty(guid))
                SessionState.SetString(SettingsGuidKey, guid);
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("SteamPipeline", EditorStyles.boldLabel);
            DrawStatus();

            _bodyScroll = EditorGUILayout.BeginScrollView(_bodyScroll);
            using (new EditorGUI.DisabledScope(_busy))
            {
                DrawProjectSection();
                EditorGUILayout.Space(6);
                DrawSteamSection();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(_busy))
                DrawActions();

            if (_busy)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    "A build or upload is running. Cancel it from the progress window. Closing that window also cancels.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4);
            DrawLog();
        }

        void DrawStatus()
        {
            var steamcmdPath = SteamPipelinePaths.CachedSteamCmdLabel(out var steamcmdOk);
            var valid = _settings != null && SteamAppIdValidation.TryValidate(_settings, out _);
            string summary;
            if (_settings == null)
                summary = "No settings asset";
            else
            {
                var branch = string.IsNullOrWhiteSpace(_settings.releaseBranch)
                    ? "branch (none)"
                    : "branch " + _settings.releaseBranch.Trim();
                summary = $"App {_settings.appId}   ·   Depot {_settings.depotId}   ·   {branch}";
            }

            var line = (steamcmdOk ? "steamcmd ready" : "steamcmd missing") + "   ·   " + summary;
            if (!steamcmdOk || _settings == null || !valid)
                EditorGUILayout.HelpBox(steamcmdOk ? line : line + "\n" + steamcmdPath, MessageType.Warning);
            else
                EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
        }

        void DrawProjectSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Project", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    var picked = (SteamPipelineSettings)EditorGUILayout.ObjectField(
                        "Settings", _settings, typeof(SteamPipelineSettings), false);
                    if (GUILayout.Button("Create", EditorStyles.miniButton, GUILayout.Width(64)))
                    {
                        var created = CreateSettingsAssetAndReturn();
                        if (created != null)
                            picked = created;
                    }

                    if (picked != _settings)
                        BindSettings(picked);
                }

                if (_settings == null || _serialized == null)
                {
                    EditorGUILayout.HelpBox(
                        "Create a settings asset with the Create button, or SteamPipeline > Create Settings Asset.",
                        MessageType.Warning);
                    EditorGUILayout.Space(4);
                    DrawPathRows();
                    return;
                }

                _serialized.Update();
                DrawUIntProperty("appId", "App ID");
                DrawUIntProperty("depotId", "Depot ID");
                DrawProperty("releaseBranch", "Release Branch");
                DrawProperty("unityBuildSubfolder", "Unity Build Folder");
                DrawProperty("depotContentSubfolder", "Depot Subfolder");
                DrawProperty("buildDescription", "Build Description");
                DrawProperty("cleanContentFolder", "Clean Content Folder");
                DrawProperty("writeSteamAppIdTxt", "Write local steam_appid.txt");
                _serialized.ApplyModifiedProperties();

                if (!SteamAppIdValidation.TryValidate(_settings, out var err))
                    EditorGUILayout.HelpBox(err, MessageType.Error);

                EditorGUILayout.Space(4);
                DrawPathRows();
            }
        }

        void DrawPathRows()
        {
            var packageRoot = SteamPipelinePaths.CachedPackageRootLabel();
            DrawPathRow("Package", packageRoot, Directory.Exists(packageRoot));
            var steamcmd = SteamPipelinePaths.CachedSteamCmdLabel(out var steamcmdOk);
            DrawPathRow("steamcmd", steamcmd, steamcmdOk);
            DrawPathRow("Staging", SteamPipelinePaths.ProjectStagingRoot, Directory.Exists(SteamPipelinePaths.ProjectStagingRoot));
            var vdf = SteamPipelinePaths.GeneratedAppBuildVdf;
            DrawPathRow("VDF", vdf, File.Exists(vdf));
        }

        void DrawProperty(string propertyName, string label)
        {
            var prop = _serialized.FindProperty(propertyName);
            if (prop == null)
                return;
            EditorGUILayout.PropertyField(prop, new GUIContent(label, prop.tooltip));
        }

        void DrawUIntProperty(string propertyName, string label)
        {
            var prop = _serialized.FindProperty(propertyName);
            if (prop == null)
                return;

            EditorGUI.BeginChangeCheck();
            var entered = EditorGUILayout.LongField(new GUIContent(label, prop.tooltip), prop.longValue);
            if (!EditorGUI.EndChangeCheck())
                return;

            if (entered < 0)
                entered = 0;
            if (entered > uint.MaxValue)
                entered = uint.MaxValue;
            prop.longValue = entered;
        }

        void DrawPathRow(string label, string path, bool exists)
        {
            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(rect.x, rect.y, 70f, rect.height);
            var buttonRect = new Rect(rect.xMax - 48f, rect.y, 48f, rect.height);
            var pathRect = new Rect(labelRect.xMax + 4f, rect.y, Mathf.Max(20f, buttonRect.x - labelRect.xMax - 8f), rect.height);
            EditorGUI.LabelField(labelRect, label);
            EditorGUI.SelectableLabel(pathRect, string.IsNullOrEmpty(path) ? "(unknown)" : path, EditorStyles.miniLabel);
            var parent = Path.GetDirectoryName(path ?? string.Empty);
            var canReveal = exists || (!string.IsNullOrEmpty(parent) && Directory.Exists(parent));
            using (new EditorGUI.DisabledScope(!canReveal))
            {
                if (GUI.Button(buttonRect, "Show", EditorStyles.miniButton))
                    RevealPath(path);
            }
        }

        static void RevealPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            if (File.Exists(path) || Directory.Exists(path))
            {
                EditorUtility.RevealInFinder(path);
                return;
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                EditorUtility.RevealInFinder(parent);
        }

        void DrawSteamSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Steam Login", EditorStyles.boldLabel);
                _username = EditorGUILayout.TextField("Username", _username ?? string.Empty);

                using (new EditorGUILayout.HorizontalScope())
                {
                    var password = _password ?? string.Empty;
                    _password = _showPassword
                        ? EditorGUILayout.TextField("Password", password)
                        : EditorGUILayout.PasswordField("Password", password);

                    var show = GUILayout.Toggle(
                        _showPassword,
                        _showPassword ? "Hide" : "Show",
                        EditorStyles.miniButton,
                        GUILayout.Width(52));
                    if (show != _showPassword)
                    {
                        _showPassword = show;
                        EditorGUIUtility.editingTextField = false;
                        GUI.FocusControl(null);
                    }
                }

                EditorGUILayout.LabelField(
                    "Password stays in memory until the action starts, then it is cleared. It is not saved. Login opens steamcmd for Steam Guard.",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        void DrawActions()
        {
            SteamPipelinePaths.CachedSteamCmdLabel(out var steamcmdOk);
            var settingsOk = _settings != null && SteamAppIdValidation.TryValidate(_settings, out _);
            var userOk = !string.IsNullOrWhiteSpace(_username);
            var canUpload = settingsOk && userOk && steamcmdOk;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

                var old = GUI.backgroundColor;
                if (canUpload)
                    GUI.backgroundColor = new Color(0.45f, 0.78f, 0.50f);
                using (new EditorGUI.DisabledScope(!canUpload))
                {
                    if (GUILayout.Button("Build & Upload", GUILayout.Height(32)))
                        Defer(() =>
                        {
                            if (ConfirmUpload(uploadOnly: false))
                                _ = RunAsync(BuildAndUploadAsync);
                        });
                }
                GUI.backgroundColor = old;

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!userOk || !steamcmdOk))
                    {
                        if (GUILayout.Button(new GUIContent("Login / Guard", "Opens steamcmd so you can complete Steam Guard."), GUILayout.Height(26)))
                            _ = RunAsync(LoginAsync);
                    }

                    using (new EditorGUI.DisabledScope(!settingsOk))
                    {
                        if (GUILayout.Button("Build Only", GUILayout.Height(26)))
                            _ = RunAsync(BuildOnlyAsync);
                    }

                    using (new EditorGUI.DisabledScope(!canUpload))
                    {
                        if (GUILayout.Button("Upload Only", GUILayout.Height(26)))
                            Defer(() =>
                            {
                                if (ConfirmUpload(uploadOnly: true))
                                    _ = RunAsync(UploadOnlyAsync);
                            });
                    }
                }

                if (GUILayout.Button(new GUIContent("Update Steam SDK", "Runs the bundled SteamCMD so Valve can download the latest ContentBuilder files into SteamworksSDK~."), GUILayout.Height(26)))
                    Defer(ConfirmAndUpdateSteamCmd);

                if (!userOk)
                    EditorGUILayout.LabelField("Enter a Steam username to log in or upload.", EditorStyles.miniLabel);
                else if (!steamcmdOk)
                    EditorGUILayout.LabelField("steamcmd.exe was not found. Login and upload stay disabled.", EditorStyles.miniLabel);
            }
        }

        void Defer(Action action)
        {
            if (_deferScheduled || action == null)
                return;

            _deferScheduled = true;
            EditorApplication.delayCall += () =>
            {
                _deferScheduled = false;
                if (this == null)
                    return;
                action();
            };
        }

        bool ConfirmUpload(bool uploadOnly)
        {
            if (_settings == null)
                return false;

            var branch = string.IsNullOrWhiteSpace(_settings.releaseBranch)
                ? "(none)"
                : _settings.releaseBranch.Trim();
            string sizeLine;
            if (!uploadOnly && _settings.cleanContentFolder)
            {
                sizeLine = "The staging folder is cleared before the new build is copied.";
            }
            else
            {
                var staged = SteamPipelinePaths.GetContentFolderByteSize();
                var stagedText = staged > 0 ? SteamCmdProgressInfo.FormatSize(staged) : "empty";
                sizeLine = uploadOnly
                    ? "Staged content: " + stagedText
                    : "Currently staged: " + stagedText + ". The new build is copied on top of that folder.";
            }

            var action = uploadOnly ? "Upload Only" : "Build & Upload";
            var message =
                $"{action}\n\n" +
                $"App ID: {_settings.appId}\n" +
                $"Depot ID: {_settings.depotId}\n" +
                $"Branch: {branch}\n" +
                sizeLine;
            return EditorUtility.DisplayDialog("SteamPipeline", message, action, "Cancel");
        }

        void DrawLog()
        {
            var show = _showConsole;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(show ? "Hide console" : "Show console", EditorStyles.miniButton, GUILayout.Height(22)))
                    _showConsole = !show;

                if (show)
                {
                    _followLog = GUILayout.Toggle(_followLog, "Follow", EditorStyles.miniButton, GUILayout.Width(56), GUILayout.Height(22));
                    if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(48), GUILayout.Height(22)))
                        EditorGUIUtility.systemCopyBuffer = CurrentLogText();
                    if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(48), GUILayout.Height(22)))
                    {
                        _logLines.Clear();
                        _lastAppended = null;
                        _logVersion++;
                    }
                }
            }

            if (!show)
                return;

            if (_followLog)
                _logScroll.y = float.MaxValue;

            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(160));
            var text = CurrentLogText();
            var style = LogStyle();
            var height = string.IsNullOrEmpty(text)
                ? EditorGUIUtility.singleLineHeight
                : style.CalcHeight(new GUIContent(text), Mathf.Max(80f, position.width - 48f));
            EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(text) ? " " : text, style, GUILayout.MinHeight(Mathf.Max(EditorGUIUtility.singleLineHeight, height)));
            EditorGUILayout.EndScrollView();
        }

        string CurrentLogText()
        {
            if (_logTextVersion != _logVersion)
            {
                _logText = _logLines.Count == 0 ? string.Empty : string.Join("\n", _logLines);
                _logTextVersion = _logVersion;
            }

            return _logText;
        }

        static GUIStyle LogStyle()
        {
            if (_logStyle != null)
                return _logStyle;

            _logStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = false,
                richText = false,
                fontSize = 11,
                alignment = TextAnchor.UpperLeft
            };

            try
            {
                var font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New" }, 11);
                if (font != null)
                    _logStyle.font = font;
            }
            catch
            {
            }

            return _logStyle;
        }

        async Task RunAsync(Func<Task> action)
        {
            if (_busy)
                return;

            _busy = true;
            _showConsole = true;
            SteamCredentialsStore.Username = _username ?? string.Empty;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            Repaint();
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppendLog("Failed: " + ex.Message, important: true);
                _progress?.Complete(false, ex.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _busy = false;
                _password = string.Empty;
                _cts?.Dispose();
                _cts = null;
                Repaint();
            }
        }

        string TakePassword()
        {
            var password = _password ?? string.Empty;
            _password = string.Empty;
            return password;
        }

        void ConfirmAndUpdateSteamCmd()
        {
            const string message =
                "Update the bundled SteamCMD in SteamworksSDK~?\n\n" +
                "SteamCMD downloads the latest ContentBuilder files from Valve. " +
                "Login files already in that folder stay put.\n\n" +
                "This does not download the partner-site Steamworks SDK zip.";
            if (!EditorUtility.DisplayDialog("SteamPipeline", message, "Update Steam SDK", "Cancel"))
                return;

            _ = RunAsync(UpdateSteamCmdAsync);
        }

        async Task UpdateSteamCmdAsync()
        {
            OpenProgress("Update Steam SDK");
            await WaitForProgressPaint();
            if (Cancelled())
            {
                _progress?.Complete(false, "Cancelled.");
                AppendLog("Steam SDK update cancelled.", important: true);
                return;
            }

            _progress?.SetPhase("Updating SteamCMD…", 0f, "SteamCMD is downloading the latest ContentBuilder files.", true);
            AppendLog("Updating SteamCMD…", important: true);
            var result = await SteamCmdUploader.UpdateSteamCmdAsync(
                OnUploadLogLine,
                info => _progress?.ReportProgress(info),
                _cts.Token);
            SteamPipelinePaths.InvalidatePathCache();
            HandleUploadResult(result, "Steam SDK update");
        }

        async Task LoginAsync()
        {
            var password = TakePassword();
            AppendLog("Starting steamcmd login. Steam Guard may prompt in the console window.", important: true);
            var result = await SteamCmdUploader.LoginInteractiveAsync(_username, password, line => AppendLog(line), _cts.Token);
            AppendLog(result.Cancelled ? "Login cancelled." : result.Success ? "Login finished." : $"Login failed: {result.Error}", important: true);
        }

        async Task BuildOnlyAsync()
        {
            OpenProgress("Build");
            await WaitForProgressPaint();
            try
            {
                if (!TryBuildAndStage(out var error))
                {
                    ReportBuildFailure(error, "Build cancelled.");
                    return;
                }

                _progress?.Complete(true, "Build and staging complete.");
                AppendLog("Build and staging complete.", important: true);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        async Task UploadOnlyAsync()
        {
            var password = TakePassword();
            OpenProgress("Upload to Steam");
            await WaitForProgressPaint();
            if (Cancelled())
            {
                _progress?.Complete(false, "Cancelled.");
                AppendLog("Upload cancelled.", important: true);
                return;
            }

            var contentBytes = SteamPipelinePaths.GetContentFolderByteSize();
            _progress?.SetContentBytes(contentBytes);
            _progress?.SetPhase("Uploading to Steam…", 0f,
                contentBytes > 0
                    ? $"Staged {SteamCmdProgressInfo.FormatSize(contentBytes)}. steamcmd is running."
                    : "steamcmd is running.",
                indeterminate: true);
            AppendLog("Uploading to Steam…", important: true);

            var result = await SteamCmdUploader.UploadAsync(
                _settings, _username, password,
                OnUploadLogLine, info => _progress?.ReportProgress(info), _cts.Token);

            HandleUploadResult(result, "Upload");
        }

        async Task BuildAndUploadAsync()
        {
            var password = TakePassword();
            OpenProgress("Build & Upload");
            await WaitForProgressPaint();
            try
            {
                if (!TryBuildAndStage(out var error))
                {
                    ReportBuildFailure(error, "Cancelled before upload.");
                    return;
                }

                if (Cancelled())
                {
                    _progress?.Complete(false, "Cancelled.");
                    AppendLog("Cancelled before upload.", important: true);
                    return;
                }

                var contentBytes = SteamPipelinePaths.GetContentFolderByteSize();
                _progress?.SetContentBytes(contentBytes);
                _progress?.SetPhase("Uploading to Steam…", 0f,
                    contentBytes > 0
                        ? $"Build done. Staged {SteamCmdProgressInfo.FormatSize(contentBytes)}."
                        : "Build done. Starting steamcmd.",
                    indeterminate: true);
                AppendLog("Uploading to Steam…", important: true);

                var result = await SteamCmdUploader.UploadAsync(
                    _settings, _username, password,
                    OnUploadLogLine, info => _progress?.ReportProgress(info), _cts.Token);

                HandleUploadResult(result, "Build & Upload");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static Task WaitForProgressPaint()
        {
            var done = new TaskCompletionSource<bool>();
            EditorApplication.delayCall += () =>
            {
                EditorApplication.delayCall += () => done.TrySetResult(true);
            };
            return done.Task;
        }

        void ReportBuildFailure(string error, string cancelledMessage)
        {
            if (Cancelled())
            {
                _progress?.Complete(false, "Cancelled.");
                AppendLog(cancelledMessage, important: true);
                return;
            }

            var message = string.IsNullOrEmpty(error) ? "Build failed." : error;
            _progress?.Complete(false, message);
            AppendLog(message, important: true);
        }

        void HandleUploadResult(SteamCmdUploadResult result, string label)
        {
            SteamCmdUploader.DrainMainThreadQueue();
            if (result == null)
            {
                _progress?.Complete(false, $"{label} failed.");
                AppendLog($"{label} failed.", important: true);
                return;
            }

            if (result.Cancelled || Cancelled())
            {
                _progress?.Complete(false, "Cancelled by user.");
                AppendLog($"{label} cancelled.", important: true);
                return;
            }

            if (result.Success)
            {
                _progress?.Complete(true, $"{label} succeeded.");
                AppendLog($"{label} succeeded.", important: true);
            }
            else
            {
                _progress?.Complete(false, result.Error ?? $"{label} failed.");
                AppendLog($"{label} failed: {result.Error}", important: true);
            }
        }

        bool Cancelled()
        {
            return _cts == null || _cts.IsCancellationRequested;
        }

        bool TryBuildAndStage(out string error)
        {
            error = null;
            if (Cancelled())
                return false;

            _progress?.SetPhase("Building Windows 64…", 0f, "Unity is building the player. This step cannot be cancelled.", true);
            EditorUtility.DisplayProgressBar("SteamPipeline", "Building Windows 64…", 0.15f);
            AppendLog("Building Windows 64…", important: true);
            if (!SteamBuildRunner.BuildWindows(_settings, out var buildFolder, out error))
                return false;

            if (Cancelled())
                return false;

            _progress?.SetPhase("Staging build…", 0f, "Copying the player into Library/SteamPipeline/content.", true);
            EditorUtility.DisplayProgressBar("SteamPipeline", "Staging build…", 0.7f);
            AppendLog("Staging into Library/SteamPipeline/content…", important: true);
            if (!SteamBuildRunner.StageToContentBuilder(_settings, buildFolder, out error))
                return false;

            if (Cancelled())
                return false;

            var bytes = SteamPipelinePaths.GetContentFolderByteSize();
            _progress?.SetContentBytes(bytes);
            _progress?.SetPhase("Staging complete", 0f,
                bytes > 0 ? $"Staged {SteamCmdProgressInfo.FormatSize(bytes)}." : "Staging complete.",
                indeterminate: false);
            AppendLog(bytes > 0 ? $"Staged {SteamCmdProgressInfo.FormatSize(bytes)}." : "Staging complete.", important: true);
            return true;
        }

        void OpenProgress(string title)
        {
            if (_progress != null)
            {
                try
                {
                    _progress.Detach();
                    _progress.Close();
                }
                catch
                {
                }

                _progress = null;
            }

            _progress = SteamPipelineProgressWindow.ShowProgress(title);
            _progress.OnCancelRequested = OnProgressCancel;
            _progress?.SetPhase("Starting…", 0f, null, true);
        }

        void OnProgressCancel()
        {
            AppendLog("Cancel requested.", important: true);
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            Repaint();
        }

        void OnUploadLogLine(string line)
        {
            AppendLog(line);
            _progress?.AddLogLine(line);
        }

        void AppendLog(string line, bool important = false)
        {
            if (string.IsNullOrEmpty(line))
                return;
            if (line == _lastAppended)
                return;

            _lastAppended = line;
            _logLines.Add(line);
            while (_logLines.Count > MaxLogLines)
                _logLines.RemoveAt(0);
            _logVersion++;

            // Debug.Log on every steamcmd line stalls the Editor.
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
