using System;
using System.Runtime.InteropServices;

namespace Editor;

/// <summary>
/// Maintains the X11 <c>ShapeInput</c> region of a native window.
///
/// Qt's <c>WA_TransparentForMouseEvents</c> is in-process hit-testing only - it is handled by
/// walking up to the parent widget. A top level window has no parent to walk to, so on X11 the
/// server still hit-tests it, hands it every click, and Qt drops them. Worse,
/// <c>QWidget::setAttribute</c> is a documented no-op for that attribute, so the only thing that
/// ever reaches the server is <c>Qt::WindowTransparentForInput</c> - and Qt promotes the attribute
/// to that flag only while constructing or reparenting, never when the attribute is set later.
///
/// Qt also offers no API for a <i>partial</i> input region: <c>QWidget::setMask</c> only touches
/// the bounding shape. So an overlay that wants to pass clicks through to what is underneath while
/// keeping its own buttons clickable has to talk to XShape directly.
///
/// Everything here is a no-op off Linux, where the attribute already behaves.
/// </summary>
internal static class X11InputRegion
{
	const int ShapeInput = 2;
	const int ShapeSet = 0;

	[StructLayout( LayoutKind.Sequential )]
	struct XRectangle
	{
		public short x;
		public short y;
		public ushort width;
		public ushort height;
	}

	[DllImport( "libX11.so.6", EntryPoint = "XOpenDisplay" )]
	static extern IntPtr XOpenDisplay( IntPtr name );

	[DllImport( "libX11.so.6", EntryPoint = "XFlush" )]
	static extern int XFlush( IntPtr display );

	[DllImport( "libXext.so.6", EntryPoint = "XShapeCombineRectangles" )]
	static extern void XShapeCombineRectangles( IntPtr display, IntPtr window, int destKind,
		int xOff, int yOff, XRectangle[] rectangles, int nRects, int op, int ordering );

	static IntPtr _display;
	static bool _unavailable;

	/// <summary>
	/// Our own connection to the server. Qt reads X on a dedicated thread and we must not issue
	/// requests on its connection from the GUI thread; shape changes are rare and idempotent, so a
	/// second connection is cheaper than synchronising with Qt's.
	/// </summary>
	static bool TryGetDisplay( out IntPtr display )
	{
		display = _display;

		if ( _unavailable ) return false;
		if ( display != IntPtr.Zero ) return true;

		try
		{
			display = _display = XOpenDisplay( IntPtr.Zero );
		}
		catch ( DllNotFoundException )
		{
			// No Xlib - a headless or non-X session. Nothing to shape.
			_unavailable = true;
			return false;
		}
		catch ( EntryPointNotFoundException )
		{
			_unavailable = true;
			return false;
		}

		if ( display == IntPtr.Zero )
		{
			_unavailable = true;
			return false;
		}

		return true;
	}

	/// <summary>
	/// Replace <paramref name="window"/>'s input region with <paramref name="rects"/>, in window
	/// local pixels. An empty span makes the window entirely click-through: the server will hit
	/// test straight past it to whatever is underneath.
	/// </summary>
	public static void Set( IntPtr window, ReadOnlySpan<Rect> rects )
	{
		if ( !OperatingSystem.IsLinux() ) return;
		if ( window == IntPtr.Zero ) return;
		if ( !TryGetDisplay( out var display ) ) return;

		var converted = new XRectangle[rects.Length];

		for ( int i = 0; i < rects.Length; i++ )
		{
			var r = rects[i];

			// A zero or negative extent rect would be dropped by the server anyway, and a
			// negative origin clamps, so keep everything in range rather than relying on that.
			converted[i] = new XRectangle
			{
				x = (short)Math.Clamp( r.Left, short.MinValue, short.MaxValue ),
				y = (short)Math.Clamp( r.Top, short.MinValue, short.MaxValue ),
				width = (ushort)Math.Clamp( r.Width, 0, ushort.MaxValue ),
				height = (ushort)Math.Clamp( r.Height, 0, ushort.MaxValue )
			};
		}

		XShapeCombineRectangles( display, window, ShapeInput, 0, 0, converted, converted.Length, ShapeSet, 0 );
		XFlush( display );
	}

	/// <summary>
	/// Give <paramref name="window"/> back an input region covering all of <paramref name="size"/>,
	/// which is what a window has before anything shapes it.
	/// </summary>
	public static void Reset( IntPtr window, Vector2 size )
	{
		Span<Rect> full = [new Rect( 0, 0, size.x, size.y )];
		Set( window, full );
	}
}
