using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;

namespace ONI_Together.Networking
{
	internal static class ProtocolCompatibility
	{
		public const int CurrentProtocolVersion = 1;

		private static int? _packetFingerprint;
		private static string _modVersion;

		public static int PacketFingerprint
		{
			get
			{
				using var _ = Profiler.Scope();

				return _packetFingerprint ??= PacketRegistry.GetRegisteredPacketFingerprint();
			}
		}

		public static string ModVersion
		{
			get
			{
				using var _ = Profiler.Scope();

				return _modVersion ??= ONI_Together.ModUpdater.Updater.GetVersion();
			}
		}

		/// <summary>
		/// This assembly's build identity - a new value every time the mod is compiled.
		///
		/// The mod version is a release string ("0.7.3-alpha") and does not change
		/// between two builds of different source, so it cannot tell a peer on last
		/// week's code from a peer on this week's. Everyone who installs from the
		/// Workshop receives the same bytes and therefore the same value here; two
		/// people who each compiled the same source do not.
		///
		/// It is reported rather than enforced. Rejecting on it would be correct for a
		/// desync and wrong for anyone testing a local build against a Workshop friend,
		/// and being unable to connect is a worse first experience than being told why
		/// the session is behaving strangely.
		/// </summary>
		public static string BuildId =>
			typeof(ProtocolCompatibility).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8);

		public static bool Matches(int protocolVersion, int packetFingerprint, string modVersion)
		{
			using var _ = Profiler.Scope();

			// The mod version belongs in the decision, and was only ever in the
			// explanation.
			//
			// BuildMismatchReason has compared mod versions since it was written, but it
			// is called to explain a rejection this method has already made - and this
			// method looked at the protocol version and the packet fingerprint only. A
			// peer whose code differs without its packet set differing was accepted, so
			// the MOD_VERSION_MISMATCH string could never be reached and the setting
			// tooltip promising that mod versions are checked was not true.
			//
			// That combination is not theoretical. Two peers a few commits apart share a
			// packet registry and disagree about the world in ways indistinguishable from
			// the sync defects this mod exists to fix; an afternoon went into one such
			// pair before a hash comparison found it.
			//
			// An empty version means a peer too old to send one. Those already fail on
			// the metadata check, and treating a blank as a mismatch here would only
			// change which message they see.
			if (!string.IsNullOrEmpty(modVersion) && modVersion != ModVersion)
				return false;

			return protocolVersion == CurrentProtocolVersion
				&& packetFingerprint == PacketFingerprint;
		}

		public static string BuildMismatchReason(int remoteProtocolVersion, int remotePacketFingerprint, string remoteModVersion, bool hasMetadata)
		{
			using var _ = Profiler.Scope();

            if (!hasMetadata)
            {
                return STRINGS.UI.PROTOCOL.NO_METADATA;
            }

            if (remoteProtocolVersion != CurrentProtocolVersion)
            {
                return string.Format(STRINGS.UI.PROTOCOL.PROTOCOL_MISMATCH, CurrentProtocolVersion, remoteProtocolVersion);
            }

            if (remotePacketFingerprint != PacketFingerprint)
            {
                return string.Format(STRINGS.UI.PROTOCOL.PACKET_REGISTRY_MISMATCH, PacketFingerprint, remotePacketFingerprint);
            }

            if (!string.IsNullOrEmpty(remoteModVersion) && remoteModVersion != ModVersion)
            {
                return string.Format(STRINGS.UI.PROTOCOL.MOD_VERSION_MISMATCH, ModVersion, remoteModVersion);
            }

            return STRINGS.UI.PROTOCOL.INCOMPATIBLE;
        }
	}
}
