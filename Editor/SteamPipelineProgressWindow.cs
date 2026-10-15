// Copyright (c) 2026 ScarEye
// Licensed under the MIT License. See LICENSE in the package root.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SteamPipeline
{
    public sealed class SteamPipelineProgressWindow : EditorWindow
    {
        string _title = "SteamPipeline";
        string _phase = "Working…";
        string _detail = string.Empty;
        string _transferLine = string.Empty;
        long _bytesDone = -1;
        long _bytesTotal = -1;
        long _contentBytes;
        float _progress;
        bool _indeterminate;
        bool _finished;
        bool _success;
        bool _cancelRequested;
        bool _suppressCancel;
        double _startedAt;
        double _finishedAt;
        double _lastRepaintAt;
        Vector2 _logScroll;
        GUIStyle _finishedPhaseStyle;
        readonly List<string> _recentLines = new List<string>(64);

        public Action OnCancelRequested;

        public static SteamPipelineProgressWindow ShowProgress(string title)
        {
            var window = CreateInstance<SteamPipelineProgressWindow>();
            window.titleContent = new GUIContent(title ?? "SteamPipeline");
            window._title = title ?? "SteamPipeline";
            window._startedAt = EditorApplication.timeSinceStartup;
            window.minSize = new Vector2(480, 300);
            window.ShowUtility();
            window.Focus();
            return window;
        }

        public void Detach()
        {
            _suppressCancel = true;
            OnCancelRequested = null;
        }

        public void SetContentBytes(long bytes)
        {
            _contentBytes = bytes;
            RequestRepaintThrottled(force: true);
        }

        public void SetPhase(string phase, float progress01, string detail = null, bool indeterminate = false)
        {
            if (_finished)
                return;

            _phase = phase ?? _phase;
            _progress = Mathf.Clamp01(progress01);
            _indeterminate = indeterminate;
            if (detail != null)
                _detail = detail;
            RequestRepaintThrottled(force: true);
        }

        public void ReportProgress(SteamCmdProgressInfo info)
        {
            if (info == null || _finished)
                return;

            if (info.IsHeartbeat)
            {
                _detail = info.Status;
                RequestRepaintThrottled();
                return;
            }

            if (!string.IsNullOrEmpty(info.Status))
            {
                if (info.HasPercent || info.IsPhaseUpdate)
                    _detail = info.Status;
            }

            if (info.IsPhaseUpdate && !info.HasPercent)
                ApplyPhaseLabel(info.Status);

            if (info.HasByteCounts)
            {
                _bytesDone = info.BytesDone;
                _bytesTotal = info.BytesTotal;
                _transferLine = info.FormatBytes();
            }
            else if (info.BytesDone >= 0)
            {
                _bytesDone = info.BytesDone;
                if (_bytesTotal > 0)
                    _transferLine = $"{SteamCmdProgressInfo.FormatSize(_bytesDone)} / {SteamCmdProgressInfo.FormatSize(_bytesTotal)}";
            }

            if (info.HasPercent)
            {
                _progress = Mathf.Clamp01(info.Percent01);
                _indeterminate = false;
                if (string.IsNullOrEmpty(_phase) || _phase == "Working…" || _phase == "Starting…")
                    _phase = "Uploading to Steam…";
            }

            RequestRepaintThrottled();
        }

        public void AddLogLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            AddRecentLine(line.Trim());
            RequestRepaintThrottled();
        }

        public void Complete(bool success, string message)
        {
            _finished = true;
            _success = success;
            _finishedAt = EditorApplication.timeSinceStartup;
            _indeterminate = false;
            _progress = success ? 1f : _progress;
            _phase = success ? "Done" : (_cancelRequested ? "Cancelled" : "Failed");
            _detail = message ?? (success ? "Finished." : "Failed.");
            RequestRepaintThrottled(force: true);
        }

        public void RequestCancel()
        {
            if (_finished || _cancelRequested)
                return;

            _cancelRequested = true;
            _phase = "Cancelling…";
            _detail = "Stopping steamcmd…";
            try
            {
                OnCancelRequested?.Invoke();
            }
            catch
            {
            }

            SteamCmdUploader.CancelActive();
            RequestRepaintThrottled(force: true);
        }

        void ApplyPhaseLabel(string status)
        {
            if (string.IsNullOrEmpty(status))
                return;

            if (ContainsIgnoreCase(status, "Scanning content") ||
                ContainsIgnoreCase(status, "Building file") ||
                ContainsIgnoreCase(status, "Building depot") ||
                ContainsIgnoreCase(status, "Starting build"))
            {
                _phase = "Preparing depot…";
                _indeterminate = _progress <= 0f;
                return;
            }

            if (ContainsIgnoreCase(status, "Uploading"))
            {
                _phase = "Uploading to Steam…";
                return;
            }

            if (ContainsIgnoreCase(status, "Successfully") || ContainsIgnoreCase(status, "BuildID"))
                _phase = "Finishing…";
        }

        void AddRecentLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            if (_recentLines.Count > 0 && _recentLines[_recentLines.Count - 1] == line)
                return;

            _recentLines.Add(line);
            while (_recentLines.Count > 40)
                _recentLines.RemoveAt(0);
            _logScroll.y = float.MaxValue;
        }

        void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            if (SteamCmdUploader.AssemblyReloading)
                return;
            // Closing this window mid-run cancels the build or upload.
            if (!_suppressCancel && !_finished && !_cancelRequested)
                RequestCancel();
        }

        void OnEditorUpdate()
        {
            if (!_finished)
                RequestRepaintThrottled();
        }

        void RequestRepaintThrottled(bool force = false)
        {
            var now = EditorApplication.timeSinceStartup;
            if (!force && now - _lastRepaintAt < 0.2)
                return;
            _lastRepaintAt = now;
            Repaint();
        }

        void OnGUI()
        {
            var elapsed = (_finished ? _finishedAt : EditorApplication.timeSinceStartup) - _startedAt;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(_title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_phase, FinishedPhaseStyle());

            if (_finished)
            {
                var kind = _success
                    ? MessageType.Info
                    : (_cancelRequested ? MessageType.Warning : MessageType.Error);
                EditorGUILayout.HelpBox(_detail, kind);
            }
            else if (!string.IsNullOrEmpty(_detail))
            {
                EditorGUILayout.LabelField(_detail, EditorStyles.wordWrappedLabel);
            }

            EditorGUILayout.Space(4);

            if (_contentBytes > 0)
                EditorGUILayout.LabelField("Staged content", SteamCmdProgressInfo.FormatSize(_contentBytes));

            if (!string.IsNullOrEmpty(_transferLine) || _bytesTotal > 0 || _bytesDone >= 0)
            {
                var transfer = !string.IsNullOrEmpty(_transferLine)
                    ? _transferLine
                    : _bytesTotal > 0
                        ? $"{SteamCmdProgressInfo.FormatSize(Math.Max(0, _bytesDone))} / {SteamCmdProgressInfo.FormatSize(_bytesTotal)}"
                        : SteamCmdProgressInfo.FormatSize(Math.Max(0, _bytesDone));
                EditorGUILayout.LabelField("Uploaded", transfer);
            }

            var rect = EditorGUILayout.GetControlRect(false, 22);
            if (_indeterminate && !_finished)
            {
                var pulse = (float)(EditorApplication.timeSinceStartup % 1.2) / 1.2f;
                EditorGUI.ProgressBar(rect, pulse, "Working…");
            }
            else
            {
                var label = $"{Mathf.RoundToInt(_progress * 100f)}%";
                if (!string.IsNullOrEmpty(_transferLine))
                    label = $"{label}   {_transferLine}";
                EditorGUI.ProgressBar(rect, Mathf.Clamp01(_progress), label);
            }

            EditorGUILayout.LabelField($"Elapsed {FormatElapsed(elapsed)}", EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Live output", EditorStyles.miniBoldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(110), GUILayout.ExpandHeight(true));
            if (_recentLines.Count == 0)
                EditorGUILayout.LabelField("Waiting for steamcmd…", EditorStyles.miniLabel);
            else
            {
                for (var i = 0; i < _recentLines.Count; i++)
                    EditorGUILayout.LabelField(_recentLines[i], EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (_finished)
                {
                    if (GUILayout.Button("Close", GUILayout.Width(120), GUILayout.Height(26)))
                        Close();
                }
                else
                {
                    var old = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.95f, 0.45f, 0.4f);
                    using (new EditorGUI.DisabledScope(_cancelRequested))
                    {
                        if (GUILayout.Button(_cancelRequested ? "Cancelling…" : "Cancel", GUILayout.Width(120), GUILayout.Height(26)))
                            RequestCancel();
                    }
                    GUI.backgroundColor = old;
                }
            }

            EditorGUILayout.Space(6);
        }

        GUIStyle FinishedPhaseStyle()
        {
            if (!_finished)
                return EditorStyles.largeLabel;

            if (_finishedPhaseStyle == null)
                _finishedPhaseStyle = new GUIStyle(EditorStyles.largeLabel);

            var color = _success
                ? new Color(0.30f, 0.72f, 0.38f)
                : (_cancelRequested ? new Color(0.85f, 0.62f, 0.20f) : new Color(0.86f, 0.32f, 0.28f));
            _finishedPhaseStyle.normal.textColor = color;
            _finishedPhaseStyle.hover.textColor = color;
            _finishedPhaseStyle.focused.textColor = color;
            _finishedPhaseStyle.active.textColor = color;
            return _finishedPhaseStyle;
        }

        static string FormatElapsed(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (ts.TotalHours >= 1)
                return ts.ToString(@"h\:mm\:ss");
            return ts.ToString(@"mm\:ss");
        }

        static bool ContainsIgnoreCase(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
