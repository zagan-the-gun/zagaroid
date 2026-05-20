using NUnit.Framework;

/// <summary>
/// Voice / メイン Gateway 送信ペイロードの op・フィールド名が Discord 実装とずれないことの回帰テスト。
/// </summary>
public class DiscordGatewayPayloadBuilderTests
{
    private sealed class MainOpIntNullableD
    {
        public int op;
        public int? d;
    }

    private sealed class MainIdentifyD
    {
        public string token;
        public int intents;
        public MainIdentifyProps properties;
    }

    private sealed class MainIdentifyProps
    {
        public string os;
        public string browser;
        public string device;
    }

    private sealed class MainIdentifyEnvelope
    {
        public int op;
        public MainIdentifyD d;
    }

    private sealed class MainVoiceStateD
    {
        public string guild_id;
        public string channel_id;
        public bool self_mute;
        public bool self_deaf;
    }

    private sealed class MainVoiceStateEnvelope
    {
        public int op;
        public MainVoiceStateD d;
    }

    private sealed class VoiceHeartbeatEnvelope
    {
        public int op;
        public long d;
    }

    private sealed class VoiceIdentifyD
    {
        public string server_id;
        public string user_id;
        public string session_id;
        public string token;
        // Discord 2024 年導入の DAVE protocol 対応宣言。
        // 削ると Voice Gateway が `4017 E2EE/DAVE protocol required` で切断するため、
        // ペイロードに含まれていることを必ずテストで担保する。
        public int max_dave_protocol_version;
    }

    private sealed class VoiceIdentifyEnvelope
    {
        public int op;
        public VoiceIdentifyD d;
    }

    private sealed class VoiceSelectDataInner
    {
        public string address;
        public int port;
        public string mode;
    }

    private sealed class VoiceSelectData
    {
        public string protocol;
        public VoiceSelectDataInner data;
    }

    private sealed class VoiceSelectEnvelope
    {
        public int op;
        public VoiceSelectData d;
    }

    [Test]
    public void MainGateway_Heartbeat_SequenceNull_Op1()
    {
        var obj = DiscordNetworkManager.DiscordPayloadHelper.CreateHeartbeatPayload(null);
        var json = DiscordJsonSerialization.SerializeObject(obj);
        var p = DiscordJsonSerialization.Deserialize<MainOpIntNullableD>(json);
        Assert.AreEqual(1, p.op);
        Assert.IsNull(p.d);
    }

    [Test]
    public void MainGateway_Heartbeat_WithSequence_Op1()
    {
        var obj = DiscordNetworkManager.DiscordPayloadHelper.CreateHeartbeatPayload(42);
        var json = DiscordJsonSerialization.SerializeObject(obj);
        var p = DiscordJsonSerialization.Deserialize<MainOpIntNullableD>(json);
        Assert.AreEqual(1, p.op);
        Assert.AreEqual(42, p.d);
    }

    [Test]
    public void MainGateway_Identify_Op2_IntentsAndProperties()
    {
        var obj = DiscordNetworkManager.DiscordPayloadHelper.CreateIdentifyPayload("bot-token");
        var json = DiscordJsonSerialization.SerializeObject(obj);
        var p = DiscordJsonSerialization.Deserialize<MainIdentifyEnvelope>(json);
        Assert.AreEqual(2, p.op);
        Assert.IsNotNull(p.d);
        Assert.AreEqual("bot-token", p.d.token);
        Assert.AreEqual(DiscordConstants.DISCORD_INTENTS, p.d.intents);
        Assert.IsNotNull(p.d.properties);
        Assert.AreEqual("unity", p.d.properties.os);
        Assert.AreEqual("unity-bot", p.d.properties.browser);
        Assert.AreEqual("unity-bot", p.d.properties.device);
    }

    [Test]
    public void MainGateway_VoiceStateUpdate_Op4_MuteDeafFlags()
    {
        var obj = DiscordNetworkManager.DiscordPayloadHelper.CreateVoiceStateUpdatePayload("guild-x", "channel-y");
        var json = DiscordJsonSerialization.SerializeObject(obj);
        var p = DiscordJsonSerialization.Deserialize<MainVoiceStateEnvelope>(json);
        Assert.AreEqual(4, p.op);
        Assert.AreEqual("guild-x", p.d.guild_id);
        Assert.AreEqual("channel-y", p.d.channel_id);
        Assert.IsTrue(p.d.self_mute);
        Assert.IsFalse(p.d.self_deaf);
    }

    [Test]
    public void VoiceGateway_Heartbeat_Op3()
    {
        var obj = DiscordVoiceGatewayManager.VoicePayloadHelper.CreateHeartbeatPayload(777L);
        var json = DiscordJsonSerialization.SerializeObject(obj);
        var p = DiscordJsonSerialization.Deserialize<VoiceHeartbeatEnvelope>(json);
        Assert.AreEqual(3, p.op);
        Assert.AreEqual(777L, p.d);
    }

    [Test]
    public void VoiceGateway_Identify_Op0_ServerAndSessionFields()
    {
        var obj = DiscordVoiceGatewayManager.VoicePayloadHelper.CreateVoiceIdentifyPayload(
            "g1", "u1", "sess", "tok");
        var json = DiscordJsonSerialization.SerializeObject(obj);
        var p = DiscordJsonSerialization.Deserialize<VoiceIdentifyEnvelope>(json);
        Assert.AreEqual(0, p.op);
        Assert.AreEqual("g1", p.d.server_id);
        Assert.AreEqual("u1", p.d.user_id);
        Assert.AreEqual("sess", p.d.session_id);
        Assert.AreEqual("tok", p.d.token);
        // DAVE protocol 対応宣言。0 = DAVE 非対応モードで接続することを Discord に伝える。
        // ここを 1 にすると Discord は MLS 鍵交換を始めて、zagaroid 側で復号できなくなる。
        Assert.AreEqual(0, p.d.max_dave_protocol_version);
        // 念のため JSON 文字列にもフィールド名が含まれていることを確認する
        // （フィールド名のタイポを防ぐためのガード）。
        StringAssert.Contains("max_dave_protocol_version", json);
    }

    [Test]
    public void VoiceGateway_SelectProtocol_Op1_UdpAddressMode()
    {
        var obj = DiscordVoiceGatewayManager.VoicePayloadHelper.CreateSelectProtocolPayload(
            "192.168.0.1", 12345, "xsalsa20_poly1305");
        var json = DiscordJsonSerialization.SerializeObject(obj);
        var p = DiscordJsonSerialization.Deserialize<VoiceSelectEnvelope>(json);
        Assert.AreEqual(1, p.op);
        Assert.AreEqual("udp", p.d.protocol);
        Assert.AreEqual("192.168.0.1", p.d.data.address);
        Assert.AreEqual(12345, p.d.data.port);
        Assert.AreEqual("xsalsa20_poly1305", p.d.data.mode);
    }
}
