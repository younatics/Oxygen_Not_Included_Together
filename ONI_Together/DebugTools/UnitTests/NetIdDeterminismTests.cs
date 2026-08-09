using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// AUDIT #3 - do the two peers address the same object by the same NetId?
    ///
    /// That is a cross-peer property, so no single-box assertion can settle it.
    /// What this class does instead is dump each peer's (path, prefab, cell) -> id
    /// table as [NETID] records and assert only the invariants that really are
    /// local. testing/netid_compare.py then compares two dumps exactly.
    ///
    /// An earlier version guessed instead: it flagged a run of consecutive ids
    /// covering several cells as proof that the cell was missing from the hash.
    /// That is true for the workable path, but NetworkIdentity.RegisterIdentity
    /// sends anything with a Building component to GetDeterministicBuildingId,
    /// which XORs the cell in - and XORing adjacent cells produces adjacent ids.
    /// So it reported every row of tiles as a bug. Heuristics that cannot tell a
    /// correct implementation from a broken one are worse than no test at all,
    /// hence the exact dump.
    /// </summary>
    public static class NetIdDeterminismTests
    {
        private struct Entry
        {
            public int NetId;
            public int Cell;
            public string Prefab;
            public string Kind;   // which NetIdHelper branch produced this id
        }

        /// <summary>
        /// Mirrors NetworkIdentity.RegisterIdentity's branch order. If that order
        /// ever changes this must follow, or the dump mislabels rows.
        /// </summary>
        private static string ClassifyKind(GameObject go)
        {
            if (go.TryGetComponent<Building>(out _)) return "building";
            if (go.TryGetComponent<Workable>(out var w)) return "workable:" + w.GetType().Name;
            return "entity";
        }

        private static List<Entry> Collect()
        {
            var list = new List<Entry>();
            foreach (var identity in NetworkIdentityRegistry.AllIdentities)
            {
                if (identity == null || identity.gameObject == null) continue;

                var go = identity.gameObject;
                int cell = Grid.PosToCell(go);
                if (!Grid.IsValidCell(cell)) continue;

                list.Add(new Entry
                {
                    NetId = identity.NetId,
                    Cell = cell,
                    Prefab = go.PrefabID().ToString(),
                    Kind = ClassifyKind(go)
                });
            }
            return list;
        }

        [UnitTest(name: "Dump the NetId table for cross-peer comparison", category: "NetId")]
        public static UnitTestResult DumpNetIdTable()
        {
            var entries = Collect();
            if (entries.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            // Sorted so two dumps line up without the comparer having to sort.
            foreach (var e in entries.OrderBy(e => e.Kind).ThenBy(e => e.Prefab).ThenBy(e => e.Cell))
                DebugConsole.Log($"[NETID] {e.Kind}|{e.Prefab}|{e.Cell}|{e.NetId}");

            return UnitTestResult.Pass($"dumped {entries.Count} identities");
        }

        [UnitTest(name: "One id per (prefab, kind, cell)", category: "NetId")]
        public static UnitTestResult OneIdPerLocation()
        {
            var entries = Collect();
            if (entries.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            // The same physical object reachable under two addresses means
            // anything sent under the stale one lands nowhere.
            foreach (var group in entries.GroupBy(e => new { e.Prefab, e.Kind, e.Cell }))
            {
                var ids = group.Select(e => e.NetId).Distinct().ToList();
                if (ids.Count > 1)
                    return UnitTestResult.Fail(
                        $"{group.Key.Prefab} ({group.Key.Kind}) at cell {group.Key.Cell} " +
                        $"has {ids.Count} ids: {string.Join(", ", ids)}");
            }

            return UnitTestResult.Pass($"every (prefab, kind, cell) owns exactly one id across {entries.Count} identities");
        }

        [UnitTest(name: "No two objects share a NetId", category: "NetId")]
        public static UnitTestResult NoSharedIds()
        {
            var entries = Collect();
            if (entries.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            // RegisterExisting silently skips an id that is already taken, so the
            // loser keeps a NetId that resolves to somebody else's object.
            var collision = entries
                .GroupBy(e => e.NetId)
                .FirstOrDefault(g => g.Select(e => new { e.Prefab, e.Cell }).Distinct().Count() > 1);

            if (collision != null)
            {
                var where = string.Join(", ", collision.Select(e => $"{e.Prefab}@{e.Cell}"));
                return UnitTestResult.Fail($"NetId {collision.Key} is claimed by several objects: {where}");
            }

            return UnitTestResult.Pass($"no shared ids across {entries.Count} identities");
        }

        [UnitTest(name: "No registry lookup failures", category: "NetId")]
        public static UnitTestResult NoLookupFailures()
        {
            // Downstream symptom, and the cheapest divergence signal there is:
            // a packet arrived for a NetId this peer never registered.
            if (NetworkIdentityRegistry.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            int fails = NetworkIdentityRegistry.LookupFailCount;
            if (fails > 0)
                return UnitTestResult.Fail(
                    $"{fails} failed registry lookups - packets are arriving for NetIds this peer never " +
                    $"registered (registry holds {NetworkIdentityRegistry.Count})");

            return UnitTestResult.Pass($"no failed lookups; registry holds {NetworkIdentityRegistry.Count}");
        }
    }
}
