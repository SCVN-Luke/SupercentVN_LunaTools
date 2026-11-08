using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // One build problem in plain text, with where to look.
    [Serializable]
    public sealed class BuildIssue
    {
        public string Message;
        public string Details;
        public string Code;
        public string File;         // "Assets/..." when the file is in the project, otherwise as reported.
        public int Line;
        public string Symbol;       // Class or Class.Member the plugin pointed at.
        public string ClassName;    // As the plugin reports it, possibly namespace-qualified.

        public string Location =>
            string.IsNullOrEmpty(File) ? Symbol : Line > 0 ? File + ":" + Line : File;

        bool InProject => !string.IsNullOrEmpty(File) && File.StartsWith("Assets/", StringComparison.Ordinal);

        public bool CanOpen => InProject || !string.IsNullOrEmpty(ClassName);

        public void Open()
        {
            string path = InProject ? File : ScriptOf(ClassName);
            UnityEngine.Object asset = path == null ? null : AssetDatabase.LoadMainAssetAtPath(path);
            if (asset != null)
                AssetDatabase.OpenAsset(asset, InProject && Line > 0 ? Line : -1);
        }

        // The project script named after the class ("Namespace.Outer+Inner`1" -> Outer).
        static string ScriptOf(string className)
        {
            if (string.IsNullOrEmpty(className))
                return null;
            string name = className.Split('+')[0];
            name = name.Substring(name.LastIndexOf('.') + 1).Split('`')[0];
            foreach (string guid in AssetDatabase.FindAssets(name + " t:MonoScript", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == name)
                    return path;
            }
            return null;
        }
    }

    // What one Build Develop / Build & upload left behind: errors first, warnings only for reference.
    [Serializable]
    public sealed class BuildReport
    {
        public string Action;                   // "Build Develop", "Build & upload"
        public bool Upload;
        public bool Failed;
        public bool NeedsSignIn;
        public string LogPath;                  // LunaTemp/luna.log, when the build wrote one.
        public List<BuildIssue> Errors = new List<BuildIssue>();
        public List<BuildIssue> Warnings = new List<BuildIssue>();

        public string Title => NeedsSignIn ? Action + " needs Playground sign-in" : Failed ? Action + " failed" : Action;

        public string Summary =>
            NeedsSignIn ? Title
            : Failed ? Title + (Errors.Count > 0 ? ": " + Plural(Errors.Count, "error") : string.Empty)
            : null;

        public bool IsEmpty => !Failed && Errors.Count == 0 && Warnings.Count == 0;

        public static string Plural(int count, string word) => count + " " + word + (count == 1 ? string.Empty : "s");
    }

    // Reads build problems into the Assistant instead of the plugin's Project Diagnostics tab:
    //   the plugin's DiagnosticsReport (Severity 1 = error, as its red badge; everything else is a warning),
    //   texts from the plugin's own localization table, and luna.log error lines when a failed build left no entry.
    public static class BuildDiagnostics
    {
        const string LocalizationFile = "LunaPlayable Localisation - LunaPlayable.csv";   // <package>/pipeline/resources
        const int MaxLogErrors = 20;
        const int MaxPlaceholders = 10;

        // "Assets/Foo.cs(12,5): error CS1061: ..." anywhere in a log line.
        static readonly Regex CompilerError = new Regex(
            @"(?<file>[^()\r\n\]]+?\.cs)\((?<line>\d+)(?:,\d+)?\)\s*:\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.+)$", RegexOptions.IgnoreCase);
        static readonly Regex ErrorWord = new Regex(@"\b(error|fatal|exception)\b", RegexOptions.IgnoreCase);
        static readonly Regex NoErrors = new Regex(@"\b0\s+errors?\b", RegexOptions.IgnoreCase);

        static Dictionary<string, string> texts;

        public static string LogPath => Path.Combine(ProjectRoot, "LunaTemp", "luna.log");
        static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        // Taken before a build, so a report left by an earlier build is never shown as this one's.
        public static string CurrentReportId()
        {
            try
            {
                return LunaPluginBridge.TryGetDiagnostics(out string id, out _) ? id : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static BuildReport NotSignedIn(string action, bool upload) => new BuildReport
        {
            Action = action,
            Upload = upload,
            Failed = true,
            NeedsSignIn = true,
            Errors =
            {
                new BuildIssue
                {
                    Message = "Not signed in to Playground.",
                    Details = "Sign in once in the Unity Playworks Plugin (its sign-in form only exists there). " +
                              "This message clears by itself as soon as you are signed in.",
                },
            },
        };

        // Errors and warnings of the build that just ran. failed: the plugin reported the build as failed;
        // a build is also failed when the report holds an error.
        public static BuildReport Collect(string action, bool upload, bool failed, string reportIdBefore)
        {
            var report = new BuildReport { Action = action, Upload = upload, Failed = failed };
            try
            {
                if (LunaPluginBridge.TryGetDiagnostics(out string id, out LunaPluginBridge.Diagnostic[] entries) && id != reportIdBefore)
                    foreach (LunaPluginBridge.Diagnostic d in entries.OrderBy(x => x.Stage))
                        (d.IsError ? report.Errors : report.Warnings).Add(ToIssue(d));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LunaBuildAssistant] Could not read the plugin's Project Diagnostics: " + e.Message);
            }

            if (failed && report.Errors.Count == 0)
                report.Errors.AddRange(LogErrors());
            report.Errors = Distinct(report.Errors);
            report.Warnings = Distinct(report.Warnings);
            report.Failed |= report.Errors.Count > 0;
            if (System.IO.File.Exists(LogPath))
                report.LogPath = LogPath;

            foreach (BuildIssue e in report.Errors)
                Debug.LogError($"[LunaBuildAssistant] {report.Action}: {e.Message}" + (e.Location == null ? string.Empty : " (" + e.Location + ")"));
            return report;
        }

        static List<BuildIssue> Distinct(List<BuildIssue> issues) =>
            issues.GroupBy(i => i.Message + "|" + i.Location).Select(g => g.First()).ToList();

        static BuildIssue ToIssue(LunaPluginBridge.Diagnostic d)
        {
            string message = Text(d.Description, d.DescriptionValues);
            string details = d.Details == null ? null : string.Join("\n", d.Details
                .Select(key => Text(key, d.DetailsValues))
                .Where(s => !string.IsNullOrEmpty(s) && s != message));
            if (string.IsNullOrEmpty(message))
            {
                message = string.IsNullOrEmpty(details) ? "Error " + d.Code : details.Split('\n')[0];
                details = null;
            }
            string symbol = string.IsNullOrEmpty(d.ClassName) ? null
                : string.IsNullOrEmpty(d.MemberName) ? d.ClassName : d.ClassName + "." + d.MemberName;
            return new BuildIssue
            {
                Message = message,
                Details = string.IsNullOrEmpty(details) ? null : details,
                Code = d.Code,
                File = ProjectPath(d.FilePath),
                Line = d.Line,
                Symbol = symbol,
                ClassName = d.ClassName,
            };
        }

        // A localization key of the plugin (or a literal text) with its {0}… values filled in.
        static string Text(string keyOrText, string[] values)
        {
            if (string.IsNullOrWhiteSpace(keyOrText))
                return null;
            string text = Texts.TryGetValue(keyOrText.Trim(), out string english) ? english : keyOrText;
            text = text.Replace("\\n", "\n");
            if (values != null && values.Length > 0 && text.Contains("{"))
            {
                object[] args = Enumerable.Range(0, Math.Max(values.Length, MaxPlaceholders))
                    .Select(i => (object)(i < values.Length ? values[i] : string.Empty)).ToArray();
                try
                {
                    text = string.Format(text, args);
                }
                catch (FormatException)
                {
                    // Braces that are not placeholders: keep the text as it is.
                }
            }
            return text.Trim();
        }

        static Dictionary<string, string> Texts => texts ??= LoadTexts();

        // Key -> English text, from the CSV the plugin ships next to its scripts.
        static Dictionary<string, string> LoadTexts()
        {
            var result = new Dictionary<string, string>();
            try
            {
                string scripts = LunaPluginBridge.PackagePath;
                string path = scripts == null ? null : Path.Combine(scripts, "..", "pipeline", "resources", LocalizationFile);
                if (path == null || !System.IO.File.Exists(path))
                    return result;
                foreach (List<string> row in ReadCsv(System.IO.File.ReadAllText(path)))
                    if (row.Count >= 2 && row[0].Length > 0 && !result.ContainsKey(row[0]))
                        result[row[0]] = row[1];
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LunaBuildAssistant] Could not read the plugin's texts: " + e.Message);
            }
            return result;
        }

        // RFC 4180: quoted fields may hold commas, line breaks and "" for a quote.
        static IEnumerable<List<string>> ReadCsv(string csv)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < csv.Length; i++)
            {
                char c = csv[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"')
                        quoted = false;
                    else
                        field.Append(c);
                }
                else if (c == '"')
                    quoted = true;
                else if (c == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                        i++;
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row;
                    row = new List<string>();
                }
                else
                    field.Append(c);
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                yield return row;
            }
        }

        // Error lines of the last CLI run, for failures the plugin's report did not capture.
        static List<BuildIssue> LogErrors()
        {
            var issues = new List<BuildIssue>();
            string[] lines;
            try
            {
                if (!System.IO.File.Exists(LogPath))
                    return issues;
                using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                    lines = reader.ReadToEnd().Split('\n');
            }
            catch (IOException)
            {
                return issues;
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || !ErrorWord.IsMatch(line) || NoErrors.IsMatch(line))
                    continue;
                Match m = CompilerError.Match(line);
                issues.Add(m.Success
                    ? new BuildIssue
                    {
                        Message = m.Groups["msg"].Value.Trim(),
                        Code = m.Groups["code"].Value,
                        File = ProjectPath(m.Groups["file"].Value.Trim()),
                        Line = int.Parse(m.Groups["line"].Value),
                    }
                    : new BuildIssue { Message = line });
            }
            return Distinct(issues).Take(MaxLogErrors).ToList();
        }

        // "D:/…/Project/Assets/X.cs" or "Assets\X.cs" -> "Assets/X.cs"; anything else unchanged.
        static string ProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            string p = path.Trim().Replace('\\', '/');
            string root = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                p = p.Substring(root.Length);
            return p;
        }
    }
}
