using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking
{
	/// <summary>
	/// The recent build traffic, kept so a client that was disconnected can be told what
	/// it missed.
	///
	/// A first join transfers the whole save, so the client starts level with the host. A
	/// reconnect does not - the world is already loaded and the client keeps it - and
	/// nothing fills the gap for anything that happened while it was away. Measured on
	/// both reconnect runs of a six-run batch and neither of the four without one: four
	/// InsulatedLiquidConduitUnderConstruction and a Ladder standing on the host with the
	/// client holding none of them, and the unfiled list proving the client did not have
	/// them unaddressed either - twenty categories of gas, liquid, dig placer and ore,
	/// with no construction site among them.
	///
	/// Replaying is cheap because the window is small. The alternative considered was
	/// sending the host's whole building set for the client to diff, which is 5,600
	/// entries for this colony on every reconnect, to repair four objects.
	///
	/// Only what the client missed, not the whole journal. A packet the client already
	/// applied would land on an occupied cell, TryPlace would refuse it, and the refusal
	/// is counted as buildNothing - a real failure counter that would then be measuring
	/// this instead. Bounding by when the client went away keeps that number meaning what
	/// it says.
	/// </summary>
	public static class BuildJournal
	{
		/// <summary>
		/// Enough to cover a disconnect of a few minutes of ordinary building. Bounded on
		/// purpose: this is a repair window, not a history, and an unbounded one would
		/// grow for the length of the session.
		/// </summary>
		private const int Capacity = 512;

		private readonly struct Entry
		{
			public readonly float At;
			public readonly IPacket Packet;
			public Entry(float at, IPacket packet) { At = at; Packet = packet; }
		}

		private static readonly Queue<Entry> _entries = new Queue<Entry>();

		/// <summary>Build packets replayed to clients that had been away.</summary>
		public static int Replayed { get; private set; }

		/// <summary>
		/// Rejoins that found nothing to replay. Beside Replayed this separates "the
		/// client missed nothing" from "the replay never ran", which is the distinction
		/// four earlier zeroes in this project were read without.
		/// </summary>
		public static int ReplaysWithNothingToSend { get; private set; }

		/// <summary>Remember a build announcement the host has just broadcast.</summary>
		public static void Record(IPacket packet)
		{
			if (!MultiplayerSession.IsHost) return;
			if (!(packet is IReplayableOnRejoin)) return;

			_entries.Enqueue(new Entry(Time.unscaledTime, packet));
			while (_entries.Count > Capacity) _entries.Dequeue();
		}

		/// <summary>
		/// Send a rejoining client everything recorded since it went away.
		/// </summary>
		public static void ReplayTo(MultiplayerPlayer player, float since)
		{
			if (!MultiplayerSession.IsHost || player == null) return;

			int sent = 0;
			foreach (var entry in _entries)
			{
				if (entry.At < since) continue;
				PacketSender.SendToPlayer(player.PlayerId, entry.Packet);
				sent++;
			}

			Replayed += sent;
			if (sent == 0)
			{
				ReplaysWithNothingToSend++;
				return;
			}

			DebugConsole.LogWarning(
				$"[BuildJournal] replayed {sent} build packet(s) to player {player.PlayerId} " +
				$"covering the {Time.unscaledTime - since:0.0}s it was away");
		}
	}
}
