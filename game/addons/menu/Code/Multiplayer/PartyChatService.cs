using Sandbox;

namespace MenuProject.Multiplayer;

/// <summary>
/// Party conversation for the Multiplayer page, using party chat.
/// </summary>
internal static class PartyChatService
{
	internal record Line( Friend Sender, string Text );
	internal static List<Line> Messages { get; } = new();
	internal static int Revision { get; private set; }
	internal static int Unread { get; private set; }
	internal static string Error { get; private set; }
	internal static string Draft { get; set; } = "";
	static PartyRoom party;
	static bool subscribed;
	static double lastSent = -10;
	static readonly Dictionary<ulong, double> lastReceived = new();

	internal static void Tick()
	{
		if ( party == PartyRoom.Current && (party is null || subscribed) ) return;

		Detach();
		if ( party != PartyRoom.Current )
		{
			party = PartyRoom.Current;
			Messages.Clear();
			lastReceived.Clear();
			Unread = 0;
			Draft = "";
			Error = null;
			lastSent = -10;
			Revision++;
		}
		if ( party is not null )
		{
			party.OnChatMessage += Receive;
			subscribed = true;
		}
	}

	/// <summary>
	/// Release the party callback when the menu unloads or changes parties.
	/// </summary>
	internal static void Detach()
	{
		if ( party is not null && subscribed ) party.OnChatMessage -= Receive;
		subscribed = false;
	}

	internal static bool Send( string text )
	{
		Tick();
		text = text?.Trim();
		if ( party is null || !Preferences.ChatEnabled || string.IsNullOrEmpty( text ) ) return false;
		if ( text.Length > 400 )
		{
			Error = "Keep messages to 400 characters.";
			return false;
		}
		if ( RealTime.Now - lastSent < 1 )
		{
			Error = "Please wait a moment before sending another message.";
			return false;
		}

		try
		{
			party.SendChatMessage( text );
			lastSent = RealTime.Now;
			Error = null;
			return true;
		}
		catch ( Exception )
		{
			Error = "Couldn't send your message. Try again.";
			return false;
		}
	}

	internal static void Receive( Friend sender, string text )
	{
		Tick();
		if ( party is null || !Preferences.ChatEnabled || sender.IsBlocked || string.IsNullOrWhiteSpace( text ) || text.Length > 400 ) return;
		if ( !party.Members.Any( x => x.Id == sender.Id ) ) return;
		if ( lastReceived.TryGetValue( sender.Id, out var time ) && RealTime.Now - time < 0.5 ) return;
		lastReceived[sender.Id] = RealTime.Now;
		Messages.Add( new( sender, text ) );
		if ( Messages.Count > 100 ) Messages.RemoveAt( 0 );
		if ( !sender.IsMe ) Unread++;
		Revision++;
	}

	internal static void MarkRead() => Unread = 0;
}
