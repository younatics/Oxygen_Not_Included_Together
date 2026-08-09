using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
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
            // Mobility is tagged because it decides how to read a cell
            // mismatch. A building in a different cell on the two peers is a
            // bug; a critter or a hauled ore pile in a different cell is just
            // where it happens to be standing. Without the tag the comparer
            // cannot tell those apart, and neither can a reader.
            string prefix = IsMobile(go) ? "mobile/" : "";

            if (go.TryGetComponent<Building>(out _)) return prefix + "building";
            if (go.TryGetComponent<Workable>(out var w)) return prefix + "workable:" + w.GetType().Name;
            return prefix + "entity";
        }

        /// <summary>
        /// Anything that walks. Its cell is where it happened to be standing
        /// when the dump ran, so cell-based invariants do not apply to it.
        /// </summary>
        private static bool IsMobile(GameObject go)
            => go.TryGetComponent<Navigator>(out _) || go.TryGetComponent<MinionIdentity>(out _);

        private static List<Entry> Collect(bool staticOnly = false)
        {
            var list = new List<Entry>();
            foreach (var identity in NetworkIdentityRegistry.AllIdentities)
            {
                if (identity == null || identity.gameObject == null) continue;

                var go = identity.gameObject;
                if (staticOnly && IsMobile(go)) continue;

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
            // Static objects only. Three duplicants standing in one cell hold
            // three ids, which is correct and which this reported as a bug -
            // the same mistake as the consecutive-id heuristic it replaced:
            // an invariant applied to things it was never about.
            var entries = Collect(staticOnly: true);
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

        [UnitTest(name: "Every critter has an identity", category: "NetId")]
        public static UnitTestResult CrittersAreIdentified()
        {
            // A creature with NetId 0 cannot be spoken about at all: not its
            // position, not its animation, not the fact that it was moved. A
            // host logged "no netId found on" sixteen times for pokeshells and
            // juveniles, and moving one into a ranch never reached the client
            // because there was no address to send it under.
            //
            // Counted over the live world rather than asserted at spawn,
            // because the failure was never a broken spawn path - it was a
            // creature that arrived through a path nobody had hooked.
            var missing = new Dictionary<string, int>();
            int total = 0;

            foreach (var creature in UnityEngine.Object.FindObjectsByType<KPrefabID>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (creature == null) continue;
                var go = creature.gameObject;
                if (!go.HasTag(GameTags.Creature) || go.HasTag(GameTags.BaseMinion))
                    continue;

                total++;
                var identity = go.GetComponent<NetworkIdentity>();
                if (identity != null && identity.NetId != 0)
                    continue;

                string name = go.PrefabID().ToString();
                missing.TryGetValue(name, out int n);
                missing[name] = n + 1;
            }

            if (total == 0)
                return UnitTestResult.Skip("no creatures in this world");

            if (missing.Count > 0)
            {
                var worst = missing.OrderByDescending(kvp => kvp.Value)
                                   .Take(5)
                                   .Select(kvp => $"{kvp.Key} x{kvp.Value}");
                int sum = missing.Values.Sum();
                return UnitTestResult.Fail(
                    $"{sum} of {total} creatures have no NetId: {string.Join(", ", worst)}. " +
                    "The host cannot report their position or actions, so they will not move on a client.");
            }

            return UnitTestResult.Pass($"all {total} creatures carry an id");
        }

        [UnitTest(name: "No identity was minted just to send a packet", category: "NetId")]
        public static UnitTestResult NoLazyIdentities()
        {
            // A lazily attached identity is one-sided by construction. The peer
            // about to send asks for one and gets it; the peer holding the same
            // object never asks, so it has no address to resolve the packet
            // against, and everything sent about that object is dropped.
            //
            // Building sites were exactly this: 221 distinct Constructables
            // unresolvable on a client, 2523 times, while neither peer had ever
            // registered one. Anything listed here wants attaching at spawn on
            // both peers, the way BuildingSpawnPatch does it.
            var lazy = NetworkIdentity.LazyIdentities;
            if (lazy.Count == 0)
                return UnitTestResult.Pass("every identity was attached at spawn");

            var worst = lazy.OrderByDescending(kvp => kvp.Value)
                            .Take(5)
                            .Select(kvp => $"{kvp.Key} x{kvp.Value}");
            return UnitTestResult.Fail(
                $"{lazy.Count} prefabs only got an identity when a packet needed one: {string.Join(", ", worst)}");
        }

        [UnitTest(name: "No registry lookup failures", category: "NetId")]
        public static UnitTestResult NoLookupFailures()
        {
            // Downstream symptom, and the cheapest divergence signal there is:
            // a packet arrived for a NetId this peer never registered.
            if (NetworkIdentityRegistry.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            // Reported separately, because they are different bugs. A lookup
            // for id 0 is a sender that left a field unset; a lookup for a real
            // id that is not here is two peers disagreeing about an object.
            int unset = NetworkIdentityRegistry.UnsetIdLookupCount;
            if (unset > 0)
                return UnitTestResult.Fail(
                    $"{unset} packets arrived carrying NetId 0 - a sender is not filling the id in");

            int fails = NetworkIdentityRegistry.LookupFailCount;
            if (fails > 0)
                return UnitTestResult.Fail(
                    $"{fails} failed registry lookups - packets are arriving for NetIds this peer never " +
                    $"registered (registry holds {NetworkIdentityRegistry.Count})");

            return UnitTestResult.Pass($"no failed lookups; registry holds {NetworkIdentityRegistry.Count}");
        }
    }
}
