using System.Collections.Generic;
using UnityEngine;

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
    public float avatarDisplayScale = 1.0f;           // Canvas 上の表示スケール（倍率）
    public Vector2 avatarDisplayPosition = Vector2.zero; // Canvas 上の表示位置（anchoredPosition）
    
    // アバター画像アニメーション設定
    public float avatarAnimationIntervalMs = 30f; // フレーム間隔（ミリ秒）
    public float avatarAnimationWaitSeconds = 4f; // 1サイクル完了後の待機時間（秒）
    
    // アバター表示制御
    public bool avatarShowWhileTalking = false;   // 発話中のみアバターを表示
}
