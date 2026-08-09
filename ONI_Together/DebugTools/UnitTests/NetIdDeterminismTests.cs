using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// AUDIT #3. These encode the property the NetId scheme has to hold and
    /// currently does not, so they are expected to fail until NetIdHelper is
    /// fixed. Red here is the reproduction; green is the regression gate.
    ///
    /// The mechanism: GetDeterministicWorkableId calls GetDeterministicEntityId
    /// with useCell:false, so the cell never enters a workable's hash. Objects
    /// with the same prefab, element, mass and temperature therefore collide on
    /// one base hash, and the breakoff loop hands out hash+0, hash+1, hash+2 ...
    /// in arrival order. Entity creation is not replicated, so the two peers'
    /// arrival orders are structurally independent - disagreement is certain,
    /// not probable.
    ///
    /// Every check runs off the live registry. Recomputing an id would be the
    /// most direct proof, but GetDeterministicWorkableId logs a "Registered
    /// workable" line on every call, and that would inject fake registrations
    /// into Player.log and corrupt selfcheck_log.py / diff_logs.py.
    /// </summary>
    public static class NetIdDeterminismTests
    {
        private struct Entry
        {
            public int NetId;
            public int Cell;
            public string Prefab;
            public string WorkableType;
        }

        private static List<Entry> CollectWorkables()
        {
            var list = new List<Entry>();
            foreach (var identity in NetworkIdentityRegistry.AllIdentities)
            {
                if (identity == null || identity.gameObject == null) continue;

                var go = identity.gameObject;
                if (!go.TryGetComponent<Workable>(out var workable)) continue;

                int cell = Grid.PosToCell(go);
                if (!Grid.IsValidCell(cell)) continue;

                list.Add(new Entry
                {
                    NetId = identity.NetId,
                    Cell = cell,
                    Prefab = go.PrefabID().ToString(),
                    WorkableType = workable.GetType().Name
                });
            }
            return list;
        }

        [UnitTest(name: "Workable id encodes its cell", category: "NetId")]
        public static UnitTestResult WorkableIdEncodesCell()
        {
            var entries = CollectWorkables();
            if (entries.Count < 4)
                return UnitTestResult.Fail($"only {entries.Count} registered workables - load a colony and dig a few tiles first");

            // If the cell were part of the hash, ids inside one (prefab, type)
            // group would be scattered. A run of consecutive ids covering several
            // distinct cells can only come from the breakoff counter.
            foreach (var group in entries.GroupBy(e => new { e.Prefab, e.WorkableType }))
            {
                var byId = group.OrderBy(e => e.NetId).ToList();
                int runStart = 0;
                for (int i = 1; i <= byId.Count; i++)
                {
                    bool consecutive = i < byId.Count && byId[i].NetId == byId[i - 1].NetId + 1;
                    if (consecutive) continue;

                    int runLength = i - runStart;
                    if (runLength >= 3)
                    {
                        var cells = byId.Skip(runStart).Take(runLength).Select(e => e.Cell).Distinct().Count();
                        if (cells > 1)
                        {
                            return UnitTestResult.Fail(
                                $"{group.Key.Prefab}/{group.Key.WorkableType}: {runLength} consecutive ids " +
                                $"({byId[runStart].NetId}..{byId[i - 1].NetId}) span {cells} distinct cells - " +
                                "the cell is not in the workable hash, ids come from arrival order");
                        }
                    }
                    runStart = i;
                }
            }

            return UnitTestResult.Pass($"no consecutive id run spans multiple cells across {entries.Count} workables");
        }

        [UnitTest(name: "One cell holds one id per workable type", category: "NetId")]
        public static UnitTestResult CellHoldsOneIdPerType()
        {
            var entries = CollectWorkables();
            if (entries.Count < 4)
                return UnitTestResult.Fail($"only {entries.Count} registered workables - load a colony and dig a few tiles first");

            // The same physical object must not be reachable under two addresses.
            // Anything sent under the stale one lands nowhere.
            foreach (var group in entries.GroupBy(e => new { e.Prefab, e.WorkableType, e.Cell }))
            {
                var ids = group.Select(e => e.NetId).Distinct().ToList();
                if (ids.Count > 1)
                {
                    return UnitTestResult.Fail(
                        $"{group.Key.Prefab}/{group.Key.WorkableType} at cell {group.Key.Cell} " +
                        $"is registered under {ids.Count} ids: {string.Join(", ", ids)}");
                }
            }

            return UnitTestResult.Pass($"every (prefab, type, cell) has exactly one id across {entries.Count} workables");
        }

        [UnitTest(name: "No registry lookup failures", category: "NetId")]
        public static UnitTestResult NoLookupFailures()
        {
            // Downstream symptom, and the cheapest divergence signal we have:
            // a packet arrived for a NetId this peer never registered.
            int fails = NetworkIdentityRegistry.LookupFailCount;
            if (fails > 0)
                return UnitTestResult.Fail(
                    $"{fails} failed registry lookups - packets are arriving for NetIds this peer never registered " +
                    $"(registry holds {NetworkIdentityRegistry.Count})");

            return UnitTestResult.Pass($"no failed lookups; registry holds {NetworkIdentityRegistry.Count}");
        }
    }
}
