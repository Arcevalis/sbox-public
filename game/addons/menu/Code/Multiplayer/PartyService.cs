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

		await PartyDeck.EnsurePartyExists( true );
	} );

	internal static Task Join( PartyRoom.Entry party ) => Join( party.Id );

	internal static Task Join( ulong id ) => Run( async () =>
	{
		if ( PartyRoom.Current is { } current )
		{
			if ( current.Id.ValueUnsigned == id ) return;

			throw new InvalidOperationException( "Leave your current party before joining another." );
		}

		await PartyRoom.Join( id );
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
