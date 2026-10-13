namespace EngineTests;

[TestClass]
public class ShutdownTest
{
	/// <summary>
	/// Shutdown clears global context state (including Surface.All), so after re-initing
	/// we must restore the fallback surface that AssemblyInitialize installed - otherwise
	/// every trace test that happens to run after this class loses all hits.
	/// </summary>
	static void Reinit()
	{
		TestInit.TestAppSystem.Init();

		// Type libraries built before the shutdown belong to the old engine, build fresh ones
		SharedTypeLibrary.Reset();

		if ( Surface.All.Count == 0 )
		{
			Surface.All[0] = new Surface();
		}
	}

	/// <summary>
	/// Shutdown and re-init twice. Other tests still need the engine, so it ends initialized.
	/// </summary>
	[TestMethod]
	public void Multiple()
	{
		TestInit.TestAppSystem.Shutdown();
		Reinit();
		TestInit.TestAppSystem.Shutdown();
		Reinit();
	}
}
