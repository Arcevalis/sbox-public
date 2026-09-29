namespace Editor;

sealed class SceneCompileToast : ToastWidget
{
	[Event( "scene.compile.finished" )]
	static void OnCompileFinished( SceneCompileSession report, string detail, Action openReport )
	{
		var name = report.Name;
		var status = report.Status;
		var failed = status == "Failed";
		if ( !EditorPreferences.NotificationPopups
			|| EditorPreferences.CompileNotifications != EditorPreferences.NotificationLevel.ShowAlways
			&& !(failed && EditorPreferences.CompileNotifications == EditorPreferences.NotificationLevel.ShowOnError) )
			return;

		var toast = new SceneCompileToast
		{
			Title = status switch
			{
				"Done" => "Scene compiled",
				"Cancelled" => "Scene compile cancelled",
				_ => "Scene compile failed"
			},
			Subtitle = name,
			Icon = failed ? "error_outline" : status == "Done" ? "check_circle" : "cancel",
			BorderColor = failed ? Theme.Red : status == "Done" ? Theme.Green : Theme.Yellow,
			DrawTimer = false,
			IsRunning = false
		};

		var body = new Widget() { FixedWidth = 300 };
		body.Layout = Layout.Column();
		body.Layout.Spacing = 4;
		var heading = body.Layout.AddRow();
		heading.Spacing = 8;
		heading.Add( new Label( name ) { WordWrap = true, MaximumWidth = 200 }, 1 )
			.SetStyles( "font-weight: bold;" );
		heading.Add( new Button.Clear( failed ? "Open log" : "Report", "arrow_forward" )
		{
			FixedHeight = 20,
			ToolTip = failed ? "Open the compile log" : "View the compile report",
			Clicked = () =>
			{
				openReport();
				ToastManager.Dismiss( toast );
			}
		} );
		body.Layout.Add( new Label( failed ? detail : detail.Replace( "\n", ", " ) )
		{
			WordWrap = true,
			Color = Theme.TextLight.WithAlpha( 0.7f )
		} );
		toast.SetBodyWidget( body );
		ToastManager.Remove( toast, failed ? EditorPreferences.ErrorNotificationTimeout : 6 );
	}
}
