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

        // "VN/playable009" -> "009"; null when the branch carries no playable number.
        public static string NumberFromBranch(string branch)
        {
            Match m = branch == null ? Match.Empty : BranchPattern.Match(branch);
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
            return cleanSuffix.Length > 0 ? name + "_" + cleanSuffix : name;
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
