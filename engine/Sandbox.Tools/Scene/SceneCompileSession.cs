using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Sandbox;

namespace Editor;

/// <summary>
/// Owns scene compilation independently of the controls displaying its progress.
/// </summary>
public sealed class SceneCompileSession : AssetSystem.IEventListener
{
	/// <summary>
	/// The shared editor compile job, independent of any toolbar, popup, or report window.
	/// </summary>
	public static SceneCompileSession Current { get; } = new( followActiveScene: true );

	readonly List<string> _lines = new();
	readonly List<SceneCompileStage> _stages = new();
	SceneCompilerSettings _settings = new();
	SceneCompiler.Sources _sources;
	SceneCompiler.Sources _resultSources;
	SceneCompileReport _sourceReport;
	SceneCompileReport _resultReport;
	string _path;
	string _settingsError;
	string _scanError;
	string _failure;
	string _status;
	string _phase;
	bool _unsaved;
	bool _playing;
	bool _notifying;
	bool _compileOnSave;
	Scene _queuedScene;
	string _queuedPath;
	CancellationTokenSource _cancel = new();
	FastTimer _elapsed;
	FastTimer _phaseElapsed;
	SceneCompilerSettings _validatedSettings;
	Scene _validatedScene;
	bool _compilationValidated;
	bool _compilationCurrent;
	int _compilationRevision;
	HashSet<string> _compileDependencyPaths;
	HashSet<Asset> _compileDependencyAssets;

	void InvalidateCompilation()
	{
		_compilationValidated = false;
		_compilationRevision++;
	}

	void AssetSystem.IEventListener.OnAssetChanged( Asset asset )
	{
		if ( IsCompileDependency( asset ) )
			InvalidateCompilation();
	}

	/// <summary>
	/// Whether an asset change can affect the last scene compilation validation.
	/// </summary>
	public bool IsCompileDependency( Asset asset )
	{
		if ( asset is null )
			return false;

		var source = AssetSystem.FindByPath( Scene?.Source?.ResourcePath );
		if ( asset == source || _compileDependencyAssets?.Contains( asset ) == true )
			return true;

		if ( _compileDependencyPaths is null )
			return false;

		return Matches( asset.GetSourceFile( true ) ) || Matches( asset.GetCompiledFile( true ) );

		bool Matches( string path ) => !string.IsNullOrEmpty( path )
			&& _compileDependencyPaths.Contains( Path.GetFullPath( path ) );
	}

	/// <summary>
	/// Whether the draft recipe differs from the last validated compilation's saved recipe.
	/// </summary>
	public bool HasPendingSettings => Scene == _validatedScene && _validatedSettings is not null && Settings != _validatedSettings;

	/// <summary>
	/// Validate the saved compilation and its dependencies. This reads generated resources and should
	/// only be called when opening compile controls or after an asset invalidation, not per frame.
	/// </summary>
	public (bool HasCompilation, bool IsCurrent, string Error) ValidateCompilation()
	{
		InvalidateCompilation();
		_compilationValidated = true;
		_compilationCurrent = false;
		_validatedSettings = null;
		_validatedScene = Scene;
		var validation = new SceneCompileCache.ValidationScope();
		_compileDependencyPaths = validation.Paths;
		_compileDependencyAssets = validation.Assets;
		var asset = AssetSystem.FindByPath( Scene?.Source?.ResourcePath );
		if ( asset is null || !SceneCompileCache.HasCompilation( asset, validation ) )
			return (false, true, null);

		if ( !SceneCompileCache.Validate( asset, out var error, validation ) )
			return (true, false, error);

		try
		{
			_validatedSettings = SceneCompilerSettings.Load( asset );
			_compilationCurrent = true;
			return (true, true, null);
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException )
		{
			Log.Warning( $"Could not read scene compile status: {e.Message}" );
			return (true, false, e.Message);
		}
	}

	/// <summary>
	/// Validate saved compilation files without hashing or decoding resources on the editor thread.
	/// </summary>
	public async Task<(bool HasCompilation, bool IsCurrent, string Error)> ValidateCompilationAsync()
	{
		InvalidateCompilation();
		var revision = _compilationRevision;
		var scene = Scene;
		var path = scene?.Source?.ResourcePath;
		var asset = AssetSystem.FindByPath( scene?.Source?.ResourcePath );
		if ( asset is null )
			return (false, true, null);

		var source = asset.GetSourceFile( true );
		var compiled = asset.GetCompiledFile( true );
		if ( string.IsNullOrEmpty( compiled ) )
			compiled = source + "_c";
		try
		{
			var validation = await Task.Run( () => SceneCompileCache.ValidateFiles( source, compiled ) );
			if ( revision == _compilationRevision && scene == Scene && path == Scene?.Source?.ResourcePath )
			{
				_validatedScene = scene;
				_validatedSettings = validation.HasCompilation && validation.IsCurrent ? SceneCompilerSettings.Load( asset ) : null;
				_compileDependencyPaths = new( validation.Paths.Select( Path.GetFullPath ), StringComparer.OrdinalIgnoreCase );
				_compileDependencyAssets = null;
				_compilationCurrent = validation.HasCompilation && validation.IsCurrent;
				_compilationValidated = true;
			}
			return (validation.HasCompilation, validation.IsCurrent, validation.Error);
		}
		catch
		{
			if ( revision == _compilationRevision )
			{
				_compilationCurrent = false;
				_compilationValidated = true;
			}
			throw;
		}
	}

	/// <summary>
	/// The selected source scene, pinned to the job's scene while compilation is running.
	/// </summary>
	public Scene Scene { get; private set; }

	/// <summary>
	/// The source scene's display name.
	/// </summary>
	public string Name { get; private set; } = "No scene";

	internal SceneCompiler.Sources Sources => HasResult ? _resultSources : _sources;

	/// <summary>
	/// Source information for the latest result, or the current preview before a compile.
	/// Null when no sources are available.
	/// </summary>
	public SceneCompileReport Report => HasResult ? _resultReport : _sourceReport;

	/// <summary>
	/// Whether source information is available to display in a report.
	/// </summary>
	public bool HasSources => Report is not null;

	public bool HasCompileGeometry => _sources?.HasCompileGeometry == true;

	/// <summary>
	/// A settings, source-scan, or compile error, or null when none has been recorded.
	/// </summary>
	public string Error => _settingsError ?? _scanError ?? _failure;

	/// <summary>
	/// Whether the job is still running, including cancellation and cleanup.
	/// </summary>
	public bool Running { get; private set; }

	/// <summary>
	/// Whether cancellation has been requested and the job has not finished yet.
	/// </summary>
	public bool Cancelling { get; private set; }

	/// <summary>
	/// Whether this session contains a completed, failed, or cancelled result.
	/// </summary>
	public bool HasResult { get; private set; }

	/// <summary>
	/// The running phase or final result, or an empty string before starting.
	/// </summary>
	public string Status => Cancelling ? "Cancelling" : _status ?? "";

	/// <summary>
	/// Progress through the current phase, rather than an estimate of total compile time.
	/// Negative when the phase has no measurable total.
	/// </summary>
	public float Fraction { get; private set; }

	/// <summary>
	/// The retained compile log, in append order.
	/// </summary>
	public IReadOnlyList<string> Lines => _lines;

	/// <summary>
	/// Successful compilation's summary lines, or null when no summary was produced.
	/// </summary>
	public string[] Summary { get; private set; }

	/// <summary>
	/// Measurements for the displayed successful compile, or null when none are available.
	/// </summary>
	public SceneCompileStatistics Statistics { get; internal set; }

	/// <summary>
	/// The cancellation token observed by the compiler for the current job.
	/// </summary>
	internal CancellationToken Cancel => _cancel.Token;

	/// <summary>
	/// Raised when settings, source information, progress, or log output changes.
	/// Subscribers must unsubscribe when their views are destroyed.
	/// </summary>
	public event Action Changed;

	/// <summary>
	/// Whether the selected saved scene can start a compile with the current draft recipe.
	/// </summary>
	public bool CanCompile => !Running && HasCompileGeometry
		&& _compilationValidated && (!_compilationCurrent || HasPendingSettings)
		&& _settingsError is null && _scanError is null && EligibilityError() is null;

	/// <summary>
	/// An extra aggregate's cost in fragments. Higher values favor fewer, larger aggregates.
	/// Changes affect the draft recipe and are saved only when compiling.
	/// </summary>
	/// <exception cref="InvalidDataException">The value is not finite and positive.</exception>
	/// <exception cref="InvalidOperationException">A compile is running.</exception>
	public float AggregateCost
	{
		get => Settings.AggregateCost;
		set => Settings = Settings with { AggregateCost = value };
	}

	/// <summary>
	/// The maximum geometry chunk size before subdivision.
	/// Changes affect the draft recipe and are saved only when compiling.
	/// </summary>
	/// <exception cref="InvalidDataException">The value is not finite and positive.</exception>
	/// <exception cref="InvalidOperationException">A compile is running.</exception>
	public float MaxChunkSize
	{
		get => Settings.MaxChunkSize;
		set => Settings = Settings with { MaxChunkSize = value };
	}

	/// <summary>
	/// Compile after saving the active scene. Disabled by default and saved immediately in scene metadata.
	/// </summary>
	public bool CompileOnSave
	{
		get => _compileOnSave;
		set
		{
			if ( Running || Game.IsPlaying || !Scene.IsValid() || string.IsNullOrEmpty( _path )
				|| Scene.Source?.ResourcePath != _path
				|| SceneEditorSession.Active is not { IsPrefabSession: false } active || active.Scene != Scene )
				throw new InvalidOperationException( "Open a saved scene outside play mode to change compile on save." );

			SceneCompilerSettings.SaveCompileOnSave( AssetSystem.FindByPath( _path ), value );
			_compileOnSave = value;
			if ( !value )
				ClearQueuedCompile();
			Notify();
		}
	}

	/// <summary>
	/// Reset the draft recipe to built-in defaults without saving it.
	/// </summary>
	/// <exception cref="InvalidOperationException">A compile is running.</exception>
	public void ResetSettings() => Settings = new();

	internal SceneCompilerSettings Settings
	{
		get => _settings;
		set
		{
			if ( Running )
				throw new InvalidOperationException( "Cannot change the recipe while compiling." );

			ArgumentNullException.ThrowIfNull( value );
			value.Validate();
			_settings = value;
			_settingsError = null;
			Notify();
		}
	}

	SceneCompileSession( bool followActiveScene = false )
	{
		if ( followActiveScene )
			EditorEvent.Register( this );
	}

	[EditorEvent.Frame]
	void FollowActiveScene()
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( _queuedScene is not null && (Game.IsPlaying || !_queuedScene.IsValid()
			|| scene != _queuedScene || scene.Source?.ResourcePath != _queuedPath) )
			ClearQueuedCompile();

		if ( Running )
		{
			if ( EligibilityError() is not null )
				CancelCompile();
			return;
		}

		if ( scene != Scene || scene?.Source?.ResourcePath != _path
			|| (scene?.Editor?.HasUnsavedChanges ?? false) != _unsaved || Game.IsPlaying != _playing )
			Refresh();

		if ( _queuedScene is null )
			return;

		ClearQueuedCompile();
		if ( CompileOnSave && EligibilityError() is null )
			_ = StartAsync();
	}

	[Event( "scene.saved" )]
	void OnSceneSaved( Scene scene )
	{
		if ( Scene == scene )
			InvalidateCompilation();

		if ( Running && Scene == scene )
			CancelCompile();

		if ( Game.IsPlaying || !scene.IsValid() || scene.Editor?.HasUnsavedChanges != false
			|| SceneEditorSession.Active is not { IsPrefabSession: false } active || active.Scene != scene )
			return;

		var path = scene.Source?.ResourcePath;
		Refresh();
		try
		{
			if ( !SceneCompilerSettings.LoadCompileOnSave( AssetSystem.FindByPath( path ) ) )
				return;
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException )
		{
			Log.Error( e, "Could not read compile on save setting" );
			return;
		}

		_queuedScene = scene;
		_queuedPath = path;
		if ( Running && Scene == scene )
			CancelCompile();
	}

	internal void OnSceneEdited( Scene scene )
	{
		if ( scene != Scene )
			return;

		if ( scene.Editor?.HasUnsavedChanges == true )
			InvalidateCompilation();
		if ( Running )
		{
			if ( EligibilityError() is not null )
				CancelCompile();
			return;
		}

		Refresh();
	}

	void ClearQueuedCompile()
	{
		_queuedScene = null;
		_queuedPath = null;
	}

	/// <summary>
	/// Rescan the active editor scene when idle. Preserve its draft settings and completed
	/// result unless the selected scene or source path has changed.
	/// </summary>
	public void Refresh()
	{
		if ( Running )
			return;

		RefreshSources();
		Notify();
	}

	void ClearResult()
	{
		_failure = null;
		_status = null;
		_phase = null;
		Summary = null;
		Statistics = null;
		HasResult = false;
		_resultSources = null;
		_resultReport = null;
		Fraction = 0;
		_lines.Clear();
		_stages.Clear();
	}

	void RefreshSources()
	{
		var scene = SceneEditorSession.Active?.Scene;
		var path = scene?.Source?.ResourcePath;
		if ( scene != Scene || path != _path )
		{
			InvalidateCompilation();
			Scene = scene;
			_path = path;
			_settingsError = null;
			_validatedScene = null;
			_validatedSettings = null;
			_compileDependencyPaths = null;
			_compileDependencyAssets = null;
			ClearResult();
			_settings = new();
			_compileOnSave = false;

			try
			{
				var asset = path is null ? null : AssetSystem.FindByPath( path );
				_settings = SceneCompilerSettings.Load( asset );
				if ( scene is not PrefabScene )
					_compileOnSave = SceneCompilerSettings.LoadCompileOnSave( asset );
			}
			catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException )
			{
				Log.Error( e, "Could not load scene compile settings" );
				_settingsError = $"Could not load scene compile settings: {e.Message}";
			}
		}

		Name = string.IsNullOrEmpty( path ) ? scene?.Name ?? "No scene" : Path.GetFileNameWithoutExtension( path );
		_unsaved = scene?.Editor?.HasUnsavedChanges ?? false;
		_playing = Game.IsPlaying;
		_scanError = EligibilityError( requireSaved: false );
		_sources = null;
		_sourceReport = null;
		if ( _scanError is not null )
			return;

		try
		{
			_sources = SceneCompiler.Scan( Scene, out _scanError );
			if ( _sources is not null )
			{
				if ( !_sources.HasCompileGeometry )
				{
					if ( Scene.Editor?.HasUnsavedChanges == false && _sources.Asset is not null )
						SceneCompileCache.ClearCompilation( _sources.Asset );
					ClearResult();
				}

				_sourceReport = new SceneCompileReport( _sources.Name, _sources.Meshes.Length, _sources.Props.Length,
					Array.AsReadOnly( _sources.Skipped.Select( skip => new SceneCompileSkip( skip.Component, skip.Label, skip.Reason ) ).ToArray() ) );
			}
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException )
		{
			Log.Error( e, "Could not scan scene for compilation" );
			_scanError = $"Could not scan scene for compilation: {e.Message}";
		}
	}

	string EligibilityError( bool requireSaved = true )
	{
		if ( Game.IsPlaying )
			return "Stop playing before compiling the scene.";
		if ( !Scene.IsValid() )
			return "No scene is open.";
		if ( SceneEditorSession.Active is not { IsPrefabSession: false } active || active.Scene != Scene )
			return "Open a scene rather than a prefab to compile.";
		if ( Scene.Source?.ResourcePath != _path )
			return "The scene moved. Refresh before compiling it.";
		if ( requireSaved && (Scene.Editor is null || Scene.Editor.HasUnsavedChanges) )
			return "Save the scene, then use Scene > Compile Scene. Unsaved changes cannot be compiled.";

		return null;
	}

	/// <summary>
	/// Refresh the source scan and compile the active scene. Errors and cancellation are
	/// retained in the session; invalid startup input also requests an error report.
	/// An up-to-date compilation with an unchanged recipe is left untouched.
	/// </summary>
	/// <returns>A task that completes after the job and its cleanup finish.</returns>
	public async Task StartAsync()
	{
		if ( Running )
		{
			Line( Cancelling ? "Cancellation is already requested." : "A scene compile is already running." );
			return;
		}

		// Lock before notifications or pumping can re-enter through another compile control.
		ClearQueuedCompile();
		Running = true;
		Cancelling = false;
		_cancel.Dispose();
		_cancel = new();
		_elapsed = FastTimer.StartNew();
		var enteredCompiler = false;

		try
		{
			InvalidateCompilation();
			RefreshSources();
			var nothingToCompile = _settingsError is null && _scanError is null && _sources is { HasCompileGeometry: false };
			if ( _settingsError is null && _scanError is null && HasCompileGeometry )
			{
				var compilation = await ValidateCompilationAsync();
				Cancel.ThrowIfCancellationRequested();
				if ( _compilationValidated && compilation.HasCompilation && compilation.IsCurrent && !HasPendingSettings )
				{
					SceneCompileCache.PruneGenerations( _sources.Asset.GetSourceFile( true ) );
					Running = false;
					Notify();
					return;
				}
			}

			ClearResult();
			_status = nothingToCompile ? "Nothing to compile" : "Preparing";
			Fraction = nothingToCompile ? 0 : -1;

			if ( nothingToCompile )
			{
				_lines.Add( _status );
				Running = false;
				Notify();
				return;
			}

			if ( Error is { } error )
				throw new InvalidOperationException( error );
			if ( _sources is null )
				throw new InvalidOperationException( "There is no saved scene to compile." );

			_settings.Validate();
			_lines.Add( $"{_sources.Meshes.Length} meshes, {_sources.Props.Length} props to compile" );
			Notify();
			Cancel.ThrowIfCancellationRequested();
			enteredCompiler = true;
			var summary = await SceneCompiler.Compile( _sources, _settings, this );
			Finish( summary is null ? "Cancelled" : "Done", summary );
		}
		catch ( OperationCanceledException )
		{
			Finish( "Cancelled" );
		}
		catch ( Exception e )
		{
			Log.Error( e, "Compile Scene failed" );
			_failure = e.Message;
			Line( e.Message );
			Finish( "Failed" );

			if ( !enteredCompiler )
				EditorEvent.Run( "scene.compile.show-report", CreateReportSnapshot(), "Report" );
		}
	}

	/// <summary>
	/// Request cancellation without marking the job complete before its cleanup finishes.
	/// </summary>
	public void RequestCancel()
	{
		ClearQueuedCompile();
		CancelCompile();
	}

	[EditorEvent.Hotload]
	[Event( "app.exit" )]
	void OnEditorReset() => RequestCancel();

	void CancelCompile()
	{
		if ( !Running || Cancelling )
			return;

		Cancelling = true;
		_cancel.Cancel();
		Notify();
	}

	/// <summary>
	/// Begin a compiler phase, recording the elapsed time of the previous phase.
	/// </summary>
	/// <param name="title">The phase's display name.</param>
	internal void Phase( string title )
	{
		EndPhase();
		_phase = title;
		_phaseElapsed = FastTimer.StartNew();
		_status = title;
		Fraction = -1;
		Notify();
	}

	void EndPhase()
	{
		if ( _phase is null )
			return;

		var duration = TimeSpan.FromMilliseconds( _phaseElapsed.ElapsedMilliSeconds );
		_stages.Add( new SceneCompileStage( _phase, duration ) );
		_lines.Add( $"{_phase,-26}{duration.TotalSeconds,6:n2}s" );
		_phase = null;
	}

	/// <summary>
	/// Update progress through the current compiler phase.
	/// </summary>
	/// <param name="current">The number of completed items.</param>
	/// <param name="total">The number of items in the phase, or zero when unknown.</param>
	internal void Step( int current, int total )
	{
		Fraction = total > 0 ? (float)current / total : -1;
		Notify();
	}

	/// <summary>
	/// Append a line to the retained compile log and notify its views.
	/// </summary>
	/// <param name="text">The text to append.</param>
	internal void Line( string text )
	{
		_lines.Add( text );
		Notify();
	}

	/// <summary>
	/// Finalize the job after compiler cleanup and emit its completion notification once.
	/// </summary>
	/// <param name="title">The result: Done, Failed, or Cancelled.</param>
	/// <param name="summary">Summary lines produced by a successful compile.</param>
	void Finish( string title, string[] summary = null )
	{
		if ( !Running )
			return;

		EndPhase();
		_status = title;
		Summary = summary;
		Fraction = 1;
		HasResult = true;
		_resultSources = _sources;
		_resultReport = _sourceReport;
		var duration = TimeSpan.FromMilliseconds( _elapsed.ElapsedMilliSeconds );
		if ( title == "Done" && Statistics is not null )
		{
			Statistics.CompletedAt = DateTimeOffset.Now;
			Statistics.Duration = duration;
			Statistics.Stages = Array.AsReadOnly( _stages.ToArray() );
		}
		else
		{
			Statistics = null;
		}
		InvalidateCompilation();
		if ( title == "Done" )
		{
			_validatedScene = Scene;
			_validatedSettings = Settings;
			_compilationCurrent = true;
			_compilationValidated = true;
		}
		_lines.Add( $"{title} in {duration.TotalSeconds:n2}s" );
		Running = false;
		Cancelling = false;

		if ( title == "Cancelled" && _queuedScene == Scene )
		{
			Notify();
			return;
		}

		// The callback owns a snapshot: changing the active scene must not redirect an old toast.
		var report = CreateReportSnapshot();
		var detail = title switch
		{
			"Done" => string.Join( "\n", summary ?? [] ),
			"Failed" => Error ?? "Scene compilation failed.",
			_ => "The previous compiled scene is unchanged."
		};
		Notify();
		EditorEvent.Run( "scene.compile.finished", report, detail,
			(Action)(() => EditorEvent.Run( "scene.compile.show-report", report, title == "Failed" ? "Log" : "Report" )) );
	}

	/// <summary>
	/// Copy report state into a detached session that does not follow active-scene changes.
	/// Component references still identify the original source objects for click-to-reveal.
	/// </summary>
	/// <returns>A detached copy for displaying the retained report and log.</returns>
	public SceneCompileSession CreateReportSnapshot()
	{
		var report = new SceneCompileSession
		{
			Scene = Scene,
			Name = Name,
			_path = _path,
			_settings = _settings,
			_compileOnSave = _compileOnSave,
			_settingsError = _settingsError,
			_scanError = _scanError,
			_failure = _failure,
			_status = _status,
			_sources = Sources,
			_resultSources = Sources,
			_sourceReport = Report,
			_resultReport = Report,
			Summary = Summary,
			Statistics = Statistics,
			HasResult = HasResult,
			Fraction = Fraction
		};
		report._lines.AddRange( _lines );
		return report;
	}

	void Notify()
	{
		if ( _notifying )
			return;

		_notifying = true;
		try
		{
			Changed?.Invoke();
		}
		finally
		{
			_notifying = false;
		}
	}
}

/// <summary>
/// Read-only source information for compile reports, independent of compiler internals.
/// </summary>
/// <param name="Name">The source scene's display name.</param>
/// <param name="MeshCount">The number of eligible mesh components.</param>
/// <param name="PropCount">The number of eligible prop renderers.</param>
/// <param name="Skipped">Components excluded from compilation and their reasons.</param>
public sealed record SceneCompileReport( string Name, int MeshCount, int PropCount, IReadOnlyList<SceneCompileSkip> Skipped );

/// <summary>
/// A component left unchanged by compilation and the reason it was skipped.
/// </summary>
/// <param name="Component">The original source component, which may later be destroyed.</param>
/// <param name="Label">The category of source geometry.</param>
/// <param name="Reason">Why the component was excluded from compilation.</param>
public sealed record SceneCompileSkip( Component Component, string Label, string Reason );
