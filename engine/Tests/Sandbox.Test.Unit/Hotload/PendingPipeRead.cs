using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sandbox;
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace HotloadTests
{
	/// <summary>
	/// A static <see cref="CancellationTokenSource"/> whose token is passed to a pending pipe read
	/// must not be walked during a hotload. The walk reaches the read's cancellation callback,
	/// cannot resolve the scope method behind it, and substitutes a delegate that throws, so the
	/// pending read never completes and the listener it belongs to ends with no log line of its
	/// own (the hotload logs the warnings).
	/// <see cref="SkipHotloadAttribute"/> on the owning static keeps the whole graph a leaf.
	/// </summary>
	[TestClass]
	[DoNotParallelize]
	public class PendingPipeReadTest : HotloadTestBase
	{
		/// <summary>
		/// Marked the same way as <c>Editor.Mcp.McpPipeServer.lifetime</c>. The hotload must not
		/// touch anything reachable from here.
		/// </summary>
		[SkipHotload]
		[Reset]
		static CancellationTokenSource MarkedLifetime;

		/// <summary>
		/// Same shape without the attribute, to show what the walk does to a live token source.
		/// </summary>
		[Reset]
		static CancellationTokenSource UnmarkedLifetime;

		static async Task<byte[]> ReadOnce( Stream stream, CancellationToken cancellation )
		{
			var buffer = new byte[64];
			var read = await stream.ReadAsync( buffer, cancellation );
			return buffer[..read];
		}

		static void CreatePair( string name, out NamedPipeServerStream server, out NamedPipeClientStream client )
		{
			server = new NamedPipeServerStream( name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly );
			client = new NamedPipeClientStream( ".", name, PipeDirection.InOut, PipeOptions.Asynchronous );
		}

		[TestMethod]
		public async Task MarkedSourceKeepsPendingRead()
		{
			var name = $"mcp-hl-{Guid.NewGuid().ToString( "N" )[..8]}";
			CreatePair( name, out var server, out var client );

			using ( server )
			using ( client )
			{
				var connecting = server.WaitForConnectionAsync();
				await client.ConnectAsync( 5000 );
				await connecting;

				var lifetime = new CancellationTokenSource();
				MarkedLifetime = lifetime;
				var read = ReadOnce( server, lifetime.Token );

				try
				{
					// Let the read reach the OS and install its cancellation registration.
					await Task.Delay( 100 );
					Assert.IsFalse( read.IsCompleted, "read should still be pending" );

					Hotload();

					await client.WriteAsync( new byte[] { 42 }, 0, 1 );
					var received = await read.WaitAsync( TimeSpan.FromSeconds( 5 ) );

					CollectionAssert.AreEqual( new byte[] { 42 }, received );
				}
				finally
				{
					// The read is finished here, so only dispose. Cancelling would run the callback a
					// broken walk has replaced, which throws and masks whatever went wrong first.
					lifetime.Dispose();
				}
			}
		}

		/// <summary>
		/// Characterizes the hole this fix closes: without <see cref="SkipHotloadAttribute"/> the walk
		/// reaches the generic BCL lambda behind the pipe read's cancellation callback and stubs it
		/// (<c>Sandbox.Hotload/Upgraders/DelegateUpgrader.cs:97</c>), so the pending read never
		/// completes and the hotload reports the scope method it could not resolve. If that guard is
		/// ever widened this test fails, and should then be flipped to assert the read completes.
		/// </summary>
		[TestMethod]
		public async Task UnmarkedSourceBreaksPendingRead()
		{
			// The read is serviced by SocketAsyncContext only for Unix domain sockets, so the walk
			// can only break it like this on Linux. Windows completes the read without the scope method.
			if ( !OperatingSystem.IsLinux() )
				Assert.Inconclusive( "the pending pipe read only goes through SocketAsyncContext on Unix sockets" );

			var name = $"mcp-hl-{Guid.NewGuid().ToString( "N" )[..8]}";
			CreatePair( name, out var server, out var client );

			using ( server )
			using ( client )
			{
				var connecting = server.WaitForConnectionAsync();
				await client.ConnectAsync( 5000 );
				await connecting;

				var lifetime = new CancellationTokenSource();
				UnmarkedLifetime = lifetime;
				var read = ReadOnce( server, lifetime.Token );

				try
				{
					await Task.Delay( 100 );
					Assert.IsFalse( read.IsCompleted, "read should still be pending" );

					var result = Hotload( true );

					Console.WriteLine( $"HasErrors = {result.HasErrors}" );
					Console.WriteLine( $"Warnings = {result.Warnings.Count()}" );
					foreach ( var warning in result.Warnings )
					{
						Console.WriteLine( $"  {warning}" );
					}

					Assert.IsTrue( result.HasErrors, "unmarked pending read broke without hotload errors" );
					Assert.IsTrue( result.Warnings.Any( warning => warning.ToString().Contains( "StartAsyncOperation" ) ),
						"expected a StartAsyncOperation warning naming the broken read" );

					// Data is waiting, so a healthy read would finish well inside five seconds.
					await client.WriteAsync( new byte[] { 42 }, 0, 1 );
					await Assert.ThrowsExceptionAsync<TimeoutException>(
						async () => await read.WaitAsync( TimeSpan.FromSeconds( 5 ) ),
						"the broken read should never complete" );
				}
				finally
				{
					// The broken read faults once the pipe closes. Observe it so it does not surface
					// later, and only dispose: cancelling would invoke the stubbed callback and throw.
					_ = read.ContinueWith( static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted );
					lifetime.Dispose();
				}
			}
		}

		/// <summary>
		/// The pipe server's lifetime source carries the same attribute. Without it the hotload walks
		/// a live cancellation registration and the listener ends with no log line of its own.
		/// </summary>
		[TestMethod]
		public void McpPipeServerLifetimeIsSkipped()
		{
			var field = typeof( Editor.Mcp.McpPipeServer ).GetField( "lifetime",
				BindingFlags.Static | BindingFlags.NonPublic );

			Assert.IsNotNull( field, "Editor.Mcp.McpPipeServer.lifetime field not found" );
			Assert.IsTrue( field.IsDefined( typeof( SkipHotloadAttribute ) ),
				"Editor.Mcp.McpPipeServer.lifetime must stay [SkipHotload] or a hotload breaks the pending read" );
		}
	}
}
