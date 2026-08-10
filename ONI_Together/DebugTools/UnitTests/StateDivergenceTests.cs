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

        [UnitTest(name: "Dump duplicant position state for cross-peer comparison", category: "Divergence")]
        public static UnitTestResult DumpMinions()
        {
            // One duplicant out of twenty two never receives a position, and the
            // ids match on both peers, so the usual explanations are already
            // ruled out. Guessing further is what this project keeps paying for;
            // this states what each peer holds so the two can be laid side by
            // side.
            int rows = 0;

            foreach (var identity in NetworkIdentityRegistry.AllIdentities.ToList())
            {
                if (identity == null || identity.gameObject == null) continue;

                var go = identity.gameObject;
                if (!go.HasTag(GameTags.BaseMinion)) continue;

                var handler = go.GetComponent<EntityPositionHandler>();
                string stamp = handler == null
                    ? "no-handler"
                    : (handler.serverTimestamp == 0 ? "never" : handler.serverTimestamp.ToString());

                DebugConsole.Log(
                    $"[MINION] {identity.NetId}|{go.GetProperName()}|{Grid.PosToCell(go)}|" +
                    $"active={go.activeInHierarchy}|handler={(handler != null)}|" +
                    $"enabled={(handler != null && handler.isActiveAndEnabled)}|recv={stamp}");
                rows++;
            }

            return rows == 0
                ? UnitTestResult.Skip("no duplicants in the registry")
                : UnitTestResult.Pass($"dumped {rows} duplicant(s)");
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

        /// <summary>
        /// Total mass per element, rounded, in a fixed order. That is what the
        /// two peers should agree about; how many separate chunks it is spread
        /// across is an artefact of how it was rebuilt.
        /// </summary>
        private static string SummariseStorage(byte[] blob)
        {
            if (blob == null || blob.Length < 8) return "empty";

            try
            {
                using var ms = new System.IO.MemoryStream(blob);
                using var reader = new System.IO.BinaryReader(ms);

                reader.ReadSingle();                 // capacity
                int count = reader.ReadInt32();

                var massByElement = new Dictionary<int, float>();
                for (int i = 0; i < count; i++)
                {
                    int hash = reader.ReadInt32();
                    float mass = reader.ReadSingle();
                    reader.ReadSingle();             // temperature
                    reader.ReadByte();               // disease index
                    reader.ReadInt32();              // disease count

                    massByElement.TryGetValue(hash, out float running);
                    massByElement[hash] = running + mass;
                }

                if (massByElement.Count == 0) return "empty";

                // Rounded to a tenth of a kilogram: the two peers sample at
                // different instants and a toilet fills continuously, so exact
                // equality would be noise, not a finding.
                return string.Join(",", massByElement
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => $"{kvp.Key}:{kvp.Value:0.0}"));
            }
            catch
            {
                return $"unreadable:{blob.Length}";
            }
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
                // Storage is summarised by what it holds, not by how it is
                // packed. Length is the wrong comparison: Storage.AddElement
                // merges a new chunk into an existing one of the same element,
                // so a host holding two piles of water rebuilds on the client as
                // one. Same contents, same mass, different item count - and
                // comparing bytes reported that as a divergence on the first run
                // this ever did. A comparison that cries wolf stops being read.
                case Misc.Variant.TypeCode.ByteArray: return SummariseStorage(v.ByteArray);
                default:                              return v.Type.ToString();
            }
        }
    }
}
