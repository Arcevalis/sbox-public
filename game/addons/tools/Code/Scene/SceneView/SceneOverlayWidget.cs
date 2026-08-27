namespace Editor;

public class SceneOverlayWidget : Widget
{
	public static SceneOverlayWidget Active { get; private set; }

	public Layout Header { get; private set; }

	internal SceneOverlayWidget( Widget parent ) : base( parent )
	{
		TranslucentBackground = true;
		NoSystemBackground = true;

		WindowFlags = WindowFlags.FramelessWindowHint | WindowFlags.Tool;

		Active = this;

		Layout = Layout.Column();
		Layout.Margin = 8;

		var header = Layout.AddRow();
		header.AddStretchCell();
		Header = header.AddRow();
		Header.Spacing = 4;

		Layout.AddStretchCell();

		// doesn't handle floating windows, but there's no way to hook into dockwrapper events right now
		EditorWindow.Moved += UpdateDimensions;

		TransparentForMouseEvents = true;
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		if ( EditorWindow.IsValid() )
		{
			EditorWindow.Moved -= UpdateDimensions;
		}
	}

	int lastGeometryHash = -1;

	[EditorEvent.Frame]
	private void UpdateDimensions()
	{
		if ( !Parent.IsValid() )
			return;

		// this wasn't always being triggered properly when relying on widget events from the parent (causing HUGE jank)
		// The children are in the hash too: we're a click-through top level window, so on X11 our
		// input region has to be kept as the union of whatever children still want clicks.
		int geometryHash = HashCode.Combine( Parent.ScreenPosition, Parent.Size );

		foreach ( var child in Children )
		{
			if ( !child.IsValid() || !child.Visible ) continue;
			geometryHash = HashCode.Combine( geometryHash, child.Position, child.Size );
		}

		if ( lastGeometryHash != geometryHash )
		{
			Position = Parent.ScreenPosition;
			Size = Parent.Size;

			// Also covers Qt destroying and recreating our native handle when a viewport is docked
			// or undocked - the fresh window comes back with a full, blocking input region.
			RefreshInputRegion();
		}

		lastGeometryHash = geometryHash;
	}

	internal RealTimeSince timeSinceNeededRedraw = 0.0f;

	[EditorEvent.Frame]
	public void Frame()
	{
		if ( timeSinceNeededRedraw > 0.1f )
		{
			Update();
			timeSinceNeededRedraw = 0.0f;
		}
	}

	protected override void OnPaint()
	{
		Active = this;

		if ( Parent is SceneViewportWidget vw && vw.SceneView.CurrentView == SceneViewWidget.ViewMode.Game )
		{
			EditorEvent.Run( "sceneview.paintoverlay" );
		}
	}
}
