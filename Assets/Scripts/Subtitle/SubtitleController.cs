using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 字幕（MCP含む）の受付・表示・翻訳を一元管理するコントローラ
/// - Port 50001（字幕AI/MCP）のメッセージ購読と処理
/// - 日本語字幕のキュー管理と表示時間更新
/// - 英語字幕の翻訳リクエスト委譲（TranslationController）
/// - OBS 送出は CentralManager 経由のイベントを利用
/// - /wipe_subtitle の送受信は CentralManager 経由に限定（委譲のみ許容）
/// </summary>
public class SubtitleController : MonoBehaviour {
    public static SubtitleController Instance { get; private set; }

    // チャンネルごとの日本語字幕キュー / 現在表示中
    private readonly Dictionary<string, Queue<CurrentDisplaySubtitle>> subtitleQueuesByChannel = new Dictionary<string, Queue<CurrentDisplaySubtitle>>();
    private readonly Dictionary<string, CurrentDisplaySubtitle> currentDisplayByChannel = new Dictionary<string, CurrentDisplaySubtitle>();

    private void Awake() {
        if (Instance == null) {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log("[SubtitleController] シングルトンとして初期化");
        } else if (Instance != this) {
            Debug.LogWarning("[SubtitleController] 別のインスタンスが存在するため破棄");
            Destroy(gameObject);
        }
    }

    // TranslationController と同様、WS購読は行わず、サーバ側から直接コールされる想定

    /// <summary>
    /// 表示中の日本語字幕の残り時間を更新し、終了時にクリアや次キューの表示を行います。
    /// </summary>
    private void Update() {
        if (currentDisplayByChannel.Count == 0) {
            return;
        }

        foreach (var kvp in currentDisplayByChannel.ToList()) {
            var channel = kvp.Key;
            var current = kvp.Value;
            if (current == null) continue;

            current.remainingDuration -= Time.deltaTime;
            if (current.remainingDuration <= 0) {
                // 日本語字幕をクリア
                CentralManager.SendObsSubtitles(current.japaneseSubtitle, "");
                if (!string.IsNullOrEmpty(current.englishSubtitle)) {
                    CentralManager.SendObsSubtitles(current.englishSubtitle, "");
                }
                currentDisplayByChannel.Remove(channel);

                // 次があれば表示開始
                if (subtitleQueuesByChannel.TryGetValue(channel, out var queue) && queue.Count > 0) {
                    var nextEntry = queue.Dequeue();
                    StartDisplayingJapaneseSubtitle(nextEntry);
                    if (!string.IsNullOrEmpty(nextEntry.englishSubtitle)) {
                        StartCoroutine(TranslateSubtitleCoroutine(nextEntry.englishSubtitle, nextEntry.japaneseText));
                    }
                }
            }
        }
    }

    /// <summary>
    /// MCP字幕通知（またはレガシーJSON）を処理（MultiPortWebSocketServerから直接呼ばれる）
    /// </summary>
    /// <param name="rawMessage">MCP/レガシー形式の字幕JSONまたはプレーン文字列</param>
    public void OnWebSocketMessage(string rawMessage) {
        Debug.Log($"[SubtitleController] 受信: raw={rawMessage}");

        string extractedText = rawMessage;
        string extractedSubtitleName = CentralManager.Instance != null ? CentralManager.Instance.GetMySubtitle() : string.Empty;
        string extractedLanguage = "auto";
        string defaultEnglishSubtitle = CentralManager.Instance != null ? CentralManager.Instance.GetMyEnglishSubtitle() : null;
        string extractedEnglishSubtitleName = defaultEnglishSubtitle;

        try {
            if (!string.IsNullOrEmpty(rawMessage)) {
                string trimmed = rawMessage.TrimStart();
                if (trimmed.StartsWith("{")) {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(rawMessage);
                    var jsonrpcVersion = obj["jsonrpc"]?.ToString();
                    if (jsonrpcVersion == "2.0") {
                        Debug.Log("[MCP] MCP準拠形式を検出しました");
                        var method = obj["method"]?.ToString();
                        var paramsObj = obj["params"] as Newtonsoft.Json.Linq.JObject;
                        if (method != "notifications/subtitle") {
                            Debug.LogWarning($"[MCP] 未対応のmethod: {method}. 'notifications/subtitle' を期待しています。");
                        }
                        if (paramsObj != null) {
                            extractedText = paramsObj["text"]?.ToString();
                            var speaker = paramsObj["speaker"]?.ToString();
                            var type = paramsObj["type"]?.ToString();
                            var language = paramsObj["language"]?.ToString();
                            if (!string.IsNullOrEmpty(language)) {
                                extractedLanguage = language;
                            }
                            if (!string.IsNullOrEmpty(speaker)) {
                                extractedSubtitleName = GetSubtitleFromSpeaker(speaker);
                                if (!string.IsNullOrEmpty(extractedSubtitleName)) {
                                    extractedEnglishSubtitleName = extractedSubtitleName + "_en";
                                }
                            }
                            Debug.Log($"[MCP] パース結果 - method: {method}, subtitle: {extractedSubtitleName}, lang: {extractedLanguage}");
                        } else {
                            Debug.LogWarning("[MCP] params が見つかりません");
                        }
                    } else {
                        var candidate = obj["text"]?.ToString();
                        if (!string.IsNullOrEmpty(candidate)) {
                            extractedText = candidate;
                        }
                        var subtitleCandidate = obj["subtitle"]?.ToString();
                        if (string.IsNullOrEmpty(subtitleCandidate)) {
                            subtitleCandidate = obj["channel"]?.ToString();
                        }
                        if (!string.IsNullOrWhiteSpace(subtitleCandidate)) {
                            extractedSubtitleName = subtitleCandidate;
                            extractedEnglishSubtitleName = subtitleCandidate + "_en";
                        }
                    }
                }
            }
        } catch (System.Exception ex) {
            Debug.LogWarning($"[SubtitleController] 字幕JSON解析に失敗（プレーン扱い）: {ex.Message}");
        }

        float calculatedDuration = CalculateDisplayDuration(extractedText?.Length ?? 0);
        var newJpEntry = new CurrentDisplaySubtitle(
            extractedText,
            extractedSubtitleName,
            extractedEnglishSubtitleName,
            calculatedDuration,
            false
        );

        ManageJapaneseSubtitleDisplay(newJpEntry);
        StartCoroutine(TranslateSubtitleCoroutine(extractedEnglishSubtitleName, extractedText));
    }

    /// <summary>
    /// CentralManager（/wipe_subtitle受信やDiscord音声など）からの日本語字幕表示依頼
    /// </summary>
    /// <param name="japaneseText">日本語字幕として表示するテキスト</param>
    /// <param name="japaneseSubtitle">OBSの日本語字幕ソース名</param>
    /// <param name="englishSubtitle">OBSの英語字幕ソース名（空なら翻訳しない）</param>
    /// <param name="isFromWipe">Wipe由来の字幕か（ループ転送防止に利用）</param>
    public void EnqueueJapaneseSubtitle(string japaneseText, string japaneseSubtitle, string englishSubtitle, bool isFromWipe) {
        if (string.IsNullOrEmpty(japaneseText) || string.IsNullOrEmpty(japaneseSubtitle)) return;
        float duration = CalculateDisplayDuration(japaneseText.Length);
        var entry = new CurrentDisplaySubtitle(japaneseText, japaneseSubtitle, englishSubtitle, duration, isFromWipe);
        ManageJapaneseSubtitleDisplay(entry);
        if (!string.IsNullOrEmpty(englishSubtitle)) {
            StartCoroutine(TranslateSubtitleCoroutine(englishSubtitle, japaneseText));
        }
    }

    /// <summary>
    /// 日本語文字数から表示時間（秒）を計算します。設定の最小/最大値でクランプされます。
    /// </summary>
    /// <param name="charCount">表示する日本語テキストの文字数</param>
    /// <returns>クランプ済みの表示秒数</returns>
    private float CalculateDisplayDuration(int charCount) {
        float charsPerSecond = CentralManager.Instance != null ? CentralManager.Instance.GetCharactersPerSecond() : 4f;
        float minDisplayTime = CentralManager.Instance != null ? CentralManager.Instance.GetMinDisplayTime() : 4.0f;
        float maxDisplayTime = CentralManager.Instance != null ? CentralManager.Instance.GetMaxDisplayTime() : 8.0f;
        float duration = (float)charCount / Mathf.Max(1f, charsPerSecond);
        duration = Mathf.Max(duration, minDisplayTime);
        return Mathf.Clamp(duration, minDisplayTime, maxDisplayTime);
    }

    /// <summary>
    /// 日本語字幕の表示状態を管理します（即時表示・結合表示・キュー投入を切り替え）。
    /// </summary>
    /// <param name="newJpEntry">新規に到着した日本語字幕エントリ</param>
    private void ManageJapaneseSubtitleDisplay(CurrentDisplaySubtitle newJpEntry) {
        var channel = newJpEntry.japaneseSubtitle;
        if (!subtitleQueuesByChannel.TryGetValue(channel, out var queue)) {
            queue = new Queue<CurrentDisplaySubtitle>();
            subtitleQueuesByChannel[channel] = queue;
        }

        if (!currentDisplayByChannel.TryGetValue(channel, out var current) || current == null) {
            StartDisplayingJapaneseSubtitle(newJpEntry);
            Debug.Log("[Subtitle] 新しい日本語字幕をすぐに表示します。");
        } else if (!current.IsCombined) {
            Debug.Log("[Subtitle] 既存の日本語字幕と新しい日本語字幕を結合して表示します。");
            CombineAndDisplayJapaneseSubtitles(current, newJpEntry);
        } else {
            Debug.Log("[Subtitle] 結合表示中のため新しい日本語字幕をキューに追加します。");
            queue.Enqueue(newJpEntry);
        }
    }

    /// <summary>
    /// 日本語字幕の表示を開始します。OBS送出と必要に応じてWipe転送を行います。
    /// </summary>
    /// <param name="jpEntry">表示する日本語字幕エントリ</param>
    private void StartDisplayingJapaneseSubtitle(CurrentDisplaySubtitle jpEntry) {
        currentDisplayByChannel[jpEntry.japaneseSubtitle] = jpEntry;
        CentralManager.SendObsSubtitles(jpEntry.japaneseSubtitle, jpEntry.japaneseText);

        if (!jpEntry.IsFromWipe) {
            string mySubtitle = CentralManager.Instance != null ? CentralManager.Instance.GetMySubtitle() : null;
            string friendSubtitle = CentralManager.Instance != null ? CentralManager.Instance.GetFriendSubtitle() : null;
            if (!string.IsNullOrEmpty(mySubtitle) && string.Equals(jpEntry.japaneseSubtitle?.Trim(), mySubtitle.Trim(), StringComparison.OrdinalIgnoreCase)) {
                var speaker = CentralManager.Instance != null ? CentralManager.Instance.GetMyName() : "streamer";
                if (string.IsNullOrEmpty(speaker)) speaker = "streamer";
                CentralManager.Instance?.SendWipeSubtitle(jpEntry.japaneseText, speaker);
            } else if (!string.IsNullOrEmpty(friendSubtitle) && string.Equals(jpEntry.japaneseSubtitle?.Trim(), friendSubtitle.Trim(), StringComparison.OrdinalIgnoreCase)) {
                var speaker = CentralManager.Instance != null ? CentralManager.Instance.GetFriendName() : "streamer";
                if (string.IsNullOrEmpty(speaker)) speaker = "streamer";
                CentralManager.Instance?.SendWipeSubtitle(jpEntry.japaneseText, speaker);
            }
        }

        Debug.Log($"[Subtitle] 日本語字幕表示開始: 『{jpEntry.japaneseText}』 残り{jpEntry.remainingDuration:F2}s");
    }

    /// <summary>
    /// 既存の日本語字幕と新規字幕を結合し、表示を更新します。
    /// </summary>
    /// <param name="existingJp">現在表示中の日本語字幕</param>
    /// <param name="newJp">新規に到着した日本語字幕</param>
    private void CombineAndDisplayJapaneseSubtitles(CurrentDisplaySubtitle existingJp, CurrentDisplaySubtitle newJp) {
        string combinedJapaneseText = $"{existingJp.japaneseText}\n{newJp.japaneseText}";
        float minDisplayTime = CentralManager.Instance != null ? CentralManager.Instance.GetMinDisplayTime() : 4.0f;
        float maxDisplayTime = CentralManager.Instance != null ? CentralManager.Instance.GetMaxDisplayTime() : 8.0f;
        float newDuration = Mathf.Clamp(existingJp.remainingDuration + newJp.displayDuration, minDisplayTime, maxDisplayTime);

        var combined = new CurrentDisplaySubtitle(
            combinedJapaneseText,
            existingJp.japaneseSubtitle,
            existingJp.englishSubtitle,
            newDuration
        );
        combined.remainingDuration = newDuration;
        combined.SetCombined(true);

        currentDisplayByChannel[existingJp.japaneseSubtitle] = combined;
        CentralManager.SendObsSubtitles(combined.japaneseSubtitle, combined.japaneseText);
        Debug.Log($"[Subtitle] 日本語字幕結合表示開始: 『{combined.japaneseText}』 残り{combined.remainingDuration:F2}s");
    }

    /// <summary>
    /// 英語字幕用の翻訳を実行し、OBSへ送出します。
    /// </summary>
    /// <param name="englishSubtitle">OBSの英語字幕ソース名</param>
    /// <param name="japaneseText">翻訳元の日本語テキスト</param>
    private IEnumerator TranslateSubtitleCoroutine(string englishSubtitle, string japaneseText) {
        if (string.IsNullOrEmpty(englishSubtitle) || string.IsNullOrEmpty(japaneseText)) yield break;

        string translated = null;
        if (CentralManager.Instance != null) {
            yield return StartCoroutine(CentralManager.Instance.TranslateForSubtitle(
                japaneseText,
                "en",
                englishSubtitle,
                (result) => { translated = result ?? string.Empty; }
            ));
        } else {
            Debug.LogWarning("[SubtitleController] CentralManagerが見つかりません。翻訳をスキップします。");
        }

        CentralManager.SendObsSubtitles(englishSubtitle, translated ?? string.Empty);
    }

    /// <summary>
    /// 話者名から設定済みの字幕ソース名を解決します。
    /// 未知の話者はMySubtitleにフォールバックします。
    /// </summary>
    /// <param name="speaker">話者表示名</param>
    /// <returns>対応する字幕ソース名</returns>
    private string GetSubtitleFromSpeaker(string speaker) {
        if (string.IsNullOrEmpty(speaker)) return string.Empty;
        string myName = CentralManager.Instance != null ? CentralManager.Instance.GetMyName() : null;
        string friendName = CentralManager.Instance != null ? CentralManager.Instance.GetFriendName() : null;
        string wipeAIName = CentralManager.Instance != null ? CentralManager.Instance.GetWipeAIName() : null;

        if (!string.IsNullOrEmpty(myName) && string.Equals(speaker.Trim(), myName.Trim(), StringComparison.OrdinalIgnoreCase)) {
            return CentralManager.Instance != null ? CentralManager.Instance.GetMySubtitle() : string.Empty;
        }
        if (!string.IsNullOrEmpty(friendName) && string.Equals(speaker.Trim(), friendName.Trim(), StringComparison.OrdinalIgnoreCase)) {
            return CentralManager.Instance != null ? CentralManager.Instance.GetFriendSubtitle() : string.Empty;
        }
        if (!string.IsNullOrEmpty(wipeAIName) && string.Equals(speaker.Trim(), wipeAIName.Trim(), StringComparison.OrdinalIgnoreCase)) {
            return CentralManager.Instance != null ? CentralManager.Instance.GetWipeAISubtitle() : string.Empty;
        }
        Debug.LogWarning($"[MCP] 不明なspeaker名: {speaker}, デフォルトの字幕チャンネルを使用します");
        return CentralManager.Instance != null ? CentralManager.Instance.GetMySubtitle() : string.Empty;
    }
}

public class CurrentDisplaySubtitle {
    public string japaneseText; // 表示中の日本語字幕テキスト
    public string japaneseSubtitle; // 日本語字幕のOBSソース名
    public string englishSubtitle; // 英語字幕のOBSソース名
    public float displayDuration; // 表示時間（秒）
    public float remainingDuration; // 残り表示時間（秒）
    public bool IsCombined { get; private set; } = false; // 結合状態
    public bool IsFromWipe { get; private set; } = false; // Wipe由来フラグ

    public CurrentDisplaySubtitle(string jpText, string jpSubtitle, string enSubtitle, float duration, bool isFromWipe = false) {
        japaneseText = jpText;
        japaneseSubtitle = jpSubtitle;
        englishSubtitle = enSubtitle;
        displayDuration = duration;
        remainingDuration = duration;
        IsCombined = false;
        IsFromWipe = isFromWipe;
    }

    public void SetCombined(bool combined) {
        IsCombined = combined;
    }
}


