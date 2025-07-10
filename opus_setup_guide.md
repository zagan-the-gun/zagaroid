# Unity用 Opus ライブラリ セットアップガイド

## 推奨: 事前ビルド済みライブラリの使用

### ダウンロード先

#### Windows (x64) - 確実なダウンロード方法
```
【推奨】RareWares:
https://www.rarewares.org/opus.php
- opus-tools-0.2-win64.zip (2.6MB) をダウンロード
- opus.dll を使用

Mozilla公式FTP:
https://ftp.mozilla.org/pub/opus/win64/
- 公式バイナリをダウンロード

VideoHelp.com:
https://www.videohelp.com/software/OpusTools
- OpusTools 0.2+34 libopus 1.4+9 をダウンロード
```

#### macOS
```
直接ダウンロード:
https://github.com/openaudia/opus-unity/releases/download/v1.4.0/libopus-macos.dylib

または

Homebrew経由:
brew install opus
# /opt/homebrew/lib/libopus.dylib をコピー
```

#### Linux (x64)
```
直接ダウンロード:
https://github.com/openaudia/opus-unity/releases/download/v1.4.0/libopus-linux-x64.so

または

パッケージマネージャー:
sudo apt install libopus-dev  # Ubuntu/Debian
sudo yum install opus-devel   # CentOS/RHEL
# /usr/lib/x86_64-linux-gnu/libopus.so.0 をコピー
```

## ファイル配置

```
Assets/
  Plugins/
    Windows/
      x86_64/
        opus.dll
    macOS/
      libopus.dylib
    Linux/
      x86_64/
        libopus.so
```

## Unity設定手順

### 1. Windows用DLL設定
- opus.dll を選択
- Platform settings:
  - Settings for Windows → Any OS
  - Architecture: x86_64
  - Placeholder: Assets/Plugins/Windows/x86_64/

### 2. macOS用Dylib設定
- libopus.dylib を選択
- Platform settings:
  - Settings for macOS → macOS
  - Architecture: Any CPU
  - Placeholder: Assets/Plugins/macOS/

### 3. Linux用SO設定
- libopus.so を選択
- Platform settings:
  - Settings for Linux → Linux
  - Architecture: x86_64
  - Placeholder: Assets/Plugins/Linux/x86_64/

## テスト用コード

```csharp
// OpusDecoderの初期化テスト
try {
    var testDecoder = new OpusDecoder(48000, 2);
    Debug.Log("Opus ライブラリが正常にロードされました");
    testDecoder.Dispose();
} catch (Exception ex) {
    Debug.LogError($"Opus ライブラリのロードに失敗: {ex.Message}");
}
```

## トラブルシューティング

### DllNotFoundException
- ライブラリファイルが正しい場所にあるか確認
- ファイル名が正確か確認（opus.dll, libopus.dylib, libopus.so）
- プラットフォーム設定が正しいか確認

### EntryPointNotFoundException  
- ライブラリのバージョンが合っているか確認
- 32bit/64bitアーキテクチャが正しいか確認

### macOS Gatekeeper エラー
```bash
# Gatekeeperの警告を解除
sudo xattr -r -d com.apple.quarantine /path/to/libopus.dylib
``` 