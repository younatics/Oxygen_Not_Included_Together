using System.IO;
using ONI_Together.Misc;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Variant is a tagged union with a separate field per type and no
    /// conversion between them. Writing an int and reading .Float returns zero -
    /// silently, with no warning anywhere.
    ///
    /// That cost a live session. Building damage was stored as an int and read
    /// as a float, so every host reading looked like zero hit points and each
    /// client damaged its own buildings by their entire health twice a second.
    /// The player saw a toilet that was broken on one peer and fine on the
    /// other, which is indistinguishable from the sync not running at all.
    /// </summary>
    public static class VariantTests
    {
        private static Variant RoundTrip(Variant v)
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                v.Write(writer);

            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            return Variant.Read(reader);
        }

        [UnitTest(name: "An int survives the wire as an int", category: "Variant")]
        public static UnitTestResult IntRoundTrips()
        {
            var sent = new Variant { Type = Variant.TypeCode.Int, Int = 137 };
            var got = RoundTrip(sent);

            if (got.Type != Variant.TypeCode.Int)
                return UnitTestResult.Fail($"type changed in transit: {got.Type}");
            if (got.Int != 137)
                return UnitTestResult.Fail($"value changed in transit: {got.Int}");

            return UnitTestResult.Pass("int round trips");
        }

        [UnitTest(name: "Reading the wrong field is visibly wrong", category: "Variant")]
        public static UnitTestResult WrongFieldIsZero()
        {
            // Pinned deliberately. This is not a bug to fix - it is how a tagged
            // union works - but a reader that picks the wrong field gets zero
            // rather than an error, and that silence is what made the damage
            // sync look like it was working.
            var got = RoundTrip(new Variant { Type = Variant.TypeCode.Int, Int = 137 });

            if (got.Float != 0f)
                return UnitTestResult.Fail(
                    "an int variant now yields something from .Float - if Variant has gained conversion, " +
                    "this test should be replaced by one that asserts the conversion is right");

            return UnitTestResult.Pass("reading the wrong field yields zero, so readers must check Type");
        }

        [UnitTest(name: "A float survives the wire as a float", category: "Variant")]
        public static UnitTestResult FloatRoundTrips()
        {
            var got = RoundTrip(new Variant { Type = Variant.TypeCode.Float, Float = 12.5f });

            if (got.Type != Variant.TypeCode.Float || got.Float != 12.5f)
                return UnitTestResult.Fail($"float did not survive: type={got.Type}, value={got.Float}");

            return UnitTestResult.Pass("float round trips");
        }
    }
}
