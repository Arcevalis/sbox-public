using Sandbox;

namespace ServicesTests;

[TestClass]
[DoNotParallelize]
public class ActivityLoadingTest
{
	[TestInitialize]
	public void Reset()
	{
		Api.Activity.CurrentLoad?.End( "superseded" );
		Api.Activity.LoadBegin( "reset", false ).End( "success" );
		Api.Activity.AcknowledgeCompletedLoad( Api.Activity.PeekCompletedLoad( "reset" ).Load );
		Api.Activity.CancelRequest( Api.Activity.PendingRequest );
		Api.Activity.LoadBegin( "reset", false ).End( "cancel" );
	}

	[TestMethod]
	public void LoadCarriesTheRequestThatStartedIt()
	{
		Api.Activity.GameRequested( new( "menu", "org.game", "home", "Trending", 3 ) );

		var load = Api.Activity.LoadBegin( "org.game#12", false );

		Assert.AreEqual( "home", load.Origin.Surface );
		Assert.AreEqual( 3, load.Origin.Position );
	}

	[TestMethod]
	public void GenericRequestDoesNotReplaceTheMenus()
	{
		Api.Activity.GameRequested( new( "menu", "org.game", "home" ) );
		Api.Activity.GameRequested( new( "console", "org.game" ), replace: false );

		Assert.AreEqual( "menu", Api.Activity.LoadBegin( "org.game", false ).Origin.Kind );
	}

	[TestMethod]
	public void LobbyJoinKeepsTheMenusOrigin()
	{
		Api.Activity.GameRequested( new( "menu", "org.game", "home", Via: "gamepage-join" ) );
		Api.Activity.GameRequested( new( "quickplay" ), replace: false );

		Assert.AreEqual( "gamepage-join", Api.Activity.LoadBegin( "org.game", true ).Origin.Via );
	}

	[TestMethod]
	public void RequestForAnotherGameIsReplaced()
	{
		Api.Activity.GameRequested( new( "menu", "org.first", "home" ) );
		Api.Activity.GameRequested( new( "menu", "org.second" ), replace: false );

		Assert.AreEqual( "org.second", Api.Activity.LoadBegin( "org.second", false ).Origin.Ident );
	}

	[TestMethod]
	public void RequestIsUsedOnce()
	{
		Api.Activity.GameRequested( new( "friend" ) );
		Api.Activity.LoadBegin( "org.game", true ).End( "cancel" );

		Assert.IsNull( Api.Activity.LoadBegin( "org.game", false ).Origin );
	}

	[TestMethod]
	public void RestartedJoinKeepsItsOrigin()
	{
		Api.Activity.GameRequested( new( "friend" ) );
		Api.Activity.LoadBegin( "org.game", true ).End( "cancel" );

		Assert.AreEqual( "friend", Api.Activity.LoadBegin( "org.game", true ).Origin?.Kind );
	}

	[TestMethod]
	public void SuccessfulLoadSurvivesFailedHeartbeatsUntilAcknowledged()
	{
		Api.Activity.GameRequested( new( "menu", "org.game", "search" ) );
		var load = Api.Activity.LoadBegin( "org.game#12", false );
		load.Stage( "install" );
		load.Downloaded( 1000, 4, 1.5 );
		Api.Activity.LoadFinished();

		Assert.IsNull( Api.Activity.PeekCompletedLoad( "org.other" ).Load );

		var (data, origin) = Api.Activity.PeekCompletedLoad( "ORG.GAME" );
		Assert.AreEqual( "success", data["outcome"] );
		Assert.AreEqual( 1000L, data["bytes"] );
		Assert.AreEqual( "search", origin.Surface );

		Assert.AreSame( data, Api.Activity.PeekCompletedLoad( "org.game" ).Load );
		Api.Activity.AcknowledgeCompletedLoad( data );
		Assert.IsNull( Api.Activity.PeekCompletedLoad( "org.game" ).Load );
	}

	[TestMethod]
	public void NewLoadSupersedesTheRunningOne()
	{
		var first = Api.Activity.LoadBegin( "org.first", false );
		var second = Api.Activity.LoadBegin( "org.second", false );

		Assert.IsTrue( first.Ended );
		Assert.AreSame( second, Api.Activity.CurrentLoad );
	}

	[TestMethod]
	public void EndedLoadIgnoresLaterStages()
	{
		var load = Api.Activity.LoadBegin( "org.game", false );
		Api.Activity.LoadAbandoned( "Map not found" );

		load.Stage( "scene" );
		Api.Activity.LoadFinished();

		Assert.IsNull( Api.Activity.CurrentLoad );
		Assert.IsNull( Api.Activity.PeekCompletedLoad( "org.game" ).Load );
	}

	[TestMethod]
	public void FailedConnectionDoesNotAttributeTheNextServerJoinToAFriend()
	{
		Api.Activity.GameRequested( new( "friend" ) );
		Api.Activity.CancelRequest( Api.Activity.PendingRequest );
		Api.Activity.GameRequested( new( "server" ), replace: false );
		Assert.AreEqual( "server", Api.Activity.LoadBegin( "org.game", true ).Origin.Kind );
	}

	[TestMethod]
	public void AnOlderConnectionFailureDoesNotClearANewerRequest()
	{
		Api.Activity.GameRequested( new( "friend" ) );
		var old = Api.Activity.PendingRequest;
		Api.Activity.GameRequested( new( "invite" ) );
		Api.Activity.CancelRequest( old );
		Assert.AreEqual( "invite", Api.Activity.LoadBegin( "org.game", true ).Origin.Kind );
	}

	[TestMethod]
	public void AnOlderHeartbeatDoesNotAcknowledgeANewerLoad()
	{
		Api.Activity.LoadBegin( "org.game", false ).End( "success" );
		var old = Api.Activity.PeekCompletedLoad( "org.game" ).Load;
		Api.Activity.LoadBegin( "org.game", false ).End( "success" );
		var latest = Api.Activity.PeekCompletedLoad( "org.game" ).Load;
		Api.Activity.AcknowledgeCompletedLoad( old );
		Assert.AreSame( latest, Api.Activity.PeekCompletedLoad( "org.game" ).Load );
	}
}
