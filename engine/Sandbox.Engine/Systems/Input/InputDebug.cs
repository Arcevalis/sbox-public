namespace Sandbox.Engine;

/// <summary>
/// Diagnostics for the input path, gated on the <c>SBOX_INPUT_DEBUG</c> environment variable.
/// <para>
/// <c>bootstrap-linux/launch/run-editor-debug.sh</c> exports that variable and greps the log for the
/// <c>[routerdbg]</c> / <c>[gamemode]</c> / <c>[inputdbg]</c> tags, but until now nothing emitted
/// them. This is the missing half.
/// </para>
/// <para>
/// Everything here logs <b>on change only</b>. <c>InputRouter.Frame()</c> runs once per rendered
/// frame, and an unconditional call from there is exactly the mistake that made
/// <c>SetRelativeMouseMode</c> fire ~150 times a second (<c>bootstrap-linux/linux-input.md</c> §5).
/// </para>
/// </summary>
internal static class InputDebug
{
	/// <summary>
	/// Read once. Nothing in here should cost anything when the variable is unset - callers that
	/// need interop to build their message must check this first.
	/// </summary>
	public static readonly bool Enabled = !string.IsNullOrEmpty( Environment.GetEnvironmentVariable( "SBOX_INPUT_DEBUG" ) );

	static readonly Dictionary<string, string> LastState = new();

	/// <summary>
	/// Log <paramref name="state"/> only when it differs from the last <paramref name="state"/>
	/// logged under <paramref name="key"/>. <paramref name="extra"/> is printed but not compared, so
	/// a counter can ride along without making every frame look like a change.
	/// </summary>
	public static void OnChange( string tag, string key, string state, string extra = null )
	{
		if ( !Enabled ) return;

		if ( LastState.TryGetValue( key, out var previous ) && previous == state )
			return;

		LastState[key] = state;

		Log.Info( extra is null ? $"[{tag}] {state}" : $"[{tag}] {state} {extra}" );
	}

	/// <summary>
	/// Log a one-off occurrence - a handover, a focus change - with no change filtering.
	/// </summary>
	public static void Event( string tag, string text )
	{
		if ( !Enabled ) return;

		Log.Info( $"[{tag}] {text}" );
	}
}
