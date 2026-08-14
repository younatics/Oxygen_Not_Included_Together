using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using UnityEngine;
using static LogicGateVisualizer;

namespace ONI_Together.Misc
{
    public static class BuildingUtils
    {
        public static bool ValidCell(GameObject visualizer, BuildingDef def, int cell, Orientation orientation)
        {
            if (Grid.IsValidCell(cell)
                && Grid.IsVisible(cell))
            {
                bool IsValidPlaceLocation = def.IsValidPlaceLocation(visualizer, cell, orientation, out string failReason);
                bool IgnorableFailReason =
                    failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_WALL
                    || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER
                    || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER_FLOOR
                    || (failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_BACK_WALL_REQUIRED);

                bool validCell = (IsValidPlaceLocation || IgnorableFailReason);
                bool replacement = false;
                return (validCell || replacement);
            }

            return false;
        }

        public static void EncodeStorageContents(Storage storage, Dictionary<string, Variant> optionalValues, string keyPrefix = "")
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(storage.capacityKg);

            var validItems = new List<GameObject>();
            for (int i = 0; i < storage.items.Count; i++)
            {
                var go = storage.items[i];
                if (go == null) continue;
                var pe = go.GetComponent<PrimaryElement>();
                if (pe == null || pe.Mass <= 0f) continue;
                if (!go.TryGetComponent<KPrefabID>(out _)) continue;

                // An assigned object is an entity, not bulk mass.
                //
                // This blob is applied by clearing the container and rebuilding it, which
                // is fine for a pile of dirt and destructive for anything carrying state of
                // its own. An atmo suit has an owner, a durability and its own oxygen, and
                // the rebuild replaced it with a fresh one: the client log shows
                // "'Atmo_Suit' created ... by GameUtil.KInstantiate" next to
                // "[Assignable_Unassign_Patch] Unassigned Atmo_Suit". The assignment was
                // being lost every time the locker resynced.
                //
                // It also closed the game. ONI's status item on the locker still pointed at
                // the suit that had just been deleted, so hovering over the locker threw
                // once per frame - measured on the client in six runs out of six, and in a
                // live session it ended the session.
                //
                // Assignable is the line because assignment is exactly the per-instance
                // state a rebuild cannot reproduce. Such items are left out of the blob
                // entirely, so the receiver has nothing to act on and cannot delete them.
                if (IsEntityNotContents(go))
                {
                    EntitiesLeftAlone++;
                    continue;
                }

                validItems.Add(go);
            }

            // One record per prefab, with the total mass. Not one per object.
            //
            // Per-object records cannot be applied. The receiver recreates items with
            // Storage.Store, and ONI stacks identical food and seeds into a single
            // object, so a refrigerator holding three MushBar on the host became one on
            // the client - measured: host x3 client x1, host x4 client x1, seven
            // containers in one run.
            //
            // The count then never matches, TryUpdateInPlace can never succeed, and the
            // storage is cleared and rebuilt on every packet for the rest of the
            // session. That is the pathology this file already carries a note about: a
            // client registered the same water pile 2109 times in 29 minutes, 14655 of
            // its 19106 workable registrations. It came back the moment storage syncing
            // reached containers that hold stackable items.
            //
            // Per-prefab totals are also the honest unit. How many objects a pile of
            // 12 kg of dirt is split into is a local detail neither peer can control;
            // that there is 12 kg of dirt in this box is what the player reads and what
            // every gameplay decision depends on.
            var byPrefab = new Dictionary<int, StoredItem>();
            var order = new List<int>();
            foreach (var go in validItems)
            {
                var pe = go.GetComponent<PrimaryElement>();
                int hash = go.GetComponent<KPrefabID>().PrefabTag.GetHashCode();

                if (!byPrefab.TryGetValue(hash, out var acc))
                {
                    order.Add(hash);
                    acc = new StoredItem { Hash = hash, DiseaseIdx = pe.DiseaseIdx };

                    // The first object of this prefab lends its address to the record.
                    // The receiver rebuilds one object per prefab, so one id is what it
                    // needs and what it can use.
                    if (go.TryGetNetIdentity(out var storedIdentity) && !storedIdentity.IsNullOrDestroyed())
                        acc.NetId = storedIdentity.NetId;
                }

                // Mass-weighted temperature, so merging two piles does not invent heat.
                float newMass = acc.Mass + pe.Mass;
                acc.Temperature = newMass > 0f
                    ? (acc.Temperature * acc.Mass + pe.Temperature * pe.Mass) / newMass
                    : pe.Temperature;
                acc.Mass = newMass;
                acc.DiseaseCount += pe.DiseaseCount;

                // Any disease beats none. Two different diseases in one prefab's pile is
                // not representable here and is not worth a wider format - the receiver
                // applies one.
                if (acc.DiseaseIdx == byte.MaxValue) acc.DiseaseIdx = pe.DiseaseIdx;

                byPrefab[hash] = acc;
            }

            writer.Write(order.Count);
            foreach (int hash in order)
            {
                var item = byPrefab[hash];
                writer.Write(item.Hash);
                writer.Write(item.Mass);
                writer.Write(item.Temperature);
                writer.Write(item.DiseaseIdx);
                writer.Write(item.DiseaseCount);
                writer.Write(item.NetId);
            }

            optionalValues[keyPrefix + "stor"] = ms.ToArray();
        }

        public static void RebuildStorageFromData(Storage storage, Dictionary<string, Variant> data, string keyPrefix = "", string diseaseReason = "Multiplayer Sync")
        {
            if (storage == null) return;

            if (data.TryGetValue(keyPrefix + "stor", out var blobVar) && blobVar.ByteArray != null)
            {
                RebuildFromBlob(storage, blobVar.ByteArray, diseaseReason);
                return;
            }
            
            DebugConsole.LogError($"[Storage/RebuildStorageFromData] Failed to rebuild storage from data! Key: {keyPrefix + "stor"} not found!");
        }

        private struct StoredItem
        {
            public int Hash;

            /// <summary>
            /// The address the sender's own object for this prefab holds, or zero.
            ///
            /// One record still covers every object of a prefab in the container - going
            /// back to per-object records is the pathology this format exists to avoid,
            /// and it cost a client 14,655 spurious registrations. This is one extra
            /// field on that record, so the receiver can name what it rebuilds instead
            /// of computing an address the other peer cannot reproduce.
            ///
            /// It is the last measured id divergence: BasicPlantBar and BasicPlantFood,
            /// 2 of 9,009 shared objects, both food recreated inside a MicrobeMusher.
            /// </summary>
            public int NetId;
            public float Mass;
            public float Temperature;
            public byte DiseaseIdx;
            public int DiseaseCount;
        }

        private static readonly List<StoredItem> _incoming = new List<StoredItem>();

        private static void RebuildFromBlob(Storage storage, byte[] blob, string diseaseReason)
        {
            using var ms = new MemoryStream(blob);
            using var reader = new BinaryReader(ms);

            float capacityKg = reader.ReadSingle();
            int count = reader.ReadInt32();

            _incoming.Clear();
            for (int i = 0; i < count; i++)
            {
                _incoming.Add(new StoredItem
                {
                    Hash = reader.ReadInt32(),
                    Mass = reader.ReadSingle(),
                    Temperature = reader.ReadSingle(),
                    DiseaseIdx = reader.ReadByte(),
                    DiseaseCount = reader.ReadInt32(),
                    NetId = reader.ReadInt32(),
                });
            }

            // Whatever path is taken below, check afterwards that it worked.
            //
            // Three explanations for one container's disagreement have now been
            // eliminated and the food still leaves, and every one of those rounds cost a
            // deploy because the question "did the apply do what it was told" had no
            // answer. Asking the container immediately afterwards separates "the packet
            // was not applied" from "it was applied and something removed it later",
            // which is the fork the remaining candidates hang off.
            //
            // The comparison is by prefab and presence, not mass: mass is corrected
            // continuously and a difference in it is not this defect.
            ApplyAndVerify(storage, diseaseReason);
        }

        /// <summary>
        /// Apply the decoded contents, then read the container back and say so when the
        /// two disagree. Named separately so the verification cannot be skipped by an
        /// early return in the middle of the apply.
        /// </summary>
        private static void ApplyAndVerify(Storage storage, string diseaseReason)
        {
            // What the sender said should be here, captured before applying, since the
            // apply path reuses the shared buffer.
            var expected = new HashSet<int>();
            for (int i = 0; i < _incoming.Count; i++)
                if (_incoming[i].Mass > 0f) expected.Add(_incoming[i].Hash);

            ApplyDecoded(storage, diseaseReason);

            if (expected.Count == 0) return;

            var present = new HashSet<int>();
            var items = storage.items;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var go = items[i];
                    if (go.IsNullOrDestroyed()) continue;
                    if (go.TryGetComponent<KPrefabID>(out var id) && !id.IsNullOrDestroyed())
                        present.Add(id.PrefabTag.GetHashCode());
                }
            }

            foreach (int hash in expected)
            {
                if (present.Contains(hash)) continue;

                ContentsMissingAfterApply++;
                DebugTools.ThrottledLog.Warn(
                    $"[Storage] '{storage.gameObject.PrefabID()}' was told to hold " +
                    $"'{new Tag(hash)}' and does not, immediately after applying - the " +
                    "packet did not land, so nothing later removed it");
            }
        }

        private static void ApplyDecoded(Storage storage, string diseaseReason)
        {
            int count = _incoming.Count;

            // Update in place when the same things are still in there.
            //
            // This used to clear the storage and build it again on every packet.
            // Structure state arrives twice a second and a toilet's water mass
            // drifts continuously, so "changed" was true almost every time - and
            // each rebuild destroyed every stored object and created new ones,
            // which minted new NetIds. One live client registered the same water
            // pile at the same cell 2109 times in 29 minutes, two ids
            // alternating every half second, and did it for six cells at once:
            // 14655 of its 19106 workable registrations were this.
            if (TryUpdateInPlace(storage, diseaseReason))
                return;

            // Not an exact match, but almost always a near one - so correct the
            // difference instead of destroying the container.
            //
            // The all-or-nothing rule meant one missing prefab condemned everything
            // beside it. A MicrobeMusher holding Dirt, Water and BasicPlantFood on the
            // host and only Dirt and Water on the client had its dirt and water
            // destroyed and rebuilt on every packet because of the third item, and the
            // client rebuilt that food 47 times in a single run - measured once the
            // rebuild counter was broken down by prefab - while ending the run without
            // it. 1,928 rebuilds against 13,318 in-place corrections.
            //
            // Destroying what is already right is also how this file's oldest pathology
            // works: every rebuilt object takes a new NetId, and a client once
            // registered the same water pile 2,109 times in 29 minutes. Extending the
            // in-place path to the partial case removes the churn for every container
            // that differs by an item rather than only for those that match exactly.
            //
            // The full clear stays below for the cases this cannot express - an object
            // that has no PrimaryElement, or a container holding something the sender
            // does not describe at all.
            if (TryReconcile(storage, diseaseReason))
                return;

            // Every rebuild destroys objects and creates new ones, and every new object
            // takes a new NetId. Counted so the ratio is visible: a healthy session
            // rebuilds when contents genuinely change and updates in place otherwise, so
            // rebuilds far outnumbering in-place corrections means the match rule is
            // broken again rather than the colony being busy.
            StorageRebuilt++;

            ClearStorage(storage);
            if (count == 0) return;

            for (int i = 0; i < _incoming.Count; i++)
            {
                int hash = _incoming[i].Hash;
                float mass = _incoming[i].Mass;
                float temperature = _incoming[i].Temperature;
                byte diseaseIdx = _incoming[i].DiseaseIdx;
                int diseaseCount = _incoming[i].DiseaseCount;
                if (mass <= 0f) continue;

                Tag tag = new Tag(hash);
                Element elementByHash = ElementLoader.GetElement(tag);
                if (elementByHash != null)
                {
                    storage.AddElement(elementByHash.id, mass, temperature, diseaseIdx, diseaseCount);
                }
                else
                {
                    var item = Assets.GetPrefab(tag);

                    // Never a live animal, whatever the blob says.
                    //
                    // A container holding a creature - an EggIncubator with a newly
                    // hatched critter in it - used to encode that critter as an item, and
                    // this line instantiated it: a client forbidden to hatch anything
                    // ended a run with four more creatures than the host and one of them
                    // addressable by nobody. The sender no longer describes them, and this
                    // refuses to build one if an older peer still does.
                    if (item.HasTag(GameTags.Creature))
                    {
                        CreaturesRefusedFromStorage++;
                        continue;
                    }
                    if (item == null)
                    {
                        // Silent until now, and it is the difference between two very
                        // different diagnoses.
                        //
                        // A MicrobeMusher holds BasicPlantFood on the host and none on
                        // the client, every run, while the Dirt and Water in the same
                        // container agree exactly. Dirt and Water are elements and take
                        // the branch above; BasicPlantFood is an item and takes this
                        // one. If the prefab cannot be found here the item is dropped
                        // and nobody is told, which looks identical to the client
                        // eating it a moment later - and those need opposite fixes.
                        ItemsNoPrefab++;
                        DebugTools.ThrottledLog.Warn(
                            $"[Storage] no prefab for stored item hash {hash} - it is " +
                            "dropped from this container and the peers will not agree " +
                            "about its contents");
                        continue;
                    }

                    var scrapObject = GameUtil.KInstantiate(item, storage.transform.position, Grid.SceneLayer.Ore);
                    if (scrapObject.TryGetComponent<PrimaryElement>(out var pe))
                    {
                        pe.Mass = mass;
                        pe.Temperature = temperature;
                        if (diseaseIdx != byte.MaxValue)
                            pe.AddDisease(diseaseIdx, diseaseCount, diseaseReason);
                    }
                    scrapObject.SetActive(true);
                    storage.Store(scrapObject, true, true);

                    // Named by the sender here too, not only on the reconcile path.
                    // Both create items, and an item named on one path and computed on
                    // the other is the divergence with extra steps.
                    NameFromSender(scrapObject, _incoming[i].NetId);

                    // Which items, not just how many.
                    //
                    // storeMade says 44 to 68 items are rebuilt a run and says nothing
                    // about whether the one item that disagrees is among them. That is
                    // the difference between "the client keeps being given this food and
                    // it keeps being destroyed" and "the last packet before the snapshot
                    // simply did not carry it", and those are a fix and a non-bug.
                    string madeName = scrapObject.PrefabID().ToString();
                    _madeByPrefab.TryGetValue(madeName, out int madeCount);
                    _madeByPrefab[madeName] = madeCount + 1;

                    // Did it actually land in the container?
                    //
                    // Store's result was discarded, and a container can refuse - a
                    // fabricator's input storage takes only what its recipes call for,
                    // and a full one takes nothing. A refused item does not vanish; it
                    // is left standing in the world where the container is, which is
                    // exactly what the measurement looks like: a MicrobeMusher holding
                    // BasicPlantFood on the host and none on the client, with the client
                    // holding twenty-two of them loose in the colony.
                    //
                    // Checked by asking the storage rather than trusting the call, since
                    // that is the thing in doubt.
                    if (!storage.items.Contains(scrapObject))
                    {
                        ItemsRefusedByStorage++;
                        DebugTools.ThrottledLog.Warn(
                            $"[Storage] '{scrapObject.PrefabID()}' was rebuilt for " +
                            $"'{storage.gameObject.PrefabID()}' and the container did not " +
                            "take it - it is loose in the world and the peers disagree");
                        continue;
                    }

                    ItemsRecreated++;
                }
            }
        }
        
        /// <summary>
        /// Stored items the receiver could not build because no prefab answered to their
        /// hash. Each one is a container the two peers cannot agree about.
        /// </summary>
        /// <summary>
        /// An object a container happens to hold that is not bulk contents.
        ///
        /// Two kinds, both learned from what happened when they were treated as goods.
        /// An assigned object - an atmo suit - carries an owner, a durability and its own
        /// oxygen, and rebuilding it hands the locker a fresh one whose owner is gone;
        /// that was a live session ending, because the locker's status item then pointed
        /// at a deleted object and threw once per frame.
        ///
        /// A creature is worse. An EggIncubator holds a newly hatched critter, and this
        /// encoded it as an item, so the client's rebuild instantiated a live animal:
        /// "made[BasicPlantFood:23 HatchBaby:2]" on a client that is forbidden to hatch
        /// anything, next to "client drew 'HatchBaby' with no id". The client ended the
        /// run with 81 creatures against the host's 77 and one of them addressable by
        /// nobody, which is what the suite has been reporting as a different animal every
        /// run - CrabBaby, then HatchBaby, whichever happened to be in an incubator.
        ///
        /// Creatures have their own replication with their own identity path. A container
        /// they are standing in is not a description of them.
        /// </summary>
        private static bool IsEntityNotContents(GameObject go)
        {
            if (go.IsNullOrDestroyed()) return false;
            if (go.TryGetComponent<Assignable>(out var owned) && !owned.IsNullOrDestroyed()) return true;
            return go.HasTag(GameTags.Creature);
        }

        /// <summary>
        /// Creatures a storage packet asked this peer to build, and did not get. Each one
        /// would have been a live animal nobody could address.
        /// </summary>
        /// <summary>
        /// Give a rebuilt item the address the sender's own copy holds.
        ///
        /// Only on a client. Addresses come from the host, and a host adopting a
        /// client's number is the same divergence from the other end - the rule the
        /// build-order naming already follows.
        ///
        /// Without it each peer names its own copy and the two disagree by
        /// construction, which is the whole of the remaining id divergence: 2 of 9,009
        /// shared objects, both food rebuilt inside a fabricator.
        /// </summary>
        private static void NameFromSender(GameObject made, int netId)
        {
            if (netId == 0) return;
            if (MultiplayerSession.IsHost) return;
            if (!made.TryGetComponent<Networking.Components.NetworkIdentity>(out var identity)
                || identity.IsNullOrDestroyed()) return;
            if (identity.NetId == netId) return;

            identity.OverrideNetId(netId);
            ItemsNamedBySender++;
        }

        /// <summary>Rebuilt items given the sender's address instead of a local one.</summary>
        public static int ItemsNamedBySender { get; private set; }

        public static int CreaturesRefusedFromStorage { get; private set; }

        public static int ItemsNoPrefab { get; private set; }

        /// <summary>
        /// Items this peer created from a storage packet. The activity number beside
        /// ItemsNoPrefab: nothing dropped means nothing wrong only when this is not
        /// also zero.
        /// </summary>
        public static int ItemsRecreated { get; private set; }

        /// <summary>
        /// Items rebuilt for a container that then refused to hold them. They are left
        /// loose in the world, so the container disagrees with the other peer and the
        /// colony gains an object nobody asked for.
        /// </summary>
        public static int ItemsRefusedByStorage { get; private set; }

        /// <summary>
        /// Contents the sender listed that are not in the container the instant after
        /// applying. Non-zero means the packet is not landing; zero, with a container
        /// that still disagrees at the end of the run, means something removes it after.
        /// </summary>
        public static int ContentsMissingAfterApply { get; private set; }

        private static readonly Dictionary<string, int> _madeByPrefab = new Dictionary<string, int>();

        /// <summary>What this peer rebuilt into containers, worst first.</summary>
        public static string MadeBreakdown()
        {
            if (_madeByPrefab.Count == 0) return "none";
            var parts = new List<string>();
            foreach (var kv in _madeByPrefab.OrderByDescending(kv => kv.Value).Take(8))
                parts.Add($"{kv.Key}:{kv.Value}");
            return string.Join(" ", parts);
        }

        /// <summary>Storages corrected without destroying anything.</summary>
        public static int StorageUpdatedInPlace { get; private set; }

        /// <summary>Storages torn down and rebuilt, which mints new NetIds.</summary>
        public static int StorageRebuilt { get; private set; }

        /// <summary>
        /// Massless entries left alone rather than deleted - machine working buffers.
        /// </summary>
        public static int MasslessEntriesPreserved { get; private set; }

        /// <summary>
        /// Assigned objects left out of the sync - suits and anything else whose owner,
        /// durability or contents a rebuild would destroy.
        /// </summary>
        public static int EntitiesLeftAlone { get; private set; }

        /// <summary>
        /// True if the storage already holds the same set of prefabs, in which case
        /// only mass, temperature and disease need correcting - no object is destroyed
        /// and none is created, so nothing takes a new NetId.
        ///
        /// Matched per prefab, not per object slot.
        ///
        /// Per-slot matching required the two peers to have split their piles into the
        /// same number of objects, and they cannot: ONI stacks identical food and seeds
        /// on store, so a refrigerator with three MushBar on the host holds one on the
        /// client. The count never lined up, this always returned false, and the
        /// storage was cleared and rebuilt twice a second forever - which is exactly
        /// the object churn that once produced 14655 spurious registrations on one
        /// client.
        ///
        /// A genuine change - a prefab appearing or disappearing - still falls through
        /// to the full rebuild, because the prefab sets differ then.
        /// </summary>
        private static bool TryUpdateInPlace(Storage storage, string diseaseReason)
        {
            var items = storage.items;
            if (items == null) return false;

            // What is here, grouped the same way the sender grouped it.
            var localByPrefab = new Dictionary<int, List<PrimaryElement>>();
            for (int i = 0; i < items.Count; i++)
            {
                var go = items[i];
                if (go.IsNullOrDestroyed()) return false;
                if (!go.TryGetComponent<PrimaryElement>(out var pe) || pe.IsNullOrDestroyed()) return false;

                // Massless entries are invisible to the sender, so they must be
                // invisible here too.
                //
                // The encoder skips anything at or below zero mass. A pump or a shower
                // holds exactly that - an element object with no mass, for part of a
                // tick - so its container encodes as empty, and this side read "empty"
                // as "everything in here was removed" and deleted the buffer object.
                // The comparison showed it plainly: GasPump and Shower entries at 0 kg
                // present on the host and gone on the client.
                //
                // Deleting a machine's working buffer is not a cosmetic difference. It
                // is the client's pump losing the object it dispenses from.
                if (pe.Mass <= 0f)
                {
                    MasslessEntriesPreserved++;
                    continue;
                }

                // Assigned objects are invisible to the sender, so they must be invisible
                // here too - see the matching skip in EncodeStorageContents.
                if (IsEntityNotContents(go))
                {
                    EntitiesLeftAlone++;
                    continue;
                }
                // The same accessor the encoder used. Tag exposes GetHash() and
                // GetHashCode() and they are not the same number, so comparing one
                // against the other never matched and the storage was torn down and
                // rebuilt on every packet regardless.
                if (!go.TryGetComponent<KPrefabID>(out var storedPrefab) || storedPrefab.IsNullOrDestroyed())
                    return false;

                int hash = storedPrefab.PrefabTag.GetHashCode();
                if (!localByPrefab.TryGetValue(hash, out var list))
                {
                    list = new List<PrimaryElement>();
                    localByPrefab[hash] = list;
                }
                list.Add(pe);
            }

            // Entries with no mass are skipped by the sender, so they cannot be
            // expected here either.
            int expectedPrefabs = 0;
            for (int i = 0; i < _incoming.Count; i++)
            {
                if (_incoming[i].Mass > 0f) expectedPrefabs++;
            }
            if (localByPrefab.Count != expectedPrefabs) return false;

            for (int i = 0; i < _incoming.Count; i++)
            {
                if (_incoming[i].Mass <= 0f) continue;
                if (!localByPrefab.ContainsKey(_incoming[i].Hash)) return false;
            }

            // Only now, once every prefab is known to be present, is anything written.
            // A half-applied correction would be worse than none.
            for (int i = 0; i < _incoming.Count; i++)
            {
                if (_incoming[i].Mass <= 0f) continue;

                var local = localByPrefab[_incoming[i].Hash];

                // Spread the total over however many objects this peer split it into.
                // Setting the first to the whole amount and the rest to zero would make
                // them vanish from the next encode and start the churn again.
                float share = _incoming[i].Mass / local.Count;
                foreach (var pe in local)
                {
                    pe.Mass = share;
                    pe.Temperature = _incoming[i].Temperature;
                    if (_incoming[i].DiseaseIdx != byte.MaxValue && pe.DiseaseIdx != _incoming[i].DiseaseIdx)
                        pe.AddDisease(_incoming[i].DiseaseIdx, _incoming[i].DiseaseCount / local.Count, diseaseReason);
                }
            }

            StorageUpdatedInPlace++;
            return true;
        }

        /// <summary>
        /// Bring a container to what the sender described by changing only what differs.
        ///
        /// Returns false when the local contents cannot be reasoned about item by item,
        /// leaving the caller to fall back on the full rebuild.
        ///
        /// Every skip rule here mirrors one in TryUpdateInPlace and in the encoder,
        /// because a thing the sender cannot see must be invisible to this too: massless
        /// working buffers, which a pump holds for part of a tick, and assigned objects
        /// like atmo suits, whose owner and oxygen a rebuild destroys.
        /// </summary>
        private static bool TryReconcile(Storage storage, string diseaseReason)
        {
            var items = storage.items;
            if (items == null) return false;

            var localByPrefab = new Dictionary<int, List<PrimaryElement>>();
            for (int i = 0; i < items.Count; i++)
            {
                var go = items[i];
                if (go.IsNullOrDestroyed()) return false;
                if (!go.TryGetComponent<PrimaryElement>(out var pe) || pe.IsNullOrDestroyed()) return false;

                if (pe.Mass <= 0f) continue;
                if (IsEntityNotContents(go)) continue;
                if (!go.TryGetComponent<KPrefabID>(out var id) || id.IsNullOrDestroyed()) return false;

                int hash = id.PrefabTag.GetHashCode();
                if (!localByPrefab.TryGetValue(hash, out var list))
                    localByPrefab[hash] = list = new List<PrimaryElement>();
                list.Add(pe);
            }

            // Correct or create, one prefab at a time.
            var wanted = new HashSet<int>();
            for (int i = 0; i < _incoming.Count; i++)
            {
                if (_incoming[i].Mass <= 0f) continue;
                wanted.Add(_incoming[i].Hash);

                if (localByPrefab.TryGetValue(_incoming[i].Hash, out var local))
                {
                    // Spread the total over however many objects this peer split it
                    // into, as the exact-match path does.
                    float share = _incoming[i].Mass / local.Count;
                    foreach (var pe in local)
                    {
                        pe.Mass = share;
                        pe.Temperature = _incoming[i].Temperature;
                        if (_incoming[i].DiseaseIdx != byte.MaxValue && pe.DiseaseIdx != _incoming[i].DiseaseIdx)
                            pe.AddDisease(_incoming[i].DiseaseIdx, _incoming[i].DiseaseCount / local.Count, diseaseReason);
                    }
                    continue;
                }

                if (!CreateInto(storage, _incoming[i], diseaseReason))
                    return false;
            }

            // Remove only what the sender no longer lists, and only whole prefabs.
            foreach (var pair in localByPrefab)
            {
                if (wanted.Contains(pair.Key)) continue;
                foreach (var pe in pair.Value)
                {
                    if (pe.IsNullOrDestroyed()) continue;
                    storage.Remove(pe.gameObject);
                    pe.gameObject.DeleteObject();
                }
            }

            StorageReconciled++;
            return true;
        }

        /// <summary>
        /// Put one described item into a container. False when it cannot be built or the
        /// container will not take it - both already counted, and both mean the caller
        /// should not pretend the container is now correct.
        /// </summary>
        private static bool CreateInto(Storage storage, StoredItem item, string diseaseReason)
        {
            Tag tag = new Tag(item.Hash);

            Element element = ElementLoader.GetElement(tag);
            if (element != null)
            {
                storage.AddElement(element.id, item.Mass, item.Temperature, item.DiseaseIdx, item.DiseaseCount);
                return true;
            }

            var prefab = Assets.GetPrefab(tag);

            // See the matching refusal in the full rebuild: storage packets do not
            // create creatures.
            if (prefab != null && prefab.HasTag(GameTags.Creature))
            {
                CreaturesRefusedFromStorage++;
                return false;
            }

            if (prefab == null)
            {
                ItemsNoPrefab++;
                return false;
            }

            var made = GameUtil.KInstantiate(prefab, storage.transform.position, Grid.SceneLayer.Ore);
            if (made.TryGetComponent<PrimaryElement>(out var pe))
            {
                pe.Mass = item.Mass;
                pe.Temperature = item.Temperature;
                if (item.DiseaseIdx != byte.MaxValue)
                    pe.AddDisease(item.DiseaseIdx, item.DiseaseCount, diseaseReason);
            }
            made.SetActive(true);
            storage.Store(made, true, true);

            if (!storage.items.Contains(made))
            {
                ItemsRefusedByStorage++;
                return false;
            }

            NameFromSender(made, item.NetId);

            string madeName = made.PrefabID().ToString();
            _madeByPrefab.TryGetValue(madeName, out int madeCount);
            _madeByPrefab[madeName] = madeCount + 1;
            ItemsRecreated++;
            return true;
        }

        /// <summary>
        /// Containers corrected by adding or removing only what differed, without
        /// destroying the items that were already right.
        /// </summary>
        public static int StorageReconciled { get; private set; }

        private static void ClearStorage(Storage storage)
        {
            for (int i = storage.items.Count - 1; i >= 0; i--)
            {
                var item = storage.items[i];

                // A storage can hold an entry whose GameObject is already gone -
                // the item was consumed or destroyed elsewhere this frame and the
                // list has not caught up. DeleteObject reads a component off it,
                // and Unity throws on member access to a destroyed object even
                // though it compares equal to null, so the whole rebuild aborted
                // partway through: the client dropped five StructureStatePackets
                // with a NullReferenceException, each one leaving that storage
                // half-cleared and never refilled.
                if (item.IsNullOrDestroyed())
                    continue;

                // Not the massless ones. The sender never described them, so the blob
                // says nothing about whether they should still be here - and they are
                // the working buffers of pumps, showers and vents.
                if (item.TryGetComponent<PrimaryElement>(out var keep)
                    && !keep.IsNullOrDestroyed() && keep.Mass <= 0f)
                {
                    MasslessEntriesPreserved++;
                    continue;
                }

                // Never an assigned object. Deleting a suit loses its owner and its
                // oxygen, and leaves the locker's status item pointing at a destroyed
                // GameObject - which throws once per frame the moment anybody hovers over
                // it, and ends the session.
                if (IsEntityNotContents(item))
                {
                    EntitiesLeftAlone++;
                    continue;
                }

                item.DeleteObject();
            }
            storage.items.Clear();
        }
        
        // UP = Utility Path
        private const int UP_FIRST_CELL_BITS = 22;
        private const int UP_SEG_BITS = 4;
        private const int UP_SEG_COUNT_BITS = 2;
        private const int UP_MAX_SEGMENTS = 2;
        private const int UP_MAX_LEN_PER_SEG = 4;
        private const int UP_MAX_CELLS_PER_CHUNK = 1 + UP_MAX_SEGMENTS * UP_MAX_LEN_PER_SEG; // 9
        
        // Derived bit masks/shifts
        private const int UP_FIRST_CELL_MASK = (1 << UP_FIRST_CELL_BITS) - 1;
        private const int UP_SEGMENTS_BITS = UP_SEG_BITS * UP_MAX_SEGMENTS;
        private const int UP_SEGMENTS_MASK = (1 << UP_SEGMENTS_BITS) - 1;
        private const int UP_SEGMENTS_SHIFT = UP_FIRST_CELL_BITS;
        private const int UP_SEG_COUNT_MASK = (1 << UP_SEG_COUNT_BITS) - 1;
        private const int UP_SEG_COUNT_SHIFT = UP_FIRST_CELL_BITS + UP_SEGMENTS_BITS;
        
        /// <summary>
        /// Encodes a utility build path into an array of 32-bit chunks, each packing up to 9 cells.
        /// Bits 0-21: firstCell index. Bits 22-29: up to 2 direction-run segments (4-bit each:
        /// 2-bit direction + 2-bit run length-1). Bits 30-31: segment count.
        /// </summary>
        public static uint[] EncodeUtilityPath(List<BaseUtilityBuildTool.PathNode> path)
        {
            if (path == null || path.Count <= 1)
                return null;

            List<uint> chunks = new List<uint>();
            int pos = 0;
            int count = path.Count;

            while (pos < count)
            {
                int chunkEnd = pos + UP_MAX_CELLS_PER_CHUNK;
                if (chunkEnd > count)
                    chunkEnd = count;

                int chunkSize = chunkEnd - pos;
                if (chunkSize <= 1)
                    break;

                int firstCell = path[pos].cell;
                uint data = (uint)(firstCell & UP_FIRST_CELL_MASK);

                int segmentsPacked = 0;
                int segmentCount = 0;
                int i = pos + 1;

                while (i < chunkEnd && segmentCount < UP_MAX_SEGMENTS)
                {
                    int from = path[i - 1].cell;
                    int to = path[i].cell;
                    UtilityConnections dir = UtilityConnectionsExtensions.DirectionFromToCell(from, to);
                    if (dir == (UtilityConnections)0)
                        break;

                    int dirIndex;
                    if (dir == UtilityConnections.Right) dirIndex = 0;
                    else if (dir == UtilityConnections.Up) dirIndex = 1;
                    else if (dir == UtilityConnections.Left) dirIndex = 2;
                    else dirIndex = 3;

                    int len = 1;
                    i++;
                    while (i < chunkEnd && len < UP_MAX_LEN_PER_SEG)
                    {
                        int prev = path[i - 1].cell;
                        int curr = path[i].cell;
                        if (UtilityConnectionsExtensions.DirectionFromToCell(prev, curr) != dir)
                            break;
                        len++;
                        i++;
                    }

                    int seg = (dirIndex & UP_SEG_COUNT_MASK) | (((len - 1) & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_BITS);
                    segmentsPacked |= seg << (segmentCount * UP_MAX_LEN_PER_SEG);
                    segmentCount++;
                }

                data |= (uint)(segmentsPacked & UP_SEGMENTS_MASK) << UP_SEGMENTS_SHIFT;
                data |= (uint)(segmentCount & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_SHIFT;

                chunks.Add(data);
                pos = i;
            }

            return chunks.ToArray();
        }
        
        /// <summary>
        /// Decodes an array of 9-cell chunk uints back into a flat int[] of Grid cell indices.
        /// Each chunk is decoded via DecodeChunk and concatenated in order.
        /// </summary>
        public static int[] DecodeUtilityPath(uint[] pathData)
        {
            if (pathData == null || pathData.Length == 0)
                return null;

            List<int> cells = new List<int>(pathData.Length * UP_MAX_CELLS_PER_CHUNK);

            foreach (uint chunk in pathData)
            {
                if (chunk == 0)
                    continue;

                int[] chunkCells = DecodeUtilityPathChunk(chunk);
                if (chunkCells != null)
                    cells.AddRange(chunkCells);
            }

            return cells.ToArray();
        }

        /// <summary>
        /// Encodes a utility build path into an array of 64-bit chunks. Lower 32 bits = path data
        /// (same as EncodeUtilityPath). Upper 32 bits = validity bitmask (bits 0-8 for up to 9 cells).
        /// </summary>
        public static ulong[] EncodeUtilityPathWithValidity(List<BaseUtilityBuildTool.PathNode> path)
        {
            if (path == null || path.Count <= 1)
                return null;

            List<ulong> chunks = new List<ulong>();
            int pos = 0;
            int count = path.Count;

            while (pos < count)
            {
                int chunkEnd = pos + UP_MAX_CELLS_PER_CHUNK;
                if (chunkEnd > count)
                    chunkEnd = count;

                uint validityMask = 0;
                for (int j = pos; j < chunkEnd; j++)
                {
                    if (path[j].valid)
                        validityMask |= 1u << (j - pos);
                }

                int firstCell = path[pos].cell;
                uint data = (uint)(firstCell & UP_FIRST_CELL_MASK);

                int segmentsPacked = 0;
                int segmentCount = 0;
                int i = pos + 1;

                while (i < chunkEnd && segmentCount < UP_MAX_SEGMENTS)
                {
                    int from = path[i - 1].cell;
                    int to = path[i].cell;
                    UtilityConnections dir = UtilityConnectionsExtensions.DirectionFromToCell(from, to);
                    if (dir == (UtilityConnections)0)
                        break;

                    int dirIndex;
                    if (dir == UtilityConnections.Right) dirIndex = 0;
                    else if (dir == UtilityConnections.Up) dirIndex = 1;
                    else if (dir == UtilityConnections.Left) dirIndex = 2;
                    else dirIndex = 3;

                    int len = 1;
                    i++;
                    while (i < chunkEnd && len < UP_MAX_LEN_PER_SEG)
                    {
                        int prev = path[i - 1].cell;
                        int curr = path[i].cell;
                        if (UtilityConnectionsExtensions.DirectionFromToCell(prev, curr) != dir)
                            break;
                        len++;
                        i++;
                    }

                    int seg = (dirIndex & UP_SEG_COUNT_MASK) | (((len - 1) & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_BITS);
                    segmentsPacked |= seg << (segmentCount * UP_MAX_LEN_PER_SEG);
                    segmentCount++;
                }

                data |= (uint)(segmentsPacked & UP_SEGMENTS_MASK) << UP_SEGMENTS_SHIFT;
                data |= (uint)(segmentCount & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_SHIFT;

                chunks.Add(((ulong)validityMask << 32) | data);
                pos = i;
            }

            return chunks.ToArray();
        }

        /// <summary>
        /// Decodes a single 32-bit chunk into an array of Grid cell indices.
        /// Bits 0–21: firstCell. Bits 22–29: up to two 4-bit direction-run segments
        /// (2-bit direction, 2-bit run length − 1). Bits 30–31: segment count.
        /// Reconstructs cells by walking from firstCell through each direction-run.
        /// Returns null if data is 0 or firstCell is invalid.
        /// </summary>
        public static int[] DecodeUtilityPathChunk(uint data)
        {
            if (data == 0)
                return null;

            int firstCell = (int)(data & ((1 << UP_FIRST_CELL_BITS) - 1));
            int segmentsPacked = (int)((data >> UP_FIRST_CELL_BITS) & ((1 << (UP_SEG_BITS * UP_MAX_SEGMENTS)) - 1));
            int segmentCount = (int)((data >> (UP_FIRST_CELL_BITS + UP_SEG_BITS * UP_MAX_SEGMENTS)) & ((1 << UP_SEG_COUNT_BITS) - 1));

            if (!Grid.IsValidCell(firstCell))
                return null;

            List<int> cells = new List<int>(UP_MAX_CELLS_PER_CHUNK);
            cells.Add(firstCell);
            int cell = firstCell;

            for (int s = 0; s < segmentCount && s < UP_MAX_SEGMENTS; s++)
            {
                int seg = (segmentsPacked >> (s * UP_SEG_BITS)) & 0xF;
                int dir = seg & 0x3;
                int len = ((seg >> 2) & 0x3) + 1;

                int delta;
                switch (dir)
                {
                    case 0: delta = 1; break;
                    case 1: delta = Grid.WidthInCells; break;
                    case 2: delta = -1; break;
                    case 3: delta = -Grid.WidthInCells; break;
                    default: continue;
                }

                for (int i = 0; i < len; i++)
                {
                    cell += delta;
                    if (!Grid.IsValidCell(cell))
                        break;
                    cells.Add(cell);
                }
            }

            return cells.ToArray();
        }

        /// <summary>
        /// Iterates every ILogicUIElement in uiVisElements and removes any whose
        /// cell has no Building component on any object layer. Called periodically
        /// to sweep up orphaned port entries that survive the normal cleanup path.
        /// </summary>
        public static void CleanupOrphanedLogicVisElements()
        {
            var mgr = Game.Instance.logicCircuitManager;
            var elems = mgr.GetVisElements();
            for (int i = elems.Count - 1; i >= 0; i--)
            {
                var elem = elems[i];
                int cell = elem.GetLogicUICell();
                if (!Grid.IsValidCell(cell))
                    continue;

                bool hasBuilding = false;
                foreach (var layer in Grid.ObjectLayers)
                {
                    if (layer.TryGetValue(cell, out var obj) && obj != null)
                    {
                        if (obj.GetComponent<Building>() != null)
                        {
                            hasBuilding = true;
                            break;
                        }
                    }
                }

                if (!hasBuilding)
                    mgr.RemoveVisElem(elem);
            }
        }

    }
}
