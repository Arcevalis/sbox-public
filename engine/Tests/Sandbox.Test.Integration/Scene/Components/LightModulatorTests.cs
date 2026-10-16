namespace SceneTests.Components;

/// <summary>
/// Camera preparation must publish current modulation data even when the scene has never ticked.
/// </summary>
[TestClass]
public class LightModulatorRenderPreparationTests
{
	/// <summary>
	/// Small changes to reciprocal axes can move a large pane into a light's range.
	/// </summary>
	[TestMethod]
	public void SmallRotationRefreshesCandidates()
	{
		if ( !Graphics.IsAvailable )
			Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );

		var scene = Scene.CreateEditorScene();
		using var scope = scene.Push();

		try
		{
			var light = new ScenePointLight( scene.SceneWorld, new Vector3( 42, 499, 0 ), 1, Color.White );
			var pane = scene.CreateObject().Components.Create<LightModulator>();
			pane.Texture = Texture.White;
			pane.Size = new Vector2( 1000, 1000 );
			pane.WorldPosition = new Vector3( 50, 0, 0 );

			var system = scene.GetSystem<LightModulatorSystem>();
			scene.PreCameraRender();
			Assert.AreEqual( 0, system.CandidateCount );

			pane.WorldRotation = Rotation.FromYaw( 1 );
			scene.PreCameraRender();
			Assert.AreEqual( 1, system.CandidateCount, "A small rotation must include lights newly intersecting the pane." );
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>
	/// Appearance edits upload rectangles without rebuilding candidates; light changes do the reverse.
	/// </summary>
	[TestMethod]
	public void AppearanceChangesReuseCandidates()
	{
		if ( !Graphics.IsAvailable )
			Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );

		var scene = Scene.CreateEditorScene();
		using var scope = scene.Push();

		try
		{
			var light = scene.CreateObject().Components.Create<PointLight>();
			light.Radius = 100;
			light.Shadows = false;

			var pane = scene.CreateObject().Components.Create<LightModulator>();
			pane.Texture = Texture.White;
			pane.WorldPosition = new Vector3( 50, 0, 0 );

			var system = scene.GetSystem<LightModulatorSystem>();
			scene.PreCameraRender();
			Assert.AreEqual( 1, system.CandidateCount );
			Assert.AreEqual( 1, system.RectangleUploadCount );
			var rebuildCount = system.RebuildCount;
			var uploadCount = system.RectangleUploadCount;
			var buildCount = pane.DataBuildCount;

			System.Action[] appearanceChanges =
			[
				() => pane.Tint = Color.Red,
				() => pane.Brightness = 2,
				() => pane.Tiling = new Vector2( 2, 3 ),
				() => pane.Offset = new Vector2( 0.25f, 0.5f ),
				() => pane.TextureRotation = 45,
				() => pane.ScrollSpeed = Vector2.One,
				() => pane.Strength = 0,
				() => pane.Texture = Texture.Black
			];

			foreach ( var change in appearanceChanges )
			{
				change();
				scene.PreCameraRender();
				Assert.AreEqual( rebuildCount, system.RebuildCount, "Appearance must not rebuild candidate lists." );
				Assert.AreEqual( ++uploadCount, system.RectangleUploadCount, "Changed appearance must reach the GPU." );
				Assert.AreEqual( ++buildCount, pane.DataBuildCount );
				Assert.AreEqual( 1, system.CandidateCount );

				scene.PreCameraRender();
				Assert.AreEqual( uploadCount, system.RectangleUploadCount, "Unchanged captures must reuse the upload." );
				Assert.AreEqual( buildCount, pane.DataBuildCount, "Unchanged captures must not repack rectangles." );
			}

			light.Radius = 25;
			scene.PreCameraRender();
			Assert.AreEqual( ++rebuildCount, system.RebuildCount );
			Assert.AreEqual( 0, system.CandidateCount );
			Assert.AreEqual( uploadCount, system.RectangleUploadCount, "Light changes must not upload unchanged rectangles." );

			pane.WorldPosition = new Vector3( 10, 0, 0 );
			scene.PreCameraRender();
			Assert.AreEqual( ++rebuildCount, system.RebuildCount );
			Assert.AreEqual( ++uploadCount, system.RectangleUploadCount );
			Assert.AreEqual( 1, system.CandidateCount );

			pane.Size = new Vector2( 128, 128 );
			scene.PreCameraRender();
			Assert.AreEqual( ++rebuildCount, system.RebuildCount, "Resizing must invalidate the broad phase." );
			Assert.AreEqual( ++uploadCount, system.RectangleUploadCount );
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>
	/// Unticked scenes, same-frame changes and repeated captures use the current cached snapshot.
	/// </summary>
	[TestMethod]
	public void PrepareUntickedSceneAndImmediateChanges()
	{
		if ( !Graphics.IsAvailable )
			Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );

		var scene = Scene.CreateEditorScene();
		using var scope = scene.Push();

		try
		{
			var light = scene.CreateObject().Components.Create<PointLight>();
			light.Radius = 100;
			light.Shadows = false;

			// Sunlight uses the scene rectangle buffer without adding local-light candidates.
			var sun = scene.CreateObject().Components.Create<DirectionalLight>();
			sun.Shadows = false;

			var pane = scene.CreateObject().Components.Create<LightModulator>();
			pane.Texture = Texture.White;
			pane.WorldPosition = new Vector3( 50, 0, 0 );

			var camera = scene.CreateObject().Components.Create<CameraComponent>();
			var system = scene.GetSystem<LightModulatorSystem>();
			Assert.IsNotNull( system );
			Assert.AreEqual( 0, system.RebuildCount );
			scene.Signal( GameObjectSystem.Stage.FinishUpdate );
			Assert.AreEqual( 0, system.RebuildCount, "Simulation must not perform a second rendering refresh." );
			Assert.AreEqual( 0, pane.DataBuildCount );

			// No EditorTick: this is the preparation path used by on-demand captures.
			scene.PreCameraRender();
			Assert.AreEqual( 1, system.CandidateCount );
			Assert.AreEqual( 1, camera.SceneCamera.Attributes.GetInt( "LightModulatorRectangleCount" ) );
			Assert.AreEqual( 1, camera.SceneCamera.Attributes.GetInt( "LightModulatorCandidateCount" ) );

			var rebuildCount = system.RebuildCount;
			scene.PreCameraRender();
			Assert.AreEqual( rebuildCount, system.RebuildCount, "An unchanged capture must reuse the GPU snapshot." );
			Assert.AreEqual( 1, pane.DataBuildCount );

			pane.WorldPosition = new Vector3( 200, 0, 0 );
			scene.PreCameraRender();
			Assert.AreEqual( 0, system.CandidateCount );
			Assert.AreEqual( 0, camera.SceneCamera.Attributes.GetInt( "LightModulatorCandidateCount" ) );
			Assert.AreEqual( 1, camera.SceneCamera.Attributes.GetInt( "LightModulatorRectangleCount" ),
				"Sunlight must retain rectangles outside every local light's range." );

			pane.WorldPosition = new Vector3( 50, 0, 0 );
			scene.PreCameraRender();
			Assert.AreEqual( 1, system.CandidateCount );

			pane.Enabled = false;
			scene.PreCameraRender();
			Assert.AreEqual( 0, system.CandidateCount );
			Assert.AreEqual( 0, camera.SceneCamera.Attributes.GetInt( "LightModulatorRectangleCount" ) );
			Assert.AreEqual( 0, camera.SceneCamera.Attributes.GetInt( "LightModulatorCandidateCount" ) );

			pane.Enabled = true;
			scene.PreCameraRender();
			Assert.AreEqual( 1, system.CandidateCount );
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>
	/// Parent transforms, texture handle replacement and raw scene-light deletion invalidate cached rendering data.
	/// </summary>
	[TestMethod]
	public void CachedDataTracksExternalChanges()
	{
		if ( !Graphics.IsAvailable )
			Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );

		var scene = Scene.CreateEditorScene();
		using var scope = scene.Push();
		using var texture = Texture.Create( 1, 1 ).WithData( new byte[] { 255, 255, 255, 255 } ).Finish();

		try
		{
			var light = new ScenePointLight( scene.SceneWorld, Vector3.Zero, 100, Color.White );
			var parent = scene.CreateObject();
			var paneObject = scene.CreateObject();
			paneObject.Parent = parent;
			var pane = paneObject.Components.Create<LightModulator>();
			pane.Texture = texture;
			pane.WorldPosition = new Vector3( 50, 0, 0 );
			var camera = scene.CreateObject().Components.Create<CameraComponent>();
			var system = scene.GetSystem<LightModulatorSystem>();

			using var hook = scene.AddHook( GameObjectSystem.Stage.PrepareRender, 0,
				() => scene.RenderAttributes.Set( "RenderPreparationTest", 42 ), nameof( LightModulatorRenderPreparationTests ), "Test preparation" );
			scene.PreCameraRender();
			Assert.AreEqual( 42, camera.SceneCamera.Attributes.GetInt( "RenderPreparationTest" ), "Preparation precedes camera attribute copying." );
			Assert.AreEqual( 1, system.CandidateCount );

			var buildCount = pane.DataBuildCount;
			var rebuildCount = system.RebuildCount;
			texture.CopyFrom( Texture.Black );
			scene.PreCameraRender();
			Assert.AreEqual( ++buildCount, pane.DataBuildCount, "Replacing a texture handle must refresh its bindless index." );
			Assert.AreEqual( rebuildCount, system.RebuildCount );

			parent.WorldPosition = new Vector3( 200, 0, 0 );
			scene.PreCameraRender();
			Assert.AreEqual( ++buildCount, pane.DataBuildCount );
			Assert.AreEqual( 0, system.CandidateCount );

			light.Position = pane.WorldPosition;
			scene.PreCameraRender();
			Assert.AreEqual( 1, system.CandidateCount );
			Assert.AreEqual( buildCount, pane.DataBuildCount );

			light.Delete();
			scene.PreCameraRender();
			Assert.AreEqual( 0, system.CandidateCount, "Deferred native deletion must not leave a light in the snapshot." );

			scene.ReloadSystems();
			scene.PreCameraRender();
			Assert.AreEqual( 1, camera.SceneCamera.Attributes.GetInt( "LightModulatorRectangleCount" ) );
			Assert.AreEqual( buildCount, pane.DataBuildCount, "Reloaded systems can reuse existing component snapshots." );
		}
		finally
		{
			scene.Destroy();
		}
	}
}
