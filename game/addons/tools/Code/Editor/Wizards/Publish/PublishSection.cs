namespace Editor;

/// <summary>Consistent action sizing and native button styling throughout publishing.</summary>
internal sealed class PublishButton : Button
{
	public static Color SecondaryTint => "#48494c";

	public PublishButton( string title, string icon = null, bool primary = false ) : base( title, icon )
	{
		FixedHeight = 30;
		Tint = primary ? Theme.Primary : SecondaryTint;
	}

	// Virtual list actions use the same border and gradient as native Buttons.
	public static void DrawBackground( Rect rect, bool hovered )
	{
		var color = SecondaryTint.ToHsv();
		var background = color with { Value = color.Value + (hovered ? 0.2f : 0) };
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( background with { Value = background.Value + 0.04f, Saturation = color.Saturation * 0.8f } );
		Paint.DrawRect( rect, 3 );
		Paint.SetBrushLinear( rect.TopLeft, rect.BottomRight, background, background with { Value = background.Value - 0.03f } );
		Paint.DrawRect( rect.Shrink( 1 ), 3 );
		Paint.SetDefaultFont();
	}

	public static void DrawAction( Rect rect, string text, string icon, Color color )
	{
		Paint.SetDefaultFont();
		var textWidth = Paint.MeasureText( text ).x;
		var left = rect.Center.x - (textWidth + 20) / 2;
		Paint.SetPen( color );
		Paint.DrawIcon( new Rect( left, rect.Center.y - 8, 16, 16 ), icon, 16 );
		Paint.DrawText( new Rect( left + 20, rect.Top, textWidth, rect.Height ), text, TextFlag.Center );
	}
}

/// <summary>A lightly separated section shared by the publishing settings and wizard.</summary>
internal class PublishSection : Widget
{
	public Layout Header { get; }

	public PublishSection( Widget parent, string title, Color accent, string icon, int? count = null, Pixmap iconImage = null ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 10;
		Layout.Alignment = TextFlag.Top;
		Header = Layout.AddRow();
		Header.Spacing = 8;
		if ( iconImage is not null || !string.IsNullOrEmpty( icon ) )
		{
			var symbol = Header.Add( new Widget( this ) { FixedSize = 20 } );
			symbol.OnPaintOverride = () =>
			{
				if ( iconImage is not null )
				{
					Paint.ClearPen();
					Paint.Draw( symbol.LocalRect.Shrink( 2 ), iconImage );
				}
				else
				{
					Paint.SetPen( accent );
					Paint.DrawIcon( symbol.LocalRect, icon, 18 );
				}
				return true;
			};
		}
		Header.Add( new Label.Subtitle( title ) { Color = Theme.Text, WordWrap = false, FixedHeight = 32 } );
		if ( count is { } value )
		{
			var badge = Header.Add( new Label( $"{value:n0}" ) { Alignment = TextFlag.Center, FixedHeight = 22, MinimumWidth = 28 } );
			badge.SetStyles( $"background-color: {Theme.Text.WithAlpha( 0.08f ).Hex}; color: {Theme.Text.Hex}; border-radius: 10px; padding: 2px 8px;" );
		}
		Header.AddStretchCell();
	}

	protected override void OnPaint()
	{
		Paint.SetPen( Theme.Text.WithAlpha( 0.10f ), 1 );
		Paint.SetBrush( Theme.ControlBackground.WithAlpha( 0.32f ) );
		Paint.DrawRect( LocalRect.Shrink( 1 ), 4 );
	}
}
