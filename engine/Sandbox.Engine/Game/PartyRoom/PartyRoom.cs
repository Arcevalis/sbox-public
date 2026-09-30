using NativeEngine;
using Sandbox.Engine;
using Sandbox.Modals;
using Sandbox.Network;
using Steamworks;
using Steamworks.Data;
using System.Runtime.CompilerServices;
using System.Globalization;

namespace Sandbox;

/// <summary>
/// A Party. A Party with your friends.
/// </summary>
public partial class PartyRoom : ILobby
{
	/// <summary>
	/// The party we currently belong to, if any.
	/// </summary>
	public static PartyRoom Current { get; private set; }

	Steamworks.Data.Lobby steamLobby;

	/// <summary>
	/// The unique identifier of this party.
	/// </summary>
	public SteamId Id => steamLobby.Id.Value;

	/// <summary>
	/// The server this party is playing on, if any. This is set by the owner of the party, and read by clients to know where to connect to.
	/// </summary>
	internal string GameAddress => steamLobby.GetData( "gameaddress" );

	/// <summary>
	/// The name of this party.
	/// </summary>
	public string Name
	{
		get => steamLobby.GetData( "name" );
		set => steamLobby.SetData( "name", value );
	}

	/// <summary>
	/// The maximum number of members allowed in this party.
	/// </summary>
	public int MaxMembers
	{
		get => steamLobby.MaxMembers;
		set => steamLobby.MaxMembers = value;
	}

	/// <summary>
	/// The current number of members in this party.
	/// </summary>
	public int MemberCount => steamLobby.MemberCount;

	internal int NetworkChannel => (int)(Id % int.MaxValue);

	PartyRoom( Lobby value )
	{
		steamLobby = value;
		UpdateLobbyData();

		if ( Owner.IsMe )
		{
			steamLobby.SetData( "lobby_type", "party" );
			steamLobby.SetData( "api", Protocol.Api.ToString() );
			steamLobby.SetData( "protocol", Protocol.Network.ToString() );
			steamLobby.SetData( "buildid", $"{Application.Version}" );
			steamLobby.SetData( "dev", Application.IsEditor ? "1" : "0" );
			steamLobby.SetData( "_ownerid", Owner.Id.ToString() );
		}

		LobbyManager.Register( this );
		VoiceManager.OnCompressedVoiceData += OnVoiceRecorded;
	}

	/// <summary>
	/// Leave this party and stop following its leader.
	/// </summary>
	public void Leave()
	{
		_join?.Dispose();
		_join = null;

		using ( GlobalContext.MenuScope() )
		{
			Event.EventSystem.RunInterface<IEventListener>( x => x.OnLeftParty( Current ) );
		}

		steamLobby.Leave();
		steamLobby = default;

		if ( Current == this )
		{
			Current = default;
		}

		// this is no longer needed, we don't need to get messages about it
		// we don't need to know when people enter and leave, so forget about it.
		LobbyManager.Unregister( this );

		VoiceManager.OnCompressedVoiceData -= OnVoiceRecorded;
	}

	/// <summary>
	/// Set the owner to someone else. You need to be the owner
	/// </summary>
	public bool SetOwner( SteamId friend )
	{
		return steamLobby.SetOwner( friend );
	}


	internal void InviteFriend( SteamId steamid )
	{
		if ( steamLobby.InviteFriend( steamid.ValueUnsigned ) )
		{
			Log.Info( $"Party invite to {steamid} sent" );
		}
		else
		{
			Log.Warning( $"Party invite to {steamid} was not sent" );
		}
	}

	internal void InviteFriend()
	{
		steamLobby.InviteOverlay();
	}

	/// <summary>
	/// Allow communication via voice when in the main menu.
	/// </summary>
	public bool VoiceCommunicationAllowed => IGameInstance.Current is null;

	RealTimeSince timeSinceUpdate = 0;

	bool _voiceRecording;

	/// <summary>
	/// Whether we are recording voice for the party.
	/// </summary>
	public bool VoiceRecording
	{
		get => _voiceRecording;
		set
		{
			if ( _voiceRecording == value ) return;
			_voiceRecording = value;

			if ( _voiceRecording )
			{
				VoiceManager.StartRecording();
			}
			else
			{
				VoiceManager.StopRecording();
			}
		}
	}

	/// <summary>
	/// Voice data has been recieved. Send it to everyone.
	/// </summary>
	private void OnVoiceRecorded( Memory<byte> memory )
	{
		// Don't send 
		if ( !VoiceCommunicationAllowed )
			return;

		using var bs = ByteStream.Create( 32 );
		bs.Write( MessageIdentity.VoiceMessage );
		bs.WriteArray( memory.ToArray() );

		foreach ( var friend in Members )
		{
			int flags = 0;
			flags = 8; // k_nSteamNetworkingSend_Reliable
			flags |= 32; // k_nSteamNetworkingSend_AutoRestartBrokenSession

			unsafe
			{
				fixed ( byte* pData = bs.ToSpan() )
				{
					Steam.SteamNetworkingMessages().SendMessageToUser( friend.Id, (IntPtr)pData, bs.Length, flags, NetworkChannel );
				}
			}
		}
	}

	/// <summary>
	/// Determine our state to be sent to other party members
	/// </summary>
	OwnerJoinState DetermineJoinState()
	{
		return DetermineJoinState( IGameInstance.Current?.IsLoading ?? LoadingScreen.IsVisible,
			Networking.IsConnecting, GetGameAddress(), Application.GamePackage is not null && Application.GamePackage is not LocalPackage );
	}

	internal static OwnerJoinState DetermineJoinState( bool loading, bool connecting, string address, bool hasGame )
	{
		if ( loading || connecting ) return OwnerJoinState.Loading;
		if ( !string.IsNullOrWhiteSpace( address ) ) return OwnerJoinState.Ready;
		return hasGame ? OwnerJoinState.Unavailable : OwnerJoinState.None;
	}

	static string GetGameAddress()
	{
		if ( Networking.System?.Sockets.OfType<SteamLobbySocket>().FirstOrDefault() is { } lobby )
			return lobby.LobbySteamId.ToString();
		if ( Networking.IsClient && Networking.LastConnectionString != "local" )
			return Networking.LastConnectionString;
		return null;
	}

	internal void Tick()
	{
		// Record the voice if voice button less than 0.3 seconds old
		VoiceRecording = VoiceCommunicationAllowed && timeSinceWantVoiceSend < 0.1f;

		ReadVoiceChannel();

		if ( timeSinceUpdate < 0.5f ) return;
		timeSinceUpdate = 0;

		ShareMemberStatus();

		if ( Owner.IsMe )
		{
			steamLobby.SetData( "api", Protocol.Api.ToString() );
			_join?.Dispose();
			_join = null;
			steamLobby.SetData( "protocol", Protocol.Network.ToString() );
			steamLobby.SetData( "buildid", $"{Application.Version}" );
			steamLobby.SetData( "dev", Application.IsEditor ? "1" : "0" );
			steamLobby.SetData( "_ownerid", Owner.Id.ToString() );
			steamLobby.SetData( "package", Application.GamePackage?.GetIdent( false, true ) );
			steamLobby.SetData( "packagetitle", Application.GamePackage?.Title );

			var state = DetermineJoinState();
			steamLobby.SetData( "joinstate", state.ToString() );
			var progress = state == OwnerJoinState.Loading ? LoadingScreen.Progress : null;
			steamLobby.SetData( "download_fraction", progress?.Fraction.ToString( "F4", CultureInfo.InvariantCulture ) ?? "" );
			steamLobby.SetData( "download_title", progress?.Title ?? "" );

			if ( state is OwnerJoinState.Ready )
			{
				steamLobby.SetData( "gameaddress", GetGameAddress() );
			}
			else
			{
				steamLobby.DeleteData( "gameaddress" );
			}
		}
		else
		{
			UpdateFollowing();
		}
	}

	unsafe void ReadVoiceChannel()
	{
		var net = Steam.SteamNetworkingMessages();

		int batchSize = 8;
		IntPtr* ptr = stackalloc IntPtr[batchSize];

		while ( true )
		{
			int count = net.ReceiveMessagesOnChannel( NetworkChannel, (IntPtr)ptr, batchSize );
			if ( count == 0 )
				break;

			for ( int i = 0; i < count; i++ )
			{
				var msg = Unsafe.Read<Sandbox.Network.SteamNetworkMessage>( (void*)ptr[i] );

				if ( !Members.Any( x => x.Id == msg.IdentitySteamId ) )
				{
					Log.Trace( $"Dropping message from {msg.IdentitySteamId} not in party" );
					continue;
				}

				using var data = new ByteStream( msg.Data, msg.Size );

				var iMessageType = data.Read<MessageIdentity>();


				if ( iMessageType == MessageIdentity.VoiceMessage )
				{
					var message = data.ReadArraySpan<byte>( 1024 * 1024 );
					var voiceData = message.ToArray();

					OnVoiceData?.Invoke( new Friend( msg.IdentitySteamId ), voiceData );

					using ( GlobalContext.MenuScope() )
					{
						Event.EventSystem.RunInterface<IEventListener>( x => x.OnVoiceMessage( new Friend( msg.IdentitySteamId ), voiceData ) );
					}
				}

				net.ReleaseMessage( ptr[i] );
			}
		}
	}

	/// <summary>
	/// Create an open party with the local player as its leader.
	/// </summary>
	[Obsolete]
	public static Task<PartyRoom> Create( int maxMembers )
	{
		return Create( maxMembers, $"{Utility.Steam.PersonaName}'s Party", true );
	}

	/// <summary>
	/// Create a party, optionally making it discoverable and joinable by everyone.
	/// </summary>
	public static async Task<PartyRoom> Create( int maxMembers, string name, bool ispublic )
	{
		var lobby = await Steamworks.SteamMatchmaking.CreateLobbyAsync( ispublic ? LobbyType.Public : LobbyType.Private, maxMembers );

		if ( !lobby.HasValue )
		{
			Log.Warning( "Failed to create party" );
			return null;
		}

		lobby.Value.SetData( "name", name );

		var party = new PartyRoom( lobby.Value );

		Current = party;

		return party;
	}

	/// <summary>
	/// Make this party discoverable and joinable by everyone. Only the leader can do this.
	/// </summary>
	public void MakePublic()
	{
		if ( Current != this || !Owner.IsMe )
		{
			throw new InvalidOperationException( "Only the party leader can open the party." );
		}

		if ( !steamLobby.SetPublic() )
		{
			throw new InvalidOperationException( "Could not open the party. Please try again." );
		}
	}

	/// <summary>
	/// Join a party by its identifier, checking that it still exists and is a party.
	/// </summary>
	public static async Task<bool> Join( ulong id )
	{
		if ( Current?.Id.ValueUnsigned == id ) return true;

		var party = new Lobby( id );
		if ( !await party.Refresh() || !party.IsParty ) return false;

		return await Join( party );
	}

	internal static async Task<bool> Join( Lobby lobby )
	{
		Current?.Leave();

		if ( Application.IsEditor )
			return false;

		var result = await lobby.Join();
		if ( result != RoomEnter.Success )
		{
			switch ( result )
			{
				case RoomEnter.DoesntExist:
					IModalSystem.Current?.Notice( "Joining failed", "The party doesn't exist anymore.", "heart_broken" );
					break;
				case RoomEnter.Full:
					IModalSystem.Current?.Notice( "Joining failed", "The party is full.", "heart_broken" );
					break;
				default:
					IModalSystem.Current?.Notice( "Joining failed", $"Failed to join the party: {result}.", "heart_broken" );
					break;
			}

			Log.Warning( $"Failed to join party ({result})" );
			return false;
		}

		Current?.Leave();
		Current = new PartyRoom( lobby );

		using ( GlobalContext.MenuScope() )
		{
			Event.EventSystem.RunInterface<IEventListener>( x => x.OnJoinedParty( Current ) );
		}

		return true;
	}


	/// <summary>
	/// A list of members in this party.
	/// </summary>
	public IEnumerable<Friend> Members => steamLobby.Members.Select( x => new Friend( x ) );

	/// <summary>
	/// The current party leader.
	/// </summary>
	public Friend Owner { get; private set; }



	/// <summary>
	/// Find open parties with space for another member.
	/// </summary>
	public static async Task<Entry[]> Find()
	{
		var found = await Steamworks.SteamMatchmaking.LobbyList
														.WithKeyValue( "lobby_type", "party" )
														.WithSlotsAvailable( 1 )
														.RequestAsync( default );

		if ( found is null )
			return Array.Empty<Entry>();

		return found.Select( x => new Entry( x ) ).ToArray();
	}

	ulong ILobby.Id => steamLobby.Id;

	/// <summary>
	/// What package is this party's owner playing?
	/// </summary>
	public string PackageIdent => steamLobby.GetData( "package" );

	void ILobby.OnMemberEnter( Friend friend )
	{
		Log.Info( $"Party member entered {friend}" );
		OnJoin?.Invoke( friend );

		using ( GlobalContext.MenuScope() )
		{
			Event.EventSystem.RunInterface<IEventListener>( x => x.OnMemberJoin( friend ) );
		}
	}

	void ILobby.OnMemberLeave( Friend friend )
	{
		Log.Info( $"Party member leave {friend}" );
		OnLeave?.Invoke( friend );

		using ( GlobalContext.MenuScope() )
		{
			Event.EventSystem.RunInterface<IEventListener>( x => x.OnMemberLeave( friend ) );
		}
	}

	void ILobby.OnMemberUpdated( Friend friend )
	{
		// Members share their join progress through this every half second - trace, not info
		Log.Trace( $"Party member updated {friend}" );
	}

	void ILobby.OnLobbyUpdated()
	{
		UpdateLobbyData();
	}

	void UpdateLobbyData()
	{
		Owner = new Friend( steamLobby.Owner );
	}

	RealTimeSince timeSinceWantVoiceSend = 60;

	/// <summary>
	/// Called each frame that a client wants to broadcast their voice
	/// </summary>
	internal void SetBroadcastVoice()
	{
		timeSinceWantVoiceSend = 0;
	}

	/// <summary>
	/// A discoverable party with its current membership and game details.
	/// </summary>
	public struct Entry
	{
		private Lobby x;

		/// <summary>
		/// The unique identifier of this party.
		/// </summary>
		public readonly ulong Id => x.Id;

		/// <summary>
		/// The party name.
		/// </summary>
		public readonly string Name => x.GetData( "name" );

		/// <summary>
		/// The number of members currently in the party.
		/// </summary>
		public readonly int Members => x.MemberCount;

		/// <summary>
		/// Whether the party has no space for another member.
		/// </summary>
		public readonly bool IsFull => x.MemberCount >= x.MaxMembers;

		/// <summary>
		/// The Steam identifier of the party leader.
		/// </summary>
		public readonly long OwnerId => x.GetData( "_ownerid" ).ToLong( 0 );

		/// <summary>
		/// Whether the party leader has a game server to follow.
		/// </summary>
		public readonly bool IsPlaying => !string.IsNullOrWhiteSpace( x.GetData( "gameaddress" ) );

		/// <summary>
		/// The package the party leader is playing.
		/// </summary>
		public readonly string Package => x.GetData( "package" );

		/// <summary>
		/// The title of the game the party leader is playing.
		/// </summary>
		public readonly string GameTitle => x.GetData( "packagetitle" );

		internal Entry( Lobby x )
		{
			this.x = x;
		}

		/// <summary>
		/// Join this party.
		/// </summary>
		public async Task Join()
		{
			await PartyRoom.Join( x );
		}
	}
}
