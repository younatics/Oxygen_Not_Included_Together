using HarmonyLib;
using Klei.AI;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Animation;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.DuplicantActions;
using System;
using System.Linq;
using Shared.Profiling;
using static STRINGS.UI.CLUSTERMAP.ROCKETS;

namespace ONI_Together.Patches.KleiPatches
{
	class KAnimControllerBase_Patches
	{
		/// Playing Overrides

		/// <summary>
		/// Animations are never blocked. Stated plainly, because it used to be
		/// disguised.
		///
		/// There was a flag here - _allowedToPlayAnims - with AllowAnims and
		/// ForbidAnims called from eight places around the code that applies received
		/// animations. Nothing read it: CanPlayAnims returned a literal true and the
		/// expression that would have consulted the flag was commented out beside it.
		/// So eight call sites carefully toggled a value with no effect, and anyone
		/// reading them would reasonably conclude that a client's local animations are
		/// gated while a remote one is being applied. They are not.
		///
		/// Removed rather than repaired. Restoring the gate means changing what the
		/// client draws, and that has to be measured, not assumed - twice yesterday a
		/// change to animation and UI paths made on reasoning alone killed the client.
		/// Deleting the machinery leaves the behaviour exactly as it has been, and
		/// stops the code claiming a control it does not have.
		/// </summary>
		public static bool CanPlayAnims => true;



		///Play() has internal calls to "Queue", prevent duplicate entries
		static bool LockAnimSending = false;
		static void Unlock() => LockAnimSending = false;
		static void SendAnimPacketToClients(KAnimControllerBase __instance, bool queueing, HashedString[] anims, KAnim.PlayMode mode = KAnim.PlayMode.Once, float speed = 1f, float time_offset = 0f)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession || MultiplayerSession.IsClient)
				return;
			if (__instance.gameObject.IsNullOrDestroyed() || !__instance.gameObject.TryGetComponent<KPrefabID>(out var id))
				return;

			if (!id.HasTag(GameTags.BaseMinion) && !id.HasTag(GameTags.Creature)) // Allow BaseMinion and Creature
				return;

			int netId = __instance.GetNetId();
			if(netId == 0)
			{
				DebugConsole.LogWarning("no netId found on " + __instance.GetProperName());
				return;
			}

			if (LockAnimSending)
				return;

			LockAnimSending = true;
			PacketSender.SendToAllClients(new PlayAnimPacket(netId, anims, queueing,mode,speed,time_offset));
		}

		[HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.Play), [typeof(HashedString), typeof(KAnim.PlayMode), typeof(float), typeof(float)])]
		public class KAnimControllerBase_Play_Patch
		{
			public static bool Prefix(KAnimControllerBase __instance, HashedString anim_name, KAnim.PlayMode mode, float speed, float time_offset)
			{
				using var _ = Profiler.Scope();

				try
				{
					if (!MultiplayerSession.InSession)
						return true;
					if (__instance.IsNullOrDestroyed() || !__instance.enabled) return CanPlayAnims;

					if(MultiplayerSession.IsHost)
						SendAnimPacketToClients(__instance, false, [anim_name],mode,speed,time_offset);
					return CanPlayAnims;
				}
				catch (Exception ex)
				{
					DebugConsole.LogError($"[KAnimControllerBase_Play_Patch.Prefix] {ex}");
					return true;
				}
			}

			public static void Postfix(KAnimControllerBase __instance) => Unlock();
		}

		[HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.Play), [typeof(HashedString[]), typeof(KAnim.PlayMode)])]
		public class KAnimControllerBase_PlayRange_Patch
		{
			public static bool Prefix(KAnimControllerBase __instance, HashedString[] anim_names, KAnim.PlayMode mode)
			{
				using var _ = Profiler.Scope();

				try
				{
					if (!MultiplayerSession.InSession)
						return true;
					if (__instance.IsNullOrDestroyed() || !__instance.enabled) return CanPlayAnims;
					if (MultiplayerSession.IsHost)
						SendAnimPacketToClients(__instance, false, anim_names, mode);
					return CanPlayAnims;
				}
				catch (Exception ex)
				{
					DebugConsole.LogError($"[KAnimControllerBase_PlayRange_Patch.Prefix] {ex}");
					return true;
				}
			}

			public static void Postfix(KAnimControllerBase __instance) => Unlock();
		}

		[HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.Queue))]
		public class KAnimControllerBase_Queue_Patch
		{
			public static bool Prefix(KAnimControllerBase __instance, HashedString anim_name, KAnim.PlayMode mode, float speed, float time_offset)
			{
				using var _ = Profiler.Scope();

				try
				{
					if (!MultiplayerSession.InSession)
						return true;
					if (__instance.IsNullOrDestroyed() || !__instance.enabled) return CanPlayAnims;
					if (MultiplayerSession.IsHost)
						SendAnimPacketToClients(__instance, true, [anim_name], mode, speed, time_offset);
					return CanPlayAnims;
				}
				catch (Exception ex)
				{
					DebugConsole.LogError($"[KAnimControllerBase_Queue_Patch.Prefix] {ex}");
					return true;
				}
			}

			public static void Postfix(KAnimControllerBase __instance) => Unlock();
		}

		/// Kanim Overrides

		private static bool TogglingOverrideFromPacket = false;

		/// <summary>
		/// Whether this controller can carry an anim override at all.
		///
		/// This killed a client. Klei's AddAnimOverrides asserts "Anim overrides
		/// containing additional symbols require a symbol override controller"
		/// when the target has no SymbolOverrideController, and RemoveAnimOverrides
		/// walks straight into SymbolOverrideControllerUtil.TryRemoveBuildOverride
		/// with a null controller and throws. Both happened, in that order, at
		/// 13:15:38.395 - the assert at ERROR level, the NullReferenceException
		/// 10 ms later - and the process ran OnApplicationQuit a second after.
		///
		/// Nothing on the receiving side checked the target. The sender only sends
		/// for BaseMinion, but the id it sends is resolved on the other peer, and
		/// when an id resolves to the wrong object - which this session did
		/// repeatedly, 39 VitalStats packets landing on something with no Calories
		/// in the same twenty seconds - the override is applied to whatever came
		/// back. A duplicant has a SymbolOverrideController; a critter, an item or
		/// a building does not.
		///
		/// Checking is not a substitute for resolving ids correctly. It is the
		/// difference between a missing animation and a closed game.
		/// </summary>
		private static bool CanCarryOverrides(KAnimControllerBase kbac, string kanim, bool adding)
		{
			if (kbac.IsNullOrDestroyed() || kbac.gameObject.IsNullOrDestroyed())
				return false;

			if (kbac.GetComponent<SymbolOverrideController>() == null)
			{
				ThrottledLog.Warn(
					$"[AnimOverride] refusing to {(adding ? "add" : "remove")} '{kanim}' on " +
					$"'{kbac.gameObject.PrefabID()}': it has no SymbolOverrideController. " +
					"Applying it asserts inside Klei and then throws, which closes the game. " +
					"The id this packet carried almost certainly resolved to the wrong object.");
				return false;
			}
			return true;
		}

		internal static void AddKanimOverride(KAnimControllerBase kbac, string kanim, float priority)
		{
			using var _ = Profiler.Scope();

			if (!CanCarryOverrides(kbac, kanim, adding: true))
				return;

			TogglingOverrideFromPacket = true;
			try
			{
				if (Assets.TryGetAnim(kanim, out var anim))
				{
					kbac.AddAnimOverrides(anim, priority);
				}
				else
					DebugConsole.LogWarning("could not find anim " + kanim);

				// Was one unthrottled line per override. 2037 additions and 2069
				// removals in a fourteen-minute session, and they are what the tail
				// of a dying log is made of - which is exactly when the log has to
				// be readable.
				ThrottledLog.Info("[AnimOverride] overrides added");
			}
			finally { TogglingOverrideFromPacket = false; }
		}

		internal static void RemoveKanimOverride(KAnimControllerBase kbac, string kanim)
		{
			using var _ = Profiler.Scope();

			if (!CanCarryOverrides(kbac, kanim, adding: false))
				return;

			TogglingOverrideFromPacket = true;
			try
			{
				if (Assets.TryGetAnim(kanim, out var anim))
				{
					kbac.RemoveAnimOverrides(anim);
				}
				else
					DebugConsole.LogWarning("could not find anim " + kanim);

				ThrottledLog.Info("[AnimOverride] overrides removed");
			}
			finally { TogglingOverrideFromPacket = false; }
		}


		[HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.AddAnimOverrides))]
		public class KAnimControllerBase_AddAnimOverrides_Patch
		{
			public static bool Prefix(KAnimControllerBase __instance, KAnimFile kanim_file, float priority = 0f)
			{
				using var _ = Profiler.Scope();

				try
				{
					if (!MultiplayerSession.InSession) return kanim_file != null;

					//leave to minions for now, potentially remove later
					if (!__instance.HasTag(GameTags.BaseMinion))
						return kanim_file != null;

					if (MultiplayerSession.IsClient)
						return TogglingOverrideFromPacket;

					Console.WriteLine("sending addAnimOveridePacket");
					PacketSender.SendToAllClients(new ToggleAnimOverridePacket(__instance.gameObject, kanim_file, priority));
					return kanim_file != null;
				}
				catch (Exception ex)
				{
					DebugConsole.LogError($"[KAnimControllerBase_AddAnimOverrides_Patch.Prefix] {ex}");
					return kanim_file != null;
				}
			}
		}

		[HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.RemoveAnimOverrides))]
		public class KAnimControllerBase_RemoveAnimOverrides_Patch
		{
			public static bool Prefix(KAnimControllerBase __instance, KAnimFile kanim_file)
			{
				using var _ = Profiler.Scope();

				try
				{
					if (!MultiplayerSession.InSession) return kanim_file != null;

					//leave to minions for now, potentially remove later
					if (!__instance.HasTag(GameTags.BaseMinion))
						return kanim_file != null;

					if (MultiplayerSession.IsClient)
						return TogglingOverrideFromPacket;

					Console.WriteLine("sending removeAnimOveridePacket");
					PacketSender.SendToAllClients(new ToggleAnimOverridePacket(__instance.gameObject, kanim_file));
					return kanim_file != null;
				}
				catch (Exception ex)
				{
					DebugConsole.LogError($"[KAnimControllerBase_RemoveAnimOverrides_Patch.Prefix] {ex}");
					return kanim_file != null;
				}
			}
		}

		/// Symbol Visibility

		[HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.SetSymbolVisiblity))]
		public class KAnimControllerBase_SetSymbolVisiblity_Patch
		{
			public static void Prefix(KAnimControllerBase __instance, KAnimHashedString symbol, bool is_visible)
			{
				using var _ = Profiler.Scope();

				try
				{
					if (!Utils.IsHostMinion(__instance))
						return;

					PacketSender.SendToAllClients(new SymbolVisibilityTogglePacket(__instance, symbol, is_visible));
				}
				catch (Exception ex)
				{
					DebugConsole.LogError($"[KAnimControllerBase_SetSymbolVisiblity_Patch.Prefix] {ex}");
				}
			}
		}
	}
}
