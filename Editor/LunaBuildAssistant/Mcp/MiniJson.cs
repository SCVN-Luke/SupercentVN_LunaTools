using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Supercent.Util.Editor
{
    // Small JSON reader/writer for the Dev Tools channel and the MCP server (no Unity API, no package).
    // Objects read as Dictionary<string, object>, arrays as List<object>, numbers as double.
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            var parser = new Parser(json);
            object value = parser.Value();
            parser.SkipSpace();
            if (!parser.AtEnd)
                throw new FormatException("Unexpected text after JSON at " + parser.Position);
            return value;
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        // Convenience reads on parsed objects.
        public static Dictionary<string, object> Obj(object value) => value as Dictionary<string, object>;

        public static string Str(Dictionary<string, object> obj, string key, string fallback = null) =>
            obj != null && obj.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback;

        public static double Num(Dictionary<string, object> obj, string key, double fallback) =>
            obj != null && obj.TryGetValue(key, out object v) && v is double d ? d : fallback;

        public static bool Bool(Dictionary<string, object> obj, string key, bool fallback) =>
            obj != null && obj.TryGetValue(key, out object v) && v is bool b ? b : fallback;

        static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (depth > 64)
                throw new InvalidOperationException("JSON nesting too deep");
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case RawJson raw:
                    sb.Append(raw.Json);
                    return;
                case IDictionary dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (!first)
                            sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        WriteValue(sb, entry.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem)
                            sb.Append(',');
                        firstItem = false;
                        WriteValue(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
            }
            if (value is float f)
                value = (double)f;
            if (value is double d)
            {
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is IFormattable number && (value is int || value is long || value is short || value is byte || value is uint || value is ulong || value is decimal))
            {
                sb.Append(number.ToString(null, CultureInfo.InvariantCulture));
                return;
            }
            if (value is Enum)
            {
                WriteString(sb, value.ToString());
                return;
            }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // Already serialized JSON, written as is.
        public sealed class RawJson
        {
            public readonly string Json;
            public RawJson(string json) { Json = json; }
        }

        sealed class Parser
        {
            readonly string s;
            int i;

            public Parser(string json) { s = json ?? string.Empty; }

            public int Position => i;
            public bool AtEnd => i >= s.Length;

            public void SkipSpace()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                    i++;
            }

            public object Value()
            {
                SkipSpace();
                if (i >= s.Length)
                    throw new FormatException("Unexpected end of JSON");
                char c = s[i];
                if (c == '{') return Object();
                if (c == '[') return Array();
                if (c == '"') return String();
                if (c == 't') return Literal("true", true);
                if (c == 'f') return Literal("false", false);
                if (c == 'n') return Literal("null", null);
                return Number();
            }

            object Literal(string word, object value)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                    throw new FormatException("Unexpected token at " + i);
                i += word.Length;
                return value;
            }

            Dictionary<string, object> Object()
            {
                var obj = new Dictionary<string, object>();
                i++;
                SkipSpace();
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    SkipSpace();
                    if (i >= s.Length || s[i] != '"')
                        throw new FormatException("Expected a property name at " + i);
                    string key = String();
                    SkipSpace();
                    Expect(':');
                    obj[key] = Value();
                    SkipSpace();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect('}');
                    return obj;
                }
            }

            List<object> Array()
            {
                var list = new List<object>();
                i++;
                SkipSpace();
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value());
                    SkipSpace();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(']');
                    return list;
                }
            }

            void Expect(char c)
            {
                if (i >= s.Length || s[i] != c)
                    throw new FormatException("Expected '" + c + "' at " + i);
                i++;
            }

            string String()
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"')
                        return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (i >= s.Length)
                        break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length)
                                throw new FormatException("Bad \\u escape at " + i);
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default:
                            throw new FormatException("Bad escape at " + i);
                    }
                }
                throw new FormatException("Unterminated string");
            }

            object Number()
            {
                int start = i;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+'))
                    i++;
                if (start == i)
                    throw new FormatException("Unexpected character '" + s[i] + "' at " + i);
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
