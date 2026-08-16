using System.IO;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.Networking.Packets.Core
{
    /// <summary>
    /// A slice of what the host holds, so the client can notice what it is missing without
    /// anybody having to know what kind of object went astray.
    ///
    /// Every defect this project found today in the "the client does not have it" family
    /// was found by hand, one type at a time, after a player noticed. A Wheezewort a
    /// duplicant sowed exists on the host and never on the client - the host says so in its
    /// own log, once, in a line nobody reads unless they already suspect plants. Before
    /// that it was a repair proxy, and before that a fabricator's second container. Each
    /// was a day of log reading, and each fix covered exactly one kind of object.
    ///
    /// The harness already answers the general question: netid_compare takes both peers'
    /// id lists after a run and prints what one has and the other does not. That is how the
    /// missing plant was found at all. Nothing does it while the game is running, so a
    /// player gets no warning and the evidence only exists if somebody was running the
    /// scenario suite.
    ///
    /// So the host walks its own registry, forty ids at a time, four times a second, and
    /// the client looks each one up. That is 640 bytes a second against the ninety thousand
    /// sends this host already makes in three thousand frames, and a colony of ten thousand
    /// objects is covered end to end in about a minute.
    ///
    /// Ids only, deliberately. Prefab names would make the report readable on the spot and
    /// would also multiply the packet size by five and push it past the 1000-byte Riptide
    /// limit, into the chunking path this codebase already documents as where the bugs
    /// live. The client cannot name an object it does not have, but the host's log already
    /// prints "[Announce] sent &lt;prefab&gt;#&lt;id&gt;" and the NETID rows carry the same pairing,
    /// so an id is enough to find out what it was.
    ///
    /// This reports and does not repair. Creating the missing object is the obvious next
    /// step and it is the one that killed the client twice today - a duplicant rebuilt from
    /// a prefab name has no personality and the renderer throws every frame, and a plant
    /// rebuilt the same way threw from Unity's LateUpdate 159 times in one run. Detection
    /// is worth having on its own: it turns "a player says buildings disappear" into a
    /// counter and a list of ids.
    /// </summary>
    public class IdCensusPacket : IPacket
    {
        /// <summary>
        /// Which pass over the registry this batch belongs to.
        ///
        /// The client needs cycle boundaries to tell a real absence from a packet still in
        /// flight. An object announced a moment ago is legitimately missing right now and
        /// will be there next pass; one missing on two consecutive passes is not in flight,
        /// it is gone. Without this the report would be mostly noise, and noise in a
        /// warning is how a real signal gets ignored.
        /// </summary>
        public int Cycle;

        public int[] NetIds;

        /// <summary>
        /// The player-set priority of each id, packed as class*100+value, or -1 for an
        /// object that has no Prioritizable.
        ///
        /// Riding along here rather than getting its own mechanism, because the walk is
        /// already happening and the field is two bytes.
        ///
        /// The gap it closes was reported from real play and took a while to place. An
        /// atmo suit checkpoint "did not match" between the peers - and the three
        /// checkpoints themselves agree, at the same cells with the same ids, in every run.
        /// The suits inside them do not: the host holds Atmo_Suit#1776225776 at priority 8
        /// and the client holds it at 5, which is the default, so the client never heard
        /// about the change. A waiting-chore difference follows from that, and the two
        /// together are what a player sees as a building out of sync.
        ///
        /// Priority is sampled periodically for structures - AddCommonState, inside
        /// StructureSyncerBase - and a suit is a Pickupable, so nothing sampled it. Its
        /// only path was event interception, and the client's own counter reads
        /// prioritiesDropped=89: changes discarded because the object had no id yet. There
        /// is nothing to re-send a dropped one, which is the same shape as every other
        /// defect this project has had to chase by hand.
        /// </summary>
        public short[] Priorities;

        /// <summary>
        /// Per id: bit 0 says this object can be swept, bit 1 says it is marked for it.
        ///
        /// Sweep marks are replicated by replaying the Clear tool at the same cell on the
        /// far peer, and nothing else. Measured: 45 items marked on the host against 37 on
        /// the client, 8 marked on the host and on neither the other way round, after marks
        /// were set through MarkForClear rather than through a drag.
        ///
        /// That gap matters in ordinary play for a reason the drag path cannot cover: a
        /// mark is also CLEARED when a duplicant picks the item up, and that is not a tool
        /// action at all. The client's duplicants never pick anything up, so a mark it
        /// received stays on screen after the host's colony has collected the debris - which
        /// is what "sweep does not work" looks like from the client's side.
        ///
        /// One byte per id on a walk that is already happening, and it covers marks made or
        /// cleared by any path rather than by the one path somebody remembered to patch.
        /// </summary>
        public byte[] Flags;

        internal const byte FlagClearable = 1;
        internal const byte FlagMarkedForClear = 2;

        private const short NoPriority = -1;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Cycle);
            int n = NetIds?.Length ?? 0;
            writer.Write(n);
            for (int i = 0; i < n; i++)
            {
                writer.Write(NetIds[i]);
                writer.Write(Priorities != null && i < Priorities.Length ? Priorities[i] : NoPriority);
                writer.Write(Flags != null && i < Flags.Length ? Flags[i] : (byte)0);
            }
        }

        public void Deserialize(BinaryReader reader)
        {
            Cycle = reader.ReadInt32();
            int count = reader.ReadInt32();

            // A length read off the wire is a length somebody else chose. This one only
            // ever comes from our own sender, but the packet-robustness tests feed
            // handlers deliberate rubbish, and a bad count here would ask for an array of
            // whatever integer the fuzzer picked.
            if (count < 0 || count > 4096)
            {
                NetIds = new int[0]; Priorities = new short[0]; Flags = new byte[0];
                return;
            }

            NetIds = new int[count];
            Priorities = new short[count];
            Flags = new byte[count];
            for (int i = 0; i < count; i++)
            {
                NetIds[i] = reader.ReadInt32();
                Priorities[i] = reader.ReadInt16();
                Flags[i] = reader.ReadByte();
            }
        }

        public void OnDispatched()
        {
            if (MultiplayerSession.IsHost) return;
            IdCensus.Receive(Cycle, NetIds, Priorities, Flags);
        }
    }
}
