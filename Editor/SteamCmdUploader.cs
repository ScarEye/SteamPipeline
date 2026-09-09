using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        public int ExitCode;
        public string Output;
        public string Error;
    }

    public static class SteamCmdUploader
    {
        public static bool SteamCmdExists => File.Exists(SteamPipelinePaths.SteamCmdExe);

        public static async Task<SteamCmdUploadResult> LoginInteractiveAsync(string username, string password, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return Fail("Steam username is required.");
            }

            // First login may prompt for Steam Guard in the console window.
            var args = string.IsNullOrEmpty(password)
                ? $"+login {Quote(username)} +quit"
                : $"+login {Quote(username)} {Quote(password)} +quit";

            return await RunSteamCmdAsync(args, log, showWindow: true);
        }

        public static async Task<SteamCmdUploadResult> UploadAsync(
            SteamPipelineSettings settings,
            string username,
            string password,
            Action<string> log)
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

            var loginPart = string.IsNullOrEmpty(password)
                ? $"+login {Quote(username)}"
                : $"+login {Quote(username)} {Quote(password)}";

            var args = $"{loginPart} +run_app_build {Quote(vdfPath)} +quit";
            return await RunSteamCmdAsync(args, log, showWindow: false);
        }

        static async Task<SteamCmdUploadResult> RunSteamCmdAsync(string arguments, Action<string> log, bool showWindow)
        {
            var exe = SteamPipelinePaths.SteamCmdExe;
            if (!File.Exists(exe))
                return Fail($"steamcmd.exe not found: {exe}");

            var builderDir = Path.GetDirectoryName(exe);
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
            else
            {
                psi.RedirectStandardOutput = false;
                psi.RedirectStandardError = false;
            }

            var output = new StringBuilder();
            try
            {
                using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    if (!showWindow)
                    {
                        process.OutputDataReceived += (_, e) =>
                        {
                            if (e.Data == null) return;
                            output.AppendLine(e.Data);
                            log?.Invoke(e.Data);
                        };
                        process.ErrorDataReceived += (_, e) =>
                        {
                            if (e.Data == null) return;
                            output.AppendLine(e.Data);
                            log?.Invoke(e.Data);
                        };
                    }

                    if (!process.Start())
                        return Fail("Failed to start steamcmd.");

                    if (!showWindow)
                    {
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                    }

                    await Task.Run(() => process.WaitForExit());

                    var code = process.ExitCode;
                    var text = output.ToString();
                    var success = code == 0;
                    if (!success && string.IsNullOrWhiteSpace(text))
                        text = $"steamcmd exited with code {code}";

                    return new SteamCmdUploadResult
                    {
                        Success = success,
                        ExitCode = code,
                        Output = text,
                        Error = success ? null : $"steamcmd failed (exit {code}). Check Steam Guard login / AppID / DepotID."
                    };
                }
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
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

            // Hide password token after +login <user> <password>
            var parts = args.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 2; i++)
            {
                if (parts[i] == "+login")
                {
                    // parts[i+1]=user, parts[i+2] may be password unless next is +command
                    if (i + 2 < parts.Length && !parts[i + 2].StartsWith("+"))
                        parts[i + 2] = "***";
                    break;
                }
            }

            return string.Join(" ", parts);
        }
    }
}
