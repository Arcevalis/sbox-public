using Sandbox.Engine;

namespace EngineTests;

[TestClass]
public class GameSurfaceTest
{
	[TestMethod]
	public void NeedsResyncIgnoresEqualSizes()
	{
		Assert.IsFalse( GameSurface.NeedsResync( new Vector2( 1920, 1080 ), new Vector2( 1920, 1080 ) ) );
	}

	[TestMethod]
	public void NeedsResyncToleratesOnePixelRounding()
	{
		Assert.IsFalse( GameSurface.NeedsResync( new Vector2( 1920, 1080 ), new Vector2( 1921, 1079 ) ) );
	}

	[TestMethod]
	public void NeedsResyncTripsPastOnePixel()
	{
		Assert.IsTrue( GameSurface.NeedsResync( new Vector2( 1920, 1080 ), new Vector2( 1922, 1080 ) ) );
		Assert.IsTrue( GameSurface.NeedsResync( new Vector2( 1920, 1080 ), new Vector2( 1920, 1082 ) ) );
	}

	[TestMethod]
	public void NeedsResyncTripsFromZeroSizedSwapchain()
	{
		Assert.IsTrue( GameSurface.NeedsResync( new Vector2( 0, 0 ), new Vector2( 1920, 1080 ) ) );
	}
}
