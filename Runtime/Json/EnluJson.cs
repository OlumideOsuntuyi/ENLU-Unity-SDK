#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace Enlu
{
    /// <summary>
    /// Implement on your own class (with a public parameterless constructor) to read it from a
    /// parsed JSON value in <see cref="EnluJson.ConvertTo{T}(object)"/>, for example as the result
    /// type of <c>ExecuteFunctionAsync&lt;T&gt;</c>.
    /// </summary>
    public interface IEnluJsonReadable
    {
        /// <summary>Fills this object from a parsed JSON value (usually a dictionary).</summary>
        void ReadJson(object? node);
    }

    /// <summary>
    /// Self-contained JSON support with no package dependencies.
    /// <para>Parsed values use plain .NET types: <c>Dictionary&lt;string, object&gt;</c> for objects,
    /// <c>List&lt;object&gt;</c> for arrays, <c>long</c> for integers, <c>double</c> for other numbers,
    /// <c>string</c>, <c>bool</c> and <c>null</c>.</para>
    /// <para>For your own <c>[Serializable]</c> classes the SDK falls back to Unity's
    /// <c>JsonUtility</c> (see <see cref="ObjectSerializer"/> and <see cref="ObjectDeserializer"/>).</para>
    /// </summary>
    public static class EnluJson
    {
        /// <summary>
        /// Serialises objects the built-in writer does not know (your own classes).
        /// In Unity this defaults to <c>JsonUtility.ToJson</c>. Set it to plug in another library.
        /// </summary>
        public static Func<object, string>? ObjectSerializer { get; set; }

        /// <summary>
        /// Deserialises JSON text into a type the built-in converter does not know (your own classes).
        /// In Unity this defaults to <c>JsonUtility.FromJson</c>. Set it to plug in another library.
        /// </summary>
        public static Func<string, Type, object?>? ObjectDeserializer { get; set; }

        static EnluJson()
        {
#if UNITY_5_3_OR_NEWER
            ObjectSerializer = o => UnityEngine.JsonUtility.ToJson(o);
            ObjectDeserializer = (json, type) => UnityEngine.JsonUtility.FromJson(json, type);
#endif
        }

        /// <summary>Parses JSON text. Throws <see cref="EnluJsonException"/> when it is not valid JSON.</summary>
        public static object? Parse(string json) => EnluJsonParser.Parse(json);

        /// <summary>Parses JSON text and returns false instead of throwing when it is invalid.</summary>
        public static bool TryParse(string json, out object? value)
        {
            try
            {
                value = EnluJsonParser.Parse(json);
                return true;
            }
            catch (EnluJsonException)
            {
                value = null;
                return false;
            }
        }

        /// <summary>Parses JSON text that must be an object.</summary>
        public static Dictionary<string, object?> ParseObject(string json)
        {
            if (EnluJsonParser.Parse(json) is Dictionary<string, object?> o) return o;
            throw new EnluJsonException("Expected a JSON object", 0);
        }

        /// <summary>
        /// Serialises dictionaries, lists, arrays, primitives, enums (as names), dates (ISO 8601 UTC),
        /// <see cref="IEnluJsonWritable"/>, <see cref="EnluRawJson"/> and, through
        /// <see cref="ObjectSerializer"/>, your own <c>[Serializable]</c> classes.
        /// </summary>
        public static string Serialize(object? value, bool pretty = false) => EnluJsonWriter.Write(value, pretty);

        /// <summary>Returns the value as a JSON object, or null if it is not one.</summary>
        public static Dictionary<string, object?>? AsObject(object? node) => node as Dictionary<string, object?>;

        /// <summary>Returns the value as a JSON array, or null if it is not one.</summary>
        public static List<object?>? AsArray(object? node) => node as List<object?>;

        /// <summary>Reads a property of an object, or null when missing.</summary>
        public static object? Get(Dictionary<string, object?>? obj, string key)
        {
            if (obj == null) return null;
            return obj.TryGetValue(key, out var v) ? v : null;
        }

        /// <summary>Reads a string property, or <paramref name="fallback"/> when missing or not a string.</summary>
        public static string? GetString(Dictionary<string, object?>? obj, string key, string? fallback = null) =>
            Get(obj, key) is string s ? s : fallback;

        /// <summary>Reads a boolean property, or <paramref name="fallback"/>.</summary>
        public static bool GetBool(Dictionary<string, object?>? obj, string key, bool fallback = false) =>
            Get(obj, key) is bool b ? b : fallback;

        /// <summary>Reads a numeric property as double, or <paramref name="fallback"/>.</summary>
        public static double GetDouble(Dictionary<string, object?>? obj, string key, double fallback = 0) =>
            ToDouble(Get(obj, key), fallback);

        /// <summary>Reads a numeric property as long, or <paramref name="fallback"/>.</summary>
        public static long GetLong(Dictionary<string, object?>? obj, string key, long fallback = 0) =>
            ToLong(Get(obj, key), fallback);

        /// <summary>Reads a numeric property as int (clamped), or <paramref name="fallback"/>.</summary>
        public static int GetInt(Dictionary<string, object?>? obj, string key, int fallback = 0)
        {
            var v = Get(obj, key);
            if (!IsNumber(v)) return fallback;
            var l = ToLong(v, fallback);
            if (l > int.MaxValue) return int.MaxValue;
            if (l < int.MinValue) return int.MinValue;
            return (int)l;
        }

        /// <summary>Reads an object property, or null.</summary>
        public static Dictionary<string, object?>? GetObject(Dictionary<string, object?>? obj, string key) =>
            Get(obj, key) as Dictionary<string, object?>;

        /// <summary>Reads an array property, or null.</summary>
        public static List<object?>? GetArray(Dictionary<string, object?>? obj, string key) =>
            Get(obj, key) as List<object?>;

        /// <summary>Reads an array of strings (non-strings are skipped). Never null.</summary>
        public static List<string> GetStringList(Dictionary<string, object?>? obj, string key)
        {
            var list = new List<string>();
            var arr = GetArray(obj, key);
            if (arr == null) return list;
            foreach (var item in arr) if (item is string s) list.Add(s);
            return list;
        }

        /// <summary>Reads an object of string values (non-strings are skipped). Never null.</summary>
        public static Dictionary<string, string> GetStringMap(Dictionary<string, object?>? obj, string key)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var o = GetObject(obj, key);
            if (o == null) return map;
            foreach (var kv in o) if (kv.Value is string s) map[kv.Key] = s;
            return map;
        }

        /// <summary>True when the value is a parsed JSON number.</summary>
        public static bool IsNumber(object? v) => v is long || v is double || v is int || v is float || v is decimal;

        /// <summary>Converts a parsed JSON number to double.</summary>
        public static double ToDouble(object? v, double fallback = 0)
        {
            switch (v)
            {
                case long l: return l;
                case double d: return d;
                case int i: return i;
                case float f: return f;
                case decimal m: return (double)m;
                default: return fallback;
            }
        }

        /// <summary>Converts a parsed JSON number to long (fractions are truncated).</summary>
        public static long ToLong(object? v, long fallback = 0)
        {
            switch (v)
            {
                case long l: return l;
                case int i: return i;
                case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                    if (d >= long.MaxValue) return long.MaxValue;
                    if (d <= long.MinValue) return long.MinValue;
                    return (long)d;
                case float f when !float.IsNaN(f) && !float.IsInfinity(f): return (long)f;
                case decimal m: return (long)m;
                default: return fallback;
            }
        }

        /// <summary>Parses an ISO 8601 timestamp from the API, or null.</summary>
        public static DateTime? ToDateTime(object? v)
        {
            if (v is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                return dt;
            return null;
        }

        /// <summary>
        /// Converts a parsed JSON value to <typeparamref name="T"/>. Supports <c>object</c>, primitives,
        /// <c>string</c>, <c>Dictionary&lt;string, object&gt;</c>, <c>List&lt;object&gt;</c>,
        /// <c>List&lt;string&gt;</c>, <c>string[]</c>, <c>Dictionary&lt;string, string&gt;</c>,
        /// <c>Dictionary&lt;string, double&gt;</c>, <see cref="IEnluJsonReadable"/> and, through
        /// <see cref="ObjectDeserializer"/> (JsonUtility in Unity), your own <c>[Serializable]</c> classes.
        /// </summary>
        public static T ConvertTo<T>(object? node) => (T)ConvertTo(node, typeof(T))!;

        /// <summary>Non-generic form of <see cref="ConvertTo{T}(object)"/>.</summary>
        public static object? ConvertTo(object? node, Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type == typeof(object)) return node;

            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                if (node == null) return null;
                type = underlying;
            }

            if (node == null)
            {
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }

            if (type == typeof(string))
            {
                if (node is string s) return s;
                if (node is bool || IsNumber(node)) return Convert.ToString(node, CultureInfo.InvariantCulture);
                return Serialize(node);
            }
            if (type == typeof(bool))
            {
                if (node is bool b) return b;
                throw Mismatch(node, type);
            }
            if (type == typeof(double)) return RequireNumber(node, type);
            if (type == typeof(float)) return (float)RequireNumber(node, type);
            if (type == typeof(decimal)) return (decimal)RequireNumber(node, type);
            if (type == typeof(long)) return ToLong(RequireNumberNode(node, type));
            if (type == typeof(int)) return checked((int)ToLong(RequireNumberNode(node, type)));
            if (type == typeof(short)) return checked((short)ToLong(RequireNumberNode(node, type)));
            if (type == typeof(byte)) return checked((byte)ToLong(RequireNumberNode(node, type)));
            if (type == typeof(uint)) return checked((uint)ToLong(RequireNumberNode(node, type)));
            if (type.IsEnum)
            {
                if (node is string es) return Enum.Parse(type, es, true);
                if (IsNumber(node)) return Enum.ToObject(type, ToLong(node));
                throw Mismatch(node, type);
            }
            if (type == typeof(DateTime))
            {
                var dt = ToDateTime(node);
                if (dt.HasValue) return dt.Value;
                throw Mismatch(node, type);
            }

            if (type == typeof(Dictionary<string, object?>) || type == typeof(IDictionary<string, object?>) ||
                type == typeof(IReadOnlyDictionary<string, object?>))
            {
                if (node is Dictionary<string, object?> d) return d;
                throw Mismatch(node, type);
            }
            if (type == typeof(List<object?>) || type == typeof(IList<object?>) || type == typeof(IReadOnlyList<object?>) ||
                type == typeof(IEnumerable<object?>))
            {
                if (node is List<object?> l) return l;
                throw Mismatch(node, type);
            }
            if (type == typeof(List<string>) || type == typeof(string[]) || type == typeof(IList<string>) ||
                type == typeof(IReadOnlyList<string>))
            {
                if (!(node is List<object?> l)) throw Mismatch(node, type);
                var strings = new List<string>(l.Count);
                foreach (var item in l) strings.Add((string)ConvertTo(item, typeof(string))!);
                return type == typeof(string[]) ? (object)strings.ToArray() : strings;
            }
            if (type == typeof(Dictionary<string, string>))
            {
                if (!(node is Dictionary<string, object?> d)) throw Mismatch(node, type);
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in d) map[kv.Key] = (string)ConvertTo(kv.Value, typeof(string))!;
                return map;
            }
            if (type == typeof(Dictionary<string, double>))
            {
                if (!(node is Dictionary<string, object?> d)) throw Mismatch(node, type);
                var map = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var kv in d) map[kv.Key] = RequireNumber(kv.Value, typeof(double));
                return map;
            }
            if (typeof(IEnluJsonReadable).IsAssignableFrom(type))
            {
                var readable = (IEnluJsonReadable)Activator.CreateInstance(type)!;
                readable.ReadJson(node);
                return readable;
            }

            var deserializer = ObjectDeserializer;
            if (deserializer != null)
            {
                if (!(node is IDictionary) && !(node is IList))
                    throw Mismatch(node, type);
                return deserializer(Serialize(node), type);
            }
            throw new NotSupportedException("Cannot convert JSON to " + type.FullName +
                ". Use object, a dictionary, a primitive, IEnluJsonReadable, or set EnluJson.ObjectDeserializer.");
        }

        private static object RequireNumberNode(object node, Type type)
        {
            if (!IsNumber(node)) throw Mismatch(node, type);
            return node;
        }

        private static double RequireNumber(object? node, Type type)
        {
            if (!IsNumber(node)) throw Mismatch(node, type);
            return ToDouble(node);
        }

        private static InvalidCastException Mismatch(object? node, Type type)
        {
            var kind = node == null ? "null" : node is string ? "string" : node is bool ? "boolean" :
                IsNumber(node) ? "number" : node is IDictionary ? "object" : node is IList ? "array" : node.GetType().Name;
            return new InvalidCastException("Cannot convert a JSON " + kind + " to " + type.Name + ".");
        }
    }
}
