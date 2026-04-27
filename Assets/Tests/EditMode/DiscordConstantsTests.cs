using NUnit.Framework;

public class DiscordConstantsTests
{
    [Test]
    public void Network_Defaults()
    {
        Assert.AreEqual(4096, DiscordConstants.WEBSOCKET_BUFFER_SIZE);
        Assert.AreEqual(5000, DiscordConstants.RECONNECT_DELAY);
    }

    [Test]
    public void Audio_SampleRatesChannelsAndScale()
    {
        Assert.AreEqual(48000, DiscordConstants.SAMPLE_RATE_48K);
        Assert.AreEqual(16000, DiscordConstants.SAMPLE_RATE_16K);
        Assert.AreEqual(2, DiscordConstants.CHANNELS_STEREO);
        Assert.AreEqual(32768.0f, DiscordConstants.PCM_SCALE_FACTOR, 0.0001f);
    }

    [Test]
    public void WitAiApi_Format()
    {
        Assert.AreEqual(16000, DiscordConstants.WITA_API_SAMPLE_RATE);
        Assert.AreEqual(1, DiscordConstants.WITA_API_CHANNELS);
    }

    [Test]
    public void Gateway_Intents_ValueIsStable()
    {
        Assert.AreEqual(32509, DiscordConstants.DISCORD_INTENTS);
    }

    [Test]
    public void SilenceDetection_Thresholds()
    {
        Assert.AreEqual(0.001f, DiscordConstants.SILENCE_THRESHOLD, 1e-6f);
        Assert.AreEqual(1000, DiscordConstants.SILENCE_DURATION_MS);
    }
}
