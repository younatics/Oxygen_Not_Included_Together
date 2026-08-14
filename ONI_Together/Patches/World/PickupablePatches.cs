using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	public static class PickupablePatches
	{
        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.Take))]
        public static class PickupableTakePatch
        {
            public static void Postfix(Pickupable __instance)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession)
                        return;

                    var identity = __instance.GetNetIdentity();
                    if (identity == null || identity.NetId == 0)
                        return;
                    PacketSender.SendToAllClients(new PickupItemPacket { NetId = identity.NetId });
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[PickupableTakePatch] Exception: {ex}");
                }
            }
        }

        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.TakeUnit))]
        public static class PickupableTakeUnitPatch
        {
            public static void Postfix(Pickupable __instance)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession)
                        return;

                    var identity = __instance.GetNetIdentity();
                    if (identity == null || identity.NetId == 0)
                        return;
                    PacketSender.SendToAllClients(new PickupItemPacket { NetId = identity.NetId });
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[PickupableTakePatch] Exception: {ex}");
                }
            }
        }

        /// <summary>
        /// When two piles merge, the name goes to the survivor.
        ///
        /// Loose piles merge constantly - a dropped ore lands on one already there and
        /// the game folds them into one object, destroying the other. Nothing was
        /// watching that, so whichever object died took its NetId with it.
        ///
        /// Measured, it is the largest remaining disagreement between the two peers.
        /// One run ended with the client holding 265 loose items it had no name for and
        /// the host holding 381 names for loose items the client's registry did not have
        /// - all of them the same kinds, Oxygen, DirtyWater, Cuprite, Water,
        /// BasicPlantFood. The announcement side is not the problem: the host announced
        /// 208 spawns that run and every skipped one was a building or a placer, which
        /// the counters now state directly.
        ///
        /// What happens instead is that the client's own simulation makes a pile at the
        /// same place, the host's named copy arrives, and the two merge - and if the
        /// unnamed one is the survivor, the id the host is still using is now held by
        /// nothing. Every packet about that pile is a failed lookup from then on, which
        /// is where a client's lookup counter in the thousands comes from while the
        /// host's stays in the tens.
        ///
        /// Only a missing name is filled in. When both objects have one, one of them
        /// has to lose and the game has already chosen the survivor; overriding that
        /// would move an id the other peer is not moving, which is the shape of every
        /// rename loop this project has had. Those are counted and left alone.
        /// </summary>
        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.Absorb))]
        public static class PickupableAbsorbedPatch
        {
            /// <summary>Names carried across a merge instead of dying with the absorbed pile.</summary>
            public static int NamesRescued { get; private set; }

            /// <summary>
            /// Merges where both piles had a name. One is lost by the game's own choice
            /// and this does not interfere; the number is here so that if it climbs, the
            /// loss is visible rather than inferred from a lookup counter.
            /// </summary>
            public static int BothNamed { get; private set; }

            /// <summary>
            /// Merges where the game flagged neither side, or both. The direction is read
            /// from wasAbsorbed rather than assumed from which argument is which, so if
            /// that assumption ever stops holding this counter says so instead of the
            /// patch silently naming the wrong object.
            /// </summary>
            public static int DirectionUnclear { get; private set; }

            /// <summary>
            /// A Postfix, because wasAbsorbed is what decides which of the two objects
            /// this is about and the game has not set it yet on the way in.
            ///
            /// Direction is not inferred from the parameter names. The signature is
            /// Absorb(Pickupable pickupable) and the obvious reading - the argument is
            /// consumed - is probably right, but "probably right about a game API" is how
            /// three compiles and two misdiagnoses went in this project. The game marks
            /// the consumed object itself, so that flag is the one consulted.
            /// </summary>
            public static void Postfix(Pickupable __instance, Pickupable pickupable)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (!MultiplayerSession.InSession) return;
                    if (__instance == null || pickupable == null) return;

                    Pickupable dyingSide, survivingSide;
                    if (pickupable.wasAbsorbed && !__instance.wasAbsorbed)
                    {
                        dyingSide = pickupable;
                        survivingSide = __instance;
                    }
                    else if (__instance.wasAbsorbed && !pickupable.wasAbsorbed)
                    {
                        dyingSide = __instance;
                        survivingSide = pickupable;
                    }
                    else
                    {
                        DirectionUnclear++;
                        return;
                    }

                    var dying = dyingSide.GetNetIdentity();
                    if (dying == null || dying.NetId == 0) return;

                    var survivor = survivingSide.GetNetIdentity();
                    if (survivor == null) return;

                    if (survivor.NetId != 0)
                    {
                        BothNamed++;
                        return;
                    }

                    survivor.OverrideNetId(dying.NetId);
                    NamesRescued++;
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[PickupableAbsorbedPatch] Exception: {ex}");
                }
            }
        }

        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.OnCleanUp))]
        public static class PickupableCleanedUpPatch
        {
            private static long _skipCount;

            public static void Postfix(Pickupable __instance)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (__instance == null)
                        return;

                    if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession)
                        return;

                    var identity = __instance.GetNetIdentity();
                    if (identity == null || identity.NetId == 0)
                    {
                        long n = ++_skipCount;
                        if (n <= 5 || n % 100 == 0)
                        {
                            string name = __instance != null && __instance.gameObject != null ? __instance.gameObject.name : "<null>";
                            DebugConsole.Log($"[GroundPickup] skip NetId=0 name={name} #{n}");
                        }
                        return;
                    }

                    // Sent with where and what, not just the number. The client needs a
                    // second way to find the item: when the id misses it used to keep the
                    // pile forever, and the host would later reissue that number.
                    PacketSender.SendToAllClients(
                        new GroundItemPickedUpPacket(__instance.gameObject, identity.NetId));
                    //PacketSender.SendToAllClients(new PickupItemPacket { NetId = identity.NetId }); // Display FX for object
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[PickupableCleanedUpPatch] Exception: {ex}");
                }
            }
        }
    }
}
