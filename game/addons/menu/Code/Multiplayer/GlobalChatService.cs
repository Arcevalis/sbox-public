using Sandbox;
using System.Text.Json;

namespace MenuProject.Multiplayer;

/// <summary>
/// Global chat membership exists only while its view is open. Parties have their own lifetime.
/// </summary>
internal static class GlobalChatService
{
	internal static MenuChatChannel Channel { get; private set; }
	internal static List<ChatLine> Messages { get; } = new();
	internal static bool Connecting { get; private set; }
	internal static string Error { get; private set; }
	internal static int Revision { get; private set; }
	internal static bool Visible { get; private set; }
	internal static string Draft { get; set; } = "";
	internal record ChatLine( Friend Sender, string Text, ulong PartyId );

	static readonly Dictionary<ulong, double> lastMessages = new();
	static readonly Dictionary<string, string> filters = new() { ["lobby_type"] = "menu_chat", ["channel"] = "global" };
	static int generation;
	static bool refreshing;
	static double nextRefresh;
	static double nextJoin;
	static double lastSent = -10;
	static double lastInvite = -10;

	sealed class Message
	{
		/// <summary>
		/// Plain text sent to everyone in global chat.
		/// </summary>
		public string Text { get; set; }

		/// <summary>
		/// Optional invitation to an existing party.
		/// </summary>
		public ulong PartyId { get; set; }
	}

	internal static void Open()
	{
		Visible = true;
		Tick();
	}

	internal static void Close()
	{
		if ( !Visible && Channel is null ) return;
		Visible = false;
		generation++;
		Channel?.Dispose();
		Channel = null;
		Messages.Clear();
		lastMessages.Clear();
		Error = null;
		nextJoin = 0;
		nextRefresh = 0;
		Revision++;
	}

	internal static void Tick()
	{
		// The menu scene stops ticking when a game opens, so MenuSystem also calls this.
		if ( !Application.IsEditor && !Game.IsMainMenuVisible ) Close();
		if ( !Visible ) return;
		if ( Channel is null )
		{
			if ( !Connecting && RealTime.Now >= nextJoin ) _ = Join();
			return;
		}
		if ( !Connecting && !refreshing && RealTime.Now >= nextRefresh ) _ = Refresh();
	}

	static async Task Join()
	{
		Connecting = true;
		var version = generation;
		MenuChatChannel joined = null;
		try
		{
			var found = await MenuChatChannel.FindAsync( filters );
			if ( version != generation || !Visible ) return;
			foreach ( var channel in found.OrderBy( x => x.Id ) )
			{
				try { joined = await MenuChatChannel.JoinAsync( channel.Id ); }
				catch ( InvalidOperationException ) { }
				if ( joined is not null || version != generation || !Visible ) break;
			}
			if ( version != generation || !Visible ) return;
			joined ??= await MenuChatChannel.CreateAsync( 250, filters );
			if ( version != generation || !Visible ) return;
			Attach( joined );
			joined = null;
			Error = null;
		}
		catch ( Exception e )
		{
			if ( version == generation && Visible )
			{
				Error = e.Message;
				nextJoin = RealTime.Now + 10;
			}
		}
		finally
		{
			joined?.Dispose();
			Connecting = false;
			Revision++;
		}
	}

	static void Attach( MenuChatChannel channel )
	{
		Channel?.Dispose();
		Channel = channel;
		Messages.Clear();
		lastMessages.Clear();
		channel.MessageReceived += Receive;
		channel.Changed += Changed;
		Changed();
	}

	static async Task Refresh()
	{
		refreshing = true;
		nextRefresh = RealTime.Now + 15;
		var version = generation;
		try
		{
			// Merge channels created at the same time once Steam lists both of them.
			var current = Channel;
			if ( current is null || Connecting ) return;
			var found = await MenuChatChannel.FindAsync( filters );
			if ( version != generation || !Visible || Channel != current || Connecting ) return;
			var older = found.Where( x => x.Id < current.Id ).OrderBy( x => x.Id ).FirstOrDefault();
			if ( older is null ) return;
			Connecting = true;
			try
			{
				var joined = await MenuChatChannel.JoinAsync( older.Id );
				if ( version != generation || !Visible )
				{
					joined.Dispose();
				}
				else
				{
					Attach( joined );
				}
			}
			finally { Connecting = false; }
		}
		catch ( Exception e )
		{
			if ( version == generation && Visible ) Error = e.Message;
		}
		finally { refreshing = false; Revision++; }
	}

	internal static void InviteParty()
	{
		if ( Channel is null || PartyRoom.Current is not { } party || !party.Owner.IsMe ) return;
		if ( RealTime.Now - lastInvite < 10 )
		{
			Error = "Please wait a moment before posting another invitation.";
			return;
		}
		try
		{
			party.MakePublic();
			if ( SendMessage( "Anyone want to play? Join my party!", party.Id.ValueUnsigned ) ) lastInvite = RealTime.Now;
			nextRefresh = 0;
		}
		catch ( Exception e ) { Error = e.Message; }
	}

	internal static bool Send( string text ) => SendMessage( text, 0 );

	static bool SendMessage( string text, ulong partyId )
	{
		text = text?.Trim();
		if ( string.IsNullOrEmpty( text ) || Channel is null ) return false;
		if ( RealTime.Now - lastSent < 1 )
		{
			Error = "Please wait a moment before sending another message.";
			return false;
		}
		if ( text.Length > 400 ) text = text[..400];
		if ( !Channel.Send( JsonSerializer.Serialize( new Message { Text = text, PartyId = partyId } ) ) )
		{
			Error = "The message could not be sent.";
			return false;
		}
		Error = null;
		lastSent = RealTime.Now;
		return true;
	}

	static void Receive( Friend sender, string payload )
	{
		if ( sender.IsBlocked || !Visible ) return;
		if ( lastMessages.TryGetValue( sender.Id, out var last ) && RealTime.Now - last < 0.5 ) return;
		try
		{
			var message = JsonSerializer.Deserialize<Message>( payload );
			if ( string.IsNullOrWhiteSpace( message?.Text ) || message.Text.Length > 400 ) return;
			lastMessages[sender.Id] = RealTime.Now;
			Messages.Add( new( sender, message.Text, message.PartyId ) );
			if ( Messages.Count > 100 ) Messages.RemoveAt( 0 );
			Revision++;
		}
		catch ( JsonException ) { }
	}

	static void Changed() => Revision++;
}
