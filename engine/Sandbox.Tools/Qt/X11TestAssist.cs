using Sandbox.Engine;
using System;
using System.Runtime.InteropServices;

namespace Editor;

/// <summary>
/// Managed XTEST edge-snap assist for XWayland: re-issues a cursor warp's
/// destination as fake device motion so it lands mid-drag (plain warps don't
/// under the Wayland implicit grab). Explicit calls at the managed warp sites,
/// no interposition, no X roundtrips. No-op off Linux; latches unavailable
/// without libXtst. Public only for the tools addons assembly.
/// </summary>
public static class X11TestAssist
{
	/// <summary>
	/// Longest we go between injections for one drag.
	/// </summary>
	const long CooldownMs = 500;

	[DllImport( "libX11.so.6", EntryPoint = "XDefaultScreen" )]
	static extern int XDefaultScreen( IntPtr display );

	[DllImport( "libXtst.so.6", EntryPoint = "XTestFakeMotionEvent" )]
	static extern int XTestFakeMotionEvent( IntPtr display, int screen, int x, int y, ulong delay );

	static long _lastFireMs;
	static bool _unavailable;

	/// <summary>
	/// Fire XTEST motion at the warp destination. Call after issuing the Qt warp;
	/// no-op unless buttons are held, past cooldown, and XTEST is available.
	/// </summary>
	public static void Fire( Widget widget, Vector2 local )
	{
		if ( !OperatingSystem.IsLinux() ) return;
		if ( widget is null ) return;
		if ( Application.MouseButtons == MouseButtons.None ) return;

		var now = Environment.TickCount64;
		if ( now - _lastFireMs < CooldownMs ) return;
		if ( _unavailable ) return;

		try
		{
			if ( !X11InputRegion.TryGetDisplay( out var display ) ) return;

			// Root pixels: Qt global mapping scaled by the widget's pixel ratio.
			var global = widget.ToScreen( local );
			var scale = widget.DpiScale;
			var x = (int)(global.x * scale);
			var y = (int)(global.y * scale);

			_lastFireMs = now;
			XTestFakeMotionEvent( display, XDefaultScreen( display ), x, y, 0 );
			X11InputRegion.XFlush( display );

			InputDebug.Event( "gamemode", $"xtest-assist to=({x},{y})" );
		}
		catch ( DllNotFoundException )
		{
			_unavailable = true;
		}
		catch ( EntryPointNotFoundException )
		{
			_unavailable = true;
		}
	}
}
