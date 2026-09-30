using Sandbox;

namespace MenuProject.Multiplayer;

/// <summary>
/// Creates and joins parties from the multiplayer page and shared invitations.
/// </summary>
internal static class PartyService
{
	internal static bool Busy { get; private set; }
	internal static string Error { get; private set; }

	internal static Task Create() => Run( async () =>
	{
		if ( PartyRoom.Current is not null ) return;

		var party = await PartyRoom.Create( PartyDeck.MAX_MEMBERS, $"{Sandbox.Utility.Steam.PersonaName}'s party", true );
		if ( party is null ) throw new InvalidOperationException( "Could not create the party." );
	} );

	internal static Task Join( PartyRoom.Entry party ) => Join( party.Id );

	internal static Task Join( ulong id ) => Run( async () =>
	{
		if ( PartyRoom.Current is { } current )
		{
			if ( current.Id.ValueUnsigned == id ) return;

			throw new InvalidOperationException( "Leave your current party before joining another." );
		}

		if ( !await PartyRoom.Join( id ) )
		{
			throw new InvalidOperationException( "Could not join that party. It may be full or no longer available." );
		}
	} );

	static async Task Run( Func<Task> action )
	{
		if ( Busy ) return;

		Busy = true;
		Error = null;

		try
		{
			await action();
		}
		catch ( Exception e )
		{
			Error = e.Message;
		}
		finally
		{
			Busy = false;
		}
	}
}
