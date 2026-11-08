#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;

namespace Enlu.Tests
{
    public sealed class EnluJsonTests
    {
        [Test]
        public void ParsesPrimitives()
        {
            Assert.That(EnluJson.Parse("null"), Is.Null);
            Assert.That(EnluJson.Parse("true"), Is.EqualTo(true));
            Assert.That(EnluJson.Parse(" false "), Is.EqualTo(false));
            Assert.That(EnluJson.Parse("\"hi\""), Is.EqualTo("hi"));
            Assert.That(EnluJson.Parse("42"), Is.EqualTo(42L));
            Assert.That(EnluJson.Parse("-7"), Is.EqualTo(-7L));
            Assert.That(EnluJson.Parse("0"), Is.EqualTo(0L));
            Assert.That(EnluJson.Parse("1.5"), Is.EqualTo(1.5d));
            Assert.That(EnluJson.Parse("-2.5e3"), Is.EqualTo(-2500d));
            Assert.That(EnluJson.Parse("1E2"), Is.EqualTo(100d));
        }

        [Test]
        public void IntegersBeyondLongBecomeDouble()
        {
            var v = EnluJson.Parse("123456789012345678901234567890");
            Assert.That(v, Is.TypeOf<double>());
            Assert.That((double)v!, Is.EqualTo(1.2345678901234568E+29).Within(1e15));
        }

        [Test]
        public void ParsesNestedStructures()
        {
            var o = EnluJson.ParseObject("{\"a\":[1,2,{\"b\":null}],\"c\":{\"d\":\"e\"},\"f\":[]}");
            var a = EnluJson.GetArray(o, "a")!;
            Assert.That(a.Count, Is.EqualTo(3));
            Assert.That(a[0], Is.EqualTo(1L));
            Assert.That(EnluJson.AsObject(a[2])!.ContainsKey("b"), Is.True);
            Assert.That(EnluJson.GetString(EnluJson.GetObject(o, "c"), "d"), Is.EqualTo("e"));
            Assert.That(EnluJson.GetArray(o, "f")!.Count, Is.EqualTo(0));
        }

        [Test]
        public void DecodesEscapesAndUnicode()
        {
            var s = (string)EnluJson.Parse("\"q\\\" b\\\\ s\\/ n\\n t\\t u\\u00e9 pair\\ud83d\\ude00\"")!;
            Assert.That(s, Is.EqualTo("q\" b\\ s/ n\n t\t u\u00e9 pair\U0001F600"));
            Assert.That(EnluJson.Parse("\"\u00fcnic\u00f6de\""), Is.EqualTo("\u00fcnic\u00f6de"));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("{")]
        [TestCase("[1,]")]
        [TestCase("{\"a\":1,}")]
        [TestCase("{a:1}")]
        [TestCase("01")]
        [TestCase("1.")]
        [TestCase(".5")]
        [TestCase("-")]
        [TestCase("1e")]
        [TestCase("tru")]
        [TestCase("nul")]
        [TestCase("\"unterminated")]
        [TestCase("\"bad \\x escape\"")]
        [TestCase("\"\\u12G4\"")]
        [TestCase("[1] 2")]
        [TestCase("NaN")]
        public void RejectsInvalidJson(string text)
        {
            Assert.Throws<EnluJsonException>(() => EnluJson.Parse(text));
            Assert.That(EnluJson.TryParse(text, out _), Is.False);
        }

        [Test]
        public void RejectsRawControlCharactersInStrings()
        {
            Assert.Throws<EnluJsonException>(() => EnluJson.Parse("\"a\nb\""));
        }

        [Test]
        public void RejectsVeryDeepNesting()
        {
            var deep = new string('[', 1000) + new string(']', 1000);
            Assert.Throws<EnluJsonException>(() => EnluJson.Parse(deep));
        }

        [Test]
        public void LastDuplicateKeyWins()
        {
            var o = EnluJson.ParseObject("{\"a\":1,\"a\":2}");
            Assert.That(o["a"], Is.EqualTo(2L));
        }

        [Test]
        public void SerializesPrimitivesAndCollections()
        {
            var value = new Dictionary<string, object?>
            {
                ["s"] = "x",
                ["i"] = 3,
                ["l"] = 9007199254740993L,
                ["d"] = 0.1,
                ["f"] = 0.25f,
                ["b"] = true,
                ["n"] = null,
                ["list"] = new List<object?> { 1, "two", null },
                ["arr"] = new[] { 1, 2 },
                ["map"] = new Dictionary<string, int> { ["k"] = 1 },
            };
            Assert.That(EnluJson.Serialize(value), Is.EqualTo(
                "{\"s\":\"x\",\"i\":3,\"l\":9007199254740993,\"d\":0.1,\"f\":0.25,\"b\":true,\"n\":null," +
                "\"list\":[1,\"two\",null],\"arr\":[1,2],\"map\":{\"k\":1}}"));
        }

        [Test]
        public void EscapesStrings()
        {
            Assert.That(EnluJson.Serialize("a\"b\\c\n\r\t\b\f\u0001\u2028"), Is.EqualTo("\"a\\\"b\\\\c\\n\\r\\t\\b\\f\\u0001\\u2028\""));
            Assert.That(EnluJson.Serialize("</script>"), Is.EqualTo("\"</script>\""));
            Assert.That(EnluJson.Serialize("\u00e9\U0001F600"), Is.EqualTo("\"\u00e9\U0001F600\""));
        }

        [Test]
        public void NonFiniteNumbersBecomeNull()
        {
            Assert.That(EnluJson.Serialize(double.NaN), Is.EqualTo("null"));
            Assert.That(EnluJson.Serialize(double.PositiveInfinity), Is.EqualTo("null"));
            Assert.That(EnluJson.Serialize(float.NegativeInfinity), Is.EqualTo("null"));
        }

        [Test]
        public void SerializesEnumsDatesAndGuids()
        {
            Assert.That(EnluJson.Serialize(DataVisibility.Public), Is.EqualTo("\"Public\""));
            var dt = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);
            Assert.That(EnluJson.Serialize(dt), Is.EqualTo("\"2026-01-02T03:04:05.678Z\""));
            var g = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
            Assert.That(EnluJson.Serialize(g), Is.EqualTo("\"0f8fad5b-d9cb-469f-a165-70867728950e\""));
        }

        [Test]
        public void NumbersIgnoreTheCurrentCulture()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.That(EnluJson.Serialize(1.5), Is.EqualTo("1.5"));
                Assert.That(EnluJson.Parse("1.5"), Is.EqualTo(1.5d));
                Assert.That(EnluJson.Serialize(1234567.25m), Is.EqualTo("1234567.25"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Test]
        public void RoundTripsComplexDocuments()
        {
            const string json = "{\"name\":\"Ollie\",\"inv\":[{\"id\":\"sword\",\"qty\":1},{\"id\":\"gem\",\"qty\":12}],\"pos\":{\"x\":1.25,\"y\":-3},\"tags\":[],\"meta\":{}}";
            var parsed = EnluJson.Parse(json);
            Assert.That(EnluJson.Serialize(parsed), Is.EqualTo(json));
            Assert.That(EnluJson.Serialize(EnluJson.Parse(EnluJson.Serialize(parsed, true))), Is.EqualTo(json));
        }

        [Test]
        public void PrettyPrintIsValidJson()
        {
            var pretty = EnluJson.Serialize(new Dictionary<string, object?> { ["a"] = new List<object?> { 1, 2 }, ["b"] = new Dictionary<string, object?>() }, true);
            Assert.That(pretty, Does.Contain("\n"));
            Assert.That(EnluJson.TryParse(pretty, out _), Is.True);
        }

        [Test]
        public void RawJsonIsEmbeddedAndValidated()
        {
            var o = new Dictionary<string, object?> { ["args"] = new EnluRawJson("{\"x\": 1}") };
            Assert.That(EnluJson.Serialize(o), Is.EqualTo("{\"args\":{\"x\": 1}}"));
            Assert.Throws<EnluJsonException>(() => EnluJson.Serialize(new EnluRawJson("{oops")));
        }

        private sealed class Writable : IEnluJsonWritable
        {
            public object? ToJsonValue() => new Dictionary<string, object?> { ["hp"] = 10 };
        }

        [Test]
        public void UsesIEnluJsonWritable()
        {
            Assert.That(EnluJson.Serialize(new List<object?> { new Writable() }), Is.EqualTo("[{\"hp\":10}]"));
        }

        private sealed class Reward : IEnluJsonReadable
        {
            public string Item = string.Empty;
            public int Count;

            public void ReadJson(object? node)
            {
                var o = EnluJson.AsObject(node);
                Item = EnluJson.GetString(o, "item") ?? string.Empty;
                Count = EnluJson.GetInt(o, "count");
            }
        }

        [Test]
        public void ConvertsToRequestedTypes()
        {
            Assert.That(EnluJson.ConvertTo<int>(5L), Is.EqualTo(5));
            Assert.That(EnluJson.ConvertTo<long>(5L), Is.EqualTo(5L));
            Assert.That(EnluJson.ConvertTo<double>(5L), Is.EqualTo(5d));
            Assert.That(EnluJson.ConvertTo<float>(0.5d), Is.EqualTo(0.5f));
            Assert.That(EnluJson.ConvertTo<bool>(true), Is.True);
            Assert.That(EnluJson.ConvertTo<string>("x"), Is.EqualTo("x"));
            Assert.That(EnluJson.ConvertTo<string>(12L), Is.EqualTo("12"));
            Assert.That(EnluJson.ConvertTo<int?>(null), Is.Null);
            Assert.That(EnluJson.ConvertTo<int>(null), Is.EqualTo(0));
            Assert.That(EnluJson.ConvertTo<DataVisibility>("public"), Is.EqualTo(DataVisibility.Public));
            Assert.That(EnluJson.ConvertTo<DateTime>("2026-03-04T05:06:07.000Z"), Is.EqualTo(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc)));

            var list = EnluJson.ConvertTo<List<string>>(EnluJson.Parse("[\"a\",\"b\"]"));
            Assert.That(list, Is.EqualTo(new List<string> { "a", "b" }));
            var arr = EnluJson.ConvertTo<string[]>(EnluJson.Parse("[\"a\"]"));
            Assert.That(arr, Is.EqualTo(new[] { "a" }));
            var map = EnluJson.ConvertTo<Dictionary<string, double>>(EnluJson.Parse("{\"a\":1,\"b\":2.5}"));
            Assert.That(map["b"], Is.EqualTo(2.5));
            var smap = EnluJson.ConvertTo<Dictionary<string, string>>(EnluJson.Parse("{\"a\":\"x\"}"));
            Assert.That(smap["a"], Is.EqualTo("x"));

            var reward = EnluJson.ConvertTo<Reward>(EnluJson.Parse("{\"item\":\"gem\",\"count\":3}"));
            Assert.That(reward.Item, Is.EqualTo("gem"));
            Assert.That(reward.Count, Is.EqualTo(3));
        }

        [Test]
        public void ConversionMismatchesThrow()
        {
            Assert.Throws<InvalidCastException>(() => EnluJson.ConvertTo<int>("nope"));
            Assert.Throws<InvalidCastException>(() => EnluJson.ConvertTo<bool>(1L));
            Assert.Throws<InvalidCastException>(() => EnluJson.ConvertTo<Dictionary<string, object?>>(EnluJson.Parse("[1]")));
            Assert.Throws<OverflowException>(() => EnluJson.ConvertTo<int>(5000000000L));
        }

        [Test]
        public void HelpersReturnFallbacksForWrongTypes()
        {
            var o = EnluJson.ParseObject("{\"s\":\"x\",\"n\":3,\"b\":true}");
            Assert.That(EnluJson.GetString(o, "n"), Is.Null);
            Assert.That(EnluJson.GetInt(o, "s", -1), Is.EqualTo(-1));
            Assert.That(EnluJson.GetBool(o, "missing", true), Is.True);
            Assert.That(EnluJson.GetDouble(o, "n"), Is.EqualTo(3d));
            Assert.That(EnluJson.GetStringList(o, "missing"), Is.Empty);
        }
    }
}
