using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Patches.World;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    /// <summary>
    /// Watches hit points fall on a client and says whether anything asked for it.
    ///
    /// One tile keeps losing health on the client while the host holds it whole,
    /// and it comes back faster than reconciliation removes it. Both methods that
    /// can reduce BuildingHP are patched and refuse local damage: the event fires
    /// constantly and is refused about 190 times a run, and DoDamage has never
    /// fired once. So the number moves by a third route, and every guess about
    /// which one has been wrong - the field name took three guesses, the event
    /// payload took two, and this session's rule by now is to enumerate rather
    /// than speculate.
    ///
    /// This samples hit points directly and reports drops that arrived while
    /// neither patch saw anything. That distinguishes "something is calling a
    /// method I have not patched" from "the value is being written behind both of
    /// them", which are different bugs with different fixes and look identical
    /// from the comparison output.
    ///
    /// Bounded on purpose: a rotating window rather than a full scan per frame, and
    /// a handful of recorded examples rather than a line per drop. Per-cell logging
    /// froze a host earlier in this work.
    /// </summary>
    public class ClientDamageWatcher : MonoBehaviour
    {
        public static ClientDamageWatcher Instance { get; private set; }

        /// <summary>Buildings inspected per frame. A full pass over a large colony lands inside a second.</summary>
        private const int PerFrame = 256;

        /// <summary>How long a cached component list is trusted before it is rebuilt.</summary>
        private const float RefreshInterval = 5f;

        private readonly System.Collections.Generic.List<BuildingHP> _indexScratch = new System.Collections.Generic.List<BuildingHP>();
        private float _nextRefresh;
        private int _cursor;

        private readonly Dictionary<int, int> _lastHp = new Dictionary<int, int>();

        private long _lastEventActivity;

        public int Drops { get; private set; }
        public int UnexplainedDrops { get; private set; }

        /// <summary>
        /// Buildings whose hit points are remembered. Reported by the health row:
        /// this keeps an entry per NetId and never drops one, so it is a table
        /// that can only grow, and a growth curve should say by how much.
        /// </summary>
        public int TrackedCount => _lastHp.Count;
        private readonly List<string> _examples = new List<string>();

        private void OnEnable() => Instance = this;

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        public void ResetForNewSession()
        {
            _lastHp.Clear();
            _examples.Clear();
            Drops = 0;
            UnexplainedDrops = 0;
            _cursor = 0;
            _nextRefresh = 0f;
            _indexScratch.Clear();
        }

        public string Describe()
        {
            string examples = _examples.Count == 0 ? "none" : string.Join(" ", _examples);
            return $"drops={Drops} unexplained={UnexplainedDrops} examples=[{examples}]";
        }

        private void Update()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient)
                return;
            if (Grid.WidthInCells == 0)
                return;

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + RefreshInterval;

                // From the index rather than from the scene.
                //
                // This line was Object.FindObjectsByType<BuildingHP>() and it was the
                // single most expensive thing the mod did on a client: 3,246 ms a minute
                // over twelve refreshes, about 270 ms each, on a colony of some 1,400
                // damageable buildings. It never showed in the average frame time - work
                // that fits inside the wait on the native simulation costs nothing in wall
                // clock - but it is most of the difference between a worst frame of 84 ms
                // with the session ended and 250 to 475 ms while connected, which is the
                // stutter a player actually feels.
                //
                // The examination below was already spread over frames. Only the rescan
                // was not, and a rescan is exactly the thing that does not need doing.
                BuildingHPIndex.CopyTo(_indexScratch);
                if (_cursor >= _indexScratch.Count) _cursor = 0;
            }

            if (_indexScratch.Count == 0)
                return;

            // Whether either patch saw anything at all since the last sample. A
            // drop with no activity on either is one that bypassed both.
            long activity = BuildingHP_OnDoBuildingDamage_Patch.SuppressedCount
                          + BuildingHP_OnDoBuildingDamage_Patch.AllowedCount
                          + BuildingHP_OnDoBuildingDamage_Patch.DirectSuppressed;
            bool eventsQuiet = activity == _lastEventActivity;
            _lastEventActivity = activity;

            int examined = 0;
            while (examined < PerFrame && examined < _indexScratch.Count)
            {
                if (_cursor >= _indexScratch.Count) _cursor = 0;
                var hp = _indexScratch[_cursor++];
                examined++;

                if (hp.IsNullOrDestroyed() || hp.gameObject.IsNullOrDestroyed())
                    continue;

                var identity = hp.gameObject.GetExistingNetIdentity();
                if (identity == null || identity.NetId == 0)
                    continue;

                int current = hp.HitPoints;
                if (!_lastHp.TryGetValue(identity.NetId, out int previous))
                {
                    _lastHp[identity.NetId] = current;
                    continue;
                }

                if (current >= previous)
                {
                    _lastHp[identity.NetId] = current;
                    continue;
                }

                Drops++;
                if (eventsQuiet)
                {
                    UnexplainedDrops++;
                    if (_examples.Count < 4)
                    {
                        _examples.Add(
                            $"{hp.gameObject.PrefabID()}@{Grid.PosToCell(hp.gameObject)}:{previous}->{current}");
                        DebugConsole.Log(
                            $"[DamageWatch] {hp.gameObject.PrefabID()} at {Grid.PosToCell(hp.gameObject)} " +
                            $"lost {previous - current} hit points with neither damage patch seeing anything");
                    }
                }

                _lastHp[identity.NetId] = current;
            }
        }
    }
}
