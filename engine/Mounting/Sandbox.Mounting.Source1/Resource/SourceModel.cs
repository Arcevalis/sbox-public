using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed class SourceModel( BaseMount mount, string path, (MdlFile Mdl, VtxFile Vtx, PhyFile Physics) source,
	Transform[] referencePose = null, IReadOnlyList<SourceAnimation> animations = null )
{
	internal (MdlFile Mdl, VtxFile Vtx, PhyFile Physics) Source { get; } = source;
	internal Transform[] ReferencePose { get; } = referencePose;
	internal IReadOnlyList<SourceAnimation> Animations { get; } = animations ?? Array.Empty<SourceAnimation>();

	internal Model Build( string name ) =>
		new SourceModelBuilder( mount, path ).BuildModel( Source.Mdl, Source.Vtx, Source.Physics, name, ReferencePose, Animations );

	internal static SourceModel Read( BaseMount mount, string filename )
	{
		filename = filename.Replace( '\\', '/' );
		using var mdlStream = mount.OpenContentFile( filename );
		var mdl = new MdlFile( mdlStream );
		PhyFile physics = null;
		var phyPath = System.IO.Path.ChangeExtension( filename, ".phy" );
		mount.TryOpenContentFile( phyPath, out var phyStream );
		using ( phyStream )
		{
			if ( phyStream is not null )
			{
				try
				{
					physics = new PhyFile( phyStream, phyPath, mdl.Checksum );
				}
				catch ( Exception e ) when ( e is InvalidDataException or NotSupportedException )
				{
					Log.Trace( $"{filename}: physics was not imported: {e.Message}" );
				}
			}
		}
		Transform[] referencePose = null;
		if ( physics is { Solids.Count: 1, Constraints.Count: 0 } && !mdl.IsStaticProp )
		{
			try
			{
				referencePose = mdl.ReadPhysicsReferencePose( mdlStream, path =>
					mount.OpenContentFile( path.StartsWith( "models/", StringComparison.OrdinalIgnoreCase )
						? path : filename[..(filename.LastIndexOf( '/' ) + 1)] + path ) );
			}
			catch ( Exception e ) when ( e is InvalidDataException or NotSupportedException or FileNotFoundException )
			{
				Log.Trace( $"{filename}: rigid collision reference alignment is unavailable; authored bind pose retained: {e.Message}" );
			}
		}
		IReadOnlyList<SourceAnimation> animations = Array.Empty<SourceAnimation>();
		if ( !mdl.IsStaticProp )
		{
			try
			{
				if ( mdl.IncludeCount > 0 )
				{
					try
					{
						var graph = mdl.ResolveIncludes( filename, mount.OpenContentFile );
						animations = mdl.ReadAnimations( graph, mdlStream, mount.OpenContentFile );
					}
					catch ( Exception e ) when ( e is IOException or InvalidDataException or ArgumentException or NotSupportedException or UnauthorizedAccessException )
					{
						Log.Trace( $"{filename}: shared animations unavailable; trying root-only: {e.Message}" );
						animations = ReadRootAnimations();
					}
				}
				else
				{
					animations = ReadRootAnimations();
				}

				IReadOnlyList<SourceAnimation> ReadRootAnimations() => mdl.ReadAnimations( mdlStream, path =>
					mount.OpenContentFile( path.StartsWith( "models/", StringComparison.OrdinalIgnoreCase )
						? path : filename[..(filename.LastIndexOf( '/' ) + 1)] + path ) );
			}
			catch ( Exception e ) when ( e is IOException or InvalidDataException or ArgumentException or NotSupportedException or UnauthorizedAccessException )
			{
				Log.Trace( $"{filename}: animations were not imported: {e.Message}" );
			}
		}
		if ( mdl.TotalVertices == 0 )
			return new SourceModel( mount, filename, (mdl, null, physics), referencePose, animations );
		using var vvdStream = mount.OpenContentFile( System.IO.Path.ChangeExtension( filename, ".vvd" ) );
		using var vtxStream = mount.OpenContentFile( System.IO.Path.ChangeExtension( filename, ".dx90.vtx" ) );
		var vvd = new VvdFile( vvdStream );
		return new SourceModel( mount, filename, (mdl, new VtxFile( vtxStream, mdl, vvd ), physics), referencePose, animations );
	}
}
