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

            if (rtpHeader == null || rtpHeader.Length < 12)
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
                // Discord の実装差異に対応: 末尾が24バイト全ノンス or 12バイトノンス後半
                int len = encryptedData.Length;
                
                // まず24バイト全ノンス（一般的な実装）を試す
                if (len >= TAG_SIZE + 24 + 1)
                {
                    byte[] nonce24 = new byte[NONCE_SIZE];
                    Array.Copy(encryptedData, len - 24, nonce24, 0, 24);
                    byte[] ct24 = new byte[len - 24];
                    Array.Copy(encryptedData, 0, ct24, 0, ct24.Length);
                    var dec24 = DecryptXSalsa20Poly1305(ct24, nonce24, secretKey, suppressErrorLog: true);
                    if (dec24 != null)
                    {
                        // 成功：24バイト全ノンスで復号成功
                        return dec24;
                    }
                    // 失敗した場合は次の方法を試す（2回目の試行ではエラーログを表示）
                }
                
                // 次に12バイトノンス後半（RTPヘッダー前半12 + パケット末尾12）を試す
                if (len >= TAG_SIZE + SUFFIX_SIZE + 1)
                {
                    Array.Copy(rtpHeader, 0, nonce, 0, 12);
                    Array.Copy(encryptedData, len - SUFFIX_SIZE, nonce, 12, SUFFIX_SIZE);
                    ciphertext = new byte[len - SUFFIX_SIZE];
                    Array.Copy(encryptedData, 0, ciphertext, 0, ciphertext.Length);
                }
                else
                {
                    // 小さすぎるパケット（Keep-Alive等）は静かにスキップ
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

                // AEAD AAD は RTPヘッダー全体（拡張ヘッダー含む）
                // rtpsize系では、未暗号化ヘッダー全体をAADとして使用
                byte[] aeadAad = rtpHeader;

                byte[] gcmCipherWithTag = new byte[payloadLen];
                Array.Copy(encryptedData, 0, gcmCipherWithTag, 0, payloadLen);

                byte[] plaintext = DecryptAesGcmRtpsize(gcmCipherWithTag, aeadAad, seqSuffix, secretKey, rtpHeader);
                return plaintext;
            }
            else if (encryptionMode == "aead_xchacha20_poly1305_rtpsize")
            {
                // AEAD XChaCha20-Poly1305 (rtpsize): RTPヘッダーはAAD、Nonceは 24バイト
                // Discord の仕様に基づき、末尾に 4バイトのインクリメンタルカウンタ(リトルエンディアン)が付与される。
                // encryptedData = [ciphertext || tag(16) || nonce_suffix(4)]
                if (encryptedData.Length < TAG_SIZE + 4 + 1)
                {
                    return null;
                }
                
                // RTPヘッダーのPayload Type（PT）を確認
                // RTPヘッダー構造: byte[0] = V+P+X+CC, byte[1] = M(1bit) + PT(7bit)
                byte rtpByte1 = rtpHeader[1];
                int payloadType = rtpByte1 & 0x7F; // 下位7ビットがPT
                
                // Opus音声パケットのPTは通常96-111の範囲
                // PTが96未満、またはパケットサイズが小さすぎる場合は制御パケット
                if (payloadType < 72 || payloadType > 127 || encryptedData.Length <= 56)
                {
                    // 制御パケット（Keep-Alive/RTCP等）はスキップ
                    return null;
                }

                int payloadLen = encryptedData.Length - 4; // 末尾の4バイトはseq/nonceカウンタ
                byte[] seqSuffix = new byte[4];
                Array.Copy(encryptedData, payloadLen, seqSuffix, 0, 4);

                // AEAD AAD は RTPヘッダー全体（拡張ヘッダー含む）
                byte[] aeadAad = rtpHeader;

                byte[] xchacha20CipherWithTag = new byte[payloadLen];
                Array.Copy(encryptedData, 0, xchacha20CipherWithTag, 0, payloadLen);

                byte[] plaintext = DecryptXChaCha20Poly1305Rtpsize(xchacha20CipherWithTag, aeadAad, seqSuffix, secretKey, rtpHeader);
                return plaintext;
            }
            else
            {
                Debug.LogError($"❌ 未対応の暗号化モード: {encryptionMode}");
                return null;
            }

            // XSalsa20 + Poly1305復号を実行（ciphertextはタグを含む）
            byte[] decryptedData = DecryptXSalsa20Poly1305(ciphertext, nonce, secretKey);
            
            // 復号結果を返す（エラーログは DecryptXSalsa20Poly1305 内で処理済み）
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
    /// aeadAad: RTPヘッダー全体（拡張ヘッダー含む、12〜16バイト）
    /// seqSuffix: 末尾4バイトのインクリメンタルカウンター（LE）
    /// secretKey: 32バイト
    /// rtpHeader: SSRC等を含むRTPヘッダー（IV組み立てに使用）
    /// </summary>
    private static byte[] DecryptAesGcmRtpsize(byte[] ciphertextWithTag, byte[] aeadAad, byte[] seqSuffix, byte[] secretKey, byte[] rtpHeader)
    {
        try
        {
            if (secretKey == null || secretKey.Length != 32) {
                Debug.LogError($"❌ AES-GCM キー長が不正");
                return null;
            }
            if (aeadAad == null || aeadAad.Length < 12) {
                Debug.LogError($"❌ AAD長が不正（12バイト以上必要）");
                return null;
            }

                // Discord rtpsize系のAES-GCM IVは 12 バイト。
                // IV構築: カウンターをリトルエンディアンのまま使用
                byte[] iv = new byte[12];
                // SeqSuffixをそのままIVの先頭4バイトにコピー（リトルエンディアン）
                Array.Copy(seqSuffix, 0, iv, 0, 4);
                // 残り8バイトは0のまま

            // デバッグログは削減（復号化成功を確認済み）

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
    /// AEAD XChaCha20-Poly1305 (rtpsize) で復号
    /// ciphertextWithTag: [ciphertext || tag(16)]
    /// aeadAad: RTPヘッダー全体（拡張ヘッダー含む、12〜16バイト）
    /// seqSuffix: 末尾4バイトのインクリメンタルカウンター（LE）
    /// secretKey: 32バイト
    /// rtpHeader: SSRC等を含むRTPヘッダー（Nonce組み立てに使用）
    /// </summary>
    private static byte[] DecryptXChaCha20Poly1305Rtpsize(byte[] ciphertextWithTag, byte[] aeadAad, byte[] seqSuffix, byte[] secretKey, byte[] rtpHeader)
    {
        try
        {
            if (secretKey == null || secretKey.Length != 32) {
                Debug.LogError($"❌ XChaCha20-Poly1305 キー長が不正");
                return null;
            }
            if (aeadAad == null || aeadAad.Length < 12) {
                Debug.LogError($"❌ AAD長が不正（12バイト以上必要）");
                return null;
            }

            // Discord rtpsize系のXChaCha20-Poly1305 Nonceは 24 バイト。
            // Nonce構築: インクリメンタルカウンター(4バイト、リトルエンディアン) + 0埋め(20バイト)
            // AES-GCMと同じ構成を使用
            byte[] nonce24 = new byte[24];
            
            // インクリメンタルカウンター（リトルエンディアンのまま）
            Array.Copy(seqSuffix, 0, nonce24, 0, 4);
            // 残り20バイトは0のまま

            // XChaCha20: HChaCha20でキー導出してからChaCha20-Poly1305を使用
            byte[] subkey = HChaCha20(secretKey, nonce24);
            
            // ChaCha20のnonceは残りの8バイト（nonce24[16..23]）
            // BouncyCastleのChaCha20Poly1305は12バイトnonceを要求
            byte[] chachaNonce = new byte[12];
            Array.Copy(nonce24, 16, chachaNonce, 4, 8); // nonce24[16:24] → chachaNonce[4:12]

            // BouncyCastle の ChaCha20-Poly1305 実装を使用
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            var aeadParams = new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
                new Org.BouncyCastle.Crypto.Parameters.KeyParameter(subkey),
                128, // tag bits
                chachaNonce,
                aeadAad
            );
            cipher.Init(false, aeadParams);

            byte[] output = new byte[cipher.GetOutputSize(ciphertextWithTag.Length)];
            int outLen = cipher.ProcessBytes(ciphertextWithTag, 0, ciphertextWithTag.Length, output, 0);
            outLen += cipher.DoFinal(output, outLen);

            if (outLen == 0) {
                Debug.LogError($"❌ XChaCha20-Poly1305 復号結果が空");
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
            Debug.LogError($"❌ XChaCha20-Poly1305復号エラー: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// HChaCha20: XChaCha20のキー導出関数
    /// RFC 7539のChaCha20を基にした32バイトのサブキー導出
    /// </summary>
    private static byte[] HChaCha20(byte[] key, byte[] nonce24)
    {
        if (key == null || key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes");
        if (nonce24 == null || nonce24.Length < 16)
            throw new ArgumentException("Nonce must be at least 16 bytes");

        // ChaCha20の定数 "expand 32-byte k"
        uint[] state = new uint[16];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;

        // キーを設定（リトルエンディアン）
        for (int i = 0; i < 8; i++)
            state[4 + i] = BitConverter.ToUInt32(key, i * 4);

        // Nonceの最初の16バイトを設定
        for (int i = 0; i < 4; i++)
            state[12 + i] = BitConverter.ToUInt32(nonce24, i * 4);

        // HChaCha20: 20ラウンド実行（ChaCha20と同じ）
        uint[] working = new uint[16];
        Array.Copy(state, working, 16);

        for (int i = 0; i < 10; i++)
        {
            // ダブルラウンド
            QuarterRound(working, 0, 4, 8, 12);
            QuarterRound(working, 1, 5, 9, 13);
            QuarterRound(working, 2, 6, 10, 14);
            QuarterRound(working, 3, 7, 11, 15);
            QuarterRound(working, 0, 5, 10, 15);
            QuarterRound(working, 1, 6, 11, 12);
            QuarterRound(working, 2, 7, 8, 13);
            QuarterRound(working, 3, 4, 9, 14);
        }

        // 出力: state[0..3]とworking[12..15]を組み合わせて32バイトのキーを生成
        byte[] output = new byte[32];
        for (int i = 0; i < 4; i++)
        {
            byte[] bytes = BitConverter.GetBytes(working[i]);
            Array.Copy(bytes, 0, output, i * 4, 4);
        }
        for (int i = 0; i < 4; i++)
        {
            byte[] bytes = BitConverter.GetBytes(working[12 + i]);
            Array.Copy(bytes, 0, output, 16 + i * 4, 4);
        }

        return output;
    }

    /// <summary>
    /// ChaCha20のクォーターラウンド関数
    /// </summary>
    private static void QuarterRound(uint[] x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] ^= x[a]; x[d] = RotateLeft(x[d], 16);
        x[c] += x[d]; x[b] ^= x[c]; x[b] = RotateLeft(x[b], 12);
        x[a] += x[b]; x[d] ^= x[a]; x[d] = RotateLeft(x[d], 8);
        x[c] += x[d]; x[b] ^= x[c]; x[b] = RotateLeft(x[b], 7);
    }

    /// <summary>
    /// 32ビット左ローテーション
    /// </summary>
    private static uint RotateLeft(uint value, int count)
    {
        return (value << count) | (value >> (32 - count));
    }

    /// <summary>
    /// 暗号化モードに応じたnonce構成を生成
    /// </summary>
    private static byte[] CreateNonce(byte[] rtpHeader, string encryptionMode) {
        byte[] nonce = new byte[NONCE_SIZE];
        
        switch (encryptionMode) {
            case "xsalsa20_poly1305":
                // 標準モード: RTPヘッダー(12バイト) + 12バイトの0埋め
                Array.Copy(rtpHeader, 0, nonce, 0, 12);
                break;
                
            case "xsalsa20_poly1305_suffix":
                // suffix モード: RTPヘッダー(12バイト) + 12バイトの0埋め（同じ構成）
                Array.Copy(rtpHeader, 0, nonce, 0, 12);
                break;
                
            case "aead_xchacha20_poly1305_rtpsize":
                // XChaCha20モード（実装済み）
                Debug.LogError("❌ XChaCha20モードはDecryptVoicePacketで直接処理されます");
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
    private static byte[] DecryptXSalsa20Poly1305(byte[] encryptedData, byte[] nonce, byte[] key, bool suppressErrorLog = false)
    {
        try
        {
            if (encryptedData.Length < TAG_SIZE)
            {
                // 小さすぎるパケット（Keep-Alive等）は静かにスキップ
                return null;
            }

            // TweetNaclSharpの高レベルAPIを使用してsecretbox復号
            byte[] plaintext = NaclFast.SecretboxOpen(encryptedData, nonce, key);
            
            if (plaintext == null)
            {
                // エラーログの抑制が要求されていない場合のみ、大きなパケットの失敗を警告
                if (!suppressErrorLog && encryptedData.Length > 100)
                {
                    Debug.LogWarning($"⚠️ 音声パケットの復号失敗 (サイズ: {encryptedData.Length}バイト)");
                }
                return null;
            }

            return plaintext;
        }
        catch (Exception ex)
        {
            if (!suppressErrorLog)
            {
                Debug.LogError($"❌ TweetNaclSharp復号エラー: {ex.Message}");
                Debug.LogError($"エラーの詳細: {ex.StackTrace}");
            }
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