using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SteamPipeline
{
    public static class SteamCredentialsStore
    {
        const string UsernameKey = "SteamPipeline.SteamUsername";

        public static string Username
        {
            get => EditorPrefs.GetString(UsernameKey, string.Empty);
            set => EditorPrefs.SetString(UsernameKey, value ?? string.Empty);
        }
    }

    public sealed class SteamCmdUploadResult
    {
        public bool Success;
        public bool Cancelled;
        public int ExitCode;
        public string Output;
        public string Error;
    }

    public static class SteamCmdUploader
    {
        static Process _activeProcess;
        static readonly object _processLock = new object();

        public static bool SteamCmdExists => File.Exists(SteamPipelinePaths.SteamCmdExe);

        public static bool IsRunning
        {
            get
            {
                lock (_processLock)
                {
                    try
                    {
                        return _activeProcess != null && !_activeProcess.HasExited;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        public static void CancelActive()
        {
            lock (_processLock)
            {
                try
                {
                    if (_activeProcess != null && !_activeProcess.HasExited)
                    {
                        _activeProcess.Kill();
                        _activeProcess.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SteamPipeline] Failed to kill steamcmd: " + ex.Message);
                }
            }
        }

        public static async Task<SteamCmdUploadResult> LoginInteractiveAsync(
            string username,
            string password,
            Action<string> log,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                return Fail("Steam username is required.");

            var args = string.IsNullOrEmpty(password)
                ? $"+login {Quote(username)} +quit"
                : $"+login {Quote(username)} {Quote(password)} +quit";

            return await RunSteamCmdAsync(args, log, null, showWindow: true, cancellationToken);
        }

        public static async Task<SteamCmdUploadResult> UploadAsync(
            SteamPipelineSettings settings,
            string username,
            string password,
            Action<string> log,
            Action<SteamCmdProgressInfo> progress,
            CancellationToken cancellationToken = default)
        {
            if (!SteamAppIdValidation.TryValidate(settings, out var validationError))
                return Fail(validationError);

            if (!SteamCmdExists)
            {
                return Fail(
                    $"steamcmd.exe not found at {SteamPipelinePaths.SteamCmdExe}. Run it once to bootstrap, or check SteamworksSDK~/tools/ContentBuilder/builder.");
            }

            string vdfPath;
            try
            {
                vdfPath = SteamVdfGenerator.GenerateAppBuild(settings);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }

            SteamPipelinePaths.EnsureStagingFolders();

            var contentBytes = SteamPipelinePaths.GetContentFolderByteSize();
            progress?.Invoke(new SteamCmdProgressInfo
            {
                BytesDone = 0,
                BytesTotal = contentBytes > 0 ? contentBytes : -1,
                Percent01 = 0f,
                Status = contentBytes > 0
                    ? $"Content staged: {SteamCmdProgressInfo.FormatSize(contentBytes)} (Steam may upload less if unchanged chunks exist)"
                    : "Starting steamcmd upload…"
            });

            var loginPart = string.IsNullOrEmpty(password)
                ? $"+login {Quote(username)}"
                : $"+login {Quote(username)} {Quote(password)}";

            var args = $"{loginPart} +run_app_build {Quote(vdfPath)} +quit";
            return await RunSteamCmdAsync(args, log, progress, showWindow: false, cancellationToken);
        }

        static async Task<SteamCmdUploadResult> RunSteamCmdAsync(
            string arguments,
            Action<string> log,
            Action<SteamCmdProgressInfo> progress,
            bool showWindow,
            CancellationToken cancellationToken)
        {
            var exe = SteamPipelinePaths.SteamCmdExe;
            if (!File.Exists(exe))
                return Fail($"steamcmd.exe not found: {exe}");

            var builderDir = Path.GetDirectoryName(exe);
            var consoleLogPath = Path.Combine(builderDir ?? ".", "logs", "console_log.txt");

            // Truncate old console log so we only tail this run
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(consoleLogPath) ?? builderDir);
                File.WriteAllText(consoleLogPath, string.Empty);
            }
            catch
            {
                // non-fatal
            }

            log?.Invoke($"[SteamPipeline] {exe} {SanitizeArgsForLog(arguments)}");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = builderDir,
                UseShellExecute = false,
                CreateNoWindow = !showWindow
            };

            if (!showWindow)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }

            var output = new StringBuilder();
            CancellationTokenRegistration ctr = default;

            try
            {
                using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    lock (_processLock)
                    {
                        _activeProcess = process;
                    }

                    if (!showWindow)
                    {
                        process.OutputDataReceived += (_, e) =>
                        {
                            if (e.Data == null) return;
                            output.AppendLine(e.Data);
                            HandleOutputLine(e.Data, log, progress);
                        };
                        process.ErrorDataReceived += (_, e) =>
                        {
                            if (e.Data == null) return;
                            output.AppendLine(e.Data);
                            HandleOutputLine(e.Data, log, progress);
                        };
                    }

                    if (!process.Start())
                        return Fail("Failed to start steamcmd.");

                    if (!showWindow)
                    {
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                    }

                    ctr = cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (!process.HasExited)
                                process.Kill();
                        }
                        catch
                        {
                            // ignored
                        }
                    });

                    // Tail console_log.txt on a background loop (stdout is often block-buffered on Windows)
                    var tailTask = showWindow
                        ? Task.CompletedTask
                        : TailConsoleLogAsync(consoleLogPath, log, progress, process, cancellationToken);

                    await Task.Run(() =>
                    {
                        while (!process.HasExited)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                try { if (!process.HasExited) process.Kill(); } catch { /* ignored */ }
                                break;
                            }

                            process.WaitForExit(200);
                        }

                        try { process.WaitForExit(); } catch { /* ignored */ }
                    }, CancellationToken.None);

                    try { await tailTask; } catch { /* ignored */ }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new SteamCmdUploadResult
                        {
                            Success = false,
                            Cancelled = true,
                            ExitCode = -1,
                            Error = "Cancelled by user.",
                            Output = output.ToString()
                        };
                    }

                    var code = process.ExitCode;
                    var text = output.ToString();
                    var success = code == 0;
                    if (!success && string.IsNullOrWhiteSpace(text))
                        text = $"steamcmd exited with code {code}";

                    return new SteamCmdUploadResult
                    {
                        Success = success,
                        Cancelled = false,
                        ExitCode = code,
                        Output = text,
                        Error = success ? null : $"steamcmd failed (exit {code}). Check Steam Guard login / AppID / DepotID."
                    };
                }
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new SteamCmdUploadResult
                    {
                        Success = false,
                        Cancelled = true,
                        ExitCode = -1,
                        Error = "Cancelled by user.",
                        Output = output.ToString()
                    };
                }

                return Fail(ex.Message);
            }
            finally
            {
                ctr.Dispose();
                lock (_processLock)
                {
                    _activeProcess = null;
                }
            }
        }

        static void HandleOutputLine(string line, Action<string> log, Action<SteamCmdProgressInfo> progress)
        {
            log?.Invoke(line);
            if (progress != null && SteamCmdProgressParser.TryParse(line, out var info))
                progress.Invoke(info);
        }

        static async Task TailConsoleLogAsync(
            string logPath,
            Action<string> log,
            Action<SteamCmdProgressInfo> progress,
            Process process,
            CancellationToken cancellationToken)
        {
            long position = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var exited = false;
                try { exited = process.HasExited; } catch { exited = true; }

                try
                {
                    if (File.Exists(logPath))
                    {
                        using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (stream.Length >= position)
                            {
                                stream.Seek(position, SeekOrigin.Begin);
                                using (var reader = new StreamReader(stream, Encoding.UTF8))
                                {
                                    string chunk;
                                    while ((chunk = await reader.ReadLineAsync()) != null)
                                    {
                                        // steamcmd often rewrites progress with '\r' — split those too
                                        var parts = chunk.Split('\r');
                                        foreach (var part in parts)
                                        {
                                            if (string.IsNullOrWhiteSpace(part))
                                                continue;

                                            var trimmed = part.Trim();
                                            if (trimmed == ".")
                                            {
                                                // steamcmd heartbeat while scanning/chunking — keep UI alive
                                                progress?.Invoke(new SteamCmdProgressInfo
                                                {
                                                    RawLine = ".",
                                                    Status = "steamcmd still working (scanning / processing chunks)…",
                                                    Percent01 = -1f,
                                                    BytesDone = -1,
                                                    BytesTotal = -1,
                                                    IsHeartbeat = true
                                                });
                                                continue;
                                            }

                                            HandleOutputLine(trimmed, log, progress);
                                        }
                                    }

                                    position = stream.Position;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // file may be locked briefly
                }

                if (exited)
                    break;

                await Task.Delay(150, CancellationToken.None);
            }
        }

        static SteamCmdUploadResult Fail(string error) =>
            new SteamCmdUploadResult { Success = false, ExitCode = -1, Error = error, Output = error };

        static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";
            if (value.IndexOfAny(new[] { ' ', '"', '\t' }) < 0)
                return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        static string SanitizeArgsForLog(string args)
        {
            if (string.IsNullOrEmpty(args))
                return args;

            var parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 2; i++)
            {
                if (parts[i] == "+login")
                {
                    if (i + 2 < parts.Length && !parts[i + 2].StartsWith("+"))
                        parts[i + 2] = "***";
                    break;
                }
            }

            return string.Join(" ", parts);
        }
    }
}
