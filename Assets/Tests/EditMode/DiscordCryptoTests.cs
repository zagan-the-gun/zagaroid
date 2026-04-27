using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using TweetNaclSharp;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// DiscordCrypto.DecryptVoicePacket のユニットテスト。
/// 不正入力のケースでは製品コードが Debug.LogError するため、Console に赤いログが出ます。
/// これはテスト失敗ではなく、LogAssert.Expect で「想定内」として扱っています。
/// Unity は想定ログでも Console へそのまま出すことが多いです。Console の「Error Pause」がオンだと実行が止まるのでオフ推奨。
/// </summary>
public class DiscordCryptoTests
{
    private static byte[] TestKey32()
    {
        var k = new byte[32];
        for (int i = 0; i < 32; i++)
            k[i] = (byte)(i + 1);
        return k;
    }

    /// <summary>RTP 固定ヘッダ（PT=96）。GCM / XChaCha の音声パケット分岐を通す。</summary>
    private static byte[] VoiceRtpHeader12()
    {
        var rtp = new byte[12];
        rtp[0] = 0x80;
        rtp[1] = 0x60; // M=0, PT=96
        rtp[2] = 0x00;
        rtp[3] = 0x00;
        rtp[4] = 0x00;
        rtp[5] = 0x00;
        rtp[6] = 0x00;
        rtp[7] = 0x00;
        rtp[8] = 0x00;
        rtp[9] = 0x00;
        rtp[10] = 0x00;
        rtp[11] = 0x00;
        return rtp;
    }

    /// <summary>Discord rtpsize AES-GCM と同じ IV/AAD で暗号化し、wire = ciphertext||tag||seqSuffix4 を組み立てる。</summary>
    private static byte[] EncryptAesGcmRtpsizeWire(byte[] plaintext, byte[] rtpHeader, byte[] key32, byte[] seqSuffix4)
    {
        var iv = new byte[12];
        Array.Copy(seqSuffix4, 0, iv, 0, 4);
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key32), 128, iv, rtpHeader));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        len += cipher.DoFinal(output, len);
        if (len != output.Length)
        {
            var trimmed = new byte[len];
            Array.Copy(output, 0, trimmed, 0, len);
            output = trimmed;
        }

        var wire = new byte[len + 4];
        Array.Copy(output, 0, wire, 0, len);
        Array.Copy(seqSuffix4, 0, wire, len, 4);
        return wire;
    }

    /// <summary>DecryptXChaCha20Poly1305Rtpsize と同じ subkey / nonce / AAD で暗号化し、wire = ciphertext||tag||seqSuffix4 を返す。</summary>
    private static byte[] EncryptXChaCha20Poly1305RtpsizeWire(byte[] plaintext, byte[] rtpHeader, byte[] key32, byte[] seqSuffix4)
    {
        var nonce24 = new byte[24];
        Array.Copy(seqSuffix4, 0, nonce24, 0, 4);
        byte[] subkey = DiscordCrypto.HChaCha20(key32, nonce24);
        var chachaNonce = new byte[12];
        Array.Copy(nonce24, 16, chachaNonce, 4, 8);

        var cipher = new ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(subkey), 128, chachaNonce, rtpHeader));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        len += cipher.DoFinal(output, len);
        if (len != output.Length)
        {
            var trimmed = new byte[len];
            Array.Copy(output, 0, trimmed, 0, len);
            output = trimmed;
        }

        var wire = new byte[len + 4];
        Array.Copy(output, 0, wire, 0, len);
        Array.Copy(seqSuffix4, 0, wire, len, 4);
        return wire;
    }

    [Test]
    public void Xsalsa20Poly1305_RoundTrip()
    {
        var key = TestKey32();
        var rtp = VoiceRtpHeader12();
        var nonce24 = new byte[24];
        Array.Copy(rtp, 0, nonce24, 0, 12);
        var plain = new byte[] { 0x0f, 0x1e, 0x2d, 0x3c, 0x4b };
        var boxed = NaclFast.Secretbox(plain, nonce24, key);
        var decrypted = DiscordCrypto.DecryptVoicePacket(boxed, rtp, key, "xsalsa20_poly1305");
        CollectionAssert.AreEqual(plain, decrypted);
    }

    [Test]
    public void Xsalsa20Poly1305Suffix_TwelveByteNonceSuffix_RoundTrip()
    {
        var key = TestKey32();
        var nonce24 = new byte[24];
        for (int i = 0; i < 24; i++)
            nonce24[i] = (byte)(0x40 + i);
        var rtp = new byte[12];
        Array.Copy(nonce24, 0, rtp, 0, 12);
        var suffix12 = new byte[12];
        Array.Copy(nonce24, 12, suffix12, 0, 12);
        var plain = new byte[] { 0x01 };
        var boxed = NaclFast.Secretbox(plain, nonce24, key);
        var wire = new byte[boxed.Length + 12];
        Array.Copy(boxed, 0, wire, 0, boxed.Length);
        Array.Copy(suffix12, 0, wire, boxed.Length, 12);
        Assert.Less(wire.Length, 17 + 24 + 1, "24バイト末尾ノンス試行分岐に入らない長さであること");
        var decrypted = DiscordCrypto.DecryptVoicePacket(wire, rtp, key, "xsalsa20_poly1305_suffix");
        CollectionAssert.AreEqual(plain, decrypted);
    }

    [Test]
    public void Xsalsa20Poly1305Suffix_TwentyFourByteNonceSuffix_RoundTrip()
    {
        var key = TestKey32();
        var nonce24 = new byte[24];
        for (int i = 0; i < 24; i++)
            nonce24[i] = (byte)(0x80 + i);
        var rtp = VoiceRtpHeader12();
        var plain = new byte[64];
        for (int i = 0; i < plain.Length; i++)
            plain[i] = (byte)i;
        var boxed = NaclFast.Secretbox(plain, nonce24, key);
        var wire = new byte[boxed.Length + 24];
        Array.Copy(boxed, 0, wire, 0, boxed.Length);
        Array.Copy(nonce24, 0, wire, boxed.Length, 24);
        var decrypted = DiscordCrypto.DecryptVoicePacket(wire, rtp, key, "xsalsa20_poly1305_suffix");
        CollectionAssert.AreEqual(plain, decrypted);
    }

    [Test]
    public void AeadAes256GcmRtpsize_RoundTrip()
    {
        var key = TestKey32();
        var rtp = VoiceRtpHeader12();
        var seq = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var plain = new byte[48];
        for (int i = 0; i < plain.Length; i++)
            plain[i] = (byte)(i ^ 0x5a);
        var wire = EncryptAesGcmRtpsizeWire(plain, rtp, key, seq);
        Assert.Greater(wire.Length, 56);
        var decrypted = DiscordCrypto.DecryptVoicePacket(wire, rtp, key, "aead_aes256_gcm_rtpsize");
        CollectionAssert.AreEqual(plain, decrypted);
    }

    [Test]
    public void AeadXChaCha20Poly1305Rtpsize_RoundTrip()
    {
        var key = TestKey32();
        var rtp = VoiceRtpHeader12();
        var seq = new byte[] { 0x0a, 0x0b, 0x0c, 0x0d };
        var plain = new byte[48];
        for (int i = 0; i < plain.Length; i++)
            plain[i] = (byte)(i ^ 0x37);
        var wire = EncryptXChaCha20Poly1305RtpsizeWire(plain, rtp, key, seq);
        Assert.Greater(wire.Length, 56);
        var decrypted = DiscordCrypto.DecryptVoicePacket(wire, rtp, key, "aead_xchacha20_poly1305_rtpsize");
        CollectionAssert.AreEqual(plain, decrypted);
    }

    [Test]
    public void AeadAes256GcmRtpsize_BufferTooShort_ReturnsNull()
    {
        var key = TestKey32();
        var rtp = VoiceRtpHeader12();
        var enc = new byte[19];
        Assert.IsNull(DiscordCrypto.DecryptVoicePacket(enc, rtp, key, "aead_aes256_gcm_rtpsize"));
    }

    [Test]
    public void AeadAes256GcmRtpsize_PacketLengthAtOrBelow56_ReturnsNull()
    {
        var key = TestKey32();
        var rtp = VoiceRtpHeader12();
        var enc = new byte[56];
        Array.Clear(enc, 0, enc.Length);
        Assert.IsNull(DiscordCrypto.DecryptVoicePacket(enc, rtp, key, "aead_aes256_gcm_rtpsize"));
    }

    [Test]
    public void AeadAes256GcmRtpsize_ControlPayloadType_ReturnsNull()
    {
        var rtp = new byte[12];
        rtp[0] = 0x80;
        rtp[1] = 0x10; // PT=16（音声レンジ外）
        var key = TestKey32();
        var enc = new byte[60];
        Array.Clear(enc, 0, enc.Length);
        var result = DiscordCrypto.DecryptVoicePacket(enc, rtp, key, "aead_aes256_gcm_rtpsize");
        Assert.IsNull(result);
    }

    [Test]
    public void AeadXChaCha20Poly1305Rtpsize_BufferTooShort_ReturnsNull()
    {
        var key = TestKey32();
        var rtp = VoiceRtpHeader12();
        var enc = new byte[19];
        Assert.IsNull(DiscordCrypto.DecryptVoicePacket(enc, rtp, key, "aead_xchacha20_poly1305_rtpsize"));
    }

    [Test]
    public void AeadXChaCha20Poly1305Rtpsize_PacketLengthAtOrBelow56_ReturnsNull()
    {
        var key = TestKey32();
        var rtp = VoiceRtpHeader12();
        var enc = new byte[56];
        Array.Clear(enc, 0, enc.Length);
        Assert.IsNull(DiscordCrypto.DecryptVoicePacket(enc, rtp, key, "aead_xchacha20_poly1305_rtpsize"));
    }

    [Test]
    public void AeadXChaCha20Poly1305Rtpsize_ControlPayloadType_ReturnsNull()
    {
        var rtp = new byte[12];
        rtp[0] = 0x80;
        rtp[1] = 0x10;
        var key = TestKey32();
        var enc = new byte[60];
        Array.Clear(enc, 0, enc.Length);
        var result = DiscordCrypto.DecryptVoicePacket(enc, rtp, key, "aead_xchacha20_poly1305_rtpsize");
        Assert.IsNull(result);
    }

    [Test]
    public void DecryptVoicePacket_NullCipher_ReturnsNull_AndLogs()
    {
        LogAssert.Expect(LogType.Error, new Regex("暗号化データが不正"));
        Assert.IsNull(DiscordCrypto.DecryptVoicePacket(null, VoiceRtpHeader12(), TestKey32()));
    }

    [Test]
    public void DecryptVoicePacket_ShortCipher_ReturnsNull_AndLogs()
    {
        LogAssert.Expect(LogType.Error, new Regex("暗号化データが不正"));
        Assert.IsNull(DiscordCrypto.DecryptVoicePacket(new byte[4], VoiceRtpHeader12(), TestKey32()));
    }

    [Test]
    public void DecryptVoicePacket_ShortRtp_ReturnsNull_AndLogs()
    {
        LogAssert.Expect(LogType.Error, new Regex("RTPヘッダーが不正"));
        Assert.IsNull(DiscordCrypto.DecryptVoicePacket(new byte[32], new byte[4], TestKey32()));
    }

    [Test]
    public void DecryptVoicePacket_InvalidKeyLength_ReturnsNull_AndLogs()
    {
        LogAssert.Expect(LogType.Error, new Regex("暗号化キーが不正"));
        Assert.IsNull(DiscordCrypto.DecryptVoicePacket(new byte[32], VoiceRtpHeader12(), new byte[16]));
    }

    [Test]
    public void DecryptVoicePacket_UnknownMode_ReturnsNull_AndLogs()
    {
        LogAssert.Expect(LogType.Error, new Regex("未対応の暗号化モード"));
        Assert.IsNull(DiscordCrypto.DecryptVoicePacket(new byte[32], VoiceRtpHeader12(), TestKey32(), "unsupported_mode_xyz"));
    }

    [Test]
    public void BytesToHex_PrefixAndEllipsis()
    {
        var hex = DiscordCrypto.BytesToHex(new byte[] { 0xAB, 0xCD, 0xEF }, maxLength: 2);
        StringAssert.Contains("AB", hex);
        StringAssert.Contains("CD", hex);
        StringAssert.Contains("...", hex);
    }

    [Test]
    public void BytesToHex_NullReturnsLiteral()
    {
        Assert.AreEqual("null", DiscordCrypto.BytesToHex(null));
    }
}
