// 아주 작은 JSON 도구.
//
// 주고받는 메시지는 {"t":"move","x":12,"y":34} 처럼 한 겹짜리뿐이다.
// 이걸 위해 외부 라이브러리를 끌어오면 단일 파일 배포가 깨진다.
// 필요한 만큼만 직접 만든다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RemoteControl
{
    internal static class Json
    {
        /// <summary>한 겹짜리 객체를 읽는다. 값은 문자열·숫자·참거짓만 다룬다.</summary>
        internal static Dictionary<string, object> Parse(string s)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(s)) return d;

            int i = 0;
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '{') return d;
            i++;

            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') break;

                string key = ReadString(s, ref i);
                if (key == null) break;

                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') break;
                i++;

                SkipWs(s, ref i);
                object val = ReadValue(s, ref i);
                d[key] = val;

                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                break;
            }
            return d;
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
                char c = s[i++];
                if (c == '"') return sb.ToString();

                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                                 CultureInfo.InvariantCulture, out code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static object ReadValue(string s, ref int i)
        {
            if (i >= s.Length) return null;

            char c = s[i];
            if (c == '"') return ReadString(s, ref i);

            if (c == 't' && i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }

            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' ||
                                     s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;

            double dv;
            if (double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out dv)) return dv;
            return null;
        }

        // ---------------------------------------------------------------- 읽기 도우미

        internal static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? v as string : null;
        }

        internal static int Int(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            if (v is double) return (int)Math.Round((double)v);

            int r;
            return int.TryParse(v.ToString(), out r) ? r : def;
        }

        internal static double Num(Dictionary<string, object> d, string k, double def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            return v is double ? (double)v : def;
        }

        internal static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            return def;
        }

        // ---------------------------------------------------------------- 쓰기

        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
