using System;
using System.Collections.Generic;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceModelBuilder
{
	internal static void AddAnimations( ModelBuilder builder, MdlFile mdl, IReadOnlyList<SourceAnimation> animations )
	{
		if ( animations == null || animations.Count == 0 || mdl.BoneCount == 0 )
			return;
		foreach ( var animation in animations )
		{
			if ( animation.Frames <= 0 || animation.LocalFrames == null
				|| animation.LocalFrames.Length != (long)animation.Frames * mdl.BoneCount )
				continue;
			// Animation tracks are parent-local, independently of the bind skeleton's
			// representation. Composing here applies the hierarchy twice at playback.
			var frames = animation.LocalFrames;
			var built = builder.AddAnimation( animation.Name, animation.Fps );
			built.Looping = animation.Looping;
			for ( var frame = 0; frame < animation.Frames; frame++ )
				built.AddFrame( new Span<Transform>( frames, frame * mdl.BoneCount, mdl.BoneCount ) );
		}
	}
}
