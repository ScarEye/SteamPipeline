// Copyright (c) 2026 ScarEye
// Licensed under the MIT License. See LICENSE in the package root.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SteamPipeline
{
    /// <summary>
    /// Small status window shown while Unity builds and steamcmd uploads (steamcmd stays hidden).
    /// </summary>
    public sealed class SteamPipelineProgressWindow : EditorWindow
    {
        string _title = "SteamPipeline";
        string _phase = "Working…";
        string _detail = string.Empty;
        string _lastLine = string.Empty;
        string _transferLine = string.Empty;
        long _bytesDone = -1;
        long _bytesTotal = -1;
        long _contentBytes;
        float _progress;
        bool _indeterminate;
        bool _finished;
        bool _success;
        bool _cancelRequested;
        double _startedAt;
        double _finishedAt;
        double _lastRepaintAt;
        Vector2 _logScroll;
        readonly List<string> _recentLines = new List<string>(64);

        public Action OnCancelRequested;

        public bool CancelRequested => _cancelRequested;
        public bool IsFinished => _finished;

        public static SteamPipelineProgressWindow ShowProgress(string title)
        {
            var window = CreateInstance<SteamPipelineProgressWindow>();
            window.titleContent = new GUIContent(title ?? "SteamPipeline");
            window._title = title ?? "SteamPipeline";
            window._startedAt = EditorApplication.timeSinceStartup;
            window.minSize = new Vector2(420, 260);
            window.maxSize = new Vector2(640, 420);
            window.ShowUtility();
            window.Focus();
            return window;
        }

        public void SetContentBytes(long bytes)
        {
            _contentBytes = bytes;
            if (_bytesTotal <= 0 && bytes > 0)
                _bytesTotal = bytes;
            RequestRepaintThrottled(force: true);
        }

        public void SetPhase(string phase, float progress01, string detail = null, bool indeterminate = false)
        {
            _phase = phase ?? _phase;
            _progress = Mathf.Clamp01(progress01);
            _indeterminate = indeterminate;
            if (detail != null)
                _detail = detail;
            _finished = false;
            RequestRepaintThrottled(force: true);
        }

        public void ReportProgress(SteamCmdProgressInfo info)
        {
            if (info == null)
                return;

            if (info.IsHeartbeat)
            {
                _detail = "steamcmd still working — scanning/processing (this can take a while on large depots)";
                _lastLine = _detail;
                // Keep last real % visible; don't switch to empty-looking pulse bar
                _indeterminate = false;
                if (_progress < 0.55f)
                    _progress = 0.55f;
                RequestRepaintThrottled();
                return;
            }

            if (!string.IsNullOrEmpty(info.RawLine) && info.RawLine != ".")
                AddRecentLine(info.RawLine);

            if (!string.IsNullOrEmpty(info.Status))
                _lastLine = info.Status;

            if (info.IsPhaseUpdate)
            {
                _detail = info.Status;
                _indeterminate = false;
                // File-list / finalize phase after transfer
                if (StartsWithIgnoreCase(info.Status, "Added") ||
                    StartsWithIgnoreCase(info.Status, "Modified") ||
                    ContainsIgnoreCase(info.Status, "new chunks") ||
                    ContainsIgnoreCase(info.Status, "Changes to baseline"))
                {
                    _phase = "Finalizing depot (listing changes)…";
                    if (_progress < 0.97f)
                        _progress = 0.97f;
                }
            }

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
                else if (_contentBytes > 0)
                    _transferLine = $"{SteamCmdProgressInfo.FormatSize(_bytesDone)} / {SteamCmdProgressInfo.FormatSize(_contentBytes)} (staged)";
            }

            if (info.HasPercent)
            {
                // Keep upload in the upper band if this window is used after build
                _progress = Mathf.Clamp01(Mathf.Max(_progress, Mathf.Lerp(0.55f, 0.99f, info.Percent01)));
                _indeterminate = false;
            }

            RequestRepaintThrottled();
        }

        public void ReportLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            if (SteamCmdProgressParser.TryParse(line, out var info))
            {
                ReportProgress(info);
                return;
            }

            _lastLine = line.Trim();
            AddRecentLine(_lastLine);
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
            _detail = "Stopping steamcmd / current step…";
            OnCancelRequested?.Invoke();
            SteamCmdUploader.CancelActive();
            RequestRepaintThrottled(force: true);
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
            if (!_finished && !_cancelRequested)
            {
                // Closing the window mid-run = cancel
                RequestCancel();
            }
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
            EditorGUILayout.LabelField(_phase, EditorStyles.largeLabel);

            if (!string.IsNullOrEmpty(_detail))
                EditorGUILayout.LabelField(_detail, EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(4);

            if (_contentBytes > 0)
                EditorGUILayout.LabelField("Staged content size", SteamCmdProgressInfo.FormatSize(_contentBytes));

            if (!string.IsNullOrEmpty(_transferLine) || _bytesTotal > 0 || _bytesDone >= 0)
            {
                var transfer = !string.IsNullOrEmpty(_transferLine)
                    ? _transferLine
                    : _bytesTotal > 0
                        ? $"{SteamCmdProgressInfo.FormatSize(Math.Max(0, _bytesDone))} / {SteamCmdProgressInfo.FormatSize(_bytesTotal)}"
                        : SteamCmdProgressInfo.FormatSize(Math.Max(0, _bytesDone));
                EditorGUILayout.LabelField("Uploaded / Total", transfer);
            }

            var rect = EditorGUILayout.GetControlRect(false, 20);
            if (_indeterminate && !_finished)
            {
                var pulse = (float)(EditorApplication.timeSinceStartup % 1.2) / 1.2f;
                EditorGUI.ProgressBar(rect, pulse, "Working…");
            }
            else
            {
                var label = $"{Mathf.RoundToInt(_progress * 100f)}%";
                if (!string.IsNullOrEmpty(_transferLine))
                    label = $"{label}  {_transferLine}";
                EditorGUI.ProgressBar(rect, _progress, label);
            }

            EditorGUILayout.LabelField($"Elapsed: {FormatElapsed(elapsed)}", EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Live output", EditorStyles.miniBoldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(90), GUILayout.MaxHeight(160));
            if (_recentLines.Count == 0)
                EditorGUILayout.LabelField("Waiting for steamcmd output…", EditorStyles.miniLabel);
            else
            {
                for (var i = 0; i < _recentLines.Count; i++)
                    EditorGUILayout.LabelField(_recentLines[i], EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_lastLine))
                EditorGUILayout.HelpBox(_lastLine, MessageType.None);

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (_finished)
                {
                    if (GUILayout.Button("Close", GUILayout.Width(110), GUILayout.Height(26)))
                        Close();
                }
                else
                {
                    var old = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.95f, 0.45f, 0.4f);
                    using (new EditorGUI.DisabledScope(_cancelRequested))
                    {
                        if (GUILayout.Button(_cancelRequested ? "Cancelling…" : "Cancel", GUILayout.Width(110), GUILayout.Height(26)))
                            RequestCancel();
                    }
                    GUI.backgroundColor = old;
                }
            }

            EditorGUILayout.Space(6);
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

        static bool StartsWithIgnoreCase(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
    }
}
