using UnityEngine;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine.Networking;
using System.Collections;
using Newtonsoft.Json;


// DeepLのREST-APIクライアント
public class DeepLApiClient : MonoBehaviour {
    // 基本URL
    private const string BASE = "https://api-free.deepl.com";
    // 翻訳 URL
    private const string TRANSLATE_URL = BASE + "/v2/translate";

    private void Start() {
    }

    public IEnumerator PostTranslate(string text, string toLang, Action<string> onTranslated) {
        // APIキーの読み取り
        string authorization = CentralManager.Instance != null ? CentralManager.Instance.GetDeepLApiClientKey() : null;
        if (string.IsNullOrEmpty(authorization)) {
            Debug.LogError("でーぷるきー！よみこみえらー！");
        } else {
            Debug.Log("でーぷるきーをよみこみました！: " + authorization);
        }

        // JSON データを作成
        string data = $@"
        {{
            ""text"": [
                ""{text}""
            ],
            ""target_lang"": ""{toLang}""
        }}";

        // UnityWebRequestを使用してPOSTリクエストを送信
        using (UnityWebRequest request = new UnityWebRequest(TRANSLATE_URL, "POST")) {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(data);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization",  "DeepL-Auth-Key " + authorization);
            request.SetRequestHeader("Content-Type", "application/json");
            Debug.Log("DeepLApiClientKey: " + authorization);

            // リクエストを送信し、レスポンスを待つ
            yield return request.SendWebRequest();

            // エラーチェック
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Error: " + request.error);
            }
            else
            {
                // レスポンスの内容を取得
                string responseString = request.downloadHandler.text;

                // JSON をデシリアライズ
                var translationResponse = JsonConvert.DeserializeObject<TranslationResponse>(responseString);

                // text の内容を取得
                string translatedText = translationResponse.translations[0].text;

                // 結果をログに出力
                Debug.Log("Translated Text: " + translatedText);

                // コールバックを呼び出して翻訳結果を返す
                onTranslated?.Invoke(translatedText);
            }
        }
    }
}

// JSON 構造に対応するクラス
public class TranslationResponse
{
    public List<Translation> translations { get; set; }
}

public class Translation
{
    public string detected_source_language { get; set; }
    public string text { get; set; }
}