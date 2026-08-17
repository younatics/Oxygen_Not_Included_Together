using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using System.IO;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
    /// <summary>
    /// One building's hit points, host to client.
    ///
    /// Damage used to ride along in StructureStatePacket's optional values,
    /// which meant it was only replicated for buildings that happen to have a
    /// StructureSyncerBase - and only when that syncer's other state changed.
    /// Neither condition holds where it matters. A Tile has no syncer at all, so
    /// a tile could crack on the host and read whole on the client forever; the
    /// storage, battery and reactor syncers set checkOptionalsValuesForChanges
    /// to false, so their hit points changed without ever triggering a send.
    /// Three buildings disagreed in one measured run, one of them in the
    /// direction that says the client damaged something the host never did.
    ///
    /// So damage has its own owner now, attached to every BuildingHP, and it is
    /// deliberately viewport-blind: a cracked wall stays cracked whether or not
    /// anyone watched it break, and the packet is small and rare.
    /// </summary>
    public class BuildingDamagePacket : IPacket, IRequiresLoadedWorld
    {
        public int NetId;
        public int HitPoints;

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();
            writer.Write(NetId);
            writer.Write(HitPoints);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();
            NetId = reader.ReadInt32();
            HitPoints = reader.ReadInt32();
        }

        /// <summary>
        /// What the receiver did with these, because the sender's counters
        /// cannot say. The host reported a settled sweep - nothing changed, so
        /// everything had been delivered - while two tiles still disagreed, and
        /// there was no way to tell an unsent packet from an unapplied one.
        /// </summary>
        public static int Received { get; private set; }
        public static int Unresolved { get; private set; }
        public static int NoHitPoints { get; private set; }
        public static int Applied { get; private set; }
        public static int AlreadyEqual { get; private set; }

        /// <summary>Corrections that ran and changed nothing - the interesting failure.</summary>
        public static int Ineffective { get; private set; }

        public static void ResetForNewSession()
        {
            Received = Unresolved = NoHitPoints = Applied = AlreadyEqual = Ineffective = Forced = 0;
            RepairedThroughGame = 0;
        }

        public static string Describe() =>
            $"received={Received} applied={Applied} forced={Forced} ineffective={Ineffective} same={AlreadyEqual} " +
            $"unresolved={Unresolved} nohp={NoHitPoints} via={ForcedVia}";

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.IsClient)
                return;

            Received++;

            if (!NetworkIdentityRegistry.TryGet(NetId, out var identity))
            {
                Unresolved++;
                return;
            }

            if (identity.gameObject.IsNullOrDestroyed())
            {
                Unresolved++;
                return;
            }

            var hp = identity.gameObject.GetComponent<BuildingHP>();
            if (hp == null)
            {
                NoHitPoints++;
                return;
            }

            if (hp.HitPoints == Mathf.Clamp(HitPoints, 0, hp.MaxHitPoints))
            {
                AlreadyEqual++;
                return;
            }

            // Counted from the effect, not the call.
            //
            // "applied=15" was incremented on reaching Apply, which says nothing
            // about whether the hit points moved - and this session has already
            // lost days to a counter that measured intent. If the correction is
            // being refused downstream, that has to show up here rather than
            // read as success.
            int before = hp.HitPoints;
            Apply(hp, HitPoints);

            if (hp.HitPoints == before)
                Ineffective++;
            else
                Applied++;
        }

        /// <summary>
        /// Repairs put through the game's own BuildingHP.Repair, which fires the triggers
        /// that end the repair errand. The pair to Forced: if this rises and Forced stays
        /// at zero, the reflection write is no longer how buildings get healed here.
        /// </summary>
        public static int RepairedThroughGame { get; private set; }

        /// <summary>Repairs applied by writing the value, because the event declined them.</summary>
        public static int Forced { get; private set; }

        /// <summary>Which member the write went through, or why it could not.</summary>
        public static string ForcedVia { get; private set; } = "not needed";

        private static System.Reflection.PropertyInfo _hitPointsProperty;
        private static System.Reflection.FieldInfo _hitPointsField;
        private static bool _lookedUp;

        /// <summary>
        /// Last resort for the one case the damage event will not do: raising hit
        /// points. It names the member it found so the next reader does not have
        /// to guess whether reflection is doing anything.
        /// </summary>
        private static void ForceHitPoints(BuildingHP buildingHP, int hostHp)
        {
            if (!_lookedUp)
            {
                _lookedUp = true;
                var type = typeof(BuildingHP);
                const System.Reflection.BindingFlags any =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                _hitPointsProperty = type.GetProperty("HitPoints", any);
                if (_hitPointsProperty != null && !_hitPointsProperty.CanWrite)
                    _hitPointsProperty = null;

                if (_hitPointsProperty == null)
                {
                    // "hitpoints" - all lower case, which is why three plausible
                    // camel-cased guesses found nothing and the enumeration below
                    // had to be written to answer it.
                    foreach (var name in new[] { "hitpoints", "hitPoints", "_hitPoints", "<HitPoints>k__BackingField" })
                    {
                        _hitPointsField = type.GetField(name, any);
                        if (_hitPointsField != null && _hitPointsField.FieldType == typeof(int)) break;
                        _hitPointsField = null;
                    }
                }

                if (_hitPointsProperty != null)
                {
                    ForcedVia = "property HitPoints";
                }
                else if (_hitPointsField != null)
                {
                    ForcedVia = "field " + _hitPointsField.Name;
                }
                else
                {
                    // Names it rather than reporting a dead end. Three guesses at
                    // a field name found nothing, and one more round of guessing
                    // is exactly the loop this session keeps paying for - so it
                    // lists what is actually there and the next run reads it.
                    var candidates = new System.Collections.Generic.List<string>();
                    foreach (var f in type.GetFields(any))
                        if (f.FieldType == typeof(int) || f.FieldType == typeof(float))
                            candidates.Add($"f:{f.Name}:{f.FieldType.Name}");
                    foreach (var p in type.GetProperties(any))
                        if (p.PropertyType == typeof(int) || p.PropertyType == typeof(float))
                            candidates.Add($"p:{p.Name}:{p.PropertyType.Name}{(p.CanWrite ? "(w)" : "")}");

                    ForcedVia = "none of the guesses; numeric members are [" +
                        string.Join(" ", candidates) + "]";
                }
            }

            try
            {
                if (_hitPointsProperty != null) _hitPointsProperty.SetValue(buildingHP, hostHp);
                else if (_hitPointsField != null) _hitPointsField.SetValue(buildingHP, hostHp);
                else return;

                Forced++;

                // No follow-up event, deliberately.
                //
                // Writing the number alone can leave a stale repair errand and
                // overlay, which is normally the reason to go through the game's
                // own event rather than the field. It does not matter here: this
                // only runs on a client, and a client's chore system is switched
                // off - the host owns every errand in the colony. There is also
                // no GameHashes entry for a repair to trigger, and guessing at a
                // hash name is a worse failure than a stale icon.

            }
            catch (System.Exception ex)
            {
                ForcedVia = "failed: " + ex.GetType().Name;
            }
        }

        /// <summary>
        /// Bring this peer's damage in line with the host's.
        ///
        /// BuildingHP.HitPoints has no setter, so the difference goes through the
        /// game's own damage event - the same one raised when something actually
        /// breaks a building. That matters for more than tidiness: the event is
        /// what updates Damaged, queues the repair errand and puts the broken
        /// overlay on. Writing a number would change the number and nothing else.
        /// </summary>
        public static void Apply(BuildingHP buildingHP, int hostHitPoints)
        {
            // Clamped to what this building can hold. The game's damage handler
            // just subtracts, so an unclamped negative delta would push hit
            // points past the maximum and leave it permanently over-healed.
            int hostHp = Mathf.Clamp(hostHitPoints, 0, buildingHP.MaxHitPoints);
            int delta = buildingHP.HitPoints - hostHp;
            if (delta == 0) return;

            // Positive delta: this peer is healthier than the host, so damage it
            // by the difference. Negative: the host repaired, so heal by it.
            //
            // Inside the scope, because on a client the mod refuses locally
            // simulated damage and the source string it used to check by never
            // survived BoxingTrigger's wrapper - so this correction was being
            // refused along with the local damage it was meant to override.
            using (Patches.World.BuildingHP_OnDoBuildingDamage_Patch.HostDamageScope())
            {
                buildingHP.gameObject.BoxingTrigger((int)GameHashes.DoBuildingDamage, new BuildingHP.DamageSourceInfo
                {
                    damage = delta,
                    source = Patches.World.BuildingHP_OnDoBuildingDamage_Patch.MultiplayerSource,
                    popString = string.Empty,
                });
            }

            // Healing needs a different route.
            //
            // The damage event subtracts, and a negative amount is not a repair
            // as far as the game is concerned - it declines it. So a building the
            // host repaired stayed broken on the client forever, while damage in
            // the other direction worked: two corrections per run that ran and
            // moved nothing. The event is still the right first choice because it
            // updates Damaged, queues the repair errand and sets the overlay; this
            // only steps in when it demonstrably did nothing.
            //
            // BuildingHP.Repair before reflection, and that ordering is the fix.
            //
            // Writing the field moved the number and told nothing. Read out of
            // Assembly-CSharp, Repair raises the hit points and then fires two
            // triggers - one for the change and, when the building is whole again, a
            // second one - and those are what end the repair errand. A field write
            // fires neither, so on the client the wire came back to full health and
            // kept its repair job forever: state_compare reported
            // "chore|Wire@38797|waiting host=0 client=1" every run while hp DIFFERENT
            // stayed 0, which is precisely "both peers agree it is fixed, one of them
            // is still showing the work".
            //
            // That is not cosmetic. A player on the client sees an errand the host has
            // finished, and acts on what they see.
            if (buildingHP.HitPoints < hostHp)
            {
                buildingHP.Repair(hostHp - buildingHP.HitPoints);
                RepairedThroughGame++;
            }

            // Reflection stays, as the last resort it already was, and it now says
            // something when it runs: Repair could not get there.
            if (buildingHP.HitPoints != hostHp)
                ForceHitPoints(buildingHP, hostHp);
        }
    }
}
