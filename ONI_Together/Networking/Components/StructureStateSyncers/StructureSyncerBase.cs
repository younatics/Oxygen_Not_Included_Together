using System;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking.Components.StructureStateSyncers
{
    public abstract class StructureSyncerBase : KMonoBehaviour
    {
        protected float sendInterval = 0.5f;
        protected float timer;
        protected Operational operational;
        protected BuildingHP buildingHP;
        protected int cell;
        protected Variant lastSentValue;
        protected bool lastSentActive;
        protected Dictionary<string, Variant> lastOptionalValues;
        protected bool checkOptionalsValuesForChanges = true;
        protected bool cullByViewport = true;

        private readonly HashSet<ulong> _viewportScratch = new();

        private bool _initialized;
        private float _initializationTime;

        /// <summary>
        /// How often a structure states itself even though nothing changed.
        ///
        /// Everything here was delta-only, and a delta cannot repair a peer that is
        /// already wrong and has stopped moving. Measured across a run: a MetalRefinery
        /// holding 310 kg of water on the host and 800 on the client, a MicrobeMusher at
        /// 150 kg of dirt against 75, an AlgaeHabitat at 316 kg against 38. Sixteen
        /// containers more than 5 kg apart, and the values are clean multiples rather
        /// than drift - the client took a different number of deliveries and then both
        /// sides went idle. The host's mass never changed again, so it never spoke
        /// again, and the client stayed wrong for the rest of the session.
        ///
        /// Storage makes this worse than most: StorageStateSyncer switches off the
        /// optional-value comparison, so a container whose composition changed while its
        /// total mass held steady is not even considered changed.
        ///
        /// Fifteen seconds, phase-spread per instance so four hundred structures do not
        /// all speak on the same frame. Costs about thirty small packets a second
        /// against the ninety thousand this host already sends per three thousand
        /// frames, and it is the only thing that can close a divergence nobody is
        /// touching.
        /// </summary>
        private const float ResyncInterval = 15f;

        private float _nextResync;

        /// <summary>Sends that happened only because the keyframe came due.</summary>
        public static int ResyncsForced { get; private set; }

        public static void ResetResyncCount() => ResyncsForced = 0;
        private const float INITIAL_DELAY = 5f;

        private float _lastClientPacketTime;
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
            Initialize();
        }

        private void Update()
        {
            if (!MultiplayerSession.SessionHasPlayers) return;
            if (MultiplayerSession.IsHost)
            {
                if (!_initialized)
                {
                    _initializationTime = Time.unscaledTime;
                    _initialized = true;

                    // Spread by instance id rather than at random: scripts here may not
                    // call Math.Random, and a deterministic offset is easier to reason
                    // about anyway. Any spread will do - the point is only that four
                    // hundred structures do not come due together.
                    float phase = (System.Math.Abs(GetInstanceID()) % 1024) / 1024f;
                    _nextResync = Time.unscaledTime + INITIAL_DELAY + phase * ResyncInterval;
                    return;
                }
                if (Time.unscaledTime - _initializationTime < INITIAL_DELAY) return;
                HostUpdate();
            }
            else
            {
                ClientUpdate();
            }
        }

        private void HostUpdate()
        {
            timer += Time.unscaledDeltaTime;
            if (timer < sendInterval) return;
            timer = 0f;

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

            bool dueForResync = Time.unscaledTime >= _nextResync;

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
                    // value record does. A keyframe that came due while nobody was
                    // looking has not repaired anything, and rescheduling it would mean
                    // waiting another fifteen seconds after the client scrolls over.
                    if (dueForResync)
                    {
                        _nextResync = Time.unscaledTime + ResyncInterval;
                        ResyncsForced++;
                    }
                }
            }
        }

        private void ClientUpdate()
        {
            if (!WorldStateSyncer.TryGetLocalViewport(out var viewport))
                return;

            if (!WorldStateSyncer.IsCellInRect(cell, viewport))
                return;

            if (_lastClientPacketTime == 0 || Time.unscaledTime - _lastClientPacketTime > CLIENT_STALE_THRESHOLD)
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
        }

        protected abstract void SampleState(out Variant value, out bool active, out Dictionary<string, Variant> optionalValues);
        protected abstract void ApplyState(StructureStatePacket packet);

        protected abstract bool ShouldForceSync();

        public void HandlePacket(StructureStatePacket packet)
        {
            if (!Grid.IsValidCell(packet.Cell)) return;

            if (!MultiplayerSession.IsHost)
                _lastClientPacketTime = Time.unscaledTime;

            ApplyState(packet);
            ApplyOperationalState(packet);
            ApplyHitPoints(packet);
        }

        private void ApplyOperationalState(StructureStatePacket packet)
        {
            var op = GetComponent<Operational>();
            op?.SetActive(packet.IsActive);
        }
    }
}
