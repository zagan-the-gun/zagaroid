using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WebSocketSharp;
using Newtonsoft.Json;

/// <summary>
/// MenZ翻訳サーバー用WebSocketクライアント
/// ローカルで動作するMenZ翻訳サーバーと通信して翻訳処理を行います
/// </summary>
public class MenZTranslationClient : MonoBehaviour {
    public string MENZ_TRANSLATION_URL; // MenZ-translation接続用URL
    private WebSocket ws;
    private Dictionary<string, Action<string>> pendingRequests = new Dictionary<string, Action<string>>();
    private bool isConnected = false;
    
    void Start() {
        ConnectToServer();
    }
    
    private void ConnectToServer() {
        Debug.Log($"MenZTranslationClient: サーバーに接続を試行します - {MENZ_TRANSLATION_URL}");

        // MenZ-translation接続用アドレス読み取り
        MENZ_TRANSLATION_URL = CentralManager.Instance != null ? CentralManager.Instance.GetMenZTranslationServerUrl() : null;
        if (string.IsNullOrEmpty(MENZ_TRANSLATION_URL)) {
            Debug.LogError("めんずとらんすれーしょんゆーあーるえる！よみこみえらー！");
        } else {
            Debug.Log("めんずとらんすれーしょんゆーあーるえるをよみこみました！: " + MENZ_TRANSLATION_URL);
        }

        // 既存の接続をクリーンアップ
        if (ws != null) {
            try {
                ws.Close();
            } catch (System.Exception ex) {
                Debug.LogWarning($"既存WebSocket接続のクローズ中にエラー: {ex.Message}");
            }
            ws = null;
        }
        
        // WebSocketインスタンス作成
        ws = new WebSocket(MENZ_TRANSLATION_URL);
        
        // イベントハンドラーを設定
        ws.OnOpen += (sender, e) => {
            Debug.Log("MenZ翻訳サーバーに接続しました: " + MENZ_TRANSLATION_URL);
            isConnected = true;
        };
        
        ws.OnMessage += (sender, e) => {
            HandleMessage(e.Data);
        };
        
        ws.OnError += (sender, e) => {
            Debug.LogWarning("MenZ翻訳サーバーエラー: " + e.Message);
            isConnected = false;
        };
        
        ws.OnClose += (sender, e) => {
            Debug.Log($"MenZ翻訳サーバーから切断されました (Code: {e.Code}, Reason: {e.Reason})");
            isConnected = false;
        };
        
        try {
            ws.Connect();
        } catch (System.Exception ex) {
            Debug.LogWarning($"MenZ翻訳サーバーへの接続に失敗しました: {ex.Message}");
            isConnected = false;
        }
    }

    private void HandleMessage(string message) {
        try {
            var response = JsonConvert.DeserializeObject<MenZTranslationResponse>(message);
            
            if (response != null && !string.IsNullOrEmpty(response.request_id)) {
                if (pendingRequests.ContainsKey(response.request_id)) {
                    var callback = pendingRequests[response.request_id];
                    pendingRequests.Remove(response.request_id);
                    
                    if (response.status == "completed" && !string.IsNullOrEmpty(response.translated)) {
                        callback?.Invoke(response.translated);
                    } else if (response.status == "error") {
                        string errorMsg = response.error_message ?? "不明なエラー";
                        Debug.LogError($"MenZ翻訳サーバーエラー: {errorMsg}");
                        
                        // MPS関連のエラーの場合、より詳細な情報を提供
                        if (errorMsg.Contains("isin_Tensor_Tensor_out") && errorMsg.Contains("MPS")) {
                            Debug.LogWarning("MacOS 14.0以前でのMPS制限エラーです。MenZ翻訳サーバーでCPUモードまたはMacOS 14.0以降へのアップデートを検討してください。");
                        }

                        callback?.Invoke(null);
                    } else {
                        callback?.Invoke(null);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"MenZ翻訳レスポンス解析エラー: {ex.Message}");
        }
    }
    
    /// <summary>
    /// テキストを翻訳します（WebSocket版）
    /// </summary>
    /// <param name="text">翻訳するテキスト</param>
    /// <param name="targetLang">対象言語（例：JA、EN）</param>
    /// <param name="contextId">話者別文脈ID（任意）</param>
    /// <param name="onTranslated">翻訳完了時のコールバック</param>
    public IEnumerator PostTranslate(string text, string targetLang, string contextId, Action<string> onTranslated) {
        // 接続されていない場合は再接続を試行
        if (!isConnected) {
            Debug.Log("MenZ翻訳サーバーに接続されていません。再接続を試行します...");
            ConnectToServer();
            
            // 短時間待機して接続を確認
            yield return new WaitForSeconds(0.5f);
            
            if (!isConnected) {
                Debug.LogWarning("MenZ翻訳サーバーへの再接続に失敗しました。");
                onTranslated?.Invoke(null); // nullを返して呼び出し元に判断を委ねる
                yield break;
            }
        }
        
        string requestId = Guid.NewGuid().ToString();
        
        var request = new TranslationRequest {
            request_id = requestId,
            context_id = contextId,
            priority = "normal",
            text = text,
            source_lang = DetectSourceLanguage(text), // 言語検出
            target_lang = ConvertToNLLBCode(targetLang)
        };
        
        // コールバックを登録
        pendingRequests[requestId] = onTranslated;
        
        // リクエスト送信
        try {
            string jsonRequest = JsonConvert.SerializeObject(request);
            ws.Send(jsonRequest);
            
            Debug.Log($"翻訳リクエスト送信: {text} -> {targetLang} (検出言語: {request.source_lang})");
        } catch (Exception ex) {
            Debug.LogError("翻訳リクエスト送信エラー: " + ex.Message);
            if (pendingRequests.ContainsKey(requestId)) {
                pendingRequests.Remove(requestId);
            }
            
            // 送信エラーの場合、接続が切れている可能性がある
            isConnected = false;
            Debug.LogWarning("MenZ翻訳サーバーとの通信に失敗しました。");
            onTranslated?.Invoke(null); // nullを返して呼び出し元に判断を委ねる
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
            Debug.LogError("翻訳リクエストがタイムアウトしました。");
            onTranslated?.Invoke(null); // nullを返して呼び出し元に判断を委ねる
        }
    }
    
    /// <summary>
    /// 簡易的な言語検出を行います
    /// </summary>
    /// <param name="text">検出対象のテキスト</param>
    /// <returns>NLLBコード</returns>
    private string DetectSourceLanguage(string text) {
        if (string.IsNullOrEmpty(text)) return "eng_Latn";
        
        // 日本語の文字を含む場合
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF]")) {
            return "jpn_Jpan";
        }
        
        // 中国語の文字を含む場合（簡体字）
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u4E00-\u9FFF]") && 
            !System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u3040-\u309F\u30A0-\u30FF]")) {
            return "zho_Hans";
        }
        
        // 韓国語のハングル文字を含む場合
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[\uAC00-\uD7AF]")) {
            return "kor_Hang";
        }
        
        // ロシア語のキリル文字を含む場合
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u0400-\u04FF]")) {
            return "rus_Cyrl";
        }
        
        // その他の文字（ラテン文字ベース）は英語として扱う
        return "eng_Latn";
    }
    
    /// <summary>
    /// DeepLの言語コードをNLLBコードに変換
    /// </summary>
    private string ConvertToNLLBCode(string deepLCode) {
        switch (deepLCode.ToUpper()) {
            case "JA":
                return "jpn_Jpan";
            case "EN":
                return "eng_Latn";
            case "ZH":
                return "zho_Hans";
            case "KO":
                return "kor_Hang";
            case "FR":
                return "fra_Latn";
            case "DE":
                return "deu_Latn";
            case "ES":
                return "spa_Latn";
            case "IT":
                return "ita_Latn";
            case "PT":
                return "por_Latn";
            case "RU":
                return "rus_Cyrl";
            default:
                Debug.LogWarning($"未対応の言語コード: {deepLCode}, 英語にフォールバック");
                return "eng_Latn";
        }
    }
    
    /// <summary>
    /// 話者の文脈をクリア
    /// </summary>
    public void ClearContext(string contextId) {
        if (isConnected && !string.IsNullOrEmpty(contextId)) {
            var clearRequest = new { type = "context_clear", context_id = contextId };
            ws.Send(JsonConvert.SerializeObject(clearRequest));
            Debug.Log($"文脈クリアリクエスト送信: {contextId}");
        } else {
            Debug.LogWarning("文脈クリア失敗: サーバーに接続されていません");
        }
    }
    
    /// <summary>
    /// 接続状況を確認するメソッド
    /// </summary>
    public bool IsConnected() {
        return isConnected;
    }

    void OnDestroy() {
        Debug.Log("MenZTranslationClient: OnDestroy - クリーンアップを開始します");
        
        try {
            isConnected = false;
            
            // 接続を閉じる
            if (ws != null) {
                try {
                    ws.Close();
                } catch (System.Exception ex) {
                    Debug.LogWarning($"WebSocket接続のクローズ中にエラー: {ex.Message}");
                }
                ws = null;
            }
            
            // リクエストをクリア
            if (pendingRequests != null) {
                pendingRequests.Clear();
            }
            
            Debug.Log("MenZTranslationClient: クリーンアップが完了しました");
        } catch (System.Exception ex) {
            Debug.LogError($"OnDestroy中にエラーが発生しました: {ex.Message}");
        }
    }
}

/// <summary>
/// MenZ翻訳リクエスト用データクラス
/// </summary>
[Serializable]
public class TranslationRequest {
    public string request_id;
    public string context_id;
    public string priority;
    public string text;
    public string source_lang;
    public string target_lang;
}

/// <summary>
/// MenZ翻訳レスポンス用データクラス
/// </summary>
[Serializable]
public class MenZTranslationResponse {
    public string request_id;
    public string translated;
    public string translation_type;
    public string context_id;
    public float processing_time_ms;
    public string status;
    public string error_message;
} 