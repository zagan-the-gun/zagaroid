# AGENTS.md

このファイルは zagaroid リポジトリで作業する AI コーディングアシスタント（Cursor / Claude / Codex 等）向けの指示書です。**作業を始める前に必ず読んでください。**

## このプロジェクトの概要

zagaroid は **Unity 製の配信支援デスクトップアプリ**です。Twitch チャット・Discord ボイス・自分のマイクからの発話を、字幕化・翻訳・読み上げ・アバター演出して OBS に流します。

重い処理（STT / NMT / LLM）は本体には載っておらず、ローカル WebSocket（`ws://localhost:50001`）で連携する **兄弟リポジトリ群**（`MenZ-Whisper` / `MenZ-FuguMT` / `MenZ-ReazonSpeech` / `MenZ-Moonshine` / `MenZ-GeminiCLI`）に委譲します。詳細は `docs/architecture.md` § 1〜3。

## 作業前に必ず読むドキュメント

タスクに着手する前に、以下を順に確認してください。

1. **`docs/architecture.md`** — 全体構成・データフロー・スレッドモデル
2. **`docs/glossary.md`** — ドメイン用語（Actor / WipeAI / STTモード / チャネル 等）。**用語の取り違えが事故の原因になりやすいので必ず参照**
3. タスクに関連する縦串ドキュメント:
   - 字幕・翻訳・OBS 関連 → `docs/features/subtitle-pipeline.md`
   - Discord 関連 → **`docs/integrations/discord.md`（自前実装のため事故率が高い、必読）**
   - 兄弟アプリ間の通信プロトコル → `docs/companion-apps.md`

`docs/README.md` がドキュメントの目次です。

## 重要な前提と命名規約

### "MCP" は Anthropic の MCP ではない

コード上の `[MCP]` ログ・`IsMcpFormat()` 等は、本プロジェクト独自の **JSON-RPC 2.0 ベースの規約**です。Anthropic の Model Context Protocol（公式）とは別物。混同しないこと。

### Actor / actorName / displayName / speaker

- `actorName` — 半角英数の内部識別子（OBS ソース名生成のキー、`{actorName}_subtitle`）
- `displayName` — 表示用の自由文字
- `speaker` — JSON-RPC `params.speaker` のフィールド名。**`actorName` と一致する前提**で動作する

3 つは別物です。混同しないこと。詳細は `docs/glossary.md`。

### Wipe AI ループ防止

`MenZ-GeminiCLI` のコメントが Wipe AI 自身に再送されるとループします。防止条件:

- `ActorConfig.type = "wipe"` の Actor を登録する
- その `actorName` を `MenZ-GeminiCLI` の `[client] speaker_name` と完全一致させる
- zagaroid 側のフィルタ `actor.type != "wipe"` のときだけ再ブロードキャスト

詳細は `docs/companion-apps.md` § 6.1 / `docs/features/subtitle-pipeline.md` § 11。

### Discord は SDK 不使用の自前実装

`Assets/Scripts/Discord/` は Discord SDK を使わず、Gateway / Voice Gateway / Voice UDP / 暗号化を全て自前実装しています。**変更時は事故率が極めて高い**ため、`docs/integrations/discord.md` § 0「ここを変えると壊れる」と § 10「事故ポイント集」を必ず読んでください。

特に以下は触る前に検討:

- `SUPPORTED_ENCRYPTION_MODES` の優先順（並べ替え禁止）
- RTP 拡張プレアンブル `0xBE 0xDE` の 12 バイト除去
- `_opusDecodeLock`（Concentus は非スレッドセーフ）

### スレッドモデル

Unity メインスレッド以外に WebSocket / UDP / Timer / `Task.Run` 由来の別スレッドがあります。Unity API（`MonoBehaviour`、`AudioSource` 等）や `static event` 発火は **必ずディスパッチャ経由**で行うこと:

- `UnityMainThreadDispatcher.Instance().Enqueue(action)` — 汎用
- `MultiPortWebSocketServer.Enqueue(action)` — WebSocket サーバ系
- `DiscordBotClient.EnqueueMainThreadAction(action)` — Discord 系

詳細は `docs/architecture.md` § 8。

## コード変更時のルール

### コードと一緒にドキュメントを更新する

挙動が変わる変更を入れたら、関連するドキュメントも **同じコミットで** 更新してください。**腐ったドキュメントは無いより悪い**ため、維持できないドキュメントは作らないこと。

更新対象の典型例:

- 字幕パイプラインの挙動を変えた → `docs/features/subtitle-pipeline.md`
- 兄弟アプリとの JSON-RPC スキーマを変えた → `docs/companion-apps.md` § 3
- Discord の暗号化モードや復号ロジックを変えた → `docs/integrations/discord.md` § 4 / § 10
- `CentralManager` の `static event` を増やした → `docs/architecture.md` § 5.1 のイベント表
- 新しいドメイン用語を導入した → `docs/glossary.md`
- `PlayerPrefs` キーを増やした → `docs/architecture.md` § 9 と `docs/features/*.md` の関連設定節

### ファイルパスはフル相対パスで書く

ドキュメントやコメントでファイルを指す際は `Assets/Scripts/Discord/DiscordBotClient.cs:540-555` の形式で書いてください。AI が grep で辿れる利点があります。

### コメントは「なぜ」を書く

コードが何をしているかは読めば分かります。**意図・トレードオフ・「ここを変えると壊れる」**理由をコメントに残してください。「変数を初期化する」のような自明なコメントは入れないでください。

### Newtonsoft.Json を使う

JSON 関連は `Newtonsoft.Json`（`com.unity.nuget.newtonsoft-json`）を使います。`Vector2` 等の循環参照を避けるため、必要に応じて `Vector2Converter` を `JsonSerializerSettings.Converters` に追加してください（例: `CentralManager.GetActors() / SetActors()`）。

`JsonUtility` は OBS WebSocket クライアント（`OBSWebSocketClient.cs`）でのみ使われていますが、新規では Newtonsoft を推奨します。

## テスト

EditMode テストは `Assets/Tests/EditMode/` にあります。Discord 系の暗号化・JSON ペイロード・音声バッファのみカバー。Unity Test Runner で実行してください。

新規実装時:

- Discord 暗号化を変更したら `DiscordCryptoTests` の往復テストが通ることを確認
- Gateway ペイロードを変更したら `DiscordGatewayPayloadBuilderTests` の op コードと JSON フィールド名チェックが通ることを確認

## 兄弟リポジトリへの変更

zagaroid のワークスペースには `MenZ-Whisper` / `MenZ-FuguMT` / `MenZ-ReazonSpeech` / `MenZ-Moonshine` / `MenZ-GeminiCLI` も含まれることがあります。これらの **JSON-RPC スキーマや接続パスを変える場合**、両側を同期して変更し、`docs/companion-apps.md` § 3 のスキーマ表を更新してください。

兄弟側の README/`*.md` も該当する記述があれば併せて更新が必要です。

## やってはいけないこと

- **Discord SDK の導入提案**: 過去に検討して採用しなかった経緯があります。Unity / IL2CPP 環境で動かないため
- **`PlayerPrefs` 以外の永続化導入**: 現状すべて `PlayerPrefs` で統一されています。変更には全体合意が必要
- **`SampleScene.unity` 以外のシーン追加**: 現在シーンは 1 つで運用されています
- **新しいドキュメントを軽率に追加すること**: `docs/README.md` の「ドキュメントを増やすかどうかの判断」を読んでください

## コミットメッセージ

コミットメッセージは日本語で問題ありません。スコープ／要約／（必要なら）詳細の構造を意識してください。例:

```text
Discord音声: 暗号化モード aead_aes256_gcm_rtpsize の優先順を最上位に固定

Discord 側の Voice Gateway が複数モードを返す環境で、
xsalsa20_poly1305 が選ばれてしまうケースがあったため。
docs/integrations/discord.md § 4.1 を更新。
```

## ユーザーとの会話

- 必ず日本語で応答してください（ユーザールール）
- 推奨ドキュメント以外の `docs/` 配下を参照する場合、その存在意義をユーザーに簡潔に説明してください
- ドキュメントを新規追加する場合は、必ずユーザーに「本当に必要か」を確認してください

---

## 参考: ファイル早見表

| 領域 | ディレクトリ | 主要ファイル |
| :-- | :-- | :-- |
| ハブ | `Assets/Scripts/` | `CentralManager.cs`, `UnityMainThreadDispatcher.cs` |
| Discord | `Assets/Scripts/Discord/` | `DiscordBotClient.cs`, `DiscordNetworkManager.cs`, `DiscordVoiceGatewayManager.cs`, `DiscordVoiceUdpManager.cs`, `DiscordCrypto.cs` |
| 字幕 | `Assets/Scripts/Subtitle/` | `SubtitleController.cs` |
| 翻訳 | `Assets/Scripts/Translation/`, `Assets/Scripts/Apis/` | `TranslationController.cs`, `DeepLApiClient.cs` |
| Twitch | `Assets/Scripts/Twitch/` | `UnityTwitchChatController.cs` |
| WebSocket | `Assets/Scripts/WebSockets/` | `MultiPortWebSocketServer.cs`, `OBSWebSocketClient.cs` |
| TTS | `Assets/Scripts/VoiceVox/` | `VoiceVoxApiClient.cs` |
| UI | `Assets/Scripts/UI/`, `Assets/UI Toolkit/` | `SettingUIController.cs`, `TabController.cs`, `MainMenu.uxml` |
| Canvas | `Assets/Scripts/Canvases/` | `CanvasController.cs`, `MainCanvasAvatarController.cs`, `LogCanvasController.cs` |
| アバター | `Assets/Scripts/Animators/` | `FaceAnimatorController.cs` |
| Models | `Assets/Scripts/Models/` | `ActorConfig.cs` |
| テスト | `Assets/Tests/EditMode/` | `Discord*Tests.cs`, `ErrorHandlerTests.cs` |

詳細な構造は `docs/architecture.md` § 12 のファイル参照早見表を参照してください。
