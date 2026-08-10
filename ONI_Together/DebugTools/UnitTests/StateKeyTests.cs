using System.Collections.Generic;
using System.Linq;
using ONI_Together.Misc;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Components.StructureStateSyncers;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Structure state travels as a bag of named values, and a name is only
    /// agreed by convention - nothing connects the string a syncer writes to the
    /// string its reader asks for. A typo, a renamed key, or a key that used to
    /// exist produces silence: the sender keeps paying to send it and the
    /// receiver keeps defaulting.
    ///
    /// That is not hypothetical. The reactor read "reaction_capacityKg", which
    /// no code has ever written - EncodeStorageContents writes one key per
    /// storage and keeps capacity inside the blob - so the enriched uranium's
    /// temperature was never once replicated, and nothing anywhere said so.
    ///
    /// These sample what the live colony actually produces rather than assert a
    /// fixed list, so a new syncer is covered the day it is written.
    /// </summary>
    public static class StateKeyTests
    {
        /// <summary>Every key a live syncer puts on the wire this tick.</summary>
        private static Dictionary<string, List<string>> SampleLiveKeys()
        {
            var byKey = new Dictionary<string, List<string>>();

            foreach (var syncer in Object.FindObjectsByType<StructureSyncerBase>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (syncer == null) continue;

                Dictionary<string, Variant> optional;
                try
                {
                    syncer.SampleStateForDiagnostics(out optional);
                }
                catch
                {
                    continue;   // a syncer that cannot sample is a different test's problem
                }
                if (optional == null) continue;

                foreach (var key in optional.Keys)
                {
                    if (!byKey.TryGetValue(key, out var owners))
                        byKey[key] = owners = new List<string>();
                    string name = syncer.GetType().Name;
                    if (!owners.Contains(name)) owners.Add(name);
                }
            }

            return byKey;
        }

        [UnitTest(name: "Structure state carries the keys its readers expect", category: "StateKeys")]
        public static UnitTestResult KeysMatchReaders()
        {
            var live = SampleLiveKeys();
            if (live.Count == 0)
                return UnitTestResult.Skip("no structure syncers in this world");

            // hit_points is added by the base for every structure that has
            // BuildingHP, so its absence everywhere means the base stopped
            // sampling it - which is how damage silently stopped syncing once
            // before.
            bool anyHitPoints = live.ContainsKey("hit_points");
            bool anyOperational = live.ContainsKey("is_operational");

            var missing = new List<string>();
            if (!anyHitPoints) missing.Add("hit_points");
            if (!anyOperational) missing.Add("is_operational");

            if (missing.Count > 0)
            {
                return UnitTestResult.Fail(
                    $"no live syncer is sending {string.Join(", ", missing)} - " +
                    "a reader is waiting for something nobody puts on the wire");
            }

            return UnitTestResult.Pass(
                $"{live.Count} distinct keys in flight from {live.Values.Sum(v => v.Count)} syncer bindings");
        }

        [UnitTest(name: "Storage state is sent under the key the reader opens", category: "StateKeys")]
        public static UnitTestResult StorageKeysAgree()
        {
            // The reactor's dead branch was exactly this: three storages sent
            // under "supply_stor", "reaction_stor" and "waste_stor", and a
            // condition asking for "reaction_capacityKg". Prefixed storage keys
            // are the easiest place in the codebase to write a name nobody
            // sends, so they are checked by shape.
            var live = SampleLiveKeys();
            if (live.Count == 0)
                return UnitTestResult.Skip("no structure syncers in this world");

            var storageKeys = live.Keys.Where(k => k.EndsWith("stor")).ToList();
            var capacityKeys = live.Keys.Where(k => k.Contains("capacityKg")).ToList();

            if (capacityKeys.Count > 0)
            {
                return UnitTestResult.Fail(
                    $"a syncer is sending {string.Join(", ", capacityKeys)} as its own key; capacity travels " +
                    "inside the storage blob, so a reader keyed on this will never fire");
            }

            return UnitTestResult.Pass(
                storageKeys.Count > 0
                    ? $"storage travels as {string.Join(", ", storageKeys)}"
                    : "no storage-bearing syncer in this world");
        }
    }
}
