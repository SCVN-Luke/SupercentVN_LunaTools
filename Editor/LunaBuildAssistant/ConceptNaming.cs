using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supercent.Util.Editor
{
    // Concept names follow playable<number>_<game>_<yymmdd>[_<suffix>], e.g. playable009_bulldozermaster_261027.
    // New names copy the casing of the app's latest concept so the Creative Library stays uniform.
    public static class ConceptNaming
    {
        const string DefaultPrefix = "Playable";     // Used only when the app has no concept yet.
        const int NumberWidth = 3;

        static readonly Regex NamePattern = new Regex(@"^(?<prefix>playable)(?<number>\d+)_(?<game>[^_]+)_(?<date>\d{6})(?:_(?<suffix>.+))?$", RegexOptions.IgnoreCase);
        static readonly Regex BranchPattern = new Regex(@"playable[_-]?(?<number>\d+)", RegexOptions.IgnoreCase);
        // Fallback: the last number token of the branch's last segment, e.g. "VN/luna_dragonburst_002". At most 4 digits so
        // a date (yymmdd) is never taken for a playable number.
        static readonly Regex BranchNumberToken = new Regex(@"(?:^|[_.-])(?<number>\d{1,4})(?=$|[_.-])", RegexOptions.RightToLeft);
        static readonly Regex NonWord = new Regex(@"[^\w\d]+");    // Same set the Playworks upload tab replaces.
        static readonly Regex NonAlphanumeric = new Regex(@"[^\p{L}\p{Nd}]+");   // "_" would split the game token.

        public struct Parts
        {
            public string Prefix, Number, Game, Date, Suffix;
            public int NumberValue => int.Parse(Number, CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string name, out Parts parts)
        {
            parts = default;
            Match m = name == null ? Match.Empty : NamePattern.Match(name.Trim());
            if (!m.Success)
                return false;
            parts = new Parts
            {
                Prefix = m.Groups["prefix"].Value,
                Number = m.Groups["number"].Value,
                Game = m.Groups["game"].Value,
                Date = m.Groups["date"].Value,
                Suffix = m.Groups["suffix"].Success ? m.Groups["suffix"].Value : null,
            };
            return true;
        }

        // "VN/playable009" -> "009", "VN/luna_dragonburst_002" -> "002"; null when the branch carries no playable number.
        public static string NumberFromBranch(string branch)
        {
            if (string.IsNullOrWhiteSpace(branch))
                return null;
            Match m = BranchPattern.Match(branch);
            if (m.Success)
                return m.Groups["number"].Value;
            string last = branch.Substring(branch.LastIndexOf('/') + 1);
            m = BranchNumberToken.Match(last);
            return m.Success ? m.Groups["number"].Value : null;
        }

        public static string Suggest(IEnumerable<string> conceptNames, string appName, string branch, DateTime today, string suffix)
        {
            Parts[] parsed = Parse(conceptNames);
            Parts latest = parsed.OrderByDescending(p => p.NumberValue).ThenByDescending(p => p.Date, StringComparer.Ordinal).FirstOrDefault();
            bool hasLatest = parsed.Length > 0;

            string prefix = hasLatest ? latest.Prefix : DefaultPrefix;
            string game = hasLatest ? latest.Game : NonAlphanumeric.Replace(appName ?? string.Empty, string.Empty);
            string number = NumberFromBranch(branch)
                         ?? ((hasLatest ? latest.NumberValue : 0) + 1).ToString(CultureInfo.InvariantCulture);
            if (number.Length < NumberWidth)
                number = number.PadLeft(NumberWidth, '0');

            string name = $"{prefix}{number}_{game}_{today.ToString("yyMMdd", CultureInfo.InvariantCulture)}";
            string cleanSuffix = Sanitize(suffix).Trim('-', '_');
            if (cleanSuffix.Length > 0)
                name += "_" + cleanSuffix;
            // Never suggest a name the app already has (e.g. the branch's concept was created today): add _2, _3, ...
            var taken = new HashSet<string>(conceptNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string unique = name;
            for (int n = 2; taken.Contains(unique); n++)
                unique = name + "_" + n.ToString(CultureInfo.InvariantCulture);
            return unique;
        }

        // Latest concept carrying the branch's playable number, or null to create a new one.
        public static string DefaultConcept(IEnumerable<string> conceptNames, string branch)
        {
            string number = NumberFromBranch(branch);
            if (number == null)
                return null;
            int value = int.Parse(number, CultureInfo.InvariantCulture);
            return conceptNames
                .Select(n => TryParse(n, out Parts p) ? new { Name = n, Parts = p } : null)
                .Where(x => x != null && x.Parts.NumberValue == value)
                .OrderByDescending(x => x.Parts.Date, StringComparer.Ordinal)
                .Select(x => x.Name)
                .FirstOrDefault();
        }

        // What the Playworks upload tab would store for this name.
        public static string Sanitize(string name) => NonWord.Replace((name ?? string.Empty).Trim(), "-");

        static Parts[] Parse(IEnumerable<string> names) =>
            (names ?? Enumerable.Empty<string>())
                .Select(n => TryParse(n, out Parts p) ? (Parts?)p : null)
                .Where(p => p.HasValue).Select(p => p.Value).ToArray();
    }
}
