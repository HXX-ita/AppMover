using System;
using System.Collections.Generic;
using System.Text;

namespace AppMover.Core
{
    /// <summary>
    /// 极简 JSON 读写。
    ///
    /// 为什么不引第三方库：本项目刻意保持零依赖（零安装分发）。
    /// 这里的解析器只处理**我们自己写出的扁平对象**（一层，值为字符串/数字/布尔），
    /// 不做完整 JSON 解析，因此不存在通用解析器的复杂度与风险。
    /// </summary>
    public static class JsonLite
    {
        public static string Escape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static void AppendString(StringBuilder sb, string key, string value, bool first)
        {
            if (!first) sb.Append(",");
            sb.Append("\"").Append(Escape(key)).Append("\":\"").Append(Escape(value)).Append("\"");
        }

        public static void AppendNumber(StringBuilder sb, string key, long value, bool first)
        {
            if (!first) sb.Append(",");
            sb.Append("\"").Append(Escape(key)).Append("\":").Append(value.ToString());
        }

        public static void AppendBool(StringBuilder sb, string key, bool value, bool first)
        {
            if (!first) sb.Append(",");
            sb.Append("\"").Append(Escape(key)).Append("\":").Append(value ? "true" : "false");
        }

        /// <summary>解析一行扁平 JSON 对象为字典。无法解析时返回空字典（调用方按「记录损坏」处理）。</summary>
        public static Dictionary<string, string> ParseFlatObject(string line)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(line)) return map;

            int i = 0;
            int n = line.Length;
            SkipWs(line, ref i);
            if (i >= n || line[i] != '{') return map;
            i++;

            while (i < n)
            {
                SkipWs(line, ref i);
                if (i >= n) break;
                if (line[i] == '}') break;
                if (line[i] == ',') { i++; continue; }
                if (line[i] != '"') break;

                string key = ReadString(line, ref i);
                SkipWs(line, ref i);
                if (i >= n || line[i] != ':') break;
                i++;
                SkipWs(line, ref i);
                if (i >= n) break;

                string val;
                if (line[i] == '"') val = ReadString(line, ref i);
                else
                {
                    int start = i;
                    while (i < n && line[i] != ',' && line[i] != '}') i++;
                    val = line.Substring(start, i - start).Trim();
                }
                if (key != null) map[key] = val;
            }
            return map;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char e = s[i + 1];
                    i += 2;
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i, 4), System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else if (c == '"') { i++; return sb.ToString(); }
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }
    }
}
