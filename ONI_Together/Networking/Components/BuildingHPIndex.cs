using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    /// <summary>
    /// The colony's damageable buildings, kept as a list instead of found by scanning the
    /// scene every couple of seconds.
    ///
    /// This is the mod's largest measured cost, and it is all in one call. Two places ask
    /// Unity for every BuildingHP in the scene on a timer:
    ///
    ///   ClientDamageWatcher    every 5s     ~270 ms a scan
    ///   BuildingDamageSyncer   every 2s     ~83 ms a sweep, and it scans twice
    ///
    /// Those numbers are the mod's own profiler, read off a colony of about 1,400
    /// damageable buildings: 3,246 ms of client time per minute over twelve refreshes, and
    /// 2,480 ms of host time per minute over thirty sweeps.
    ///
    /// It does not show up in the average. The same colony measured 76.2-76.8 ms a frame
    /// with the session ended and 77.1-77.8 ms while hosting, about 1 ms - because the
    /// main thread spends most of a frame waiting on the native simulation, and work that
    /// fits inside that wait costs nothing in wall clock. What it does show up in is the
    /// worst frame: 84-87 ms solo against 250-475 ms in session. A quarter to half a second
    /// is a visible stutter, and a scan that overruns the wait is exactly what produces one.
    ///
    /// So the list is maintained as buildings come and go. Both consumers read it.
    ///
    /// A slow full rescan stays as a safety net, because an index maintained by patches is
    /// only as complete as the paths those patches cover, and this codebase has been caught
    /// by an exclusion rule that ninety-one call sites went around. Once a minute it scans
    /// properly, compares, and counts the difference - so "the index is complete" is a
    /// measurement rather than an assumption, and IndexMisses staying at 0 is what says the
    /// cheap path can be trusted.
    /// </summary>
    public static class BuildingHPIndex
    {
        private static readonly HashSet<BuildingHP> _live = new HashSet<BuildingHP>();

        /// <summary>Buildings the periodic audit found that the index had missed.</summary>
        public static int IndexMisses { get; private set; }

        /// <summary>Audits run, so a zero above can be told from "it never checked".</summary>
        public static int Audits { get; private set; }

        public static int Count => _live.Count;

        internal static void Add(BuildingHP hp)
        {
            if (hp.IsNullOrDestroyed()) return;
            _live.Add(hp);
        }

        /// <summary>
        /// The current buildings, into a caller-owned list so nothing allocates per call
        /// and nothing can mutate the index while it is being walked.
        /// </summary>
        public static void CopyTo(List<BuildingHP> into)
        {
            into.Clear();
            foreach (var hp in _live)
            {
                if (hp.IsNullOrDestroyed() || hp.gameObject.IsNullOrDestroyed()) continue;
                into.Add(hp);
            }
        }

        /// <summary>
        /// Rebuild from the scene and report what the index had missed.
        ///
        /// Deliberately rare - this is the expensive call the index exists to avoid, kept
        /// only so its absence can be checked. Call it about once a minute from something
        /// that already runs on that cadence.
        /// </summary>
        public static void Audit()
        {
            var found = Object.FindObjectsByType<BuildingHP>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            int missed = 0;
            foreach (var hp in found)
            {
                if (hp.IsNullOrDestroyed()) continue;
                if (_live.Add(hp)) missed++;
            }

            // Dead entries go too, so the index cannot grow without bound on a long session.
            _live.RemoveWhere(hp => hp.IsNullOrDestroyed() || hp.gameObject.IsNullOrDestroyed());

            Audits++;
            if (missed > 0)
            {
                IndexMisses += missed;
                DebugTools.ThrottledLog.Warn(
                    $"[BuildingHPIndex] the audit found {missed} damageable building(s) the index " +
                    $"had not seen - a spawn path is not going through OnSpawn ({IndexMisses} in total)");
            }
        }

        public static void Clear()
        {
            _live.Clear();
            IndexMisses = 0;
            Audits = 0;
        }

        [HarmonyPatch(typeof(BuildingHP), "OnSpawn")]
        public static class BuildingHP_OnSpawn_Patch
        {
            public static void Postfix(BuildingHP __instance) => Add(__instance);
        }

        // There is no OnCleanUp patch, and that is not an omission.
        //
        // One was written and Harmony reported "Patching exception in method null":
        // BuildingHP does not declare OnCleanUp at all. Guessing that it did cost a run.
        //
        // Removal does not need a hook. A destroyed building is filtered out on every read
        // by CopyTo, so no consumer ever sees one, and the audit purges the set outright
        // once a minute. The set can therefore hold dead entries for up to a minute and
        // that costs a null check per entry per read, which is what a HashSet of a few
        // thousand costs anyway.
        //
        // Measured: hpIndex 6,340 against 6,338 across two runs on the same colony, so it
        // is not growing without bound, and hpIndexMisses stayed 0 - the audit found
        // nothing the OnSpawn patch had missed.
    }
}
