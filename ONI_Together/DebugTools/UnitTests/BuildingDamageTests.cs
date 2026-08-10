using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Is every damaged building covered by something that will replicate it?
    ///
    /// Damage used to ride along inside StructureStatePacket, which covered only
    /// buildings that have a StructureSyncerBase, and only when that syncer's
    /// other state happened to change. Neither condition holds where it matters:
    /// a Tile has no syncer at all, and the storage, battery and reactor syncers
    /// skip their optional values when deciding whether anything changed - so
    /// their hit points moved without ever triggering a send.
    ///
    /// It went unnoticed because the earlier comparison ran against two
    /// snapshots taken seconds apart in a running colony, and the noise from
    /// that was the same size as the bug. With the boxes paused, one tile
    /// disagreed run after run, creeping from 43 to 51 hit points on the host
    /// while the client read it whole.
    /// </summary>
    public static class BuildingDamageTests
    {
        [UnitTest(name: "Damage replication has an owner on this peer", category: "Damage")]
        public static UnitTestResult SyncerExists()
        {
            if (BuildingDamageSyncer.Instance.IsNullOrDestroyed())
            {
                return UnitTestResult.Fail(
                    "no BuildingDamageSyncer - damage is only replicated for buildings that " +
                    "happen to have a structure syncer, which excludes tiles");
            }

            return UnitTestResult.Pass("BuildingDamageSyncer is present");
        }

        /// <summary>
        /// The coverage question stated directly: a damaged building with no
        /// NetId cannot be addressed, so nothing can ever replicate it.
        /// </summary>
        [UnitTest(name: "Every damaged building can be addressed", category: "Damage")]
        public static UnitTestResult DamagedBuildingsHaveIds()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            var unaddressable = new List<string>();
            int damaged = 0;

            foreach (var hp in Object.FindObjectsByType<BuildingHP>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (hp.IsNullOrDestroyed() || hp.gameObject.IsNullOrDestroyed()) continue;
                if (hp.HitPoints >= hp.MaxHitPoints) continue;

                damaged++;

                var identity = hp.gameObject.GetExistingNetIdentity();
                if (identity == null || identity.NetId == 0)
                    unaddressable.Add($"{hp.gameObject.PrefabID()}@{Grid.PosToCell(hp.gameObject)}");
            }

            if (unaddressable.Count > 0)
            {
                return UnitTestResult.Fail(
                    $"{unaddressable.Count} of {damaged} damaged buildings have no NetId, so their " +
                    "damage cannot be replicated: " + string.Join(", ", unaddressable.Take(8)));
            }

            string traffic = " :: " + Networking.Packets.World.BuildingDamagePacket.Describe()
                + " :: " + Patches.World.BuildingHP_OnDoBuildingDamage_Patch.Describe();
            return damaged == 0
                ? UnitTestResult.Pass("nothing damaged" + traffic)
                : UnitTestResult.Pass($"{damaged} damaged buildings, all addressable" + traffic);
        }

        /// <summary>
        /// The sweep should settle. A host that reports changes every sweep
        /// forever is either failing to deliver them or failing to record what
        /// it delivered - the two shapes this work has hit repeatedly.
        /// </summary>
        [UnitTest(name: "Damage sweep is not resending the same buildings forever", category: "Damage")]
        public static UnitTestResult SweepSettles()
        {
            var syncer = BuildingDamageSyncer.Instance;
            if (syncer.IsNullOrDestroyed()) return UnitTestResult.Skip("no syncer");
            if (!MultiplayerSession.IsHost) return UnitTestResult.Skip("only the host sweeps");
            if (syncer.LastSweepScanned == 0) return UnitTestResult.Skip("no sweep has run yet");

            // A colony at speed does damage things, so this is a ratio rather
            // than zero. Half the buildings changing every two seconds is not
            // damage, it is a syncer that never remembers what it sent.
            if (syncer.LastSweepChanged > syncer.LastSweepScanned / 2)
            {
                return UnitTestResult.Fail(
                    $"{syncer.LastSweepChanged} of {syncer.LastSweepScanned} buildings reported changed " +
                    "damage in one sweep - the syncer is not recording what it sent");
            }

            return UnitTestResult.Pass(
                $"{syncer.LastSweepChanged} changed of {syncer.LastSweepScanned} scanned; " +
                Networking.Packets.World.BuildingDamagePacket.Describe());
        }
    }
}
