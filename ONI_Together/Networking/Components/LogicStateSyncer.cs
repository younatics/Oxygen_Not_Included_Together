using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.World;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    public class LogicStateSyncer : KMonoBehaviour
    {
        public static LogicStateSyncer Instance { get; private set; }

        private const float SEND_INTERVAL = 1f;
        private const float INIT_DELAY = 5f;
        private const float CLIENT_STALE_THRESHOLD = 2f;
        private const float CLIENT_REQUEST_COOLDOWN = 0.5f;

        private float _timer;
        private bool _initialized;
        private float _initTime;

        // NetId -> tracked building
        private readonly Dictionary<int, BuildingEntry> _tracked = new();

        // Client: last packet time per building for stale detection
        private readonly Dictionary<int, float> _lastPacketTime = new();
        private float _clientRequestTimer;

        // Viewport scratch buffer
        private readonly HashSet<ulong> _viewportScratch = new();

        /// <summary>
        /// How often a logic building states itself even though nothing changed.
        ///
        /// This was delta-only, and the half of the StructureSyncerBase fix that arrived
        /// here was the wrong half. The comment further down explains why the record is
        /// only updated when the packet was really delivered - that came across. The
        /// keyframe did not, and it was the part that repairs a peer already wrong.
        ///
        /// The failure it leaves open is the one measured on storage: two peers disagree
        /// once, the host's value never changes again so it never speaks again, and the
        /// client keeps the wrong value for the rest of the session. Logic is a worse
        /// place for it than storage. A signal is a single bit that stays put for hours,
        /// so "nothing changed" is the normal state of a working circuit, and a client
        /// holding the opposite bit sees doors that will not open and pumps that will not
        /// run with nothing at all to indicate why.
        ///
        /// Same interval and same phase spread as the structure syncer, for the same
        /// reason: a keyframe is one small packet, and the alternative is a divergence
        /// nobody is looking at.
        /// </summary>
        private const float RESYNC_INTERVAL = 15f;

        /// <summary>Logic sends that happened only because the keyframe came due.</summary>
        public static int LogicResyncs { get; private set; }

        private class BuildingEntry
        {
            public GameObject go;
            public Variant lastValue;
            public bool lastActive;
            public Dictionary<string, Variant> lastOptional;

            /// <summary>When this building next owes a keyframe whatever it thinks changed.</summary>
            public float nextResync;
        }

        public override void OnSpawn()
        {
            base.OnSpawn();
            Instance = this;
        }

        public override void OnCleanUp()
        {
            base.OnCleanUp();
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!MultiplayerSession.SessionHasPlayers) return;

            if (MultiplayerSession.IsHost)
                HostUpdate();
            else
                ClientUpdate();
        }

        private void HostUpdate()
        {
            if (!_initialized)
            {
                _initTime = Time.unscaledTime;
                _initialized = true;
                return;
            }
            if (Time.unscaledTime - _initTime < INIT_DELAY) return;

            _timer += Time.unscaledDeltaTime;
            if (_timer < SEND_INTERVAL) return;
            _timer = 0f;

            if (_tracked.Count == 0) return;

            // Collect all cells for viewport culling in one batch
            var toRemove = new List<int>();

            foreach (var kvp in _tracked)
            {
                int netId = kvp.Key;
                var entry = kvp.Value;

                if (entry.go.IsNullOrDestroyed())
                {
                    toRemove.Add(netId);
                    continue;
                }

                if (!SampleBuilding(entry.go, out var value, out var active, out var optional))
                    continue;

                bool dueForResync = Time.unscaledTime >= entry.nextResync;

                bool changed = LogicStatePacket.VariantValueChanged(value, entry.lastValue)
                    || active != entry.lastActive
                    || LogicStatePacket.OptionalValuesChanged(optional, entry.lastOptional)
                    || dueForResync;

                if (!changed)
                    continue;

                int cell = Grid.PosToCell(entry.go);

                var packet = new LogicStatePacket
                {
                    NetId = netId,
                    Cell = cell,
                    Value = value,
                    IsActive = active,
                    OptionalValues = optional,
                };

                int delivered = 0;

                // A keyframe ignores the viewport, which is the whole point of it.
                //
                // Culling deltas is right - nobody needs a signal change for a circuit
                // they cannot see - but it also means the divergences that survive are
                // exactly the ones nobody looked at. Sending the keyframe only to
                // watchers would leave the repair waiting on the same condition that
                // caused the damage.
                if (!dueForResync && WorldStateSyncer.Instance != null)
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

                // Recorded after the send, and only if it went somewhere - the
                // same reason as StructureSyncerBase. A wire that changes state
                // while nobody is looking at it was being marked delivered and
                // never sent again, so the client kept the old signal until
                // something else happened to that circuit. Logic state is where
                // this is least forgivable: a wire nobody watches is the normal
                // case, and a stale signal reads as a broken automation.
                if (delivered > 0 || MultiplayerSession.ConnectedPlayers.Count <= 1)
                {
                    entry.lastValue = value;
                    entry.lastActive = active;
                    entry.lastOptional = optional;

                    // The keyframe clock advances on delivery, for the same reason the
                    // record above does. One that came due while nobody was looking has
                    // repaired nothing, and rescheduling it would mean another fifteen
                    // seconds of a wrong signal after the client scrolls over.
                    if (dueForResync)
                    {
                        entry.nextResync = Time.unscaledTime + RESYNC_INTERVAL;
                        LogicResyncs++;
                    }
                }
            }

            foreach (int netId in toRemove)
                _tracked.Remove(netId);
        }

        private void ClientUpdate()
        {
            if (_tracked.Count == 0) return;

            if (!WorldStateSyncer.TryGetLocalViewport(out var viewport))
                return;

            _clientRequestTimer += Time.unscaledDeltaTime;
            if (_clientRequestTimer < CLIENT_REQUEST_COOLDOWN) return;
            _clientRequestTimer = 0f;

            foreach (var kvp in _tracked)
            {
                int netId = kvp.Key;
                var entry = kvp.Value;

                if (entry.go.IsNullOrDestroyed())
                    continue;

                int cell = Grid.PosToCell(entry.go);
                if (!WorldStateSyncer.IsCellInRect(cell, viewport))
                    continue;

                if (!_lastPacketTime.TryGetValue(netId, out var lastTime) || Time.unscaledTime - lastTime > CLIENT_STALE_THRESHOLD)
                {
                    PacketSender.SendToHost(new StructureStateRequestPacket
                    {
                        NetId = netId,
                        RequesterId = MultiplayerSession.LocalUserID,
                    }, PacketSendMode.ReliableImmediate);
                }
            }
        }

        public void HandlePacket(LogicStatePacket packet)
        {
            if (!Grid.IsValidCell(packet.Cell)) return;

            _lastPacketTime[packet.NetId] = Time.unscaledTime;

            if (!_tracked.TryGetValue(packet.NetId, out var entry))
            {
                // The id is captured when the building registers and used as the
                // key forever after. An identity that is rehoused later - and
                // duplicates from a save are rehoused now - leaves its old key
                // behind, so state for the new id lands here and is dropped.
                // Silently, until now: a wire or switch that stopped following
                // the host looked exactly like a wire that was never wired up.
                //
                // The registry still knows the object, so look it up and adopt
                // the new key rather than losing the building.
                if (!NetworkIdentityRegistry.TryGet(packet.NetId, out var identity) || identity == null)
                {
                    ThrottledLog.Warn($"[LogicState] no logic building for NetId {packet.NetId}");
                    return;
                }

                if (!TryRekey(identity.gameObject, packet.NetId, out entry))
                {
                    ThrottledLog.Warn(
                        $"[LogicState] '{identity.name}' answers to NetId {packet.NetId} but is not tracked here");
                    return;
                }
            }

            if (entry.go.IsNullOrDestroyed())
                return;

            ApplyBuildingState(entry.go, packet);
        }

        public void SendStateToClient(ulong playerId, int netId)
        {
            if (!_tracked.TryGetValue(netId, out var entry))
            {
                // The host side of the same staleness. A client whose logic
                // state has gone quiet asks for it here; an entry stranded
                // under its old key meant the answer never came, and the client
                // asked again at every cooldown with nothing to show for it on
                // either side.
                if (!NetworkIdentityRegistry.TryGet(netId, out var identity) || identity == null ||
                    !TryRekey(identity.gameObject, netId, out entry))
                {
                    ThrottledLog.Warn($"[LogicState] cannot answer a request for NetId {netId}: not tracked here");
                    return;
                }
            }

            if (entry.go.IsNullOrDestroyed())
                return;

            if (!SampleBuilding(entry.go, out var value, out var active, out var optional))
                return;

            int cell = Grid.PosToCell(entry.go);

            PacketSender.SendToPlayer(playerId, new LogicStatePacket
            {
                NetId = netId,
                Cell = cell,
                Value = value,
                IsActive = active,
                OptionalValues = optional,
            }, PacketSendMode.ReliableImmediate);
        }

        /// <summary>
        /// Move a tracked building to the id it now answers to.
        ///
        /// Tracking is keyed by the id a building had when it registered, and
        /// that is not permanent - rehousing a duplicate changes it. Without
        /// this the entry is stranded under a key nobody will ever send again.
        /// </summary>
        private bool TryRekey(GameObject go, int newNetId, out BuildingEntry entry)
        {
            entry = default;
            if (go.IsNullOrDestroyed()) return false;

            int stale = 0;
            bool found = false;
            foreach (var kvp in _tracked)
            {
                if (ReferenceEquals(kvp.Value.go, go)) { stale = kvp.Key; entry = kvp.Value; found = true; break; }
            }
            if (!found) return false;

            _tracked.Remove(stale);
            _lastPacketTime.Remove(stale);
            _tracked[newNetId] = entry;
            DebugConsole.Log($"[LogicState] '{go.name}' re-keyed from NetId {stale} to {newNetId}");
            return true;
        }

        public void Register(GameObject go)
        {
            if (go.IsNullOrDestroyed()) return;
            //if (!MultiplayerSession.SessionHasPlayers) return;

            var identity = go.GetNetIdentity();
            if (identity == null || identity.NetId == 0) return;

            int netId = identity.NetId;
            if (_tracked.ContainsKey(netId)) return;

            _tracked[netId] = new BuildingEntry
            {
                go = go,
                lastValue = default,
                lastActive = false,
                lastOptional = null,

                // Spread by id rather than at random, so a colony's worth of automation
                // does not come due on one frame. Deterministic, and the same shape the
                // structure syncer uses.
                nextResync = Time.unscaledTime + INIT_DELAY
                             + ((System.Math.Abs(netId) % 1024) / 1024f) * RESYNC_INTERVAL,
            };
        }

        public void Unregister(GameObject go)
        {
            if (go.IsNullOrDestroyed()) return;

            // Must not create one. This runs from building cleanup, and
            // attaching an identity to an object that is being destroyed threw
            // out of registration the moment lazily attached identities started
            // being registered.
            var identity = go.GetExistingNetIdentity();
            if (identity == null) return;

            _tracked.Remove(identity.NetId);
            _lastPacketTime.Remove(identity.NetId);
        }

        private bool SampleBuilding(GameObject go, out Variant value, out bool active, out Dictionary<string, Variant> optional)
        {
            value = default;
            active = false;
            optional = new Dictionary<string, Variant>();

            // Switch subclasses: sensors, LogicSwitch, LogicCounter, LogicHammer, TimerSensor, TimeOfDaySensor
            var sw = go.GetComponent<Switch>();
            if (sw != null)
            {
                value = (Variant)(sw.IsSwitchedOn ? 1 : 0);
                var op = go.GetComponent<Operational>();
                active = op != null && op.IsActive;
            }

            // LogicGate (base gates: AND, OR, NOT, XOR, multiplexer, demux)
            var gate = go.GetComponent<LogicGate>();
            if (gate != null)
            {
                // output values are already serialized bits of gate logic
                optional["out1"] = gate.outputValueOne;
                optional["out2"] = gate.outputValueTwo;
                optional["out3"] = gate.outputValueThree;
                optional["out4"] = gate.outputValueFour;

                // LogicGateFilter specific
                var filter = go.GetComponent<LogicGateFilter>();
                if (filter != null)
                {
                    optional["ticksRemaining"] = filter.delayTicksRemaining;
                    optional["wasNegative"] = filter.input_was_previously_negative ? 1 : 0;
                }

                // LogicGateBuffer specific
                var buffer = go.GetComponent<LogicGateBuffer>();
                if (buffer != null)
                {
                    optional["ticksRemaining"] = buffer.delayTicksRemaining;
                    optional["wasPositive"] = buffer.input_was_previously_positive ? 1 : 0;
                }
            }

            // LogicCounter extends Switch, but has extra state beyond switchedOn
            var counter = go.GetComponent<LogicCounter>();
            if (counter != null)
            {
                optional["currentCount"] = counter.currentCount;
                optional["wasResetting"] = counter.wasResetting ? 1 : 0;
                optional["wasIncrementing"] = counter.wasIncrementing ? 1 : 0;
                optional["receivedFirstSignal"] = counter.receivedFirstSignal ? 1 : 0;
            }

            // LogicMemory
            var memory = go.GetComponent<LogicMemory>();
            if (memory != null)
            {
                value = (Variant)(memory.value != 0 ? 1 : 0);
            }

            // LogicTimerSensor extends Switch, extra time tracking
            var timer = go.GetComponent<LogicTimerSensor>();
            if (timer != null)
            {
                optional["timeElapsed"] = timer.timeElapsedInCurrentState;
            }

            // LogicRibbonReader
            var ribbonReader = go.GetComponent<LogicRibbonReader>();
            if (ribbonReader != null)
            {
                value = (Variant)ribbonReader.selectedBit;
                optional["currentValue"] = ribbonReader.currentValue;
                optional["type"] = "reader";
            }

            // LogicRibbonWriter
            var ribbonWriter = go.GetComponent<LogicRibbonWriter>();
            if (ribbonWriter != null)
            {
                value = (Variant)ribbonWriter.selectedBit;
                optional["currentValue"] = ribbonWriter.currentValue;
                optional["type"] = "writer";
            }

            // Automatable
            var automatable = go.GetComponent<Automatable>();
            if (automatable != null)
            {
                value = (Variant)(automatable.GetAutomationOnly() ? 1 : 0);
            }

            // If we found nothing, skip this building
            if (sw == null && gate == null && memory == null && ribbonReader == null && ribbonWriter == null && automatable == null)
                return false;

            return true;
        }

        private void ApplyBuildingState(GameObject go, LogicStatePacket packet)
        {
            // Switch subclasses
            var sw = go.GetComponent<Switch>();
            if (sw != null)
            {
                bool targetOn = packet.Value.Int != 0 || (packet.Value.Type == Variant.TypeCode.Float && packet.Value.Float > 0.5f);
                if (sw.IsSwitchedOn != targetOn)
                    sw.SetState(targetOn);
            }

            // LogicGate output values
            var gate = go.GetComponent<LogicGate>();
            if (gate != null && packet.OptionalValues.TryGetValue("out1", out var out1))
            {
                gate.outputValueOne = out1.Int;
                if (packet.OptionalValues.TryGetValue("out2", out var out2))
                    gate.outputValueTwo = out2.Int;
                if (packet.OptionalValues.TryGetValue("out3", out var out3))
                    gate.outputValueThree = out3.Int;
                if (packet.OptionalValues.TryGetValue("out4", out var out4))
                    gate.outputValueFour = out4.Int;
            }

            // LogicGateFilter ticks
            var filter = go.GetComponent<LogicGateFilter>();
            if (filter != null)
            {
                if (packet.OptionalValues.TryGetValue("ticksRemaining", out var ticks))
                    filter.delayTicksRemaining = ticks.Int;
                if (packet.OptionalValues.TryGetValue("wasNegative", out var wasNeg))
                    filter.input_was_previously_negative = wasNeg.Int != 0;
            }

            // LogicGateBuffer ticks
            var buffer = go.GetComponent<LogicGateBuffer>();
            if (buffer != null)
            {
                if (packet.OptionalValues.TryGetValue("ticksRemaining", out var ticks))
                    buffer.delayTicksRemaining = ticks.Int;
                if (packet.OptionalValues.TryGetValue("wasPositive", out var wasPos))
                    buffer.input_was_previously_positive = wasPos.Int != 0;
            }

            // LogicCounter
            var counter = go.GetComponent<LogicCounter>();
            if (counter != null)
            {
                if (packet.OptionalValues.TryGetValue("currentCount", out var count))
                    counter.currentCount = count.Int;
                if (packet.OptionalValues.TryGetValue("wasResetting", out var wasRst))
                    counter.wasResetting = wasRst.Int != 0;
                if (packet.OptionalValues.TryGetValue("wasIncrementing", out var wasInc))
                    counter.wasIncrementing = wasInc.Int != 0;
                if (packet.OptionalValues.TryGetValue("receivedFirstSignal", out var rfs))
                    counter.receivedFirstSignal = rfs.Int != 0;
            }

            // LogicMemory
            var memory = go.GetComponent<LogicMemory>();
            if (memory != null)
            {
                bool targetOn = packet.Value.Int != 0 || (packet.Value.Type == Variant.TypeCode.Float && packet.Value.Float > 0.5f);
                if (memory.value != (targetOn ? 1 : 0))
                    memory.value = targetOn ? 1 : 0;
            }

            // LogicTimerSensor
            var timer = go.GetComponent<LogicTimerSensor>();
            if (timer != null)
            {
                if (packet.OptionalValues.TryGetValue("timeElapsed", out var elapsed))
                    timer.timeElapsedInCurrentState = elapsed.Float;
            }

            // LogicRibbonReader
            var ribbonReader = go.GetComponent<LogicRibbonReader>();
            if (ribbonReader != null)
            {
                ribbonReader.selectedBit = packet.Value.Int;
                if (packet.OptionalValues.TryGetValue("currentValue", out var val))
                    ribbonReader.currentValue = val.Int;
            }

            // LogicRibbonWriter
            var ribbonWriter = go.GetComponent<LogicRibbonWriter>();
            if (ribbonWriter != null)
            {
                ribbonWriter.selectedBit = packet.Value.Int;
                if (packet.OptionalValues.TryGetValue("currentValue", out var val))
                    ribbonWriter.currentValue = val.Int;
            }

            // Automatable
            var automatable = go.GetComponent<Automatable>();
            if (automatable != null)
            {
                bool targetOn = packet.Value.Int != 0 || (packet.Value.Type == Variant.TypeCode.Float && packet.Value.Float > 0.5f);
                if (automatable.GetAutomationOnly() != targetOn)
                    automatable.SetAutomationOnly(targetOn);
            }
        }
    }
}
