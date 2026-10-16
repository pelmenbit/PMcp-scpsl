using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PMcp.Util;

public sealed class JsonObject : Dictionary<string, object?>
{
    public JsonObject()
        : base(StringComparer.Ordinal) { }
}

public static class Json
{
    private const int MaxDepth = 64;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Write(object? value)
    {
        var sb = new StringBuilder(256);
        WriteValue(sb, value, 0);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? value, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidOperationException("JSON: слишком глубокая вложенность");

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
            case Enum e:
                WriteString(sb, e.ToString());
                return;
            case double d:
                WriteDouble(sb, d);
                return;
            case float f:
                WriteDouble(sb, double.Parse(f.ToString("R", Inv), Inv));
                return;
            case decimal m:
                sb.Append(m.ToString(Inv));
                return;
            case int or long or short or byte or sbyte or uint or ushort or ulong:
                sb.Append(Convert.ToString(value, Inv));
                return;
            case IDictionary dict:
                WriteObject(sb, dict, depth);
                return;
            case IEnumerable list:
                WriteArray(sb, list, depth);
                return;
            default:
                WriteString(sb, value.ToString());
                return;
        }
    }

    private static void WriteDouble(StringBuilder sb, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            sb.Append("null");
            return;
        }

        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
            sb.Append(((long)d).ToString(Inv));
        else
            sb.Append(d.ToString("R", Inv));
    }

    private static void WriteObject(StringBuilder sb, IDictionary dict, int depth)
    {
        sb.Append('{');
        bool first = true;
        foreach (DictionaryEntry entry in dict)
        {
            if (!first)
                sb.Append(',');
            first = false;
            WriteString(sb, Convert.ToString(entry.Key, Inv) ?? "");
            sb.Append(':');
            WriteValue(sb, entry.Value, depth + 1);
        }
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, IEnumerable list, int depth)
    {
        sb.Append('[');
        bool first = true;
        foreach (object? item in list)
        {
            if (!first)
                sb.Append(',');
            first = false;
            WriteValue(sb, item, depth + 1);
        }
        sb.Append(']');
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                        sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    public static object? Parse(string text)
    {
        var reader = new Reader(text ?? "");
        reader.SkipWhitespace();
        object? value = reader.ReadValue(0);
        reader.SkipWhitespace();
        if (!reader.AtEnd)
            throw reader.Error("лишние символы после JSON");
        return value;
    }

    private sealed class Reader
    {
        private readonly string _s;
        private int _pos;

        public Reader(string s) => _s = s;

        public bool AtEnd => _pos >= _s.Length;

        public FormatException Error(string what) => new FormatException($"{what} (позиция {_pos})");

        public void SkipWhitespace()
        {
            while (_pos < _s.Length && (_s[_pos] == ' ' || _s[_pos] == '\t' || _s[_pos] == '\n' || _s[_pos] == '\r'))
                _pos++;
        }

        public object? ReadValue(int depth)
        {
            if (depth > MaxDepth)
                throw Error("слишком глубокая вложенность");
            if (AtEnd)
                throw Error("неожиданный конец");

            char c = _s[_pos];
            switch (c)
            {
                case '{':
                    return ReadObject(depth);
                case '[':
                    return ReadArray(depth);
                case '"':
                    return ReadString();
                case 't':
                    ReadLiteral("true");
                    return true;
                case 'f':
                    ReadLiteral("false");
                    return false;
                case 'n':
                    ReadLiteral("null");
                    return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ReadNumber();
                    throw Error($"неожиданный символ '{c}'");
            }
        }

        private void ReadLiteral(string literal)
        {
            if (string.CompareOrdinal(_s, _pos, literal, 0, literal.Length) != 0)
                throw Error($"ожидалось {literal}");
            _pos += literal.Length;
        }

        private Dictionary<string, object?> ReadObject(int depth)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            _pos++;
            SkipWhitespace();
            if (!AtEnd && _s[_pos] == '}')
            {
                _pos++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd || _s[_pos] != '"')
                    throw Error("ожидался ключ-строка");
                string key = ReadString();
                SkipWhitespace();
                if (AtEnd || _s[_pos] != ':')
                    throw Error("ожидалось ':'");
                _pos++;
                SkipWhitespace();
                result[key] = ReadValue(depth + 1);
                SkipWhitespace();
                if (AtEnd)
                    throw Error("незакрытый объект");
                if (_s[_pos] == ',')
                {
                    _pos++;
                    continue;
                }
                if (_s[_pos] == '}')
                {
                    _pos++;
                    return result;
                }
                throw Error("ожидалось ',' или '}'");
            }
        }

        private List<object?> ReadArray(int depth)
        {
            var result = new List<object?>();
            _pos++;
            SkipWhitespace();
            if (!AtEnd && _s[_pos] == ']')
            {
                _pos++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                result.Add(ReadValue(depth + 1));
                SkipWhitespace();
                if (AtEnd)
                    throw Error("незакрытый массив");
                if (_s[_pos] == ',')
                {
                    _pos++;
                    continue;
                }
                if (_s[_pos] == ']')
                {
                    _pos++;
                    return result;
                }
                throw Error("ожидалось ',' или ']'");
            }
        }

        private string ReadString()
        {
            _pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Error("незакрытая строка");
                char c = _s[_pos++];
                if (c == '"')
                    return sb.ToString();
                if (c != '\\')
                {
                    if (c < 0x20)
                        throw Error("управляющий символ внутри строки");
                    sb.Append(c);
                    continue;
                }

                if (AtEnd)
                    throw Error("обрыв escape-последовательности");
                char e = _s[_pos++];
                switch (e)
                {
                    case '"':
                        sb.Append('"');
                        break;
                    case '\\':
                        sb.Append('\\');
                        break;
                    case '/':
                        sb.Append('/');
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'u':
                        if (_pos + 4 > _s.Length)
                            throw Error("обрыв \\u");
                        if (!int.TryParse(_s.Substring(_pos, 4), NumberStyles.HexNumber, Inv, out int code))
                            throw Error("кривой \\u");
                        sb.Append((char)code);
                        _pos += 4;
                        break;
                    default:
                        throw Error($"неизвестный escape '\\{e}'");
                }
            }
        }

        private double ReadNumber()
        {
            int start = _pos;
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    _pos++;
                else
                    break;
            }

            string token = _s.Substring(start, _pos - start);
            if (!double.TryParse(token, NumberStyles.Float, Inv, out double value))
                throw Error($"кривое число '{token}'");
            return value;
        }
    }
}
