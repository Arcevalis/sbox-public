using System;
using System.Collections.Generic;
using Sandbox;

namespace Editor;

/// <summary>
/// Edits the shared draft recipe and the scene's compile-on-save preference.
/// </summary>
internal sealed class SceneCompileSettingsWidget : Widget
{
	readonly SceneCompileSession _session = SceneCompileSession.Current;
	readonly List<Action> _refreshNumbers = new();
	readonly Checkbox _compileOnSave;
	Scene _scene;
	string _path;
	(float AggregateCost, float MaxChunkSize) _displayedSettings;
	bool _refreshing;

	internal SceneCompileSettingsWidget( Widget parent ) : base( parent )
	{
		_session.Refresh();

		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 6;
		var grid = Layout.Grid();
		grid.Spacing = 6;

		grid.AddCell( 0, 0, new Label( "Aggregate cost" ) );
		grid.AddCell( 1, 0, Number( () => _session.AggregateCost,
			value => _session.AggregateCost = value,
			"Higher values favor fewer, larger aggregates. Smaller aggregates let the renderer skip more off-screen geometry." ) );

		grid.AddCell( 0, 1, new Label( "Target chunk size" ) );
		grid.AddCell( 1, 1, Number( () => _session.MaxChunkSize,
			value => _session.MaxChunkSize = value,
			"Target chunk size in scene units. Some chunks may be larger." ) );

		Layout.Add( grid );
		Layout.Add( new Label( "These values are saved when you compile this scene." )
		{
			WordWrap = true,
			Color = Theme.TextLight.WithAlpha( 0.7f ),
			ToolTip = "Scenes without saved compile settings use the values from your last compile."
		} );
		var footer = Layout.AddRow();
		footer.Add( new Button.Clear( "Reset defaults", "restart_alt" )
		{
			ToolTip = "Reset aggregate cost and target chunk size to their default values.",
			Clicked = () =>
			{
				if ( CanEdit )
					_session.ResetSettings();

				RefreshControls();
			}
		} );
		footer.AddStretchCell();

		Layout.AddSpacingCell( 8 );
		_compileOnSave = Layout.Add( new Checkbox( "Compile on save", this )
		{
			ToolTip = "Automatically compile this scene when you save it.",
			Clicked = () =>
			{
				try
				{
					if ( CanEdit )
						_session.CompileOnSave = _compileOnSave.Value;
				}
				finally
				{
					RefreshControls();
				}
			}
		} );
		Layout.AddStretchCell();

		_session.Changed += RefreshControls;
		RefreshControls();
	}

	bool CanEdit => !_refreshing && !_session.Running && !Game.IsPlaying
		&& _scene.IsValid() && _scene == _session.Scene
		&& _path == _scene.Source?.ResourcePath
		&& SceneEditorSession.Active is { IsPrefabSession: false } active
		&& active.Scene == _scene;

	LineEdit Number( Func<float> get, Action<float> set, string tip )
	{
		var edit = new RecipeNumber( $"{get():G9}", this ) { ToolTip = tip, MaximumWidth = 120 };
		edit.EditingFinished += () =>
		{
			if ( _refreshing )
				return;

			if ( CanEdit && float.TryParse( edit.Text, out var value ) && float.IsFinite( value ) && value > 0.0f )
				set( value );

			edit.Text = $"{get():G9}";
		};

		_refreshNumbers.Add( () => edit.Text = $"{get():G9}" );
		return edit;
	}

	[EditorEvent.Frame]
	void RefreshControls()
	{
		if ( !IsValid )
			return;

		var scene = _session.Scene;
		var path = scene?.Source?.ResourcePath;
		var settings = (_session.AggregateCost, _session.MaxChunkSize);
		if ( scene != _scene || path != _path || _displayedSettings != settings )
		{
			// Replace stale text before rebinding, so a pending edit cannot affect another scene.
			_refreshing = true;
			try
			{
				foreach ( var refresh in _refreshNumbers )
					refresh();

				_scene = scene;
				_path = path;
				_displayedSettings = settings;
			}
			finally
			{
				_refreshing = false;
			}
		}

		Enabled = CanEdit;
		_compileOnSave.Value = _session.CompileOnSave;
		_compileOnSave.Enabled = CanEdit && !string.IsNullOrEmpty( _path );
	}

	public override void OnDestroyed()
	{
		_session.Changed -= RefreshControls;
		base.OnDestroyed();
	}

	sealed class RecipeNumber( string text, Widget parent ) : LineEdit( text, parent )
	{
		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );
			e.Accepted = true;
		}
	}
}
