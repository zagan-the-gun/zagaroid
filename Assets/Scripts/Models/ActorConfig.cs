public class ActorConfig
{
    public string actorName = "";      // 半角英数のみ（正規化なし）
    public string displayName = "";    // 表示名（自由文字）
    public string discordUserId = "";  // 空許容
    public bool enabled = true;         // 既定 ON
    public string type = "local";      // "local" | "friend" | "wipe"
    public bool translationEnabled = false; // 翻訳ON/OFF
}


