using System.Collections.Generic;

public class ActorConfig
{
    public string actorName = "";      // 半角英数のみ（正規化なし）
    public string displayName = "";    // 表示名（自由文字）
    public string discordUserId = "";  // 空許容
    public bool enabled = true;         // 既定 ON
    public string type = "local";      // "local" | "friend" | "wipe"
    public bool translationEnabled = false; // 翻訳ON/OFF
    public bool ttsEnabled = false;    // TTS（テキスト読み上げ）ON/OFF（type="wipe"時に機能）
    
    // 画像・アニメーション関連（type="wipe"の時に使用）
    public List<string> avatarAnimePaths = new();     // Avatar アニメーション
    public List<string> avatarLipSyncPaths = new();   // Avatar リップシンク
}


