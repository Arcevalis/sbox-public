using Sandbox.Engine;
using System;
using System.Reflection;

namespace ResourceTests;

[TestClass, DoNotParallelize]
public class SurfaceBaseIdentityTests
{
	[TestMethod]
	[DataRow( "surfaces/base.surface" )]
	[DataRow( "Surfaces/Base.SURFACE" )]
	[DataRow( @"surfaces\base.surface" )]
	[DataRow( "surfaces/base.surface_c" )]
	[DataRow( @"\Surfaces\Base.SURFACE_C" )]
	public void GetBaseSurfaceUsesRegisteredLogicalIdentity( string authoredPath )
	{
		using var context = new SurfaceContext();
		var fallback = context.Register( "surfaces/default.surface", 0 );
		var expected = context.Register( @"Surfaces\Base.SURFACE_C", 1 );
		var surface = context.Register( "surfaces/child.surface", 2 );
		surface.BaseSurface = authoredPath;

		Assert.AreEqual( "surfaces/base.surface", expected.ResourcePath );
		Assert.AreSame( expected, ResourceLibrary.Get<Surface>( "surfaces/base.surface" ) );
		Assert.AreNotSame( fallback, expected );
		Assert.AreSame( expected, surface.GetBaseSurface() );
		Assert.AreEqual( "surfaces/base.surface", surface.BaseSurface );
	}

	[TestMethod]
	[DataRow( "mount://testgame/surfaces/base.surface" )]
	[DataRow( @"MOUNT:\\TestGame\Surfaces\Base.SURFACE_C" )]
	public void GetBaseSurfacePreservesMountIdentity( string authoredPath )
	{
		using var context = new SurfaceContext();
		context.Register( "surfaces/default.surface", 0 );
		context.Register( "mount://othergame/surfaces/base.surface", 1 );
		var expected = context.Register( "mount://testgame/surfaces/base.surface", 2 );
		var surface = context.Register( "surfaces/child.surface", 3 );
		surface.BaseSurface = authoredPath;

		Assert.AreSame( expected, surface.GetBaseSurface() );
		Assert.AreEqual( "mount://testgame/surfaces/base.surface", surface.BaseSurface );
	}

	[TestMethod]
	[DataRow( null )]
	[DataRow( "" )]
	[DataRow( "Surfaces/Missing.SURFACE_C" )]
	[DataRow( @"\Surfaces\Child.SURFACE_C" )]
	public void MissingOrSelfBaseRetainsDefaultFallback( string authoredPath )
	{
		using var context = new SurfaceContext();
		var fallback = context.Register( "surfaces/default.surface", 0 );
		var surface = context.Register( "surfaces/child.surface", 1 );
		surface.BaseSurface = authoredPath;

		Assert.AreSame( fallback, surface.GetBaseSurface() );
		fallback.BaseSurface = @"\Surfaces\Default.SURFACE_C";
		Assert.IsNull( fallback.GetBaseSurface(), "The default surface must not inherit from itself." );
		fallback.BaseSurface = "surfaces/missing.surface";
		Assert.IsNull( fallback.GetBaseSurface(), "An unresolved default base must not fall back to itself." );
	}

	[TestMethod]
	[DataRow( "base" )]
	[DataRow( "BASE" )]
	[DataRow( "BaSe" )]
	public void SetBaseSurfaceMatchesResourceNameIgnoringCase( string name )
	{
		using var context = new SurfaceContext();
		context.Register( "surfaces/default.surface", 0 );
		var expected = context.Register( @"Surfaces\Base.SURFACE_C", 1 );
		var surface = context.Register( "surfaces/child.surface", 2 );

		surface.SetBaseSurface( name );

		Assert.AreEqual( "surfaces/base.surface", surface.BaseSurface );
		Assert.AreSame( expected, surface.GetBaseSurface() );
	}

	[TestMethod]
	public void GetBaseSurfaceDefensivelyNormalizesLegacyStoredReferences()
	{
		using var context = new SurfaceContext();
		context.Register( "surfaces/default.surface", 0 );
		var expected = context.Register( "surfaces/base.surface", 1 );
		var surface = context.Register( "surfaces/child.surface", 2 );
		typeof( Surface ).GetField( "_baseSurface", BindingFlags.NonPublic | BindingFlags.Instance )
			.SetValue( surface, @"\Surfaces\Base.SURFACE_C" );

		Assert.AreSame( expected, surface.GetBaseSurface() );
	}

	[TestMethod]
	[DataRow( @"\Surfaces\Base.SURFACE_C", "surfaces/base.surface" )]
	[DataRow( @"MOUNT:\\TestGame\Surfaces\Base.SURFACE_C", "mount://testgame/surfaces/base.surface" )]
	[DataRow( null, null )]
	[DataRow( "", "" )]
	public void JsonLoadAndEmissionUseCanonicalBaseReferences( string authoredPath, string canonicalPath )
	{
		using var context = new SurfaceContext();
		var fallback = context.Register( "surfaces/default.surface", 0 );
		var expected = string.IsNullOrEmpty( canonicalPath ) ? fallback : context.Register( canonicalPath, 1 );
		var surface = context.Register( "surfaces/child.surface", 2 );
		Assert.IsNull( surface.BaseSurface );

		surface.LoadFromJson( "{\"BaseSurface\":" + System.Text.Json.JsonSerializer.Serialize( authoredPath ) + "}" );

		Assert.AreEqual( canonicalPath, surface.BaseSurface );
		Assert.AreSame( expected, surface.GetBaseSurface() );
		var emitted = surface.Serialize();
		Assert.AreEqual( canonicalPath, emitted["BaseSurface"]?.GetValue<string>() );

		var restored = context.Register( "surfaces/restored.surface", 3 );
		restored.LoadFromJson( emitted.ToJsonString() );
		Assert.AreEqual( canonicalPath, restored.BaseSurface );
		Assert.AreSame( expected, restored.GetBaseSurface() );
	}

	// Real resource registration needs the main thread; only native physics index bookkeeping is seeded.
	sealed class SurfaceContext : IDisposable
	{
		readonly bool _wasMainThread = ThreadSafe.IsMainThread;
		readonly GlobalContext _context;
		GlobalContext.GlobalContextScope _scope;

		public SurfaceContext()
		{
			ThreadSafe.MarkMainThread();
			var previous = GlobalContext.Current;
			_context = new GlobalContext
			{
				TypeLibrary = previous.TypeLibrary,
				NodeLibrary = previous.NodeLibrary,
				JsonSerializerOptions = Json.options
			};
			_scope = new GlobalContext.GlobalContextScope( _context );
		}

		public Surface Register( string path, int index )
		{
			var surface = new Surface { Index = index };
			surface.Register( path );
			Surface.All.Add( index, surface );
			return surface;
		}

		public void Dispose()
		{
			try
			{
				_context.Shutdown();
			}
			finally
			{
				_scope.Dispose();
				typeof( ThreadSafe ).GetField( "isMainThread", BindingFlags.NonPublic | BindingFlags.Static ).SetValue( null, _wasMainThread );
			}
		}
	}
}
