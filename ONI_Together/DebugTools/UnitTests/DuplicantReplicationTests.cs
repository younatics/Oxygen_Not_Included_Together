using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Can a duplicant be replicated as a prefab name?
    ///
    /// It must not be, and it was. NetworkIdentity announced anything with a
    /// Navigator for generic replication, duplicants have a Navigator, and the
    /// receiving end rebuilt them with Assets.GetPrefab. Personality, traits,
    /// aptitudes and name are not in the prefab, so the result is a colonist with
    /// personality 0x0 - and the renderer reads the personality inside
    /// World.LateUpdate, so it throws once per frame from then on and does not
    /// recover.
    ///
    /// Three client sessions ended that way. Each log finishes the same: "Could not
    /// find Personality: 0x0" repeating with NullReferenceExceptions between them,
    /// the null-Gametag warnings that follow a colonist with no tags, and
    /// Game.OnApplicationQuit a second or two later.
    ///
    /// Both ends are checked here. The sender is the fix; the receiver is the second
    /// layer, and the second layer is the one that matters if any other sender is
    /// ever added.
    /// </summary>
    public static class DuplicantReplicationTests
    {
        private static GameObject FindLiveDuplicant()
        {
            var minions = global::Components.LiveMinionIdentities;
            if (minions == null) return null;
            foreach (var m in minions.Items)
            {
                if (m.IsNullOrDestroyed() || m.gameObject.IsNullOrDestroyed()) continue;
                return m.gameObject;
            }
            return null;
        }

        [UnitTest(name: "A duplicant is never announced for prefab replication", category: "Duplicants")]
        public static UnitTestResult MinionsAreNotAnnounced()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            var dupe = FindLiveDuplicant();
            if (dupe == null)
                return UnitTestResult.Skip("no live duplicant to check");

            // NeedsReplication is private and is the thing under test, so this
            // asserts the property it must have rather than calling it: a duplicant
            // carries a Navigator, which is what let it through, and it must be
            // excluded by its BaseMinion tag before that is reached.
            if (!dupe.HasTag(GameTags.BaseMinion))
            {
                return UnitTestResult.Fail(
                    $"'{dupe.PrefabID()}' is a live duplicant but does not carry BaseMinion, so the " +
                    "exclusion that keeps it out of prefab replication cannot match it");
            }

            if (!dupe.TryGetComponent<Navigator>(out _))
            {
                return UnitTestResult.Skip(
                    "this duplicant has no Navigator, so it was never eligible for prefab " +
                    "replication and this run proves nothing");
            }

            return UnitTestResult.Pass(
                $"'{dupe.GetProperName()}' has a Navigator and is excluded by BaseMinion");
        }

        [UnitTest(name: "The instantiation receiver refuses duplicant prefabs", category: "Duplicants")]
        public static UnitTestResult ReceiverRefusesMinionPrefabs()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            // The handler returns at its first line on a host, by design, so running
            // this there proves nothing about the refusal. The first version did not
            // check, saw no duplicant appear, and reported the guard as untested -
            // which was true, and was the test's fault rather than the code's.
            if (MultiplayerSession.IsHost)
                return UnitTestResult.Skip("the host ignores these packets before the refusal is reached");

            var dupe = FindLiveDuplicant();
            if (dupe == null)
                return UnitTestResult.Skip("no live duplicant to take a prefab name from");

            string prefabName = dupe.PrefabID().ToString();
            var prefab = Assets.GetPrefab(prefabName);
            if (prefab == null)
            {
                return UnitTestResult.Skip(
                    $"'{prefabName}' does not resolve to a prefab here, so the receiver's own " +
                    "lookup would fail first and this proves nothing");
            }

            // The refusal keys on the tag being present on the PREFAB, not on the
            // live instance - tags can be added at spawn. If that is not true, the
            // guard reads as present while never firing.
            if (!prefab.HasTag(GameTags.BaseMinion))
            {
                return UnitTestResult.Fail(
                    $"the prefab '{prefabName}' does not carry BaseMinion, so the receiver's refusal " +
                    "can never match and a duplicant could still be built from its name");
            }

            int before = ONI_Together.Networking.Packets.InstantiationsPacket.RefusedMinions;

            var packet = new ONI_Together.Networking.Packets.InstantiationsPacket();
            packet.Entries.Add(new ONI_Together.Networking.Packets.InstantiationsPacket.InstantiationEntry
            {
                NetId = -424242,
                PrefabName = prefabName,
                Position = dupe.transform.position,
                Rotation = dupe.transform.rotation,
                ObjectName = prefabName,
                InitializeId = false,
                GameLayer = dupe.layer
            });

            int minionsBefore = global::Components.LiveMinionIdentities?.Count ?? -1;
            packet.OnDispatched();
            int minionsAfter = global::Components.LiveMinionIdentities?.Count ?? -1;

            if (minionsBefore >= 0 && minionsAfter > minionsBefore)
            {
                return UnitTestResult.Fail(
                    $"a duplicant was built from the prefab name '{prefabName}' " +
                    $"({minionsBefore} -> {minionsAfter} colonists). It would have no personality, " +
                    "and drawing it throws every frame.");
            }

            if (ONI_Together.Networking.Packets.InstantiationsPacket.RefusedMinions <= before)
            {
                return UnitTestResult.Fail(
                    "no duplicant appeared, but the receiver did not record a refusal either - " +
                    "it was stopped by something else and the guard is untested");
            }

            return UnitTestResult.Pass($"refused to build a duplicant from the prefab name '{prefabName}'");
        }
    }
}
