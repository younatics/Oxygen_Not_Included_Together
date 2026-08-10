using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Animation;
using ONI_Together.Networking.Packets.Core;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class AnimSyncTests
	{
		[UnitTest(name: "Anim reconciliation: detects wrong animation", category: "Animation")]
		public static UnitTestResult DetectsWrongAnimation()
		{
			var identities = NetworkIdentityRegistry.AllIdentities;
			foreach (var id in identities)
			{
				if (!id.gameObject.TryGetComponent<KBatchedAnimController>(out var kbac))
					continue;
				if (!id.gameObject.GetComponent<KPrefabID>()?.HasTag(GameTags.BaseMinion) ?? true)
					continue;

				if (kbac.CurrentAnim == null)
					continue;

				string currentAnim = kbac.CurrentAnim.name;
				if (string.IsNullOrEmpty(currentAnim))
					continue;

				var wrongHash = new HashedString("fake_anim_that_doesnt_exist");
				if (kbac.currentAnim == wrongHash)
					return UnitTestResult.Fail("Hash collision with fake anim");

				return UnitTestResult.Pass($"Minion '{id.gameObject.name}' anim='{currentAnim}', would detect mismatch");
			}
			return UnitTestResult.Skip("no minions with an anim controller in the scene");
		}

		[UnitTest(name: "Anim reconciliation: elapsed time readable", category: "Animation")]
		public static UnitTestResult ElapsedTimeReadable()
		{
			var identities = NetworkIdentityRegistry.AllIdentities;
			foreach (var id in identities)
			{
				if (!id.gameObject.TryGetComponent<KBatchedAnimController>(out var kbac))
					continue;
				if (!id.gameObject.GetComponent<KPrefabID>()?.HasTag(GameTags.BaseMinion) ?? true)
					continue;

				float elapsed = kbac.GetElapsedTime();
				return UnitTestResult.Pass($"ElapsedTime={elapsed:F3}s on '{id.gameObject.name}'");
			}
			return UnitTestResult.Skip("no minions in the scene");
		}

		[UnitTest(name: "Anim reconciliation: reflection helper resolves", category: "Animation")]
		public static UnitTestResult ReflectionHelperResolves()
		{
			var identities = NetworkIdentityRegistry.AllIdentities;
			foreach (var id in identities)
			{
				if (!id.gameObject.TryGetComponent<KBatchedAnimController>(out var kbac))
					continue;

				float before = kbac.GetElapsedTime();
				AnimReconciliationHelper.TrySetElapsedTime(kbac, before);
				float after = kbac.GetElapsedTime();

				return UnitTestResult.Pass($"SetElapsedTime resolved. Before={before:F3}, After={after:F3}");
			}
			return UnitTestResult.Skip("no anim controllers in the scene");
		}

		[UnitTest(name: "Anim sync packet: roundtrip", category: "Animation")]
		public static UnitTestResult AnimSyncPacketRoundtrip()
		{
			var packet = new AnimSyncPacket
			{
				NetId = 42,
				AnimHash = new HashedString("idle_loop").hash,
				Mode = (byte)KAnim.PlayMode.Loop,
				Speed = 1.25f,
				ElapsedTime = 2.5f
			};

			using var ms = new MemoryStream();
			using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);

			ms.Position = 0;

			var copy = new AnimSyncPacket();
			using (var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);

			if (copy.NetId != packet.NetId || copy.AnimHash != packet.AnimHash || copy.Mode != packet.Mode)
				return UnitTestResult.Fail("Packet int fields did not roundtrip");
			if (copy.Speed != packet.Speed || copy.ElapsedTime != packet.ElapsedTime)
				return UnitTestResult.Fail("Packet float fields did not roundtrip");

			return UnitTestResult.Pass("AnimSyncPacket serialize/deserialize roundtrip succeeded");
		}

		[UnitTest(name: "Anim packets: bypass bulk queue", category: "Animation")]
		public static UnitTestResult AnimPacketsBypassBulkQueue()
		{
			bool animSyncBulk = typeof(IBulkablePacket).IsAssignableFrom(typeof(AnimSyncPacket));
			bool playAnimBulk = typeof(IBulkablePacket).IsAssignableFrom(typeof(PlayAnimPacket));
			if (animSyncBulk || playAnimBulk)
				return UnitTestResult.Fail("Animation packets still route through the bulk queue");

			return UnitTestResult.Pass("AnimSyncPacket and PlayAnimPacket send directly");
		}

		[UnitTest(name: "Anim sync: non-minion snapshots use coordinator", category: "Animation")]
		public static UnitTestResult NonMinionSnapshotsUseCoordinator()
		{
			bool perEntityHeartbeat = typeof(IRender1000ms).IsAssignableFrom(typeof(AnimStateSyncer));
			if (perEntityHeartbeat)
				return UnitTestResult.Fail("AnimStateSyncer still runs its own 1000ms heartbeat");

			return UnitTestResult.Pass("AnimStateSyncer relies on the shared coordinator");
		}

		[UnitTest(name: "Anim sync: non-minion entities discoverable", category: "Animation")]
		public static UnitTestResult NonMinionAnimEntitiesDiscoverable()
		{
			// Eligibility decides, and it is checked before the component is
			// demanded rather than after.
			//
			// This used to require an AnimStateSyncer on anything registered that
			// had a KBatchedAnimController and was not a duplicant - which is
			// nearly every visible object, ore included. Ore is not anim-synced and
			// should not be, so the first ineligible object the enumeration reached
			// failed the test. It surfaced when a change to stored-item ids altered
			// what came first: a copper ore instead of a critter.
			//
			// It also returned on the very first object it looked at, pass or fail,
			// so a test named for every non-minion entity examined exactly one. Both
			// halves are checked now, in both directions, across all of them.
			var missing = new List<string>();
			var extra = new List<string>();
			int eligible = 0;

			foreach (var id in NetworkIdentityRegistry.AllIdentities)
			{
				if (id.IsNullOrDestroyed() || id.gameObject.IsNullOrDestroyed()) continue;

				var go = id.gameObject;
				if (go.GetComponent<KPrefabID>()?.HasTag(GameTags.BaseMinion) ?? false)
					continue;

				bool shouldSync = AnimSyncEligibility.IsAnimatedNonMinion(go);
				bool hasSyncer = go.TryGetComponent<AnimStateSyncer>(out var _);

				if (shouldSync) eligible++;

				if (shouldSync && !hasSyncer) missing.Add(go.name);
				else if (!shouldSync && hasSyncer) extra.Add(go.name);
			}

			if (missing.Count > 0 || extra.Count > 0)
			{
				var parts = new List<string>();
				if (missing.Count > 0)
					parts.Add($"{missing.Count} eligible without a syncer: {string.Join(", ", missing.Take(5))}");
				if (extra.Count > 0)
					parts.Add($"{extra.Count} ineligible carrying one: {string.Join(", ", extra.Take(5))}");
				return UnitTestResult.Fail(string.Join("; ", parts));
			}

			return eligible == 0
				? UnitTestResult.Skip("no non-minion animated network entities in the scene")
				: UnitTestResult.Pass($"{eligible} eligible entities, all carrying a syncer, none carrying one they should not");
		}

		[UnitTest(name: "Anim resync request packet: roundtrip", category: "Animation")]
		public static UnitTestResult AnimResyncRequestPacketRoundtrip()
		{
			var packet = new AnimResyncRequestPacket
			{
				RequesterId = 99,
				NetIds = [11, 22, 33]
			};

			using var ms = new MemoryStream();
			using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);

			ms.Position = 0;

			var copy = new AnimResyncRequestPacket();
			using (var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);

			if (copy.RequesterId != packet.RequesterId)
				return UnitTestResult.Fail("RequesterId did not roundtrip");
			if (!copy.NetIds.SequenceEqual(packet.NetIds))
				return UnitTestResult.Fail("NetId list did not roundtrip");

			return UnitTestResult.Pass("AnimResyncRequestPacket serialize/deserialize roundtrip succeeded");
		}
	}
}
