using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// The suppressions are attached to the methods they name.
    ///
    /// Three fixes shipped and reported zero every run - the egg hatch block, the
    /// fabricator product block, the leftover-scaffold sweep. Zero has two meanings
    /// and the counters cannot tell them apart: the situation never arose, or the
    /// patch is not on the method at all. Guessing which is how a fix rides along for
    /// days doing nothing, and this project has already had one - the announcement in
    /// KInstantiate's postfix that caught two spawns in a whole run because most
    /// objects never go through that method.
    ///
    /// This answers half the question for free: whether the hook exists. If a patch is
    /// attached and its counter is still zero, the gameplay genuinely did not happen
    /// and the scenario is what needs extending. If it is not attached, no amount of
    /// playing would ever have moved the number.
    ///
    /// Method names are given as strings because a wrong name is exactly the failure
    /// being guarded against: nameof would refuse to compile and hide it.
    /// </summary>
    public static class PatchAttachmentTests
    {
        private static readonly (string Type, string Method, string Why)[] Expected =
        {
            ("IncubationMonitor", "SpawnBaby",
                "a client hatching its own egg is a second creature the host never issued"),
            ("ComplexFabricator", "SpawnOrderProduct",
                "a client fabricating its own product duplicates what the host announces"),
            // Private, so it is named by string and nameof cannot check it - which is
            // exactly the case this test exists for. Without it a patch that failed to
            // attach would report dropsBlocked=0, and that zero would read as the
            // diagnosis being refuted rather than the check never having run.
            ("ComplexFabricator", "DropExcessIngredients",
                "a client throwing out ingredients the host still holds empties one container every run"),
            ("BuildingHP", "OnDoBuildingDamage",
                "client-side damage must be refused, and conduit damage recorded"),
            ("Pickupable", "OnCleanUp",
                "the host tells clients when a ground item is gone"),
        };

        [UnitTest(name: "Every suppression patch is attached to its method", category: "Patches")]
        public static UnitTestResult SuppressionsAreAttached()
        {
            var patched = Harmony.GetAllPatchedMethods().ToList();
            if (patched.Count == 0)
                return UnitTestResult.Skip("Harmony reports no patched methods at all");

            var missing = new List<string>();
            var found = new List<string>();

            foreach (var (typeName, methodName, why) in Expected)
            {
                bool attached = patched.Any(m =>
                    m != null
                    && m.DeclaringType != null
                    && m.DeclaringType.Name == typeName
                    && m.Name == methodName);

                if (attached) found.Add($"{typeName}.{methodName}");
                else missing.Add($"{typeName}.{methodName} ({why})");
            }

            if (missing.Count > 0)
            {
                return UnitTestResult.Fail(
                    $"{missing.Count} suppression(s) are not on their method, so their counters can " +
                    "only ever read zero: " + string.Join("; ", missing));
            }

            return UnitTestResult.Pass($"{found.Count} attached: {string.Join(", ", found)}");
        }

        [UnitTest(name: "Patched method count is reported", category: "Patches")]
        public static UnitTestResult PatchCountIsVisible()
        {
            // Not an assertion on a number - the count changes whenever a patch is
            // added, and a test that has to be edited for every change gets edited
            // without being read. It is here so a collapse is visible: this mod patches
            // hundreds of methods, and a run reporting a handful means Harmony failed
            // early and every other result in that run is worthless.
            int n = Harmony.GetAllPatchedMethods().Count();

            if (n < 50)
                return UnitTestResult.Fail($"only {n} patched methods - Harmony did not finish applying");

            return UnitTestResult.Pass($"{n} patched methods");
        }
    }
}
