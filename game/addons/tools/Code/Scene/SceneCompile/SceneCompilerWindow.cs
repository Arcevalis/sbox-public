using System;
using System.Collections.Generic;

namespace Editor;

/// <summary>
/// A report and settings view. The shared session, not this window, owns the compile.
/// </summary>
internal sealed class SceneCompilerWindow : Dialog
{
	SceneCompileSession _session;

	readonly Label _title;
	readonly Label _status;
	readonly Bar _bar;
	readonly SegmentedControl _tabs;
	readonly Dictionary<string, Widget> _pages = new();
	readonly Widget _options;
	readonly ListView _report;
	readonly TextEdit _log;
	readonly Button _compile;

	readonly List<Group> _groups = new();
	readonly List<Entry> _lines = new();

	SceneCompileReport _sources;
	string _error;
	string[] _summary;

	int _displayedLineCount;
	bool _wasRunning;

	static SceneCompilerWindow _current;

	public override void OnDestroyed()
	{
		_session.Changed -= OnSessionChanged;
		if ( _current == this )
			_current = null;

		base.OnDestroyed();
	}

	/// <summary>
	/// Bring up the compiler for the active scene, reusing the window if it's already open.
	/// </summary>
	internal static void Open( string page = "Report" )
	{
		var session = SceneCompileSession.Current;
		session.Refresh();
		Open( session.HasResult ? session.CreateReportSnapshot() : session, page );
	}

	[Event( "scene.compile.show-report" )]
	internal static void Open( SceneCompileSession session, string page )
	{
		if ( _current is { IsValid: true } )
		{
			_current.Bind( session );
			_current.SelectPage( page );
			_current.Show();
			return;
		}

		_current = new SceneCompilerWindow( session, page );
	}

	SceneCompilerWindow( SceneCompileSession session, string page ) : base( EditorWindow )
	{
		Window.Title = "Scene Compile Report";
		Window.SetWindowIcon( "hardware" );
		Window.Size = new Vector2( 700, 620 );
		Window.StateCookie = "SceneCompiler";

		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 8;

		var header = Layout.AddRow();
		header.Spacing = 12;

		_title = header.Add( new Label( "" ) );
		_title.SetStyles( "font-weight: bold;" );

		header.AddStretchCell();

		_tabs = header.Add( new SegmentedControl() );
		_tabs.MinimumWidth = 300;
		_tabs.OnSelectedChanged = ShowPage;

		var report = new ListView( this );
		report.ItemSize = new Vector2( 0, 22 );
		report.ItemPaint = PaintEntry;
		report.ItemClicked = OnEntryClicked;
		report.Margin = 4;
		_report = report;

		_log = new TextEdit( this );
		_log.ReadOnly = true;
		_log.HorizontalScrollbarMode = ScrollbarMode.Off;
		_log.SetStyles( "font-family: Consolas, monospace; padding: 8px;" );

		_options = new SceneCompileSettingsWidget( this );

		AddPage( "Report", "list", _report );
		AddPage( "Log", "notes", _log );
		AddPage( "Settings", "settings", _options );

		ShowPage( "Report" );

		_status = Layout.Add( new Label( "" ) { WordWrap = true } );
		_bar = Layout.Add( new Bar() );
		_bar.FixedHeight = 8;

		var footer = Layout.AddRow();
		footer.Spacing = 8;
		footer.AddStretchCell();

		_compile = footer.Add( new Button.Primary( "Compile", "hardware" ) { Clicked = OnCompile } );

		Bind( session );
		SelectPage( page );

		Show();
	}

	/// <summary>
	/// Add one of the window's pages. They all fill the same space with only one of them up, so the
	/// window doesn't turn into three panes fighting over the height.
	/// </summary>
	void AddPage( string title, string icon, Widget page )
	{
		_pages[title] = page;

		_tabs.AddOption( title, icon );

		Layout.Add( page, 1 );
	}

	void ShowPage( string title )
	{
		foreach ( var (name, page) in _pages )
		{
			page.Visible = name == title;
		}
	}

	/// <summary>
	/// Bring a page up, moving the tabs with it.
	/// </summary>
	void SelectPage( string title )
	{
		_tabs.Selected = title;

		ShowPage( title );
	}

	void Bind( SceneCompileSession session )
	{
		if ( _session is not null )
			_session.Changed -= OnSessionChanged;

		_session = session;
		_session.Changed += OnSessionChanged;
		_wasRunning = session.Running;
		_displayedLineCount = 0;
		_log.Clear();
		_sources = session.Report;
		_summary = session.Summary;
		_error = session.Error;
		BuildReport();
		RefreshView();
	}

	void OnSessionChanged()
	{
		if ( !IsValid )
			return;

		var finished = _wasRunning && !_session.Running;
		var started = !_wasRunning && _session.Running;
		_wasRunning = _session.Running;

		if ( finished )
		{
			Bind( _session.CreateReportSnapshot() );
			SelectPage( _session.Status == "Failed" ? "Log" : "Report" );
			return;
		}

		if ( started )
		{
			_displayedLineCount = 0;
			_log.Clear();
			SelectPage( "Log" );
		}

		RefreshView();
	}

	void RefreshView()
	{
		_title.Text = _session.Name;
		_status.Text = _session.Status switch
		{
			"" => _session.Error ?? "Ready to compile",
			"Done" => "Compiled",
			"Failed" => "Compile failed. Open the Log tab for details.",
			_ => _session.Status
		};
		_bar.Visible = _session.Running;
		_bar.Fraction = _session.Fraction;
		_bar.Update();

		if ( _displayedLineCount > _session.Lines.Count )
		{
			_log.Clear();
			_displayedLineCount = 0;
		}

		while ( _displayedLineCount < _session.Lines.Count )
			_log.AppendPlainText( _session.Lines[_displayedLineCount++] );

		_log.ScrollToBottom();

		if ( _sources != _session.Report || _summary != _session.Summary || _error != _session.Error )
		{
			_sources = _session.Report;
			_summary = _session.Summary;
			_error = _session.Error;
			BuildReport();
		}

		UpdateControls();
	}

	[EditorEvent.Frame]
	void UpdateControls()
	{
		if ( !IsValid || _session is null )
			return;

		if ( _session.Running )
			_bar.Update();

		var current = SceneCompileSession.Current;
		_title.Text = _options.Visible ? current.Name : _session.Name;
		var running = current.Running && current.Scene == _session.Scene;
		_compile.Text = running ? current.Cancelling ? "Cancelling" : "Cancel" : "Compile";
		_compile.Icon = running ? "close" : "hardware";
		_compile.Tint = running ? Theme.ButtonBackground : Theme.Primary;
		_compile.Enabled = running ? !current.Cancelling : current.Scene == _session.Scene && current.CanCompile;
	}

	async void OnCompile()
	{
		var current = SceneCompileSession.Current;
		if ( current.Scene != _session.Scene )
			return;

		if ( current.Running )
		{
			current.RequestCancel();
			return;
		}

		if ( !current.CanCompile )
			return;

		Bind( current );
		await current.StartAsync();
	}

	/// <summary>
	/// Everything the compile is going to do, and everything it's going to leave alone grouped by
	/// why, with the objects listed under each so you can go and look at them.
	/// </summary>
	void BuildReport()
	{
		if ( !IsValid )
			return;

		_lines.Clear();
		_groups.Clear();

		if ( _sources is null )
		{
			_lines.Add( new Entry { Text = _error ?? "No scene data to compile.", Icon = "error" } );

			Flatten();
			return;
		}

		if ( _error is not null )
			_lines.Add( new Entry { Text = _error, Icon = "error" } );

		if ( _session.Statistics is { } statistics )
		{
			_lines.Add( new Entry
			{
				Text = $"Completed {statistics.CompletedAt.LocalDateTime:g} in {statistics.Duration.TotalSeconds:n2} s",
				Icon = "schedule"
			} );
			_lines.Add( new Entry
			{
				Text = $"Generated model geometry: {statistics.VertexCount:n0} vertices, {statistics.TriangleCount:n0} triangles",
				Icon = "view_in_ar"
			} );
			_lines.Add( new Entry { Text = $"Aggregate fragments: {statistics.FragmentCount:n0}", Icon = "grid_view" } );
		}

		if ( _summary is not null )
		{
			foreach ( var line in _summary )
			{
				_lines.Add( new Entry { Text = line, Icon = "done" } );
			}
		}

		_lines.Add( new Entry
		{
			Text = $"{_sources.MeshCount:n0} {(_sources.MeshCount == 1 ? "mesh" : "meshes")}, {_sources.PropCount:n0} {(_sources.PropCount == 1 ? "prop" : "props")} selected for aggregation",
			Icon = "category"
		} );

		var groups = new Dictionary<string, Group>();

		foreach ( var skip in _sources.Skipped )
		{
			var key = $"{skip.Label} excluded from aggregates - {skip.Reason}";

			if ( !groups.TryGetValue( key, out var group ) )
			{
				group = new Group { Reason = key };
				groups[key] = group;
				_groups.Add( group );
			}

			group.Objects.Add( skip.Component );
		}

		_groups.Sort( ( a, b ) => b.Objects.Count - a.Objects.Count );

		if ( _session.Statistics is { } completed )
		{
			var timings = new Group { Reason = "compile stages" };
			foreach ( var stage in completed.Stages )
				timings.Entries.Add( new Entry { Text = $"{stage.Name}: {stage.Duration.TotalSeconds:n2} s", Icon = "schedule", Indent = 20.0f } );
			_groups.Insert( 0, timings );
		}

		Flatten();
	}

	/// <summary>
	/// Feed the list what's on show right now. Folding a reason open or shut is just this again -
	/// the list paints its own rows, so nothing is created or destroyed to make it happen.
	/// </summary>
	void Flatten()
	{
		var items = new List<object>();

		foreach ( var line in _lines )
		{
			items.Add( line );
		}

		foreach ( var group in _groups )
		{
			items.Add( group.Header );

			if ( !group.Open )
				continue;

			items.AddRange( group.Entries );
			foreach ( var component in group.Objects )
			{
				items.Add( new Entry { Text = component.IsValid() ? component.GameObject.Name : "(Deleted object)", Icon = "my_location", Indent = 20.0f, Target = component } );
			}
		}

		_report.SetItems( items );
	}

	void OnEntryClicked( object item )
	{
		if ( item is not Entry entry )
			return;

		if ( entry.Group is { } group )
		{
			group.Open = !group.Open;

			Flatten();
			return;
		}

		if ( entry.Target is not null )
		{
			Reveal( entry.Target );
		}
	}

	static void PaintEntry( VirtualWidget item )
	{
		if ( item.Object is not Entry entry )
			return;

		var clickable = entry.Group is not null || entry.Target is not null;
		var hovered = item.Hovered && clickable;

		if ( hovered )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.WidgetBackground.Lighten( 0.5f ) );
			Paint.DrawRect( item.Rect, 2.0f );
		}

		var rect = item.Rect.Shrink( 4 + entry.Indent, 0, 4, 0 );
		var color = hovered ? Theme.Blue : Theme.TextControl;

		Paint.SetDefaultFont();

		var icon = entry.Group is { } group ? (group.Open ? "expand_more" : "chevron_right") : entry.Icon;

		if ( !string.IsNullOrEmpty( icon ) )
		{
			Paint.SetPen( color.WithAlpha( 0.6f ) );
			rect.Left += Paint.DrawIcon( rect, icon, 14, TextFlag.LeftCenter ).Width + 6;
		}

		Paint.SetPen( color );
		Paint.DrawText( rect, entry.Text, TextFlag.LeftCenter );
	}

	/// <summary>
	/// Select an object we skipped and look at it, so a reason in the report leads straight to the
	/// thing that caused it.
	/// </summary>
	static void Reveal( Component component )
	{
		if ( !component.IsValid() )
			return;

		var go = component.GameObject;
		var session = SceneEditorSession.Resolve( go );

		if ( session is null )
			return;

		using ( session.Scene.Push() )
		{
			session.Selection.Set( go );
			session.FrameTo( go.GetBounds() );
		}
	}

	/// <summary>
	/// A reason things were skipped, and everything it happened to.
	/// </summary>
	sealed class Group
	{
		public string Reason { get; init; }
		public List<Component> Objects { get; } = new();
		public List<Entry> Entries { get; } = new();
		public bool Open { get; set; }

		Entry _header;

		/// <summary>
		/// The row that folds this group open and shut. Held onto rather than remade, so the list
		/// keeps the item it already has laid out when the group opens.
		/// </summary>
		public Entry Header => _header ??= new Entry { Text = $"{Objects.Count + Entries.Count} {Reason}", Group = this };
	}

	/// <summary>
	/// A line of the report.
	/// </summary>
	sealed class Entry
	{
		public string Text { get; init; }
		public string Icon { get; init; }
		public float Indent { get; init; }
		public Group Group { get; init; }
		public Component Target { get; init; }
	}
	/// <summary>
	/// How far through the current phase we are, drawn as a bar because a compile has no idea how
	/// long it's going to take.
	/// </summary>
	sealed class Bar : Widget
	{
		public float Fraction { get; set; }

		protected override void OnPaint()
		{
			Paint.SetPen( Theme.ControlBackground, 1.0f );
			Paint.SetBrush( Theme.WidgetBackground.Darken( 0.1f ) );
			Paint.DrawRect( LocalRect, 2.0f );

			if ( Fraction == 0.0f )
				return;

			var filled = SceneCompileProgress.Fill( LocalRect.Shrink( 1 ), Fraction );

			Paint.ClearPen();
			Paint.SetBrush( Theme.Primary );
			Paint.DrawRect( filled, 2.0f );
		}
	}
}
