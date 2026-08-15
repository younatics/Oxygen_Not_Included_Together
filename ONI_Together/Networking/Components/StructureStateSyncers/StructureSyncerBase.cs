using System;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking.Components.StructureStateSyncers
{
    /// <summary>
    /// A structure that states itself: the cell it stands on, whether it is operational,
    /// and the fields its subclass names. The timing, the keyframe clock and the record of
    /// when this object was last corrected are <see cref="SyncedEntityBase"/>'s, because
    /// none of that is about being a building - see the note there.
    /// </summary>
    public abstract class StructureSyncerBase : SyncedEntityBase
    {
        protected Operational operational;
        protected BuildingHP buildingHP;

        /// <summary>
        /// Looked up once, like the two above, because sampling is not a rare event.
        ///
        /// These arrived as TryGetComponent inside the sampling path and the host's mean
        /// frame time went from 17.4 ms to 77 ms - measured, same run, while the client
        /// held 17.5 ms on the same build and the same colony, which is what said the
        /// cost was on the host's side of Update and not in the packets.
        ///
        /// The shape is worth remembering. A found component is the cheap case; a missing
        /// one is the expensive case, because Unity has to walk every component on the
        /// object before it can answer no. Artable is on the paintings and on nothing
        /// else, so nearly every one of these calls was a full scan that returned null -
        /// several thousand syncers asking twice a second, all of them answering no.
        ///
        /// Caching costs the ability to see a component added after spawn. Neither of
        /// these is: Prioritizable and Artable come from the prefab.
        /// </summary>
        protected Prioritizable prioritizable;
        protected Artable artable;
        protected ComplexFabricator fabricator;
        protected int cell;
        protected Variant lastSentValue;
        protected bool lastSentActive;
        protected Dictionary<string, Variant> lastOptionalValues;
        protected bool checkOptionalsValuesForChanges = true;
        protected bool cullByViewport = true;

        private readonly HashSet<ulong> _viewportScratch = new();

        /// <summary>
        /// Storage is the reason the keyframe in the base matters most here:
        /// StorageStateSyncer switches off the optional-value comparison, so a container
        /// whose composition changed while its total mass held steady is not even
        /// considered changed, and only the keyframe can repair it.
        /// </summary>
        private float _clientRequestTimer;
        private const float CLIENT_REQUEST_COOLDOWN = 0.5f;
        private const float CLIENT_STALE_THRESHOLD = 2f;

        protected abstract void Initialize();

        public override void OnSpawn()
        {
            base.OnSpawn();
            cell = Grid.PosToCell(this);
            operational = GetComponent<Operational>();
            buildingHP = GetComponent<BuildingHP>();
            prioritizable = GetComponent<Prioritizable>();
            artable = GetComponent<Artable>();
            fabricator = GetComponent<ComplexFabricator>();
            Initialize();
        }

        protected override void HostTick()
        {
            if (!DueToSample()) return;

            SampleState(out var currentValue, out var currentActive, out var optionalValues);

            if (operational != null)
                currentActive = operational.IsActive;

            // Damage rides on every structure packet, because nothing else
            // carried it. Nothing in the mod referenced BuildingHP, Damaged or
            // Repairable anywhere, so a building broken on one peer and intact
            // on the other left no trace at all - a live session had a toilet at
            // 0 hit points on the host and 30 on the client, and neither side
            // could tell. Sampled here rather than in each subclass so it covers
            // every structure that syncs, not the one that was complained about.
            AddHitPoints(ref optionalValues);
            AddCommonState(ref optionalValues);

            bool dueForResync = DueForResync;

            bool changed = StructureStatePacket.VariantValueChanged(currentValue, lastSentValue) ||
                currentActive != lastSentActive ||
                (checkOptionalsValuesForChanges && StructureStatePacket.OptionalValuesChanged(optionalValues, lastOptionalValues)) ||
                ShouldForceSync() ||
                dueForResync;

            if (changed)
            {
                var identity = gameObject.GetNetIdentity();
                if (identity.NetId == 0)
                {
                    DebugConsole.Log($"No Net ID found on sync structure of type {GetType().Name}.");
                    return;
                }

                var packet = new StructureStatePacket
                {
                    NetId = identity.NetId,
                    Cell = cell,
                    Value = currentValue,
                    IsActive = currentActive,
                    OptionalValues = optionalValues,
                    // So the receiver hands it to the matching syncer rather than to
                    // every syncer on the building - see StructureStatePacket.SyncerType.
                    SyncerType = GetType().Name,
                };

                int delivered = 0;

                // A keyframe ignores the viewport, which is the whole point of it.
                //
                // Culling deltas is right: nobody needs a progress bar for a building
                // they cannot see. But it also meant the divergences that survive are
                // exactly the ones nobody looked at, and the measurement says so - after
                // keyframes closed half the storage gaps, the ten that remained were
                // AlgaeHabitat, Compost, MetalRefinery and MicrobeMusher, with water 278
                // kg apart. That is not fifteen seconds of conversion drifting; it is a
                // correction that was never delivered, because the keyframe clock only
                // advances on delivery and delivery never happened.
                //
                // So the delta stays culled and the keyframe goes to everyone. It is one
                // small packet per structure per fifteen seconds - about thirty a second
                // for four hundred structures, against the ninety thousand sends this
                // host already makes per three thousand frames.
                if (cullByViewport && !dueForResync && WorldStateSyncer.Instance != null)
                {
                    WorldStateSyncer.Instance.GetClientsViewingCell(cell, _viewportScratch, 2);
                    foreach (var playerId in _viewportScratch)
                    {
                        if (PacketSender.SendToPlayer(playerId, packet, PacketSendMode.Unreliable))
                            delivered++;
                    }
                }
                else
                {
                    delivered = PacketSender.SendToAllClients(packet, PacketSendMode.Unreliable);
                }

                // Only call it sent if it was sent.
                //
                // This used to record the new value the moment the change was
                // detected, before the send - so a change that happened while no
                // client was looking at that cell was marked delivered and never
                // re-evaluated. The next tick saw no difference and stayed quiet,
                // for good. A tile took damage off screen on the host and the
                // client had it whole for the rest of the session, across two
                // consecutive runs, creeping from 43/100 to 44/100 on one peer
                // while the other read undamaged. Unlike position there is no
                // heartbeat here to paper over it.
                //
                // Leaving the record unchanged costs one more comparison per
                // tick and sends the moment somebody looks.
                if (delivered > 0 || MultiplayerSession.ConnectedPlayers.Count <= 1)
                {
                    lastSentValue = currentValue;
                    lastSentActive = currentActive;
                    lastOptionalValues = optionalValues;

                    // The keyframe clock advances on delivery, for the same reason the
                    // value record does - see MarkResyncDelivered.
                    if (dueForResync) MarkResyncDelivered();
                }
            }
        }

        protected override void ClientTick()
        {
            if (!WorldStateSyncer.TryGetLocalViewport(out var viewport))
                return;

            if (!WorldStateSyncer.IsCellInRect(cell, viewport))
                return;

            // Never corrected reads as -1, which is the same case as "corrected too long
            // ago" for this purpose: ask the host.
            if (SecondsSinceApplied < 0f || SecondsSinceApplied > CLIENT_STALE_THRESHOLD)
            {
                _clientRequestTimer += Time.unscaledDeltaTime;
                if (_clientRequestTimer >= CLIENT_REQUEST_COOLDOWN)
                {
                    _clientRequestTimer = 0f;

                    var identity = gameObject.GetNetIdentity();
                    if (identity.NetId == 0) return;

                    PacketSender.SendToHost(new StructureStateRequestPacket
                    {
                        NetId = identity.NetId,
                        RequesterId = MultiplayerSession.LocalUserID,
                    }, PacketSendMode.ReliableImmediate);
                }
            }
        }

        public void SendStateToClient(ulong playerId)
        {
            SampleState(out var value, out var active, out var optionalValues);
            if (operational != null)
                active = operational.IsActive;

            // The periodic path adds this and the on-demand reply did not, so a
            // client that asked for state because its own was stale got an
            // answer with the damage left out - and stayed wrong about exactly
            // the thing it had asked about.
            AddHitPoints(ref optionalValues);
            AddCommonState(ref optionalValues);

            var identity = gameObject.GetNetIdentity();
            if (identity.NetId == 0) return;

            var packet = new StructureStatePacket
            {
                NetId = identity.NetId,
                Cell = cell,
                Value = value,
                IsActive = active,
                OptionalValues = optionalValues,
            };

            PacketSender.SendToPlayer(playerId, packet, PacketSendMode.ReliableImmediate);
        }

        private const string HitPointsKey = "hit_points";

        internal const string PriorityKey = "_prio";
        internal const string ArtStageKey = "_art";
        internal const string RecipeQueueKey = "_rq";

        /// <summary>
        /// State every structure has, sampled here instead of in each subclass.
        ///
        /// This is the shape the whole mod keeps paying for. StructureSyncerBase already
        /// does the hard part - sample, compare, send a delta, resend a keyframe on a
        /// timer - and it does it for 106 of this colony's 110 building prefabs. All a
        /// subclass decides is WHICH fields ride along, and that list was written one
        /// building at a time. Anything nobody thought of is replicated by intercepting
        /// an event instead, and intercepting events means every event has to be found
        /// first - so the ones that were missed only surface when somebody plays.
        ///
        /// Three reports from one evening of real play were all that shape: an art piece
        /// finished on the host and blank on the client, because "started work" is sent
        /// and "finished" is not; buildings whose priority a player had set; and damage,
        /// which took its own dedicated field here for exactly the same reason.
        ///
        /// So fields many buildings share are sampled centrally, the way AddHitPoints
        /// already was. One entry here covers every structure that syncs rather than the
        /// one that was complained about: Prioritizable is on all 110 prefabs, Artable on
        /// every painting and sculpture in the colony.
        /// </summary>
        private void AddCommonState(ref Dictionary<string, Variant> optionalValues)
        {
            optionalValues ??= new Dictionary<string, Variant>();

            // What the player set, which is not the same as whether there is work to do.
            if (prioritizable != null)
            {
                var p = prioritizable.GetMasterPriority();
                // Class and value in one field: they are set together from the priority
                // screen and neither means anything without the other.
                optionalValues[PriorityKey] = new Variant
                {
                    Type = Variant.TypeCode.Int,
                    Int = ((int)p.priority_class * 100) + p.priority_value
                };
            }

            // The finished artwork, not the request for one.
            //
            // Artable.SetUserChosenTargetState is intercepted and replicated, so the two
            // peers agree about which piece was ordered. Nothing sends the completion,
            // and a client's duplicants cannot produce it themselves because their chores
            // do not run - so the host ends up with a finished sculpture and the client
            // with a blank one, permanently.
            if (artable != null)
            {
                optionalValues[ArtStageKey] = new Variant
                {
                    Type = Variant.TypeCode.String,
                    String = artable.CurrentStage ?? string.Empty
                };
            }

            // What is still queued, not the click that queued it.
            //
            // Every way the queue can change is already patched - Increment, Decrement and
            // Set - and a run still ended with a MetalRefinery reading 7 orders on the host
            // and 8 on the client. So this is not a missing patch, and adding a fourth one
            // would not have found it: an event that is sent and does not arrive looks
            // exactly like an event nobody sends, and neither leaves anything behind to
            // repair the peer afterwards.
            //
            // The count is small and it is the whole truth about the queue, so it rides
            // along and the keyframe corrects it within fifteen seconds whichever event was
            // lost. That is the difference worth having: the fix does not depend on knowing
            // which one it was.
            if (fabricator != null)
            {
                optionalValues[RecipeQueueKey] = new Variant
                {
                    Type = Variant.TypeCode.String,
                    String = DescribeRecipeQueue(fabricator)
                };
            }
        }

        /// <summary>
        /// The queued orders as one string, in the fabricator's own recipe order.
        ///
        /// Order matters because this is compared as text on both peers. GetRecipes comes
        /// off the prefab, so it is the same sequence on both, and empty recipes are left
        /// out so a fabricator nobody has touched compares as an empty string rather than
        /// a list of zeroes.
        /// </summary>
        private static string DescribeRecipeQueue(ComplexFabricator f)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var recipe in f.GetRecipes())
            {
                if (recipe == null) continue;
                int count = f.GetRecipeQueueCount(recipe);
                if (count == 0) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(recipe.id).Append(':').Append(count);
            }
            return sb.ToString();
        }

        /// <summary>Apply the shared fields, for every structure, like the sampling.</summary>
        protected void ApplyCommonState(StructureStatePacket packet)
        {
            if (packet.OptionalValues == null) return;

            if (packet.OptionalValues.TryGetValue(PriorityKey, out var prio)
                && prioritizable != null)
            {
                int cls = prio.Int / 100;
                int val = prio.Int % 100;
                var current = prioritizable.GetMasterPriority();
                if ((int)current.priority_class != cls || current.priority_value != val)
                {
                    // Under the same flag the dedicated priority packet uses.
                    //
                    // PrioritizablePatch hooks SetMasterPriority and sends a packet from
                    // it, so applying a correction here would bounce straight back to the
                    // sender and the two peers would trade the same value forever. That
                    // flag exists precisely to mark "this change came off the wire".
                    bool wasApplying = Packets.World.PrioritizeStatePacket.IsApplying;
                    Packets.World.PrioritizeStatePacket.IsApplying = true;
                    try
                    {
                        prioritizable.SetMasterPriority(
                            new PrioritySetting((PriorityScreen.PriorityClass)cls, val));
                    }
                    finally
                    {
                        Packets.World.PrioritizeStatePacket.IsApplying = wasApplying;
                    }
                    PrioritiesApplied++;
                }
            }

            if (packet.OptionalValues.TryGetValue(ArtStageKey, out var art)
                && artable != null)
            {
                string wanted = art.String ?? string.Empty;
                if (!string.IsNullOrEmpty(wanted) && artable.CurrentStage != wanted)
                {
                    // The game's own completion path, so the artwork, its decor value and
                    // its name change together. Writing the field would change a string
                    // and leave a blank canvas on screen.
                    artable.SetStage(wanted, true);
                    ArtStagesApplied++;
                }
            }

            if (packet.OptionalValues.TryGetValue(RecipeQueueKey, out var rq)
                && fabricator != null)
            {
                ApplyRecipeQueue(rq.String ?? string.Empty);
            }
        }

        /// <summary>
        /// Set the queue to what the host says, order by order.
        ///
        /// Under the flag the config packet uses, because SetRecipeQueueCount is itself
        /// patched to broadcast: correcting the count here without it would send the
        /// correction straight back and the two peers would trade the same number for as
        /// long as they disagreed. The flag exists to mark "this came off the wire".
        /// </summary>
        private void ApplyRecipeQueue(string wanted)
        {
            if (DescribeRecipeQueue(fabricator) == wanted) return;

            // Parsed into a lookup first, because every recipe has to be visited: one the
            // host no longer lists is one this peer must set back to zero, and a queue
            // that only ever grows would be worse than no correction at all.
            var wantedCounts = new Dictionary<string, int>();
            foreach (var part in wanted.Split(';'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                int sep = part.LastIndexOf(':');
                if (sep <= 0) continue;
                if (!int.TryParse(part.Substring(sep + 1), out int n)) continue;
                wantedCounts[part.Substring(0, sep)] = n;
            }

            bool wasApplying = Packets.World.BuildingConfigPacket.IsApplyingPacket;
            Packets.World.BuildingConfigPacket.IsApplyingPacket = true;
            try
            {
                foreach (var recipe in fabricator.GetRecipes())
                {
                    if (recipe == null) continue;
                    int want = wantedCounts.TryGetValue(recipe.id, out int n) ? n : 0;
                    if (fabricator.GetRecipeQueueCount(recipe) == want) continue;
                    fabricator.SetRecipeQueueCount(recipe, want);
                    RecipeQueuesApplied++;
                }
            }
            finally
            {
                Packets.World.BuildingConfigPacket.IsApplyingPacket = wasApplying;
            }
        }

        /// <summary>
        /// Shared fields this peer had to correct. Non-zero on a client measures what
        /// event interception was missing.
        /// </summary>
        public static int PrioritiesApplied { get; private set; }
        public static int ArtStagesApplied { get; private set; }
        public static int RecipeQueuesApplied { get; private set; }

        private void AddHitPoints(ref Dictionary<string, Variant> optionalValues)
        {
            if (buildingHP == null) return;
            optionalValues ??= new Dictionary<string, Variant>();
            optionalValues[HitPointsKey] = new Variant { Type = Variant.TypeCode.Int, Int = buildingHP.HitPoints };
        }

        /// <summary>
        /// Bring this peer's damage in line with the host's.
        ///
        /// BuildingHP.HitPoints has no setter, so the difference is applied
        /// through the game's own damage event - the same one the game raises
        /// when something actually breaks a building. That matters for more than
        /// tidiness: going through the event is what updates Damaged, queues the
        /// repair errand and puts the broken overlay on the building. Writing a
        /// number would have changed the number and nothing else.
        /// </summary>
        protected void ApplyHitPoints(StructureStatePacket packet)
        {
            if (buildingHP == null) return;
            if (packet.OptionalValues == null) return;
            if (!packet.OptionalValues.TryGetValue(HitPointsKey, out var hp)) return;

            // Clamped to what this building can actually hold. A negative delta
            // is a repair, and the game's damage handler just subtracts, so an
            // unclamped one would push hit points above the maximum and leave
            // the building permanently over-healed.
            // Read the field the value was actually written to. Variant is a
            // tagged union with separate Float and Int fields and no conversion
            // between them - an int goes in as Int and .Float stays zero.
            //
            // Reading .Float made every host reading look like zero hit points,
            // so the client damaged its own building by its entire health on
            // every packet. That is the "host is fine, client shows it broken"
            // report: the sync was not failing to run, it was running with a
            // number that was always zero.
            int hostHp = hp.Type == Variant.TypeCode.Int ? hp.Int : (int)hp.Float;
            hostHp = Mathf.Clamp(hostHp, 0, buildingHP.MaxHitPoints);
            int delta = buildingHP.HitPoints - hostHp;
            if (delta == 0) return;

            // BuildingDamageSyncer owns damage now, because this path only ever
            // covered buildings that have a syncer and only when that syncer's
            // other state changed - a tile has no syncer at all. This stays as a
            // second, earlier trigger for the buildings it does cover, and
            // delegates so there is one implementation of the correction rather
            // than two that can drift apart. Applying it twice is harmless: the
            // second call computes a delta of zero.
            BuildingDamagePacket.Apply(buildingHP, hostHp);

            // Leaves a trace, because a correction that works silently cannot be
            // told apart from one that never runs. The first attempt at this
            // could only be checked by finding a mismatch warning, and removing
            // the mismatch removed the evidence with it - a session where no
            // building happened to break looked exactly like a session where the
            // fix worked.
            // With the numbers, because without them this line was true and
            // useless: it said damage was replicated while replicating a host
            // reading of zero that never existed. One printed value would have
            // shown that in a glance.
            ThrottledLog.Warn(
                $"[StructureState] {gameObject.GetProperName()} {(delta > 0 ? "damaged" : "repaired")} " +
                $"to match host: {buildingHP.HitPoints} -> {hostHp} of {buildingHP.MaxHitPoints}");
        }

        /// <summary>
        /// The keys this syncer would put on the wire right now, for tests that
        /// check senders and readers still agree on names. Goes through the same
        /// path the host uses, including the base's own additions, so it cannot
        /// drift from what is really sent.
        /// </summary>
        public void SampleStateForDiagnostics(out Dictionary<string, Variant> optionalValues)
        {
            SampleState(out _, out _, out optionalValues);
            AddHitPoints(ref optionalValues);
            AddCommonState(ref optionalValues);
        }

        protected abstract void SampleState(out Variant value, out bool active, out Dictionary<string, Variant> optionalValues);
        protected abstract void ApplyState(StructureStatePacket packet);

        protected abstract bool ShouldForceSync();

        public void HandlePacket(StructureStatePacket packet)
        {
            if (!Grid.IsValidCell(packet.Cell)) return;

            if (!MultiplayerSession.IsHost)
                MarkStateApplied();

            ApplyState(packet);
            ApplyOperationalState(packet);
            ApplyHitPoints(packet);
            ApplyCommonState(packet);
        }

        private void ApplyOperationalState(StructureStatePacket packet)
        {
            var op = GetComponent<Operational>();
            op?.SetActive(packet.IsActive);
        }
    }
}
