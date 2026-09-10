using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamPipeline
{
    public sealed class SteamCmdProgressInfo
    {
        public float Percent01 = -1f;
        public long BytesDone = -1;
        public long BytesTotal = -1;
        public string Status = string.Empty;
        public string RawLine = string.Empty;

        public bool HasByteCounts => BytesDone >= 0 && BytesTotal > 0;
        public bool HasPercent => Percent01 >= 0f;

        public string FormatBytes()
        {
            if (!HasByteCounts)
                return string.Empty;
            return $"{FormatSize(BytesDone)} / {FormatSize(BytesTotal)}";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "—";
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var unit = 0;
            while (b >= 1024d && unit < units.Length - 1)
            {
                b /= 1024d;
                unit++;
            }

            return unit == 0
                ? $"{bytes} {units[unit]}"
                : $"{b:0.##} {units[unit]}";
        }
    }

    public static class SteamCmdProgressParser
    {
        // Update state (0x61) downloading, progress: 15.33 (350367250 / 2284840496)
        static readonly Regex StateProgressRegex = new Regex(
            @"progress:\s*(?<pct>\d+(?:\.\d+)?)\s*\(\s*(?<done>\d+)\s*/\s*(?<total>\d+)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 23.1MB (23%)   or  12.5 MiB / 40.0 MiB
        static readonly Regex MbParenPercentRegex = new Regex(
            @"(?<done>\d+(?:\.\d+)?)\s*(?<doneUnit>KB|MB|GB|MiB|GiB|B)\s*\(\s*(?<pct>\d+(?:\.\d+)?)\s*%\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex UploadedOfRegex = new Regex(
            @"(?:uploaded|uploading|processed|processing|compressed|transferred)[^\d]*(?<done>\d+(?:\.\d+)?)\s*(?<doneUnit>KB|MB|GB|B)?[^\d]*(?:of|/)\s*(?<total>\d+(?:\.\d+)?)\s*(?<totalUnit>KB|MB|GB|B)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex PlainPercentRegex = new Regex(
            @"(?<pct>\d{1,3}(?:\.\d+)?)\s*%",
            RegexOptions.Compiled);

        public static bool TryParse(string line, out SteamCmdProgressInfo info)
        {
            info = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var clean = line.Trim();
            // console_log lines often start with [timestamp]
            if (clean.StartsWith("["))
            {
                var close = clean.IndexOf(']');
                if (close > 0 && close + 1 < clean.Length)
                    clean = clean.Substring(close + 1).Trim();
            }

            if (clean == "." || clean.Length == 0)
                return false;

            info = new SteamCmdProgressInfo { RawLine = clean, Status = clean };

            var state = StateProgressRegex.Match(clean);
            if (state.Success)
            {
                info.Percent01 = Clamp01(ParseFloat(state.Groups["pct"].Value) / 100f);
                info.BytesDone = ParseLong(state.Groups["done"].Value);
                info.BytesTotal = ParseLong(state.Groups["total"].Value);
                info.Status = $"Transfer {info.FormatBytes()} ({info.Percent01 * 100f:0.#}%)";
                return true;
            }

            var mb = MbParenPercentRegex.Match(clean);
            if (mb.Success)
            {
                info.Percent01 = Clamp01(ParseFloat(mb.Groups["pct"].Value) / 100f);
                info.BytesDone = ToBytes(ParseFloat(mb.Groups["done"].Value), mb.Groups["doneUnit"].Value);
                if (info.Percent01 > 0.0001f)
                    info.BytesTotal = (long)(info.BytesDone / info.Percent01);
                info.Status = string.IsNullOrEmpty(info.FormatBytes())
                    ? $"{info.Percent01 * 100f:0.#}%"
                    : $"Transfer {info.FormatBytes()} ({info.Percent01 * 100f:0.#}%)";
                return true;
            }

            var uploaded = UploadedOfRegex.Match(clean);
            if (uploaded.Success)
            {
                var doneUnit = uploaded.Groups["doneUnit"].Success && uploaded.Groups["doneUnit"].Length > 0
                    ? uploaded.Groups["doneUnit"].Value
                    : uploaded.Groups["totalUnit"].Value;
                info.BytesDone = ToBytes(ParseFloat(uploaded.Groups["done"].Value), doneUnit);
                info.BytesTotal = ToBytes(ParseFloat(uploaded.Groups["total"].Value), uploaded.Groups["totalUnit"].Value);
                if (info.BytesTotal > 0)
                    info.Percent01 = Clamp01((float)info.BytesDone / info.BytesTotal);
                info.Status = $"Transfer {info.FormatBytes()}" +
                              (info.HasPercent ? $" ({info.Percent01 * 100f:0.#}%)" : string.Empty);
                return true;
            }

            var pct = PlainPercentRegex.Match(clean);
            if (pct.Success)
            {
                info.Percent01 = Clamp01(ParseFloat(pct.Groups["pct"].Value) / 100f);
                info.Status = $"{info.Percent01 * 100f:0.#}% — {clean}";
                return true;
            }

            // Still useful as a status line even without numbers
            info.Status = clean;
            return true;
        }

        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        static float ParseFloat(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        static long ParseLong(string s) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0L;

        static long ToBytes(float value, string unit)
        {
            var u = (unit ?? "B").Trim().ToUpperInvariant();
            double mult = 1d;
            switch (u)
            {
                case "KB":
                case "KIB":
                    mult = 1024d;
                    break;
                case "MB":
                case "MIB":
                    mult = 1024d * 1024d;
                    break;
                case "GB":
                case "GIB":
                    mult = 1024d * 1024d * 1024d;
                    break;
            }

            return (long)(value * mult);
        }
    }
}
