using NUnit.Framework;

/// <summary>
/// Discord 系 DTO の JSON 形が崩れないことの回帰テスト（実メッセージのサブセット）。
/// Newtonsoft は DiscordJsonSerialization 経由（テスト asmdef が Json.NET を直接参照できない環境対策）。
/// </summary>
public class DiscordJsonDtoTests
{
    [Test]
    public void DiscordGatewayPayload_Hello_Op10_ParsesHeartbeatInterval()
    {
        const string json = @"{""op"":10,""d"":{""heartbeat_interval"":45000},""s"":null,""t"":null}";
        var p = DiscordJsonSerialization.Deserialize<DiscordGatewayPayload>(json);
        Assert.IsNotNull(p);
        Assert.AreEqual(10, p.op);
        Assert.IsNull(p.s);
        Assert.IsNull(p.t);
        Assert.AreEqual(45000, DiscordJsonSerialization.ReadHeartbeatIntervalFromGatewayD(p.d));
    }

    [Test]
    public void DiscordGatewayPayload_Dispatch_Op0_ParsesSequenceAndEventName()
    {
        const string json = @"{""op"":0,""d"":{""foo"":1},""s"":42,""t"":""READY""}";
        var p = DiscordJsonSerialization.Deserialize<DiscordGatewayPayload>(json);
        Assert.AreEqual(0, p.op);
        Assert.AreEqual(42, p.s);
        Assert.AreEqual("READY", p.t);
        Assert.IsNotNull(p.d);
    }

    [Test]
    public void VoiceGatewayPayload_Deserializes_OpAndD()
    {
        const string json = @"{""op"":8,""d"":{""heartbeat_interval"":5500}}";
        var p = DiscordJsonSerialization.Deserialize<VoiceGatewayPayload>(json);
        Assert.IsNotNull(p);
        Assert.AreEqual(8, p.op);
        var hello = DiscordJsonSerialization.Deserialize<VoiceHelloData>(p.d.ToString());
        Assert.IsNotNull(hello);
        Assert.AreEqual(5500.0, hello.heartbeat_interval, 0.01);
    }

    [Test]
    public void ReadyData_RoundTrip()
    {
        const string json = @"{""session_id"":""sess-1"",""user"":{""id"":""u1"",""username"":""bot"",""discriminator"":""0001""}}";
        var r = DiscordJsonSerialization.Deserialize<ReadyData>(json);
        Assert.AreEqual("sess-1", r.session_id);
        Assert.IsNotNull(r.user);
        Assert.AreEqual("u1", r.user.id);
        Assert.AreEqual("bot", r.user.username);
    }

    [Test]
    public void VoiceServerData_RoundTrip()
    {
        const string json = @"{""endpoint"":""east.example.discord.media"",""token"":""voice-token""}";
        var v = DiscordJsonSerialization.Deserialize<VoiceServerData>(json);
        Assert.AreEqual("east.example.discord.media", v.endpoint);
        Assert.AreEqual("voice-token", v.token);
    }

    [Test]
    public void VoiceStateData_RoundTrip()
    {
        const string json = @"{""user_id"":""uid"",""session_id"":""sid"",""channel_id"":""cid""}";
        var v = DiscordJsonSerialization.Deserialize<VoiceStateData>(json);
        Assert.AreEqual("uid", v.user_id);
        Assert.AreEqual("sid", v.session_id);
        Assert.AreEqual("cid", v.channel_id);
    }

    [Test]
    public void VoiceReadyData_RoundTrip()
    {
        const string json = @"{""ssrc"":12345,""ip"":""127.0.0.1"",""port"":5000,""modes"":[""xsalsa20_poly1305"",""aead_aes256_gcm_rtpsize""]}";
        var v = DiscordJsonSerialization.Deserialize<VoiceReadyData>(json);
        Assert.AreEqual(12345u, v.ssrc);
        Assert.AreEqual("127.0.0.1", v.ip);
        Assert.AreEqual(5000, v.port);
        Assert.IsNotNull(v.modes);
        Assert.AreEqual(2, v.modes.Length);
        Assert.AreEqual("xsalsa20_poly1305", v.modes[0]);
    }

    [Test]
    public void VoiceSessionDescriptionData_SecretKey_FromJsonArray()
    {
        const string json = @"{""secret_key"":[1,2,3,255],""mode"":""xsalsa20_poly1305""}";
        var v = DiscordJsonSerialization.Deserialize<VoiceSessionDescriptionData>(json);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 255 }, v.secret_key);
        Assert.AreEqual("xsalsa20_poly1305", v.mode);
    }

    [Test]
    public void VoiceSpeakingData_RoundTrip()
    {
        const string json = @"{""speaking"":true,""ssrc"":99,""user_id"":""123""}";
        var v = DiscordJsonSerialization.Deserialize<VoiceSpeakingData>(json);
        Assert.IsTrue(v.speaking);
        Assert.AreEqual(99u, v.ssrc);
        Assert.AreEqual("123", v.user_id);
    }

    [Test]
    public void WitAIResponse_RoundTrip()
    {
        const string json = @"{""text"":""こんにちは"",""type"":""FINAL_UNDERSTANDING""}";
        var w = DiscordJsonSerialization.Deserialize<WitAIResponse>(json);
        Assert.AreEqual("こんにちは", w.text);
        Assert.AreEqual("FINAL_UNDERSTANDING", w.type);
    }
}
