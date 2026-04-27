using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Discord 系 Gateway / Voice の JSON デシリアライズの単一入口。
/// テストアセンブリから Newtonsoft を直接参照できない環境向け。
/// </summary>
public static class DiscordJsonSerialization
{
    public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json);

    /// <summary>送信ペイロードの JSON 化（テストで Gateway 形を検証するための入口）。</summary>
    public static string SerializeObject(object value) => JsonConvert.SerializeObject(value);

    /// <summary>DiscordNetworkManager.ProcessMainGatewayMessage の Hello 分岐と同じく d から interval を読む。</summary>
    public static int ReadHeartbeatIntervalFromGatewayD(object d)
    {
        if (d == null)
            throw new System.ArgumentNullException(nameof(d));
        var obj = JObject.Parse(d.ToString());
        return obj.Value<int>("heartbeat_interval");
    }
}
