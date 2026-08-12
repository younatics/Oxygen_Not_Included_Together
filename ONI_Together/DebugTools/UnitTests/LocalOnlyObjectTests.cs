using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Which objects are allowed to go without a network address.
    ///
    /// Creation-time attribution on a live client named thirty prefabs it makes on
    /// its own. Some are scaffolding both peers build for themselves and neither
    /// ever mentions - ChoreHelpers.CreateLocator, Navigator.OnPrefabInit,
    /// Repairable.CreateStorageProxy, the pooled FX spawners, and our own
    /// WorldStateSyncer drawing a DigPlacer. The host's announcement breakdown lists
    /// none of them, so on a client each became a preview waiting to be named by a
    /// message that is never sent.
    ///
    /// The exclusion is a name match, which is the kind of rule that quietly widens
    /// until something real falls into it. These tests are the boundary: the
    /// scaffolding stays out, and the objects that look like it but are part of the
    /// simulation stay in.
    /// </summary>
    public static class LocalOnlyObjectTests
    {
        /// <summary>Named by the attribution run, one per creating code path.</summary>
        private static readonly string[] Scaffolding =
        {
            "ApproachableLocator",
            "SleepLocator",
            "TargetLocator",
            "DigPlacer",
            "RepairableStorageProxy",
            "OxygenBubblesFX",
            "BleachStoneFX",
            "ContaminatedGasBubbleFX",
            "BreathFX",
            "OreAbsorbFx",
            "EffectTemplateFx",
            "BuildSplashFx",
            "fx_disinfect_splash",
        };

        /// <summary>
        /// Objects the same run also named, which must keep their identity.
        ///
        /// CO2 and BreathFX are the pair that decides how wide this rule may be:
        /// both come out of CO2Manager, and only the second is an effect. The first
        /// is gas that joins the simulation, so excluding it would not tidy the log,
        /// it would hide a divergence.
        /// </summary>
        private static readonly string[] Simulation =
        {
            "CO2",
            "PacuBaby",
            "MushBar",
            "BasicPlantBar",
            "SwampLilyFlower",
            "HighWattageWireComplete",
            "SplashStep",
        };

        [UnitTest(name: "Scaffolding is refused a network address", category: "Identity")]
        public static UnitTestResult ScaffoldingIsExcluded()
        {
            var missed = Scaffolding.Where(n => !NetworkIdentity.IsLocalOnlyName(n)).ToList();
            if (missed.Count > 0)
            {
                return UnitTestResult.Fail(
                    "these are built locally by both peers and named by neither, but would still " +
                    "take an id: " + string.Join(", ", missed));
            }

            return UnitTestResult.Pass($"{Scaffolding.Length} scaffolding prefabs excluded");
        }

        [UnitTest(name: "Simulation objects keep their network address", category: "Identity")]
        public static UnitTestResult SimulationIsNotExcluded()
        {
            var caught = Simulation.Where(NetworkIdentity.IsLocalOnlyName).ToList();
            if (caught.Count > 0)
            {
                return UnitTestResult.Fail(
                    "the local-only rule has widened onto objects that are part of the simulation, " +
                    "which stops them replicating rather than tidying anything: " +
                    string.Join(", ", caught));
            }

            return UnitTestResult.Pass($"{Simulation.Length} simulation prefabs still replicate");
        }

        [UnitTest(name: "The exclusion survives Unity's clone and instance suffixes", category: "Identity")]
        public static UnitTestResult SuffixesDoNotDefeatTheMatch()
        {
            // Unity appends "(Clone)" and the mod's own logging path carries instance
            // ids. A rule that matches the bare name and not the name as it actually
            // arrives is a rule that never fires.
            foreach (var variant in new[] { "SleepLocator(Clone)", "DigPlacer(Clone)" })
            {
                if (!NetworkIdentity.IsLocalOnlyName(variant))
                    return UnitTestResult.Fail($"'{variant}' was not recognised as scaffolding");
            }

            if (NetworkIdentity.IsLocalOnlyName(null) || NetworkIdentity.IsLocalOnlyName(string.Empty))
                return UnitTestResult.Fail("an empty name was treated as scaffolding");

            return UnitTestResult.Pass("clone suffixes and empty names handled");
        }

        [UnitTest(name: "Nothing in the live world is excluded by accident", category: "Identity")]
        public static UnitTestResult LiveWorldIsNotSilentlyStripped()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            // The static lists above are what was seen once. This asks the colony
            // that is actually loaded, so a prefab nobody thought of - a mod's, a
            // DLC's - cannot fall into the rule unnoticed.
            // FindObjectsByType, not FindObjectsOfType: the latter is an error in
            // this Unity, and sorting several thousand pickupables by instance id
            // would be paid for nothing here.
            var wrongly = Object.FindObjectsByType<Pickupable>(FindObjectsSortMode.None)
                .Where(p => !p.IsNullOrDestroyed() && NetworkIdentity.IsLocalOnlyName(p.name))
                .Select(p => p.name)
                .Distinct()
                .Take(8)
                .ToList();

            if (wrongly.Count > 0)
            {
                return UnitTestResult.Fail(
                    "carryable items match the scaffolding rule, so they would lose their id: " +
                    string.Join(", ", wrongly));
            }

            return UnitTestResult.Pass("no carryable item in this colony matches the rule");
        }

        [UnitTest(name: "Report why registered matter was or was not excluded",
            category: "Identity")]
        public static UnitTestResult ReportEphemeralVerdicts()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            // A breakdown, not an assertion.
            //
            // The logs said the host held fourteen Hydrogen ids; the registry-wide
            // assertion said nothing ephemeral was registered. Both were true, so the
            // predicate must be declining those objects, and no amount of reading said
            // which branch did it. Three explanations were argued for and none was
            // measured.
            //
            // This prints the branch, per prefab, for every registered object that
            // carries an element. The two peers' lines can be compared as well, which
            // is how the earlier claim - that the disagreement was gas physics - can be
            // checked against the alternative that it is storage contents.
            var byVerdict = new Dictionary<string, int>();
            var elementPrefabs = new Dictionary<string, string>();

            foreach (var identity in NetworkIdentityRegistry.AllIdentities)
            {
                if (identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed()) continue;
                if (identity.NetId == 0) continue;

                string verdict = NetworkIdentity.EphemeralVerdict(identity.gameObject);
                if (verdict == "no-primary-element") continue;

                byVerdict.TryGetValue(verdict, out int n);
                byVerdict[verdict] = n + 1;

                // One example prefab per verdict is enough to name the class of object.
                if (!elementPrefabs.ContainsKey(verdict))
                    elementPrefabs[verdict] = identity.gameObject.name;
            }

            var parts = byVerdict.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}({elementPrefabs[kv.Key]})");
            DebugConsole.Log("[EPHEMERAL] " + string.Join(" ", parts));

            return UnitTestResult.Pass(string.Join(" ", byVerdict.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        [UnitTest(name: "Nothing excluded from identity is in the registry anyway",
            category: "Identity")]
        public static UnitTestResult ExclusionsAreNotRegisteredAnyway()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            // The rule is checked against the registry, not against the code that was
            // supposed to apply it.
            //
            // A rising skip counter was read as the rule working. It only proved the rule
            // was reached, never that it was the only path - and RegisterIdentity has
            // ninety-one callers. Whatever the counters say, this asks the colony.
            //
            // Ephemeral matter only, deliberately. Scaffolding is refused at spawn and
            // granted when a sender needs it, so a registered DigPlacer is correct, not a
            // leak: asserting otherwise asserts something false, and the wider version of
            // this rule is what sent 476 packets a run to NetId 0.
            var leaked = new List<string>();
            foreach (var identity in NetworkIdentityRegistry.AllIdentities)
            {
                if (identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed()) continue;
                if (identity.NetId == 0) continue;

                // Ephemeral when it was addressed, not ephemeral now.
                //
                // A bottle of water in a storage bin is correctly identified. When a
                // duplicant empties it the same object becomes loose liquid, still
                // holding the id it was legitimately given - and the earlier version of
                // this test failed on exactly those two objects a run, on the client
                // only, with nothing wrong. An assertion that fires without a defect is
                // worse than no assertion; it teaches everyone to ignore the suite.
                if (!identity.WasEphemeralWhenAddressed) continue;

                if (leaked.Count < 8)
                    leaked.Add($"{identity.gameObject.name}#{identity.NetId} " +
                               $"(now {NetworkIdentity.EphemeralVerdict(identity.gameObject)})");
            }

            if (leaked.Count > 0)
            {
                return UnitTestResult.Fail(
                    $"{leaked.Count} object(s) were ephemeral when they were given an " +
                    $"address: {string.Join(", ", leaked)} - some path grants ids without " +
                    "going through the refusal in RegisterIdentity");
            }

            // Says what was covered. A pass with nothing refused proves nothing, and
            // this project has read that as a fix four times.
            return UnitTestResult.Pass(
                $"{NetworkIdentity.EphemeralSkipped} ephemeral object(s) refused, none " +
                $"registered; {NetworkIdentity.AddressAskedAfterRefusal} sender ask(s) " +
                $"after refusal ({NetworkIdentity.RefusedAskBreakdown()})");
        }
    }
}
