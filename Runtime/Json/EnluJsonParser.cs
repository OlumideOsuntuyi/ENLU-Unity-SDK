#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Enlu
{
    /// <summary>Thrown when text is not valid JSON.</summary>
    public sealed class EnluJsonException : FormatException
    {
        /// <summary>Character offset in the input where the problem was found.</summary>
        public int Position { get; }

        /// <summary>Creates the exception.</summary>
        public EnluJsonException(string message, int position)
            : base(message + " (at character " + position.ToString(CultureInfo.InvariantCulture) + ")")
        {
            Position = position;
        }
    }

    /// <summary>
    /// A strict, allocation-light recursive-descent JSON parser (RFC 8259).
    /// Objects become <see cref="Dictionary{TKey,TValue}"/> of string to object, arrays become
    /// <see cref="List{T}"/> of object, integers that fit become <see cref="long"/>, other numbers
    /// <see cref="double"/>, plus <see cref="string"/>, <see cref="bool"/> and null.
    /// </summary>
    internal sealed class EnluJsonParser
    {
        private const int MaxDepth = 256;
        private readonly string _s;
        private int _i;
        private readonly StringBuilder _sb = new StringBuilder();

        private EnluJsonParser(string s)
        {
            _s = s;
        }

        public static object? Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var p = new EnluJsonParser(json);
            p.SkipWhitespace();
            if (p._i >= p._s.Length) throw new EnluJsonException("The input is empty", 0);
            var value = p.ReadValue(0);
            p.SkipWhitespace();
            if (p._i != p._s.Length) throw new EnluJsonException("Unexpected text after the JSON value", p._i);
            return value;
        }

        private void SkipWhitespace()
        {
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                else break;
            }
        }

        private object? ReadValue(int depth)
        {
            if (depth > MaxDepth) throw new EnluJsonException("The JSON is nested too deeply", _i);
            if (_i >= _s.Length) throw new EnluJsonException("Unexpected end of input", _i);
            var c = _s[_i];
            switch (c)
            {
                case '{': return ReadObject(depth);
                case '[': return ReadArray(depth);
                case '"': return ReadString();
                case 't': ReadLiteral("true"); return true;
                case 'f': ReadLiteral("false"); return false;
                case 'n': ReadLiteral("null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                    throw new EnluJsonException("Unexpected character '" + c + "'", _i);
            }
        }

        private void ReadLiteral(string word)
        {
            if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                throw new EnluJsonException("Expected '" + word + "'", _i);
            _i += word.Length;
        }

        private Dictionary<string, object?> ReadObject(int depth)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            _i++; // {
            SkipWhitespace();
            if (_i < _s.Length && _s[_i] == '}')
            {
                _i++;
                return result;
            }
            while (true)
            {
                SkipWhitespace();
                if (_i >= _s.Length || _s[_i] != '"') throw new EnluJsonException("Expected a property name", _i);
                var key = ReadString();
                SkipWhitespace();
                if (_i >= _s.Length || _s[_i] != ':') throw new EnluJsonException("Expected ':'", _i);
                _i++;
                SkipWhitespace();
                // Duplicate keys: the last one wins, like JavaScript's JSON.parse.
                result[key] = ReadValue(depth + 1);
                SkipWhitespace();
                if (_i >= _s.Length) throw new EnluJsonException("Unterminated object", _i);
                var c = _s[_i++];
                if (c == '}') return result;
                if (c != ',') throw new EnluJsonException("Expected ',' or '}'", _i - 1);
            }
        }

        private List<object?> ReadArray(int depth)
        {
            var result = new List<object?>();
            _i++; // [
            SkipWhitespace();
            if (_i < _s.Length && _s[_i] == ']')
            {
                _i++;
                return result;
            }
            while (true)
            {
                SkipWhitespace();
                result.Add(ReadValue(depth + 1));
                SkipWhitespace();
                if (_i >= _s.Length) throw new EnluJsonException("Unterminated array", _i);
                var c = _s[_i++];
                if (c == ']') return result;
                if (c != ',') throw new EnluJsonException("Expected ',' or ']'", _i - 1);
            }
        }

        private string ReadString()
        {
            var start = ++_i; // skip opening quote
            // Fast path: no escapes.
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (c == '"')
                {
                    var plain = _s.Substring(start, _i - start);
                    _i++;
                    return plain;
                }
                if (c == '\\') break;
                if (c < 0x20) throw new EnluJsonException("Control character in string", _i);
                _i++;
            }
            _sb.Clear();
            _sb.Append(_s, start, _i - start);
            while (_i < _s.Length)
            {
                var c = _s[_i++];
                if (c == '"') return _sb.ToString();
                if (c < 0x20) throw new EnluJsonException("Control character in string", _i - 1);
                if (c != '\\')
                {
                    _sb.Append(c);
                    continue;
                }
                if (_i >= _s.Length) break;
                var e = _s[_i++];
                switch (e)
                {
                    case '"': _sb.Append('"'); break;
                    case '\\': _sb.Append('\\'); break;
                    case '/': _sb.Append('/'); break;
                    case 'b': _sb.Append('\b'); break;
                    case 'f': _sb.Append('\f'); break;
                    case 'n': _sb.Append('\n'); break;
                    case 'r': _sb.Append('\r'); break;
                    case 't': _sb.Append('\t'); break;
                    case 'u': _sb.Append(ReadHex4()); break;
                    default: throw new EnluJsonException("Invalid escape '\\" + e + "'", _i - 2);
                }
            }
            throw new EnluJsonException("Unterminated string", _i);
        }

        private char ReadHex4()
        {
            if (_i + 4 > _s.Length) throw new EnluJsonException("Truncated \\u escape", _i);
            var v = 0;
            for (var k = 0; k < 4; k++)
            {
                var c = _s[_i++];
                int d;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
                else throw new EnluJsonException("Invalid hex digit in \\u escape", _i - 1);
                v = (v << 4) | d;
            }
            // Surrogate pairs arrive as two consecutive escapes and are appended one char at a time.
            return (char)v;
        }

        private object ReadNumber()
        {
            var start = _i;
            var isInteger = true;
            if (_s[_i] == '-') _i++;
            if (_i >= _s.Length) throw new EnluJsonException("Invalid number", start);
            if (_s[_i] == '0')
            {
                _i++;
            }
            else if (_s[_i] >= '1' && _s[_i] <= '9')
            {
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            }
            else
            {
                throw new EnluJsonException("Invalid number", start);
            }
            if (_i < _s.Length && _s[_i] == '.')
            {
                isInteger = false;
                _i++;
                var digits = _i;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                if (_i == digits) throw new EnluJsonException("Expected digits after '.'", _i);
            }
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                isInteger = false;
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                var digits = _i;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                if (_i == digits) throw new EnluJsonException("Expected digits in exponent", _i);
            }
            var text = _s.Substring(start, _i - start);
            if (isInteger && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            throw new EnluJsonException("Invalid number", start);
        }
    }
}
