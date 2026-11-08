#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Enlu
{
    /// <summary>
    /// Implement on your own types to control exactly how they are written by
    /// <see cref="EnluJson.Serialize(object, bool)"/>. Write a single JSON value with the writer.
    /// </summary>
    public interface IEnluJsonWritable
    {
        /// <summary>Returns the value to serialise instead of this object (a dictionary, list or primitive).</summary>
        object? ToJsonValue();
    }

    /// <summary>
    /// Wraps text that is already valid JSON so it is written as-is (not quoted).
    /// Useful for passing the output of <c>JsonUtility.ToJson</c> as a function argument.
    /// </summary>
    public sealed class EnluRawJson
    {
        /// <summary>The JSON text.</summary>
        public string Json { get; }

        /// <summary>Wraps <paramref name="json"/>. It is validated when serialised.</summary>
        public EnluRawJson(string json)
        {
            Json = json ?? throw new ArgumentNullException(nameof(json));
        }

        /// <inheritdoc />
        public override string ToString() => Json;
    }

    internal sealed class EnluJsonWriter
    {
        private const int MaxDepth = 128;
        private readonly StringBuilder _sb = new StringBuilder(256);
        private readonly bool _pretty;

        private EnluJsonWriter(bool pretty)
        {
            _pretty = pretty;
        }

        public static string Write(object? value, bool pretty)
        {
            var w = new EnluJsonWriter(pretty);
            w.WriteValue(value, 0);
            return w._sb.ToString();
        }

        private void NewLine(int depth)
        {
            if (!_pretty) return;
            _sb.Append('\n');
            _sb.Append(' ', depth * 2);
        }

        private void WriteValue(object? value, int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException("The value is nested too deeply to serialise (cycle?).");
            switch (value)
            {
                case null: _sb.Append("null"); return;
                case string s: WriteString(s); return;
                case bool b: _sb.Append(b ? "true" : "false"); return;
                case char ch: WriteString(ch.ToString()); return;
                case int i: _sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: _sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: _sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case byte by: _sb.Append(by.ToString(CultureInfo.InvariantCulture)); return;
                case sbyte sb: _sb.Append(sb.ToString(CultureInfo.InvariantCulture)); return;
                case ushort us: _sb.Append(us.ToString(CultureInfo.InvariantCulture)); return;
                case uint ui: _sb.Append(ui.ToString(CultureInfo.InvariantCulture)); return;
                case ulong ul: _sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return;
                case float f: WriteDouble(f, f.ToString("R", CultureInfo.InvariantCulture)); return;
                case double d: WriteDouble(d, d.ToString("R", CultureInfo.InvariantCulture)); return;
                case decimal m: _sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case DateTime dt: WriteString(dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)); return;
                case DateTimeOffset dto: WriteString(dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)); return;
                case Guid g: WriteString(g.ToString("D")); return;
                case Enum e: WriteString(e.ToString()); return;
                case EnluRawJson raw:
                    // Validate so a bad fragment can never corrupt the request body.
                    EnluJsonParser.Parse(raw.Json);
                    _sb.Append(raw.Json.Trim());
                    return;
                case IEnluJsonWritable custom: WriteValue(custom.ToJsonValue(), depth + 1); return;
                case IDictionary dict: WriteObject(dict, depth); return;
                case IEnumerable list: WriteArray(list, depth); return;
            }
            var fallback = EnluJson.ObjectSerializer;
            if (fallback != null)
            {
                var json = fallback(value);
                EnluJsonParser.Parse(json);
                _sb.Append(json);
                return;
            }
            throw new NotSupportedException("Cannot serialise a value of type " + value.GetType().FullName +
                ". Use a dictionary, a list, a primitive, IEnluJsonWritable or EnluRawJson.");
        }

        private void WriteDouble(double d, string text)
        {
            // JSON has no NaN or Infinity; JavaScript's JSON.stringify writes null for them too.
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                _sb.Append("null");
                return;
            }
            _sb.Append(text);
        }

        private void WriteObject(IDictionary dict, int depth)
        {
            _sb.Append('{');
            var first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) _sb.Append(',');
                first = false;
                NewLine(depth + 1);
                WriteString(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                _sb.Append(_pretty ? ": " : ":");
                WriteValue(entry.Value, depth + 1);
            }
            if (!first) NewLine(depth);
            _sb.Append('}');
        }

        private void WriteArray(IEnumerable list, int depth)
        {
            _sb.Append('[');
            var first = true;
            foreach (var item in list)
            {
                if (!first) _sb.Append(',');
                first = false;
                NewLine(depth + 1);
                WriteValue(item, depth + 1);
            }
            if (!first) NewLine(depth);
            _sb.Append(']');
        }

        private void WriteString(string s)
        {
            _sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                        {
                            _sb.Append("\\u");
                            _sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _sb.Append(c);
                        }
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
