using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Supercent.Util.Editor
{
    // Input recordings of a Build Develop page (Dev Tools > Replay, luna_record / luna_replay), one JSON file each in
    // <project>/LunaRecordings/, outside Assets so Luna never ships them; commit them to share presets with the team.
    // The page records and replays (DevTools/LunaDevTools.js); this only stores files. No Unity API: the proxy calls it
    // from its own threads.
    public static class LunaRecordings
    {
        public const string Format = "luna-devtools-recording";
        public const string FolderName = "LunaRecordings";

        static string dir;

        // Set by the Editor (project folder); Unity's working directory is the project folder anyway.
        public static string Dir
        {
            get => dir ?? Path.Combine(Directory.GetCurrentDirectory(), FolderName);
            set => dir = value;
        }

        static readonly object gate = new object();

        // A file name from a recording name: path characters out, the rest kept ("full flow - win.json").
        public static string FileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A recording needs a name.");
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }));
            var sb = new StringBuilder();
            foreach (char c in name.Trim())
                sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            string safe = sb.ToString().Trim('.', ' ');
            if (safe.Length == 0)
                throw new ArgumentException("No usable character in the name \"" + name + "\".");
            if (safe.Length > 80)
                safe = safe.Substring(0, 80);
            return safe + ".json";
        }

        public static string PathOf(string name) => Path.Combine(Dir, FileName(name));

        // Summaries, newest first: name, file, length, expected outcome, checks.
        public static List<Dictionary<string, object>> List()
        {
            var list = new List<Dictionary<string, object>>();
            if (!Directory.Exists(Dir))
                return list;
            foreach (string file in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var data = MiniJson.Obj(MiniJson.Parse(File.ReadAllText(file)));
                    if (data == null || MiniJson.Str(data, "format") != Format)
                        continue;
                    list.Add(new Dictionary<string, object>
                    {
                        { "name", MiniJson.Str(data, "name", Path.GetFileNameWithoutExtension(file)) },
                        { "file", FolderName + "/" + Path.GetFileName(file) },
                        { "outcome", MiniJson.Str(data, "outcome", "") },
                        { "seconds", MiniJson.Num(data, "seconds", 0) },
                        { "ticks", MiniJson.Num(data, "ticks", 0) },
                        { "input", MiniJson.Str(data, "input", "") },
                        { "events", (data.TryGetValue("events", out object ev) && ev is List<object> evs) ? evs.Count : 0 },
                        { "checks", data.TryGetValue("checks", out object checks) ? checks : new List<object>() },
                        { "viewport", data.TryGetValue("viewport", out object vp) ? vp : null },
                        { "createdAt", MiniJson.Str(data, "createdAt", File.GetLastWriteTimeUtc(file).ToString("o")) },
                    });
                }
                catch (Exception)
                {
                    // Not a recording, or being written.
                }
            }
            return list.OrderByDescending(r => (string)r["createdAt"]).ToList();
        }

        public static string Read(string name)
        {
            string path = PathOf(name);
            if (!File.Exists(path))
            {
                // A name typed differently from its file: match on the stored name, case-insensitive.
                var match = List().FirstOrDefault(r => string.Equals((string)r["name"], name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    throw new FileNotFoundException("No recording \"" + name + "\" in " + FolderName + "/ (luna_recordings lists them).");
                path = Path.Combine(Dir, Path.GetFileName((string)match["file"]));
            }
            return File.ReadAllText(path);
        }

        // Writes the recording: one top-level field per line (outcome / checks stay easy to edit), lists on one line.
        public static string Save(Dictionary<string, object> recording)
        {
            if (recording == null || MiniJson.Str(recording, "format") != Format)
                throw new ArgumentException("Not a Dev Tools recording.");
            string name = MiniJson.Str(recording, "name");
            string path = PathOf(name);
            var sb = new StringBuilder("{\n");
            int i = 0;
            foreach (var entry in recording)
            {
                sb.Append("  ").Append(MiniJson.Write(entry.Key)).Append(": ").Append(MiniJson.Write(entry.Value));
                sb.Append(++i < recording.Count ? ",\n" : "\n");
            }
            sb.Append("}\n");
            lock (gate)
            {
                Directory.CreateDirectory(Dir);
                string temp = path + ".tmp";
                File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            return FolderName + "/" + Path.GetFileName(path);
        }

        public static bool Delete(string name)
        {
            string path = PathOf(name);
            lock (gate)
            {
                if (!File.Exists(path))
                    return false;
                File.Delete(path);
                return true;
            }
        }
    }
}
