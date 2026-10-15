// Copyright (c) 2026 ScarEye
// Licensed under the MIT License. See LICENSE in the package root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
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
        static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        static int _mainThreadId;

        public static bool AssemblyReloading { get; private set; }

        [InitializeOnLoadMethod]
        static void InstallMainThreadPump()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update -= DrainMainThreadQueue;
            EditorApplication.update += DrainMainThreadQueue;
            AssemblyReloadEvents.beforeAssemblyReload -= MarkAssemblyReloading;
            AssemblyReloadEvents.beforeAssemblyReload += MarkAssemblyReloading;
        }

        static void MarkAssemblyReloading()
        {
            AssemblyReloading = true;
        }

        public static bool SteamCmdExists => File.Exists(SteamPipelinePaths.SteamCmdExe);

        public static void DrainMainThreadQueue()
        {
            if (!IsMainThread)
                return;

            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        public static void CancelActive()
        {
            Process process;
            lock (_processLock)
                process = _activeProcess;

            KillProcessTree(process);
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

            return await RunSteamCmdAsync(args, password, log, null, showWindow: true, cancellationToken);
        }

        const string SteamCmdZipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

        public static async Task<SteamCmdUploadResult> UpdateSteamCmdAsync(
            Action<string> log,
            Action<SteamCmdProgressInfo> progress,
            CancellationToken cancellationToken = default)
        {
            var exe = SteamPipelinePaths.SteamCmdExe;
            var builderDir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(builderDir))
                return Fail("Could not resolve the SteamCMD folder.");

            if (!CanWriteDirectory(builderDir))
            {
                return Fail(
                    "The Steamworks SDK folder is read-only, so SteamCMD cannot update in place. " +
                    "That happens when Package Manager has the package in its cache. Use a writable copy of the package.");
            }

            if (!File.Exists(exe))
            {
                log?.Invoke("steamcmd.exe is missing. Downloading the official SteamCMD package…");
                progress?.Invoke(new SteamCmdProgressInfo
                {
                    Percent01 = -1f,
                    IsPhaseUpdate = true,
                    Status = "Downloading SteamCMD…"
                });

                var downloadError = await DownloadSteamCmdAsync(builderDir, log, cancellationToken);
                if (downloadError != null)
                    return Fail(downloadError);
                if (cancellationToken.IsCancellationRequested)
                    return new SteamCmdUploadResult { Success = false, Cancelled = true, ExitCode = -1, Error = "Cancelled by user." };
            }

            log?.Invoke("Running steamcmd +quit so Valve can update ContentBuilder…");
            var result = await RunSteamCmdAsync("+quit", null, log, progress, showWindow: false, cancellationToken);
            if (result.Cancelled || result.ExitCode != 7)
                return result;

            log?.Invoke("steamcmd replaced its files and asked to restart. Running it once more…");
            return await RunSteamCmdAsync("+quit", null, log, progress, showWindow: false, cancellationToken);
        }

        static async Task<string> DownloadSteamCmdAsync(string builderDir, Action<string> log, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(builderDir);
            var zipPath = Path.Combine(builderDir, "steamcmd.zip");
            try
            {
                await Task.Run(() =>
                {
                    using (var client = new WebClient())
                        client.DownloadFile(SteamCmdZipUrl, zipPath);
                }, cancellationToken);

                ExtractZipOver(zipPath, builderDir);
                var exe = Path.Combine(builderDir, "steamcmd.exe");
                if (!File.Exists(exe))
                    return "The SteamCMD package downloaded, but steamcmd.exe was not inside it.";

                log?.Invoke("Downloaded steamcmd.exe.");
                return null;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    return "Cancelled by user.";
                return "Could not download SteamCMD: " + ex.Message;
            }
            finally
            {
                try
                {
                    if (File.Exists(zipPath))
                        File.Delete(zipPath);
                }
                catch
                {
                }
            }
        }

        static void ExtractZipOver(string zipPath, string destDir)
        {
            var root = Path.GetFullPath(destDir);
            using (var archive = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                    if (string.IsNullOrEmpty(relative))
                        continue;

                    var target = Path.GetFullPath(Path.Combine(root, relative));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target) ?? root);
                    using (var input = entry.Open())
                    using (var output = File.Create(target))
                        input.CopyTo(output);
                }
            }
        }

        static bool CanWriteDirectory(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".steampipeline-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
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

            var purged = SteamBuildRunner.PurgeExcludedFromContent();
            var contentBytes = SteamPipelinePaths.GetContentFolderByteSize();
            if (purged > 0)
            {
                log?.Invoke($"Purged {purged} do-not-ship items. Staged content is {SteamCmdProgressInfo.FormatSize(contentBytes)}.");
            }

            progress?.Invoke(new SteamCmdProgressInfo
            {
                Percent01 = -1f,
                BytesDone = -1,
                BytesTotal = -1,
                IsPhaseUpdate = true,
                Status = contentBytes > 0
                    ? $"Content staged: {SteamCmdProgressInfo.FormatSize(contentBytes)}. Steam may upload less when chunks are unchanged."
                    : "Starting steamcmd…"
            });

            var loginPart = string.IsNullOrEmpty(password)
                ? $"+login {Quote(username)}"
                : $"+login {Quote(username)} {Quote(password)}";

            var args = $"{loginPart} +run_app_build {Quote(vdfPath)} +quit";
            return await RunSteamCmdAsync(args, password, log, progress, showWindow: false, cancellationToken);
        }

        static async Task<SteamCmdUploadResult> RunSteamCmdAsync(
            string arguments,
            string secret,
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

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(consoleLogPath) ?? builderDir);
                File.WriteAllText(consoleLogPath, string.Empty);
            }
            catch (Exception ex)
            {
                log?.Invoke("Could not clear the previous steamcmd log: " + ex.Message);
            }

            log?.Invoke($"{exe} {SanitizeArgsForLog(arguments, secret)}");

            // Do not redirect stdout or stderr. A captured pipe deadlocks steamcmd on Windows
            // once the depot file list fills the buffer. Progress is tailed from logs/console_log.txt.
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = builderDir,
                UseShellExecute = false,
                CreateNoWindow = !showWindow
            };

            CancellationTokenRegistration ctr = default;

            try
            {
                using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    lock (_processLock)
                        _activeProcess = process;

                    if (!process.Start())
                        return Fail("Failed to start steamcmd.");

                    ctr = cancellationToken.Register(() => KillProcessTree(process));

                    var tailTask = TailConsoleLogAsync(consoleLogPath, secret, log, progress, process, cancellationToken);

                    await Task.Run(() =>
                    {
                        while (!process.HasExited)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                KillProcessTree(process);
                                break;
                            }

                            process.WaitForExit(200);
                        }

                        try { process.WaitForExit(); }
                        catch { }
                    }, CancellationToken.None);

                    try { await tailTask; }
                    catch { }

                    DrainMainThreadQueue();
                    var text = ScrubSecret(ReadAllTextOrEmpty(consoleLogPath), secret);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new SteamCmdUploadResult
                        {
                            Success = false,
                            Cancelled = true,
                            ExitCode = -1,
                            Error = "Cancelled by user.",
                            Output = text
                        };
                    }

                    var code = process.ExitCode;
                    var success = code == 0;
                    if (!success && string.IsNullOrWhiteSpace(text))
                        text = $"steamcmd exited with code {code}";

                    return new SteamCmdUploadResult
                    {
                        Success = success,
                        Cancelled = false,
                        ExitCode = code,
                        Output = text,
                        Error = success ? null : $"steamcmd failed (exit {code}). Check Steam Guard login, App ID, and Depot ID."
                    };
                }
            }
            catch (Exception ex)
            {
                DrainMainThreadQueue();
                if (cancellationToken.IsCancellationRequested)
                {
                    return new SteamCmdUploadResult
                    {
                        Success = false,
                        Cancelled = true,
                        ExitCode = -1,
                        Error = "Cancelled by user.",
                        Output = ScrubSecret(ReadAllTextOrEmpty(consoleLogPath), secret)
                    };
                }

                return Fail(ex.Message);
            }
            finally
            {
                ctr.Dispose();
                lock (_processLock)
                    _activeProcess = null;
            }
        }

        static void HandleOutputLine(string line, string secret, Action<string> log, Action<SteamCmdProgressInfo> progress)
        {
            line = ScrubSecret(line, secret);
            log?.Invoke(line);
            if (progress != null && SteamCmdProgressParser.TryParse(line, out var info) && info.HasSignal)
                progress.Invoke(info);
        }

        static async Task TailConsoleLogAsync(
            string logPath,
            string secret,
            Action<string> log,
            Action<SteamCmdProgressInfo> progress,
            Process process,
            CancellationToken cancellationToken)
        {
            long position = 0;
            var pending = Array.Empty<byte>();

            while (true)
            {
                var exited = SafeHasExited(process);
                var cancel = cancellationToken.IsCancellationRequested;

                try
                {
                    if (File.Exists(logPath))
                    {
                        using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (stream.Length < position)
                            {
                                position = 0;
                                pending = Array.Empty<byte>();
                            }

                            if (stream.Length > position)
                            {
                                stream.Seek(position, SeekOrigin.Begin);
                                var count = (int)Math.Min(stream.Length - position, 65536);
                                var buffer = new byte[count];
                                var read = stream.Read(buffer, 0, count);
                                if (read > 0)
                                {
                                    position += read;
                                    pending = Concat(pending, buffer, read);
                                    pending = EmitCompleteLines(pending, secret, log, progress, flush: false);
                                }
                            }
                        }
                    }
                }
                catch
                {
                }

                if (exited || cancel)
                {
                    EmitCompleteLines(pending, secret, log, progress, flush: true);
                    break;
                }

                await Task.Delay(150, CancellationToken.None);
            }
        }

        static byte[] EmitCompleteLines(
            byte[] data,
            string secret,
            Action<string> log,
            Action<SteamCmdProgressInfo> progress,
            bool flush)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            var end = data.Length;
            if (!flush)
            {
                end = -1;
                for (var i = data.Length - 1; i >= 0; i--)
                {
                    if (data[i] == (byte)'\n' || data[i] == (byte)'\r')
                    {
                        end = i + 1;
                        break;
                    }
                }

                if (end < 0)
                    return data;
            }

            if (end > 0)
            {
                var text = Encoding.UTF8.GetString(data, 0, end);
                var start = 0;
                for (var i = 0; i < text.Length; i++)
                {
                    if (text[i] != '\n' && text[i] != '\r')
                        continue;

                    DispatchLine(text.Substring(start, i - start), secret, log, progress);
                    if (i + 1 < text.Length && text[i] == '\r' && text[i + 1] == '\n')
                        i++;
                    start = i + 1;
                }

                if (flush && start < text.Length)
                    DispatchLine(text.Substring(start), secret, log, progress);
            }

            if (flush || end >= data.Length)
                return Array.Empty<byte>();

            var rest = new byte[data.Length - end];
            Buffer.BlockCopy(data, end, rest, 0, rest.Length);
            return rest;
        }

        static void DispatchLine(string line, string secret, Action<string> log, Action<SteamCmdProgressInfo> progress)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            var trimmed = line.Trim();
            if (trimmed == ".")
            {
                PostToMainThread(() =>
                {
                    progress?.Invoke(new SteamCmdProgressInfo
                    {
                        RawLine = ".",
                        Status = "steamcmd is still working. Large depots can pause here while files are scanned.",
                        Percent01 = -1f,
                        BytesDone = -1,
                        BytesTotal = -1,
                        IsHeartbeat = true
                    });
                });
                return;
            }

            PostToMainThread(() => HandleOutputLine(trimmed, secret, log, progress));
        }

        static void PostToMainThread(Action action)
        {
            if (action == null)
                return;

            if (IsMainThread)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                return;
            }

            _mainThreadQueue.Enqueue(action);
        }

        static bool IsMainThread =>
            _mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        static bool SafeHasExited(Process process)
        {
            try { return process == null || process.HasExited; }
            catch { return true; }
        }

        static byte[] Concat(byte[] front, byte[] back, int backCount)
        {
            var merged = new byte[front.Length + backCount];
            if (front.Length > 0)
                Buffer.BlockCopy(front, 0, merged, 0, front.Length);
            Buffer.BlockCopy(back, 0, merged, front.Length, backCount);
            return merged;
        }

        static void KillProcessTree(Process process)
        {
            if (process == null)
                return;

            int pid;
            try
            {
                if (process.HasExited)
                    return;
                pid = process.Id;
            }
            catch
            {
                return;
            }

            var started = false;
            try
            {
                using (var killer = new Process())
                {
                    killer.StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/PID " + pid.ToString() + " /T /F",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    started = killer.Start();
                    if (started && !IsMainThread)
                        killer.WaitForExit(4000);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SteamPipeline] Could not stop steamcmd: " + ex.Message);
            }

            if (started)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
            }
        }

        static string ReadAllTextOrEmpty(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch
            {
                return string.Empty;
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

        static string SanitizeArgsForLog(string args, string secret)
        {
            if (string.IsNullOrEmpty(args))
                return args;

            var tokens = TokenizeArgs(args);
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] != "+login")
                    continue;

                var passwordIndex = i + 2;
                if (passwordIndex < tokens.Count && !tokens[passwordIndex].StartsWith("+"))
                    tokens[passwordIndex] = "***";
                break;
            }

            var rebuilt = new StringBuilder();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (i > 0)
                    rebuilt.Append(' ');
                rebuilt.Append(Quote(tokens[i]));
            }

            return ScrubSecret(rebuilt.ToString(), secret);
        }

        static List<string> TokenizeArgs(string args)
        {
            var tokens = new List<string>();
            var i = 0;
            while (i < args.Length)
            {
                while (i < args.Length && char.IsWhiteSpace(args[i]))
                    i++;
                if (i >= args.Length)
                    break;

                if (args[i] == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < args.Length)
                    {
                        if (args[i] == '\\' && i + 1 < args.Length)
                        {
                            sb.Append(args[i + 1]);
                            i += 2;
                            continue;
                        }

                        if (args[i] == '"')
                        {
                            i++;
                            break;
                        }

                        sb.Append(args[i]);
                        i++;
                    }

                    tokens.Add(sb.ToString());
                    continue;
                }

                var start = i;
                while (i < args.Length && !char.IsWhiteSpace(args[i]))
                    i++;
                tokens.Add(args.Substring(start, i - start));
            }

            return tokens;
        }

        static string ScrubSecret(string line, string secret)
        {
            if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(secret))
                return line;
            if (line.IndexOf(secret, StringComparison.Ordinal) < 0)
                return line;
            return line.Replace(secret, "***");
        }
    }
}
