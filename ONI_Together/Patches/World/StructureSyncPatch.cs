using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Components.StructureStateSyncers;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
    [HarmonyPatch(typeof(Battery), nameof(Battery.OnSpawn))]
    public static class BatterySpawnPatch
    {
        public static void Postfix(Battery __instance)
        {
            using var _ = Profiler.Scope();

            BatteryStateSyncer syncer = __instance.gameObject.AddOrGet<BatteryStateSyncer>();
        }
    }

    [HarmonyPatch(typeof(Generator), nameof(Generator.OnSpawn))]
    public static class GeneratorSpawnPatch
    {
        public static void Postfix(Generator __instance)
        {
            using var _ = Profiler.Scope();

            if (__instance.gameObject.TryGetComponent<EnergyGenerator>(out var egen))
            {
                EnergyGeneratorSyncer egenSyncer = __instance.gameObject.AddOrGet<EnergyGeneratorSyncer>();
                return;
            }

            GenericGeneratorSyncer syncer = __instance.gameObject.AddOrGet<GenericGeneratorSyncer>();
        }
    }


    /// <summary>
    /// Attach storage syncing to every building that holds inventory.
    ///
    /// It used to be attached to four hand-listed types - StorageLocker, RationBox,
    /// CargoBay, CargoBayCluster - with a note saying a general patch was "not
    /// scalable". Everything else with a Storage therefore had no storage syncing at
    /// all, and a cross-peer comparison of container contents named exactly that gap:
    /// 96 containers where both peers agreed on the item count and disagreed about the
    /// mass, and 57 the host had and the client did not. Every single divergent
    /// container was a type absent from those four lines - Refrigerator, SuitLocker,
    /// Compost, PlanterBox, AlgaeHabitat. The SuitLocker held 143 kg of oxygen on the
    /// host and 171 on the client, which is a fifth of its contents and something a
    /// player reads straight off the building.
    ///
    /// Buildings only, and not pipe machinery. A pump or a shower holds an element
    /// object in its internal storage for a fraction of a tick - the dump found them
    /// at 0 kg - and nothing addresses those: no duplicant fetches from a pump. Worse,
    /// rebuilding their contents from a packet would fight ConduitFlowSyncer over the
    /// same mass. So a storage sitting on a ConduitConsumer or a Conduit is left alone.
    ///
    /// Whether that exclusion is drawn in the right place is a measurement, not an
    /// argument: the [STORE] dump reports each container's conduit components and
    /// capacity, so anything that slips through is named by the next run rather than
    /// reasoned about. The same goes for the scalability claim - StructureSyncerBase
    /// costs appear per syncer in the HEALTH row, next to BuildingDamageSyncer which
    /// really does cost 2 seconds per 3247 frames.
    /// </summary>
    [HarmonyPatch(typeof(Storage), nameof(Storage.OnSpawn))]
    public static class Storage_OnSpawn_AttachSyncer_Patch
    {
        /// <summary>Storages given a syncer, and storages skipped, for the health row.</summary>
        public static int Attached { get; private set; }
        public static int SkippedConduit { get; private set; }
        public static int SkippedNotBuilding { get; private set; }

        public static void Postfix(Storage __instance)
        {
            using var _ = Profiler.Scope();

            if (__instance.IsNullOrDestroyed() || __instance.gameObject.IsNullOrDestroyed())
                return;

            var go = __instance.gameObject;

            // A duplicant's own storage, a critter's, a plant's: not a building, and
            // the objects inside them are replicated as carried items already.
            if (!go.TryGetComponent<Building>(out var building) || building.IsNullOrDestroyed())
            {
                SkippedNotBuilding++;
                return;
            }

            // Pipe machinery, but only the part of it a duplicant cannot reach into.
            //
            // The first version excluded anything with a ConduitConsumer, and the
            // measurement immediately named the cost: a SuitLocker has a gas pipe input
            // and was skipped, so it held 143 kg of oxygen on the host and 171 on the
            // client - a fifth of its contents, on a building the player clicks to read
            // that number. Having a pipe does not make something machinery; a suit
            // locker, a bottle emptier and a ration box with plumbing are all inventory.
            //
            // What separates the two is whether a duplicant can take things out of it. A
            // pump's internal buffer holds an element object for part of a tick - the
            // dump found those at 0 kg - and nothing fetches from a pump, so replicating
            // it would only fight ConduitFlowSyncer over the same mass.
            //
            // allowItemRemoval was tried as ONI's own answer to that question and it is
            // not one: a SuitLocker reports false - nothing fetches oxygen out of it,
            // the suit uses it in place - so it stayed excluded and stayed 28 kg apart.
            // ConduitConsumer was no better: having a pipe input is what a shower, a
            // suit locker and a bottle emptier all have, and all three are inventory.
            //
            // So the exclusion is now only an actual pipe segment, and the machine
            // buffer problem is solved where it belongs instead - the sender never
            // describes a massless entry, so the receiver no longer deletes one. That
            // makes syncing a pump harmless rather than something to be avoided by
            // guessing which components mean "machine".
            if (go.TryGetComponent<Conduit>(out var conduit) && !conduit.IsNullOrDestroyed())
            {
                SkippedConduit++;
                return;
            }

            go.AddOrGet<StorageStateSyncer>();
            Attached++;
        }
    }

    /* Not scalable, patch buildings that we want storage syncing on
    [HarmonyPatch(typeof(Storage), nameof(Storage.OnSpawn))]
    public static class StorageLocker_OnSpawn_Patch
    {
        public static void Postfix(Storage __instance)
        {
            using var _ = Profiler.Scope();
            StorageStateSyncer syncer = __instance.gameObject.AddOrGet<StorageStateSyncer>();
        }
    }

    }
    */

    /// <summary>
    /// Attach the player-set-flag syncer wherever such a flag exists.
    ///
    /// Held back once already: attaching it took a client from zero errors to 149-229 a
    /// run, all "[Storage/RebuildStorageFromData] Key: stor not found", because
    /// StructureStatePacket was identified by NetId alone and the receiver handed every
    /// packet to every syncer on the building. Most machines have both a storage and an
    /// enable toggle, so the storage syncer spent the run reading flag packets.
    ///
    /// The packet now carries the name of the syncer that produced it and is routed to
    /// the matching one, so a building may hold several. That was the only thing in the
    /// way.
    ///
    /// What it is for, measured rather than argued: a cross-peer state comparison found
    /// PressureDoor@43123 Locked on the host and Opened on the client, two more doors the
    /// same, and a Generator and an IceCooledFan paused on one side and running on the
    /// other. Those states are synced by event patches with no way to look again, so a
    /// single missed packet is permanent - and an airlock that is open on one peer and
    /// shut on the other is not a cosmetic difference.
    ///
    /// Triggered by the state rather than by a list of building types: whatever declares
    /// a BuildingEnabledButton, a Door or a ManualDeliveryKG has something a player sets
    /// and therefore something that can drift. All three declare their own OnSpawn -
    /// checked with the api verb, because patching an inherited KMonoBehaviour.OnSpawn
    /// would attach this to every object in the game.
    /// </summary>
    [HarmonyPatch]
    public static class BuildingFlagsAttachPatch
    {
        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            using var _ = Profiler.Scope();

            var component = __instance as KMonoBehaviour;
            if (component.IsNullOrDestroyed() || component.gameObject.IsNullOrDestroyed()) return;

            component.gameObject.AddOrGet<BuildingFlagsSyncer>();
        }

        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            const string name = nameof(KMonoBehaviour.OnSpawn);
            yield return AccessTools.Method(typeof(BuildingEnabledButton), name);
            yield return AccessTools.Method(typeof(Door), name);
            yield return AccessTools.Method(typeof(ManualDeliveryKG), name);
        }
    }

    [HarmonyPatch(typeof(FlushToilet), nameof(FlushToilet.OnSpawn))]
    public static class FlushToiletSpawnPatch
    {
        public static void Postfix(FlushToilet __instance)
        {
            using var _ = Profiler.Scope();
            __instance.gameObject.AddOrGet<ToiletSyncer>();
        }
    }

    [HarmonyPatch(typeof(Toilet), nameof(Toilet.OnSpawn))]
    public static class ToiletSpawnPatch
    {
        public static void Postfix(Toilet __instance)
        {
            using var _ = Profiler.Scope();
            __instance.gameObject.AddOrGet<ToiletSyncer>();
        }
    }
    
    [HarmonyPatch(typeof(Reactor), nameof(Reactor.OnSpawn))]
    public static class ReactorSpawnPatch
    {
        public static void Postfix(Reactor __instance)
        {
            using var _ = Profiler.Scope();
            __instance.gameObject.AddOrGet<ReactorStateSyncer>();
        }
    }
}
