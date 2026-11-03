using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Re-runs Luna's texture export for any settings with the tools bundled in the Luna package, so a setting
    // can be previewed (image + size) before it is applied. Same steps as the CLI's texture processor:
    //   1. ImageMagick: shrink to max size ("WxH>") and write the format (png8/png24/png32: prefix, jpeg: -quality)
    //   2. compression: webp -> cwebp -q; quantQuality -> pngquant --strip --quality 1-75;
    //      quantSize -> pngquant --quality=1-65 --speed=1 --strip; crush -> pngcrush
    // Checked byte-for-byte against LunaTemp/stage2 (app_Icon, png32 + quantSize). Encode is thread-safe.
    public static class LunaTextureEncoder
    {
        const string OutputFolder = "Library/SupercentVN/TextureEncodes";

        public struct Settings
        {
            public int MaxWidth, MaxHeight, Quality;
            public string Format, Compression;  // luna.json values: png8/png24/png32/jpeg/webp/default, quantQuality/quantSize/crush/none.

            public bool IsPng => Format == "png8" || Format == "png24" || Format == "png32";
            public bool UsesQuality => Format == "jpeg" || Format == "webp";
            public string Key => $"{Format}-{(IsPng ? Compression : "q" + Quality)}-{MaxWidth}x{MaxHeight}";
        }

        public sealed class Encoded
        {
            public Settings Settings;
            public string Label;
            public string File;                 // What Luna would ship.
            public string PreviewFile;          // A PNG of it (WEBP decoded) to show.
            public long Bytes;
            public int Width, Height;
            public string Error;
        }

        public static bool Available => Tool("magick") != null;

        // Luna's own export of the texture before encoding (LunaTemp/stage1), so the input matches the build.
        public static string SourceFor(BuildSizeReport.Entry entry)
        {
            if (string.IsNullOrEmpty(entry.lunaPath))
                return null;
            string id = Path.GetFileNameWithoutExtension(entry.lunaPath);
            string stage1 = entry.lunaPath.Replace("/stage2/", "/stage1/");
            string png = Path.Combine(Path.GetDirectoryName(stage1) ?? string.Empty, id + ".png");
            return File.Exists(png) ? png : null;
        }

        public static string FolderFor(string assetPath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, OutputFolder, Path.GetFileNameWithoutExtension(assetPath));

        // Settings worth comparing with the current one: PNG compressions, WEBP and (opaque images only) JPEG.
        public static List<Settings> Alternatives(Settings current, bool opaque)
        {
            var list = new List<Settings> { current };
            void Add(string format, string compression, int quality) =>
                list.Add(new Settings { MaxWidth = current.MaxWidth, MaxHeight = current.MaxHeight, Format = format, Compression = compression, Quality = quality });
            Add("png32", "quantSize", current.Quality);
            Add("png32", "quantQuality", current.Quality);
            Add("png32", "none", current.Quality);
            Add("webp", current.Compression, 65);
            Add("webp", current.Compression, 80);
            if (opaque)
            {
                Add("jpeg", current.Compression, 65);
                Add("jpeg", current.Compression, 80);
            }
            return list.GroupBy(s => s.Key).Select(g => g.First()).ToList();
        }

        public static bool IsOpaque(string source)
        {
            string magick = Tool("magick");
            return magick != null && Run(magick, $"identify -format \"%[opaque]\" \"{source}\"", out string output) == 0
                && output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static Encoded Encode(string source, Settings s, string folder, string name)
        {
            var result = new Encoded { Settings = s };
            try
            {
                string magick = Tool("magick") ?? throw new FileNotFoundException("ImageMagick not found in the Luna package");
                Directory.CreateDirectory(folder);
                string resize = s.MaxWidth > 0 && s.MaxHeight > 0 ? $"-resize \"{s.MaxWidth}x{s.MaxHeight}>\"" : string.Empty;
                string step1, target;
                switch (s.Format)
                {
                    case "jpeg":
                        step1 = Path.Combine(folder, name + ".jpg");
                        target = $"-quality {s.Quality} jpg:\"{step1}\"";
                        break;
                    case "png8":
                    case "png24":
                        step1 = Path.Combine(folder, name + ".step1.png");
                        target = $"{s.Format}:\"{step1}\"";
                        break;
                    default:                    // png32, webp input, default
                        step1 = Path.Combine(folder, name + ".step1.png");
                        target = $"png32:\"{step1}\"";
                        break;
                }
                Check(Run(magick, $"\"{source}\" {resize} {target}", out string error), error, "ImageMagick");

                string output = step1;
                if (s.Format == "webp")
                {
                    output = Path.Combine(folder, name + ".webp");
                    Check(Run(Need("cwebp"), $"\"{step1}\" -q {s.Quality} -o \"{output}\" -quiet", out error), error, "cwebp");
                }
                else if (s.IsPng && (s.Compression == "quantQuality" || s.Compression == "quantSize"))
                {
                    output = Path.Combine(folder, name + ".png");
                    string args = s.Compression == "quantQuality"
                        ? $"--force --strip --quality 1-75 \"{step1}\" --output \"{output}\""
                        : $"--force --quality=1-65 --speed=1 --strip \"{step1}\" --output \"{output}\"";
                    Check(Run(Need("pngquant"), args, out error), error, "pngquant");
                }
                else if (s.IsPng && s.Compression == "crush")
                {
                    output = Path.Combine(folder, name + ".png");
                    if (File.Exists(output))
                        File.Delete(output);
                    Check(Run(Need("pngcrush"), $"-q \"{step1}\" \"{output}\"", out error), error, "pngcrush");
                }

                result.File = output;
                result.Bytes = new FileInfo(output).Length;
                result.PreviewFile = output;
                if (s.Format == "webp")
                {
                    result.PreviewFile = Path.Combine(folder, name + ".preview.png");
                    Check(Run(magick, $"\"{output}\" png32:\"{result.PreviewFile}\"", out error), error, "ImageMagick");
                }
                if (Run(magick, $"identify -format \"%w %h\" \"{output}\"", out string size) == 0)
                {
                    string[] wh = size.Trim().Split(' ');
                    int.TryParse(wh[0], out result.Width);
                    if (wh.Length > 1)
                        int.TryParse(wh[1], out result.Height);
                }
            }
            catch (Exception e)
            {
                result.Error = e.Message;
            }
            return result;
        }

        static void Check(int exitCode, string error, string tool)
        {
            if (exitCode != 0)
                throw new InvalidOperationException($"{tool} failed ({exitCode}): {error.Trim()}");
        }

        static string Need(string tool) => Tool(tool) ?? throw new FileNotFoundException(tool + " not found in the Luna package");

        static int Run(string exe, string args, out string output)
        {
            var info = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process p = Process.Start(info))
            {
                System.Threading.Tasks.Task<string> stderr = p.StandardError.ReadToEndAsync();
                output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(60000);
                if (p.ExitCode != 0)
                    output = stderr.Result;
                return p.ExitCode;
            }
        }

        static readonly Dictionary<string, string> tools = new Dictionary<string, string>();

        // <Luna package>/../tools/<folder>/<mac64|win64>/...; resolved on the main thread first (PackagePath).
        static string Tool(string tool)
        {
            lock (tools)
            {
                if (tools.TryGetValue(tool, out string cached))
                    return cached;
                string package = LunaPluginBridge.PackagePath;
                string root = package == null ? null : Path.Combine(Path.GetDirectoryName(package) ?? package, "tools");
                bool windows = Application.platform == RuntimePlatform.WindowsEditor;
                string platform = windows ? "win64" : "mac64";
                string exe = null;
                if (root != null)
                {
                    if (tool == "magick")
                        exe = Directory.Exists(Path.Combine(root, "imagemagick", platform))
                            ? Directory.EnumerateFiles(Path.Combine(root, "imagemagick", platform), windows ? "magick.exe" : "magick", SearchOption.AllDirectories).FirstOrDefault()
                            : null;
                    else
                        exe = Path.Combine(root, tool == "cwebp" ? "libwebp" : tool, platform, tool + (windows ? ".exe" : string.Empty));
                }
                exe = exe != null && File.Exists(exe) ? exe : null;
                tools[tool] = exe;
                return exe;
            }
        }

        // Warms the tool paths on the main thread: Encode may run on a worker thread.
        public static void Prepare()
        {
            foreach (string tool in new[] { "magick", "pngquant", "cwebp", "pngcrush" })
                Tool(tool);
        }
    }
}
