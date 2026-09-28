using Editor;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NativeEngine;
using Sandbox.Engine;

namespace InputTests;

/// <summary>
/// Pins the Qt→SDL bridge contract: the Qt name alias set in
/// <c>ManagedTools.TryMapQtKey</c> and the shared delivery ledger in
/// <c>InputRouter</c> that collapses dual-delivered presses to exactly one.
/// All ledger times are injected fake clocks, never wall time.
/// </summary>
[TestClass, DoNotParallelize]
public class KeyBridgeTests
{
	[TestMethod]
	public void QtAliasSetMapsToEngineCodes()
	{
		var cases = new (string qt, ButtonCode code)[]
		{
			("Esc", ButtonCode.KEY_ESCAPE),
			("Tab", ButtonCode.KEY_TAB),
			("Backtab", ButtonCode.KEY_TAB),
			("Left", ButtonCode.KEY_LEFT),
			("Right", ButtonCode.KEY_RIGHT),
			("Up", ButtonCode.KEY_UP),
			("Down", ButtonCode.KEY_DOWN),
			("PageUp", ButtonCode.KEY_PAGEUP),
			("PageDown", ButtonCode.KEY_PAGEDOWN),
			("Insert", ButtonCode.KEY_INSERT),
			("Delete", ButtonCode.KEY_DELETE),
			("Control", ButtonCode.KEY_LCONTROL),
			("Shift", ButtonCode.KEY_LSHIFT),
			("Alt", ButtonCode.KEY_LALT),
			("Meta", ButtonCode.KEY_LWIN),
			("Super_L", ButtonCode.KEY_LWIN),
			("Super_R", ButtonCode.KEY_RWIN),
			("Menu", ButtonCode.KEY_APP),
			("Print", ButtonCode.KEY_PRINTSCREEN),
			("KP_Add", ButtonCode.KEY_PAD_PLUS),
			("KP_Multiply", ButtonCode.KEY_PAD_MULTIPLY),
			("KP_Divide", ButtonCode.KEY_PAD_DIVIDE),
			("F1", ButtonCode.KEY_F1),
			("F5", ButtonCode.KEY_F5),
			("F8", ButtonCode.KEY_F8),
			("F12", ButtonCode.KEY_F12),
			("A", ButtonCode.KEY_A),
			("Space", ButtonCode.KEY_SPACE),
			("Enter", ButtonCode.KEY_ENTER),
		};

		foreach ( var (qt, code) in cases )
		{
			Assert.IsTrue( ManagedTools.TryMapQtKey( qt, out var mapped ), $"Qt name {qt} should map" );
			Assert.AreEqual( code, mapped, $"Qt name {qt}" );
		}
	}

	[TestMethod]
	public void FocusTrapReasonsPinTabOnly()
	{
		Assert.IsTrue( GameMode.IsFocusTrapReason( FocusChangeReason.Tab ) );
		Assert.IsTrue( GameMode.IsFocusTrapReason( FocusChangeReason.Backtab ) );
		Assert.IsFalse( GameMode.IsFocusTrapReason( FocusChangeReason.Mouse ) );
		Assert.IsFalse( GameMode.IsFocusTrapReason( FocusChangeReason.Shortcut ) );
		Assert.IsFalse( GameMode.IsFocusTrapReason( FocusChangeReason.ActiveWindow ) );
		Assert.IsFalse( GameMode.IsFocusTrapReason( FocusChangeReason.Other ) );
		Assert.IsFalse( GameMode.IsFocusTrapReason( FocusChangeReason.None ) );
	}

	[TestMethod]
	public void UnknownQtKeyFallsThroughToPassthrough()
	{
		Assert.IsFalse( ManagedTools.TryMapQtKey( "NotAKey_xyz", out _ ) );
		Assert.IsFalse( ManagedTools.TryMapQtKey( "", out _ ) );
	}

	[TestMethod]
	public void DuplicateAcrossSourcesDeliversOnceQtFirst()
	{
		const double now = 1000.0;
		InputRouter.NoteKeyDelivery( ButtonCode.KEY_TAB, true, InputRouter.KeyDeliverySource.Qt, false, now );

		Assert.IsTrue( InputRouter.WasKeyDeliveredRecently( ButtonCode.KEY_TAB, true, now + 0.03 ) );
	}

	[TestMethod]
	public void DuplicateAcrossSourcesDeliversOnceSdlFirst()
	{
		const double now = 2000.0;
		InputRouter.NoteKeyDelivery( ButtonCode.KEY_A, true, InputRouter.KeyDeliverySource.Sdl, false, now );

		Assert.IsTrue( InputRouter.WasKeyDeliveredRecently( ButtonCode.KEY_A, true, now + 0.03 ) );
	}

	[TestMethod]
	public void StaleDeliveryForwardsAgain()
	{
		const double now = 3000.0;
		InputRouter.NoteKeyDelivery( ButtonCode.KEY_S, true, InputRouter.KeyDeliverySource.Sdl, false, now );

		Assert.IsFalse( InputRouter.WasKeyDeliveredRecently( ButtonCode.KEY_S, true, now + InputRouter.KeyDedupeWindow + 0.01 ) );
	}

	[TestMethod]
	public void PressAndReleaseAreIndependentTransitions()
	{
		const double now = 4000.0;
		InputRouter.NoteKeyDelivery( ButtonCode.KEY_D, true, InputRouter.KeyDeliverySource.Qt, false, now );

		// A release is a different transition, so it still delivers (no stuck keys).
		Assert.IsFalse( InputRouter.WasKeyDeliveredRecently( ButtonCode.KEY_D, false, now + 0.01 ) );

		InputRouter.NoteKeyDelivery( ButtonCode.KEY_D, false, InputRouter.KeyDeliverySource.Sdl, false, now + 0.01 );

		// And the next press after a release delivers again (double-tap works).
		Assert.IsFalse( InputRouter.WasKeyDeliveredRecently( ButtonCode.KEY_D, true, now + 0.02 ) );
	}

	[TestMethod]
	public void RepeatsNeverTouchDedupeState()
	{
		const double now = 5000.0;
		InputRouter.NoteKeyDelivery( ButtonCode.KEY_F, true, InputRouter.KeyDeliverySource.Sdl, true, now );

		Assert.IsFalse( InputRouter.WasKeyDeliveredRecently( ButtonCode.KEY_F, true, now + 0.01 ) );
	}
}
