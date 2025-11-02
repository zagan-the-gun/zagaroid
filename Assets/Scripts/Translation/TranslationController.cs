using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// 翻訳処理を管理するコントローラー
/// NMT（MCP経由）とDeepL APIの両方を統合管理し、フォールバック処理も担当する
/// </summary>
public class TranslationController : MonoBehaviour {
    public static TranslationController Instance { get; private set; }

    // pending requests管理（ID → コールバック）
    private Dictionary<string, Action<string>> pendingRequests = new Dictionary<string, Action<string>>();
    
    // NMT接続状態
    private bool isNmtConnected = false;
    public bool IsNmtConnected => isNmtConnected;

    // DeepL API設定
    private const string DEEPL_BASE_URL = "https://api-free.deepl.com";
    private const string DEEPL_TRANSLATE_URL = DEEPL_BASE_URL + "/v2/translate";

    private void Awake() {
        if (Instance == null) {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log("[TranslationController] シングルトンとして初期化");
        } else if (Instance != this) {
            Debug.LogWarning("[TranslationController] 別のインスタンスが存在するため破棄");
            Destroy(gameObject);
        }
    }

    private void Start() {
        // MultiPortWebSocketServerのイベントには登録しない
        // MultiPortWebSocketServerが直接OnWebSocketMessageを呼び出す
    }

    /// <summary>
    /// 統合翻訳メソッド - 設定された翻訳方式に基づいて自動的にNMTまたはDeepLを使用し、フォールバック処理も実行
    /// </summary>
    /// <param name="text">翻訳するテキスト</param>
    /// <param name="targetLang">対象言語（ISO 639-1: ja, en等、DeepLは大文字で変換）</param>
    /// <param name="speaker">話者識別子</param>
    /// <param name="onCompleted">翻訳完了時のコールバック（失敗時は元のテキストまたはnull）</param>
    public IEnumerator Translate(string text, string targetLang, string speaker, Action<string> onCompleted) {
        string originalText = text;
        string translationMode = GetTranslationMode();
        Debug.Log($"[TranslationController] 翻訳開始: mode={translationMode}, targetLang={targetLang}");

        bool firstTranslationSucceeded = false;
        string result = null;

        if (translationMode == "NMT") {
            // NMT翻訳を優先
            yield return StartCoroutine(TranslateWithNmt(text, targetLang, speaker, (nmtResult) => {
                if (!string.IsNullOrEmpty(nmtResult)) {
                    result = nmtResult;
                    firstTranslationSucceeded = true;
                }
            }));

            // NMT翻訳に失敗した場合はDeepLにフォールバック
            if (!firstTranslationSucceeded) {
                Debug.LogWarning("[TranslationController] NMT翻訳に失敗しました。DeepLにフォールバックします。");
                yield return StartCoroutine(TranslateWithDeepL(originalText, targetLang, (deepLResult) => {
                    if (!string.IsNullOrEmpty(deepLResult)) {
                        result = deepLResult;
                    } else {
                        Debug.LogWarning("[TranslationController] DeepL翻訳（フォールバック）も失敗しました。");
                        result = originalText;
                    }
                }));
            }
        } else {
            // DeepLを優先（デフォルト）
            yield return StartCoroutine(TranslateWithDeepL(text, targetLang, (deepLResult) => {
                if (!string.IsNullOrEmpty(deepLResult)) {
                    result = deepLResult;
                    firstTranslationSucceeded = true;
                }
            }));

            // DeepL翻訳に失敗した場合はNMTにフォールバック
            if (!firstTranslationSucceeded) {
                Debug.LogWarning("[TranslationController] DeepL翻訳に失敗しました。NMT翻訳にフォールバックします。");
                yield return StartCoroutine(TranslateWithNmt(originalText, targetLang, speaker, (nmtResult) => {
                    if (!string.IsNullOrEmpty(nmtResult)) {
                        result = nmtResult;
                    } else {
                        Debug.LogWarning("[TranslationController] NMT翻訳（フォールバック）も失敗しました。");
                        result = originalText;
                    }
                }));
            }
        }

        onCompleted?.Invoke(result);
    }

    /// <summary>
    /// NMT翻訳を実行します（MCP経由）
    /// </summary>
    /// <param name="text">翻訳するテキスト</param>
    /// <param name="targetLang">対象言語（ISO 639-1: ja, en等）</param>
    /// <param name="speaker">話者識別子</param>
    /// <param name="onCompleted">翻訳完了時のコールバック（失敗時はnull）</param>
    public IEnumerator TranslateWithNmt(string text, string targetLang, string speaker, Action<string> onCompleted) {
        if (!isNmtConnected) {
            Debug.LogWarning("[TranslationController] NMTが接続されていません");
            onCompleted?.Invoke(null);
            yield break;
        }

        string requestId = Guid.NewGuid().ToString();
        
        // MCPリクエスト作成
        var request = new McpTranslateRequest {
            id = requestId,
            @params = new McpTranslateParams {
                text = text,
                speaker = string.IsNullOrEmpty(speaker) ? "unknown" : speaker,
                source_lang = DetectSourceLanguage(text),
                target_lang = targetLang.ToLower(),
                priority = "normal"
            }
        };

        // コールバックを登録
        pendingRequests[requestId] = onCompleted;

        // リクエスト送信
        try {
            string jsonRequest = JsonConvert.SerializeObject(request);
            SendToNmt(jsonRequest);
            Debug.Log($"[TranslationController:NMT] 翻訳リクエスト送信: {text.Substring(0, Math.Min(20, text.Length))}... ({request.@params.source_lang} -> {request.@params.target_lang})");
        } catch (Exception ex) {
            Debug.LogError($"[TranslationController:NMT] リクエスト送信エラー: {ex.Message}");
            if (pendingRequests.ContainsKey(requestId)) {
                pendingRequests.Remove(requestId);
            }
            onCompleted?.Invoke(null);
            yield break;
        }

        // タイムアウト処理（10秒）
        float timeout = 10f;
        while (pendingRequests.ContainsKey(requestId) && timeout > 0) {
            yield return new WaitForSeconds(0.1f);
            timeout -= 0.1f;
        }

        // タイムアウトした場合の処理
        if (pendingRequests.ContainsKey(requestId)) {
            pendingRequests.Remove(requestId);
            Debug.LogError("[TranslationController:NMT] 翻訳リクエストがタイムアウトしました");
            onCompleted?.Invoke(null);
        }
    }

    /// <summary>
    /// DeepL翻訳を実行します
    /// </summary>
    /// <param name="text">翻訳するテキスト</param>
    /// <param name="targetLang">対象言語（ISO 639-1: ja, en等 → 大文字に変換されます）</param>
    /// <param name="onCompleted">翻訳完了時のコールバック（失敗時はnull）</param>
    public IEnumerator TranslateWithDeepL(string text, string targetLang, Action<string> onCompleted) {
        // APIキーの読み取り
        string authorization = CentralManager.Instance != null ? CentralManager.Instance.GetDeepLApiClientKey() : null;
        if (string.IsNullOrEmpty(authorization)) {
            Debug.LogError("[TranslationController:DeepL] APIキーが設定されていません");
            onCompleted?.Invoke(null);
            yield break;
        }

        // DeepL APIは大文字の言語コードを要求する（JA, EN等）
        string deepLTargetLang = targetLang.ToUpper();

        // JSON データを作成
        string data = $@"
        {{
            ""text"": [
                ""{text}""
            ],
            ""target_lang"": ""{deepLTargetLang}""
        }}";

        // UnityWebRequestを使用してPOSTリクエストを送信
        using (UnityWebRequest request = new UnityWebRequest(DEEPL_TRANSLATE_URL, "POST")) {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(data);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", "DeepL-Auth-Key " + authorization);
            request.SetRequestHeader("Content-Type", "application/json");

            Debug.Log($"[TranslationController:DeepL] 翻訳リクエスト送信: {text.Substring(0, Math.Min(20, text.Length))}... (-> {deepLTargetLang})");

            // リクエストを送信し、レスポンスを待つ
            yield return request.SendWebRequest();

            // エラーチェック
            if (request.result != UnityWebRequest.Result.Success) {
                Debug.LogError($"[TranslationController:DeepL] エラー: {request.error}");
                onCompleted?.Invoke(null);
            } else {
                // レスポンスの内容を取得
                string responseString = request.downloadHandler.text;

                try {
                    // JSON をデシリアライズ
                    var translationResponse = JsonConvert.DeserializeObject<DeepLTranslationResponse>(responseString);

                    // text の内容を取得
                    string translatedText = translationResponse.translations[0].text;
                    Debug.Log($"[TranslationController:DeepL] 翻訳成功: {translatedText}");

                    // コールバックを呼び出して翻訳結果を返す
                    onCompleted?.Invoke(translatedText);
                } catch (Exception ex) {
                    Debug.LogError($"[TranslationController:DeepL] レスポンス解析エラー: {ex.Message}");
                    onCompleted?.Invoke(null);
                }
            }
        }
    }

    /// <summary>
    /// MultiPortWebSocketServerから呼ばれる（MCPレスポンス受信）
    /// </summary>
    public void OnWebSocketMessage(string message) {
        try {
            var response = JsonConvert.DeserializeObject<McpTranslateResponse>(message);
            
            if (response == null || string.IsNullOrEmpty(response.id)) {
                Debug.LogWarning("[TranslationController] レスポンスにIDがありません");
                return;
            }

            if (!pendingRequests.ContainsKey(response.id)) {
                Debug.LogWarning($"[TranslationController] 未知のリクエストID: {response.id}");
                return;
            }

            var callback = pendingRequests[response.id];
            pendingRequests.Remove(response.id);

            // 成功レスポンス
            if (response.result != null && !string.IsNullOrEmpty(response.result.translated)) {
                Debug.Log($"[TranslationController] 翻訳成功: {response.result.translated}");
                callback?.Invoke(response.result.translated);
            }
            // エラーレスポンス
            else if (response.error != null) {
                Debug.LogError($"[TranslationController] 翻訳エラー (code: {response.error.code}): {response.error.message}");
                callback?.Invoke(null);
            }
            else {
                Debug.LogWarning("[TranslationController] 翻訳レスポンスが不正です");
                callback?.Invoke(null);
            }
        }
        catch (Exception ex) {
            Debug.LogError($"[TranslationController] レスポンス解析エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// NMT接続状態を更新（MultiPortWebSocketServerから呼ばれる）
    /// </summary>
    public void SetNmtConnectionState(bool connected) {
        isNmtConnected = connected;
        Debug.Log($"[TranslationController] NMT接続状態: {(connected ? "接続" : "切断")}");
    }

    /// <summary>
    /// 翻訳方式を取得（NMT or deepl）
    /// </summary>
    public string GetTranslationMode() {
        return PlayerPrefs.GetString("TranslationMode", "deepl");
    }

    /// <summary>
    /// 翻訳方式を設定（NMT or deepl）
    /// </summary>
    public void SetTranslationMode(string mode) {
        PlayerPrefs.SetString("TranslationMode", mode);
        PlayerPrefs.Save();
        Debug.Log($"[TranslationController] 翻訳方式を設定: {mode}");
    }

    /// <summary>
    /// NMTにメッセージを送信
    /// </summary>
    private void SendToNmt(string message) {
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.Instance.SendToTranslationClient(message);
        } else {
            throw new Exception("MultiPortWebSocketServerが見つかりません");
        }
    }

    /// <summary>
    /// 簡易的な言語検出（ISO 639-1形式）
    /// </summary>
    private string DetectSourceLanguage(string text) {
        if (string.IsNullOrEmpty(text)) return "en";
        
        // 日本語の文字を含む場合
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF]")) {
            return "ja";
        }
        
        // 中国語の文字を含む場合（簡体字）
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u4E00-\u9FFF]") && 
            !System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u3040-\u309F\u30A0-\u30FF]")) {
            return "zh";
        }
        
        // 韓国語のハングル文字を含む場合
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[\uAC00-\uD7AF]")) {
            return "ko";
        }
        
        // ロシア語のキリル文字を含む場合
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u0400-\u04FF]")) {
            return "ru";
        }
        
        // その他の文字（ラテン文字ベース）は英語として扱う
        return "en";
    }

    private void OnDestroy() {
        if (pendingRequests != null) {
            pendingRequests.Clear();
        }
        Debug.Log("[TranslationController] クリーンアップが完了しました");
    }
}

#region MCPデータクラス

/// <summary>
/// MCP翻訳リクエスト用データクラス（JSON-RPC 2.0準拠）
/// </summary>
[Serializable]
public class McpTranslateRequest {
    public string jsonrpc = "2.0";
    public string id;
    public string method = "translate_text";
    public McpTranslateParams @params;
}

[Serializable]
public class McpTranslateParams {
    public string text;
    public string speaker;
    public string source_lang;
    public string target_lang;
    public string priority;
}

/// <summary>
/// MCP翻訳レスポンス用データクラス（JSON-RPC 2.0準拠）
/// </summary>
[Serializable]
public class McpTranslateResponse {
    public string jsonrpc;
    public string id;
    public McpTranslateResult result;
    public McpError error;
}

[Serializable]
public class McpTranslateResult {
    public string translated;
    public float? processing_time_ms;
    public string speaker;
}

[Serializable]
public class McpError {
    public int code;
    public string message;
    public object data;
}

#endregion

#region DeepLデータクラス

/// <summary>
/// DeepL翻訳レスポンス用データクラス
/// </summary>
[Serializable]
public class DeepLTranslationResponse {
    public List<DeepLTranslation> translations { get; set; }
}

[Serializable]
public class DeepLTranslation {
    public string detected_source_language { get; set; }
    public string text { get; set; }
}

#endregion

