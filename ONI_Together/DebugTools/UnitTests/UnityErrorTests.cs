namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Did the game report any error at all while this ran?
    ///
    /// This is the gate that was missing, and its absence is why four client deaths
    /// got through every test and every scenario run. Each one looked like this: our
    /// method returns normally, ONI logs an error or a failed assert from inside its
    /// own code, and the game closes itself seconds later. Nothing in the suite asked
    /// the question "did anything go wrong", so nothing failed.
    ///
    ///   Could not find Personality: 0x0          (a portrait duplicant with no stats)
    ///   Tried adding a trait on a prefab         (the fix for the one above)
    ///   Anim overrides ... require a symbol override controller
    ///   Assert failed ... at Accessorizer.OnSpawn
    ///
    /// A count of them is a blunt instrument and that is the point: it does not need
    /// to know what can go wrong, only that something did. Everything specific can be
    /// missed by a specific test; this cannot be missed by not having thought of it.
    /// </summary>
    public static class UnityErrorTests
    {
        [UnitTest(name: "The game reported no errors this session", category: "Health")]
        public static UnitTestResult NoUnityErrors()
        {
            int count = DebugConsole.UnityErrorCount;
            if (count == 0)
                return UnitTestResult.Pass("no Unity errors or asserts since load");

            return UnitTestResult.Fail(
                $"{count} Unity error(s) or failed assert(s) since load. ONI turns these into an error " +
                "report and can close the game; every client death in this work announced itself this " +
                "way and nothing failed because of it. Search the log for '[Unity] Error' or " +
                "'Assert failed' - the stack names the cause.");
        }
    }
}
