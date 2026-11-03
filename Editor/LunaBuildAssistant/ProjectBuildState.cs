using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Project facts the Build Assistant needs: the git branch (concept number), which files
    // changed after the last private build (warn before uploading untested work) and the last app picked.
    public static class ProjectBuildState
    {
        static readonly string[] ScannedFolders = { "Assets", "ProjectSettings" };
        static readonly string[] ScannedFiles = { "Packages/manifest.json" };
        static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Assets/SVC_Luna.asset", "Assets/SVC_Luna.asset.meta",   // Rewritten by every Luna build.
        };

        static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        static string PrivateBuildKey => "SupercentVN.LunaBuildAssistant.LastPrivateBuild." + ProjectRoot;
        static string LastApplicationKey => "SupercentVN.LunaBuildAssistant.LastApplication." + ProjectRoot;

        public static DateTime? LastPrivateBuildUtc =>
            long.TryParse(EditorPrefs.GetString(PrivateBuildKey, string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                ? new DateTime(ticks, DateTimeKind.Utc) : (DateTime?)null;

        public static void MarkPrivateBuilt() =>
            EditorPrefs.SetString(PrivateBuildKey, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));

        // Last Playground application picked in the window (0 = none). Kept outside luna.json because the
        // plugin resets applicationID to 0 there whenever its applications refresh fails after a domain reload.
        public static int LastApplicationId
        {
            get => EditorPrefs.GetInt(LastApplicationKey, 0);
            set => EditorPrefs.SetInt(LastApplicationKey, value);
        }

        // Unsaved scenes plus project files written after the last private build, as project-relative paths.
        public static List<string> ChangesSincePrivateBuild()
        {
            var changes = new List<string>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (scene.isDirty)
                    changes.Add((string.IsNullOrEmpty(scene.path) ? scene.name : scene.path) + " (unsaved)");
            }

            DateTime? since = LastPrivateBuildUtc;
            if (since == null)
                return changes;

            IEnumerable<string> files = ScannedFolders
                .Select(folder => Path.Combine(ProjectRoot, folder))
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                .Concat(ScannedFiles.Select(file => Path.Combine(ProjectRoot, file)).Where(File.Exists));
            foreach (string file in files)
            {
                string relative = file.Substring(ProjectRoot.Length + 1).Replace('\\', '/');
                if (Path.GetFileName(relative).StartsWith(".", StringComparison.Ordinal) || Ignored.Contains(relative))
                    continue;
                if (File.GetLastWriteTimeUtc(file) > since.Value)
                    changes.Add(relative);
            }
            return changes;
        }

        // Current branch from .git/HEAD (also handles worktrees and submodules, where .git is a file); null if detached.
        public static string CurrentBranch()
        {
            for (var dir = new DirectoryInfo(ProjectRoot); dir != null; dir = dir.Parent)
            {
                string git = Path.Combine(dir.FullName, ".git");
                string gitDir = Directory.Exists(git) ? git : null;
                if (gitDir == null && File.Exists(git))
                {
                    Match link = Regex.Match(File.ReadAllText(git), @"gitdir:\s*(.+)");
                    if (link.Success)
                        gitDir = Path.GetFullPath(Path.Combine(dir.FullName, link.Groups[1].Value.Trim()));
                }
                if (gitDir == null)
                    continue;

                string head = Path.Combine(gitDir, "HEAD");
                Match branch = File.Exists(head) ? Regex.Match(File.ReadAllText(head), @"ref:\s*refs/heads/(.+)") : Match.Empty;
                return branch.Success ? branch.Groups[1].Value.Trim() : null;
            }
            return null;
        }
    }
}
