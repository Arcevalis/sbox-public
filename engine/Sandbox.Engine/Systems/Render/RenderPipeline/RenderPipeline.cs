using NativeEngine;

namespace Sandbox.Rendering;

/// <summary>
/// Builds the layers of native's rendering pipelines: <see cref="RenderPipeline"/> hands it each view native set up.
/// Sandbox.SceneRenderer's <c>NativeRenderPipeline</c> is the one, installed at startup.
/// </summary>
internal interface INativeRenderPipeline
{
	/// <summary>
	/// Add every layer of a view the standard pipeline set up.
	/// </summary>
	void AddLayersToView( CRenderPipelineSetup setup );

	/// <summary>
	/// Add every layer of a 3D skybox's view.
	/// </summary>
	void AddSkybox3DLayersToView( CRenderPipelineSetup setup );

	/// <summary>
	/// A view has been submitted: whatever its layers needed while it rendered can be used again.
	/// </summary>
	void OnSceneViewSubmitted( ISceneView view );

	/// <summary>
	/// Make its shaders and materials, on the main thread at startup. Its layers would otherwise make them the first time
	/// they render, on a render job, where that isn't allowed.
	/// </summary>
	void WarmUp();

	/// <summary>
	/// Free what it holds, at engine shutdown.
	/// </summary>
	void Shutdown();
}

/// <summary>
/// What native's rendering pipelines (renderingpipeline_standard.cpp) call once they've set up a view's render targets and
/// attributes. The layers come from <see cref="Current"/>.
/// </summary>
internal static class RenderPipeline
{
	/// <summary>
	/// The pipeline that adds the layers.
	/// </summary>
	internal static INativeRenderPipeline Current { get; set; }

	static bool warned;

	static INativeRenderPipeline Get()
	{
		if ( Current is null && !warned )
		{
			warned = true;
			Log.Error( "No native render pipeline is installed - views will render nothing" );
		}

		return Current;
	}

	internal static void AddLayersToView( CRenderPipelineSetup setup ) => Get()?.AddLayersToView( setup );

	internal static void AddSkybox3DLayersToView( CRenderPipelineSetup setup ) => Get()?.AddSkybox3DLayersToView( setup );

	internal static void OnSceneViewSubmitted( ISceneView view ) => Current?.OnSceneViewSubmitted( view );

	internal static void WarmUp() => Current?.WarmUp();

	internal static void Shutdown() => Current?.Shutdown();
}
