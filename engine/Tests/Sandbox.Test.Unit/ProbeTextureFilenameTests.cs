using Sandbox;
using System;
using System.Linq;

[TestClass]
public class ProbeTextureFilenameTests
{
	static readonly Guid Id = new( "0acf5da7-7982-4ad1-93b8-e2b06eadde04" );
	static readonly Guid BakeId = new( "c6e1cb8f2b694ff3b69583e3c19a1087" );

	[TestMethod]
	public void MixedCaseObjectNameAndSuffix_ComeOutLowercase()
	{
		Assert.AreEqual(
			"/ddgi/envmap_probe_irradiance_0acf5da7-7982-4ad1-93b8-e2b06eadde04_c6e1cb8f2b694ff3b69583e3c19a1087.vtex_c",
			IndirectLightVolume.GetProbeTextureFilename( "Envmap Probe", "Irradiance", Id, BakeId ) );
	}

	[TestMethod]
	public void WhitespaceRuns_CollapseToSingleUnderscores()
	{
		var filename = IndirectLightVolume.GetProbeTextureFilename( "Envmap   Probe\t(1)", "Irradiance", Id, BakeId );
		Assert.IsFalse( filename.Any( char.IsWhiteSpace ) );
		Assert.IsTrue( filename.Contains( "envmap_probe_(1)_irradiance", StringComparison.Ordinal ) );
	}

	[TestMethod]
	public void NullObjectName_FallsBackToDefaultVolumeName()
	{
		Assert.AreEqual(
			"/ddgi/ddgivolume_distance_0acf5da7-7982-4ad1-93b8-e2b06eadde04_c6e1cb8f2b694ff3b69583e3c19a1087.vtex_c",
			IndirectLightVolume.GetProbeTextureFilename( null, "Distance", Id, BakeId ) );
	}

	[TestMethod]
	public void LowercaseInput_RoundTripsUnchanged()
	{
		var filename = IndirectLightVolume.GetProbeTextureFilename( "envmap_probe", "relocation", Id, BakeId );
		Assert.AreEqual( filename.ToLowerInvariant(), filename );
		Assert.IsTrue( filename.StartsWith( "/ddgi/", StringComparison.Ordinal ) );
		Assert.IsTrue( filename.EndsWith( ".vtex_c", StringComparison.Ordinal ) );
	}
}
