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
    // private const string LOG_PREFIX = "[VOICE_ENC]"; // デバッグ用共通接頭辞（不要になったため無効化）
    private const int NONCE_SIZE = 24; // XSalsa20 nonce size
    private const int TAG_SIZE = 16;   // Poly1305 authentication tag size
    private const int SUFFIX_SIZE = 12; // xsalsa20_poly1305_suffix の末尾ノンスサイズ

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

            byte[] ciphertext = encryptedData;
            byte[] nonce = new byte[NONCE_SIZE];

            if (encryptionMode == "xsalsa20_poly1305")
            {
                // RTPヘッダ12バイト + 後半12バイトは0
                Array.Copy(rtpHeader, 0, nonce, 0, 12);
            }
            else if (encryptionMode == "xsalsa20_poly1305_suffix")
            {
                // 実装差異に対応: 末尾が24バイト全ノンス or 12バイトノンス後半
                int len = encryptedData.Length;
                bool tried24 = false;
                // まず24バイト全ノンス（一般的）
                if (len >= TAG_SIZE + 24 + 1)
                {
                    byte[] nonce24 = new byte[NONCE_SIZE];
                    Array.Copy(encryptedData, len - 24, nonce24, 0, 24);
                    byte[] ct24 = new byte[len - 24];
                    Array.Copy(encryptedData, 0, ct24, 0, ct24.Length);
                    var dec24 = DecryptXSalsa20Poly1305(ct24, nonce24, secretKey);
                    if (dec24 != null)
                    {
                        return dec24;
                    }
                    tried24 = true;
                }
                // 次に12バイトノンス後半（先頭12はRTP）
                if (len >= TAG_SIZE + SUFFIX_SIZE + 1)
                {
                    Array.Copy(rtpHeader, 0, nonce, 0, 12);
                    Array.Copy(encryptedData, len - SUFFIX_SIZE, nonce, 12, SUFFIX_SIZE);
                    ciphertext = new byte[len - SUFFIX_SIZE];
                    Array.Copy(encryptedData, 0, ciphertext, 0, ciphertext.Length);
                }
                else
                {
                    Debug.LogError("❌ suffixモードのデータが短すぎます");
                    return null;
                }
            }
            else if (encryptionMode == "aead_aes256_gcm_rtpsize")
            {
                // AEAD AES256-GCM (rtpsize): RTPヘッダーはAAD、IVは 12バイト (RFC5116/GCM)
                // Nonce/IV 構築: RFC3711ベースで AAD=RTPヘッダ、IVは 0x0000 | SSRC(4) | Packet Counter(6) XOR SALT(12)
                // ここでは Discord の仕様に基づき、末尾に 4バイトのインクリメンタルカウンタ(リトルエンディアン)が付与される。
                // encryptedData = [ciphertext || tag(16) || nonce_suffix(4)]
                if (encryptedData.Length < TAG_SIZE + 4 + 1)
                {
                    // Debug.LogError($"{LOG_PREFIX} ❌ GCMモードのデータが短すぎます len={encryptedData.Length}");
                    return null;
                }

                int payloadLen = encryptedData.Length - 4; // 末尾の4バイトはseq/nonceカウンタ
                byte[] seqSuffix = new byte[4];
                Array.Copy(encryptedData, payloadLen, seqSuffix, 0, 4);

                // AEAD AAD は RTPの未暗号化ヘッダー全体（rtpsize系は可変長）
                byte[] aeadAad = rtpHeader;

                byte[] gcmCipherWithTag = new byte[payloadLen];
                Array.Copy(encryptedData, 0, gcmCipherWithTag, 0, payloadLen);

                byte[] plaintext = DecryptAesGcmRtpsize(gcmCipherWithTag, aeadAad, seqSuffix, secretKey, rtpHeader);
                return plaintext;
            }
            else
            {
                Debug.LogError($"❌ 未対応の暗号化モード: {encryptionMode}");
                return null;
            }

            // XSalsa20 + Poly1305復号を実行（ciphertextはタグを含む）
            byte[] decryptedData = DecryptXSalsa20Poly1305(ciphertext, nonce, secretKey);

            if (decryptedData == null)
            {
                Debug.LogError($"❌ 復号失敗");
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
    /// AEAD AES-256-GCM (rtpsize) で復号
    /// ciphertextWithTag: [ciphertext || tag(16)]
    /// aeadAad: RTPヘッダー12バイト
    /// seqSuffix: 末尾4バイトのインクリメンタルカウンター（LE）
    /// secretKey: 32バイト
    /// rtpHeader: SSRC等を含む12バイト（IV組み立てに使用）
    /// </summary>
    private static byte[] DecryptAesGcmRtpsize(byte[] ciphertextWithTag, byte[] aeadAad, byte[] seqSuffix, byte[] secretKey, byte[] rtpHeader)
    {
        try
        {
            if (secretKey == null || secretKey.Length != 32) {
                Debug.LogError($"❌ AES-GCM キー長が不正");
                return null;
            }
            if (rtpHeader == null || rtpHeader.Length < 12) {
                Debug.LogError($"❌ RTPヘッダー長が不正");
                return null;
            }

                // Discord rtpsize系のAES-GCM IVは 12 バイト。
                // 一般的実装では 先頭8バイト=0、後半4バイトに末尾の32-bitカウンタ（LE）を置く。
                byte[] iv = new byte[12]; // デフォルト0埋め
                iv[8]  = seqSuffix[0];
                iv[9]  = seqSuffix[1];
                iv[10] = seqSuffix[2];
                iv[11] = seqSuffix[3];

            // デバッグログは無効化

            // BouncyCastle の GCM 実装を使用（Plugins/BouncyCastle.Crypto.dll）
            // 注意: Unityランタイムで使用可能な簡易呼び出し
            var cipher = new Org.BouncyCastle.Crypto.Modes.GcmBlockCipher(new Org.BouncyCastle.Crypto.Engines.AesEngine());
            var aeadParams = new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
                new Org.BouncyCastle.Crypto.Parameters.KeyParameter(secretKey),
                128, // tag bits
                iv,
                aeadAad
            );
            cipher.Init(false, aeadParams);

            byte[] output = new byte[cipher.GetOutputSize(ciphertextWithTag.Length)];
            int outLen = cipher.ProcessBytes(ciphertextWithTag, 0, ciphertextWithTag.Length, output, 0);
            outLen += cipher.DoFinal(output, outLen);

            if (outLen == 0) {
                Debug.LogError($"❌ AES-GCM 復号結果が空");
                return null;
            }

            // outLen で切り詰め
            if (outLen != output.Length) {
                byte[] trimmed = new byte[outLen];
                Array.Copy(output, 0, trimmed, 0, outLen);
                return trimmed;
            }
            return output;
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ AES-GCM復号エラー: {ex.Message}");
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
                Debug.LogError($"❌ データサイズが認証タグより小さい");
                return null;
            }

            // TweetNaclSharpの高レベルAPIを使用してsecretbox復号
            byte[] plaintext = NaclFast.SecretboxOpen(encryptedData, nonce, key);
            
            if (plaintext == null)
            {
                Debug.LogError($"❌ 復号失敗: 認証に失敗しました");
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