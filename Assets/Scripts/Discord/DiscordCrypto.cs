using System;
using UnityEngine;
using TweetNaclSharp;

/// <summary>
/// Discord音声データの暗号化復号処理
/// XSalsa20 + Poly1305復号を実装
/// TweetNaclSharpライブラリを使用
/// </summary>
public static class DiscordCrypto
{
    private const int NONCE_SIZE = 24; // XSalsa20 nonce size
    private const int TAG_SIZE = 16;   // Poly1305 authentication tag size

    /// <summary>
    /// Discord Voice RTPパケットを復号する
    /// 暗号化モードに応じて適切なnonce構成を使用
    /// </summary>
    /// <param name="encryptedData">暗号化されたOpusデータ</param>
    /// <param name="rtpHeader">RTPヘッダー（12バイト）</param>
    /// <param name="secretKey">32バイトの暗号化キー</param>
    /// <param name="encryptionMode">使用されている暗号化モード</param>
    /// <returns>復号されたOpusデータ、復号に失敗した場合はnull</returns>
    public static byte[] DecryptVoicePacket(byte[] encryptedData, byte[] rtpHeader, byte[] secretKey, string encryptionMode = "xsalsa20_poly1305")
    {
        try
        {
            if (encryptedData == null || encryptedData.Length < TAG_SIZE)
            {
                Debug.LogError($"❌ 暗号化データが不正: データサイズ={encryptedData?.Length ?? 0}");
                return null;
            }

            if (rtpHeader == null || rtpHeader.Length != 12)
            {
                Debug.LogError($"❌ RTPヘッダーが不正: ヘッダーサイズ={rtpHeader?.Length ?? 0}");
                return null;
            }

            if (secretKey == null || secretKey.Length != 32)
            {
                Debug.LogError($"❌ 暗号化キーが不正: キーサイズ={secretKey?.Length ?? 0}");
                return null;
            }

            // 暗号化モードに応じたnonce構成
            byte[] nonce = CreateNonce(rtpHeader, encryptionMode);
            if (nonce == null)
            {
                Debug.LogError($"❌ nonce生成失敗: モード={encryptionMode}");
                return null;
            }

            // XSalsa20 + Poly1305復号を実行
            byte[] decryptedData = DecryptXSalsa20Poly1305(encryptedData, nonce, secretKey);

            if (decryptedData == null)
            {
                Debug.LogError("❌ 復号失敗");
            }
            // 成功時のログは削減（大量になるため）

            return decryptedData;
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ 復号処理でエラーが発生: {ex.Message}");
            Debug.LogError($"エラーの詳細: {ex.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// 暗号化モードに応じたnonce構成を生成
    /// </summary>
    private static byte[] CreateNonce(byte[] rtpHeader, string encryptionMode)
    {
        byte[] nonce = new byte[NONCE_SIZE];
        
        switch (encryptionMode)
        {
            case "xsalsa20_poly1305":
                // 標準モード: RTPヘッダー(12バイト) + 12バイトの0埋め
                Array.Copy(rtpHeader, 0, nonce, 0, 12);
                break;
                
            case "xsalsa20_poly1305_suffix":
                // suffix モード: RTPヘッダー(12バイト) + 12バイトの0埋め（同じ構成）
                Array.Copy(rtpHeader, 0, nonce, 0, 12);
                break;
                
            case "aead_xchacha20_poly1305_rtpsize":
                // XChaCha20モード（後で実装）
                Debug.LogError("❌ XChaCha20モードはまだサポートされていません");
                return null;
                
            default:
                Debug.LogError($"❌ 未対応の暗号化モード: {encryptionMode}");
                return null;
        }
        
        return nonce;
    }

    /// <summary>
    /// XSalsa20 + Poly1305復号（TweetNaclSharp実装）
    /// </summary>
    private static byte[] DecryptXSalsa20Poly1305(byte[] encryptedData, byte[] nonce, byte[] key)
    {
        try
        {
            // ログを削減: 復号開始ログを削除
            
            if (encryptedData.Length < TAG_SIZE)
            {
                Debug.LogError("❌ データサイズが認証タグより小さい");
                return null;
            }

            // TweetNaclSharpの高レベルAPIを使用してsecretbox復号
            byte[] plaintext = NaclFast.SecretboxOpen(encryptedData, nonce, key);
            
            if (plaintext == null)
            {
                Debug.LogError("❌ 復号失敗: 認証に失敗しました");
                return null;
            }
            
            // 成功時のログは削減（大量になるため）

            return plaintext;
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ TweetNaclSharp復号エラー: {ex.Message}");
            Debug.LogError($"エラーの詳細: {ex.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// バイト配列を16進文字列として表示（デバッグ用）
    /// </summary>
    public static string BytesToHex(byte[] bytes, int maxLength = 16)
    {
        if (bytes == null) return "null";
        int length = Math.Min(bytes.Length, maxLength);
        string hex = BitConverter.ToString(bytes, 0, length);
        if (bytes.Length > maxLength)
        {
            hex += "...";
        }
        return hex;
    }
} 