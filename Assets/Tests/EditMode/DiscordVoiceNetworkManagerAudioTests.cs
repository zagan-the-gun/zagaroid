using NUnit.Framework;

/// <summary>RMS 計算は DiscordVoiceNetworkManager の静的メソッド（本番の無音判定と同じ）。</summary>
public class DiscordVoiceNetworkManagerAudioTests
{
    [Test]
    public void CalculateAudioLevel_NullOrEmpty_ReturnsZero()
    {
        Assert.AreEqual(0f, DiscordVoiceNetworkManager.CalculateAudioLevel(null));
        Assert.AreEqual(0f, DiscordVoiceNetworkManager.CalculateAudioLevel(new float[0]));
    }

    [Test]
    public void CalculateAudioLevel_ConstantOne_ReturnsOne()
    {
        var pcm = new float[] { 1f, 1f, 1f };
        Assert.AreEqual(1f, DiscordVoiceNetworkManager.CalculateAudioLevel(pcm), 1e-5f);
    }

    [Test]
    public void CalculateAudioLevel_SineLikeSamples_ReturnsExpectedRms()
    {
        var pcm = new float[] { 1f, 0f, -1f, 0f };
        var expected = (float)System.Math.Sqrt(0.5f);
        Assert.AreEqual(expected, DiscordVoiceNetworkManager.CalculateAudioLevel(pcm), 1e-5f);
    }
}
