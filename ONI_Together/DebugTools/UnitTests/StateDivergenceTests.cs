using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Components.StructureStateSyncers;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// What each peer believes about its own world, dumped so the two can be
    /// compared exactly.
    ///
    /// Everything else in this suite is a local invariant: ids do not collide,
    /// keys have readers, packets fit. None of them can answer the only question
    /// that matters - do the two peers agree? That is a cross-peer property, so
    /// no assertion on one box can settle it. What a single box can do is state
    /// its own view precisely enough that a comparison is mechanical.
    ///
    /// This is the same shape as the NetId dump, extended past identity to the
    /// state those identities carry. It is what would have caught the toilet
    /// reading as broken on one peer and whole on the other, weeks before a
    /// person noticed and said so.
    /// </summary>
    public static class StateDivergenceTests
    {
        [UnitTest(name: "Dump structure state for cross-peer comparison", category: "Divergence")]
        public static UnitTestResult DumpStructureState()
        {
            var rows = new List<string>();

            foreach (var syncer in Object.FindObjectsByType<StructureSyncerBase>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (syncer == null || syncer.gameObject == null) continue;

                var identity = syncer.gameObject.GetExistingNetIdentity();
                if (identity == null || identity.NetId == 0) continue;

                Dictionary<string, Misc.Variant> optional;
                try { syncer.SampleStateForDiagnostics(out optional); }
                catch { continue; }
                if (optional == null) continue;

                // Sorted by key so two dumps line up without the comparer
                // having to sort, and the storage blob reduced to a length -
                // the bytes differ between peers for reasons that are not
                // divergence, but a different item count is.
                foreach (var kvp in optional.OrderBy(k => k.Key))
                {
                    rows.Add($"[STATE] {identity.NetId}|{syncer.GetType().Name}|{kvp.Key}|{Describe(kvp.Value)}");
                }
            }

            if (rows.Count == 0)
                return UnitTestResult.Skip("no structure syncers with ids - no colony loaded");

            foreach (var row in rows.OrderBy(r => r))
                DebugConsole.Log(row);

            return UnitTestResult.Pass($"dumped {rows.Count} state values");
        }

        [UnitTest(name: "Dump building damage for cross-peer comparison", category: "Divergence")]
        public static UnitTestResult DumpDamage()
        {
            // Damage deserves its own line because it is the state that was
            // wrong for longest and the state a player notices first: a
            // building shown as broken on one screen and working on the other.
            int rows = 0;

            foreach (var hp in Object.FindObjectsByType<BuildingHP>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (hp == null || hp.gameObject == null) continue;
                if (hp.HitPoints >= hp.MaxHitPoints) continue;   // only what is damaged

                var identity = hp.gameObject.GetExistingNetIdentity();
                int netId = identity == null ? 0 : identity.NetId;

                DebugConsole.Log(
                    $"[DAMAGE] {netId}|{hp.gameObject.PrefabID()}|{Grid.PosToCell(hp.gameObject)}|" +
                    $"{hp.HitPoints}/{hp.MaxHitPoints}");
                rows++;
            }

            return UnitTestResult.Pass(rows == 0
                ? "nothing in this colony is damaged"
                : $"dumped {rows} damaged building(s)");
        }

        private static string Describe(Misc.Variant v)
        {
            switch (v.Type)
            {
                case Misc.Variant.TypeCode.Float:     return v.Float.ToString("0.###");
                case Misc.Variant.TypeCode.Int:       return v.Int.ToString();
                case Misc.Variant.TypeCode.Byte:      return v.Byte.ToString();
                case Misc.Variant.TypeCode.Boolean:   return v.Boolean ? "true" : "false";
                case Misc.Variant.TypeCode.String:    return v.String ?? "";
                // The blob's bytes legitimately differ between peers; its length
                // does not, so that is what gets compared.
                case Misc.Variant.TypeCode.ByteArray: return $"bytes:{v.ByteArray?.Length ?? 0}";
                default:                              return v.Type.ToString();
            }
        }
    }
}
