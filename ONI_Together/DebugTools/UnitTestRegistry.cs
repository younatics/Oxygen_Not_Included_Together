using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools
{
    public static class UnitTestRegistry
    {
        private static readonly List<UnitTest> _tests = new();

        public static IReadOnlyList<UnitTest> Tests => _tests;

        public static void DiscoverTests()
        {
            _tests.Clear();

            var assembly = typeof(UnitTestRegistry).Assembly; // Limit to only this assembly (for now)
            Type[] types;

            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                return;
            }

            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var attr = method.GetCustomAttribute<UnitTestAttribute>();
                    if (attr == null)
                        continue;

                    var name = attr.Name ?? $"{type.Name}.{method.Name}";
                    var category = attr.Category ?? "Uncategorized";

                    _tests.Add(new UnitTest(name, category, method));
                }
            }
        }

        /// <summary>
        /// Whether the suite is running right now.
        ///
        /// The suite is not a quiet observer. It dumps a table of eight thousand
        /// identities, feeds the packet handlers deliberate rubbish, and asks the
        /// host to resolve ids - four minutes of the heaviest traffic the session
        /// ever sees. Anything that samples the link has to know it is looking at
        /// that and not at the game.
        ///
        /// This is what the latency reading was doing wrong: the suite measured 193
        /// to 228 ms while the once-a-minute sampler, taken between test runs on the
        /// same link, read 41 to 76. The number was real and it was the suite's own
        /// load.
        /// </summary>
        public static bool IsRunning { get; private set; }

        public static void RunAll()
        {
            IsRunning = true;
            try
            {
                foreach (var test in _tests)
                    test.Run();
            }
            finally
            {
                IsRunning = false;
            }
        }

        public static void RunFailed()
        {
            IsRunning = true;
            try
            {
                foreach (var test in _tests)
                {
                    if (test.HasRun && test.IsFailed)
                        test.Run();
                }
            }
            finally
            {
                IsRunning = false;
            }
        }

        public static IEnumerable<string> GetCategories()
        {
            return _tests.Select(t => t.Category).Distinct();
        }
    }
}
