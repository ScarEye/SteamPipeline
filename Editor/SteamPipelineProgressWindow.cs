using System;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SteamPipeline
{
    /// <summary>
    /// Small status window shown while Unity builds and steamcmd uploads (steamcmd stays hidden).
    /// </summary>
    public sealed class SteamPipelineProgressWindow : EditorWindow
    {
        static readonly Regex PercentRegex = new Regex(@"(\d{1,3})\s*%", RegexOptions.Compiled);

        string _title = "SteamPipeline";
        string _phase = "Working…";
        string _detail = string.Empty;
        string _lastLine = string.Empty;
        float _progress;
        bool _indeterminate;
        bool _finished;
        bool _success;
        double _startedAt;
        double _finishedAt;

        public static SteamPipelineProgressWindow ShowProgress(string title)
        {
            var window = CreateInstance<SteamPipelineProgressWindow>();
            window.titleContent = new GUIContent(title ?? "SteamPipeline");
            window._title = title ?? "SteamPipeline";
            window._startedAt = EditorApplication.timeSinceStartup;
            window.minSize = new Vector2(360, 150);
            window.maxSize = new Vector2(520, 220);
            window.ShowUtility();
            window.Focus();
            return window;
        }

        public void SetPhase(string phase, float progress01, string detail = null, bool indeterminate = false)
        {
            _phase = phase ?? _phase;
            _progress = Mathf.Clamp01(progress01);
            _indeterminate = indeterminate;
            if (detail != null)
                _detail = detail;
            _finished = false;
            Repaint();
        }

        public void ReportLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            _lastLine = line.Trim();

            var match = PercentRegex.Match(line);
            if (match.Success &&
                float.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct))
            {
                // Map steamcmd % into the upload band (0.55 – 0.98)
                _progress = Mathf.Lerp(0.55f, 0.98f, Mathf.Clamp01(pct / 100f));
                _indeterminate = false;
            }

            Repaint();
        }

        public void Complete(bool success, string message)
        {
            _finished = true;
            _success = success;
            _finishedAt = EditorApplication.timeSinceStartup;
            _indeterminate = false;
            _progress = success ? 1f : _progress;
            _phase = success ? "Done" : "Failed";
            _detail = message ?? (success ? "Finished." : "Failed.");
            Repaint();
        }

        public void CloseIfOpen()
        {
            Close();
        }

        void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        void OnEditorUpdate()
        {
            if (!_finished)
                Repaint();
        }

        void OnGUI()
        {
            var elapsed = (_finished ? _finishedAt : EditorApplication.timeSinceStartup) - _startedAt;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(_title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_phase, EditorStyles.largeLabel);

            if (!string.IsNullOrEmpty(_detail))
                EditorGUILayout.LabelField(_detail, EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(6);

            var rect = EditorGUILayout.GetControlRect(false, 18);
            if (_indeterminate && !_finished)
            {
                var pulse = (float)(EditorApplication.timeSinceStartup % 1.2) / 1.2f;
                EditorGUI.ProgressBar(rect, pulse, "Working…");
            }
            else
            {
                var label = $"{Mathf.RoundToInt(_progress * 100f)}%";
                EditorGUI.ProgressBar(rect, _progress, label);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Elapsed: {FormatElapsed(elapsed)}", EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(_lastLine))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Latest", EditorStyles.miniBoldLabel);
                EditorGUILayout.HelpBox(_lastLine, MessageType.None);
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (_finished)
                {
                    if (GUILayout.Button("Close", GUILayout.Width(100), GUILayout.Height(24)))
                        Close();
                }
                else
                {
                    GUI.enabled = false;
                    GUILayout.Button("Running…", GUILayout.Width(100), GUILayout.Height(24));
                    GUI.enabled = true;
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
    }
}
