using ONI_Together.Misc;
using ONI_Together.Networking;
using ONI_Together.Networking.States;
using Steamworks;

public class MultiplayerPlayer
{
	public ulong PlayerId { get; private set; }
	public string PlayerName { get; set; }
	public bool IsLocal => PlayerId == NetworkConfig.GetLocalID();

	public int AvatarImageId { get; private set; } = -1;
	//public HSteamNetConnection? Connection { get; set; } = null;
	public object? Connection { get; set; } = null;
	public bool IsConnected => Connection != null;
	public bool ProtocolVerified { get; set; }

	public ClientReadyState readyState = ClientReadyState.Ready;

	/// <summary>
	/// When this player last reported that it was loading, so the send gate can
	/// give up on a client that says Loading and never says Ready rather than
	/// starving it for the rest of the session.
	/// </summary>
	public float LoadingSince;

	/// <summary>
	/// Records the state and the moment together, because the gate needs both.
	///
	/// The Loading report used to reach the Riptide server and stop there: the
	/// host knew a client was loading for the purpose of matching up its
	/// reconnect id, while the player object it broadcasts against still said
	/// Ready - the default. So world packets kept flowing to a peer with no
	/// world, and each one became a failed lookup on arrival.
	/// </summary>
	/// <summary>
	/// When this player stopped being able to receive world traffic.
	///
	/// A first join transfers the whole save and starts level. A reconnect keeps the
	/// world it already had, and anything announced once while it was away - a build
	/// order, a completion - is never said again. This is the start of the window the
	/// host replays from; zero means the player has never been away, and nothing is
	/// replayed for it.
	/// </summary>
	public float AwaySince;

	public void SetReadyState(ClientReadyState state)
	{
		// Read before the assignment, because "was it Ready until now" is the question.
		bool wasReceiving = readyState == ClientReadyState.Ready;

		readyState = state;
		if (state == ClientReadyState.Loading)
			LoadingSince = UnityEngine.Time.unscaledTime;

		if (state != ClientReadyState.Ready && wasReceiving)
			AwaySince = UnityEngine.Time.unscaledTime;
	}

    public MultiplayerPlayer(ulong playerId)
	{
		PlayerId = playerId;
		ProtocolVerified = IsLocal;
		if(NetworkConfig.IsLanConfig())
		{
            PlayerName = $"Player {playerId}";
            return;
        }

		PlayerName = Utils.TrucateName(SteamFriends.GetFriendPersonaName(playerId.AsCSteamID()));
		AvatarImageId = SteamFriends.GetLargeFriendAvatar(playerId.AsCSteamID());
	}

	public override string ToString()
	{
		return $"{PlayerName} ({PlayerId})";
	}
}
