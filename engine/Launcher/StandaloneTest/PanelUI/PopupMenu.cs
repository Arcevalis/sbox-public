using Sandbox.UI;
using Sandbox.UI.Construct;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sandbox.LauncherUI;

/// <summary>
/// A menu in its own OS window, hanging outside the window that opened it.
/// </summary>
static class PopupMenu
{
	public record Item( string Title, Action Action, string Icon = null );

	static Editor.PanelWindow open;

	public static void Close()
	{
		if ( open is null ) return;

		var window = open;
		open = null;
		window.OnCloseRequested = null;
		window.Dispose();
	}

	/// <summary>
	/// Open a menu under the cursor.
	/// </summary>
	public static void Open( Editor.PanelWindow parent, IEnumerable<Item> items )
	{
		Close();

		if ( parent is null ) return;

		var list = items.ToArray();
		var rows = list.Count( x => x is not null );

		var popup = Editor.PanelWindow.Popup( parent, parent.MousePosition, new Vector2( 180, 12 + rows * 26 + (list.Length - rows) * 11 ) );

		// A click anywhere that isn't the menu dismisses it, same as an OS menu
		popup.OnCloseRequested = Close;

		// The popup is its own window with its own root, so it carries the theme itself
		popup.Root.SetClass( "style-light", LauncherPreferences.LightTheme );

		var menu = popup.Root.Add.Panel( "dropdown" );
		menu.StyleSheet.Load( "/styles/editor.scss" );

		foreach ( var item in list )
		{
			if ( item is null )
			{
				menu.Add.Panel( "separator" );
				continue;
			}

			menu.AddChild( new Button( item.Title, item.Icon, "row", () =>
			{
				Close();
				item.Action?.Invoke();
			} ) );
		}

		open = popup;
	}
}
