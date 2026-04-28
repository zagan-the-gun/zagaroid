# 用語集

> **目的**: zagaroid プロジェクトでコード・ドキュメント・会話に登場する用語の意味を固定する。AI エージェントが指示を誤解せず、人間が用語の揺れに悩まない状態を保つ。
> **方針**: 「公式の業界用語」と「このプロジェクト特有の用語」が混在する。**特有の用語**を中心に載せ、業界用語は zagaroid 内の使われ方に絞って書く。

## アクター・話者まわり

### Actor（アクター）

字幕／読み上げ／アバター演出を束ねた **話者単位の設定**。実体は `Assets/Scripts/Models/ActorConfig.cs` の `ActorConfig`。`PlayerPrefs["Actors"]` に JSON 配列で永続化される。

主なフィールド:

- `actorName` — 半角英数の識別子。OBS 字幕ソース名のキー（`{actorName}_subtitle`）
- `displayName` — 表示用の自由文字
- `discordUserId` — Discord User ID（`type="friend"` 時に使用）
- `type` — `"local"` / `"friend"` / `"wipe"` の 3 択
- `translationEnabled` — 英語字幕を出すか
- `ttsEnabled` — VOICEVOX 読み上げをするか（`type="wipe"` 時に有効）

### actorName / displayName / speaker

3 つは **別々のもの**。混同しやすいので注意:

- `actorName` — 内部識別子。半角英数。OBS のソース名生成に使う
- `displayName` — 画面・字幕に出す表示名（自由文字）
- `speaker` — JSON-RPC 通信の `params.speaker` フィールド。**zagaroid 側は `actorName` と一致する前提**で扱う（`GetActorByName()` で解決）

新しい兄弟アプリを書くときは、`speaker = actorName` の規約を守ること（ズレると `mySubtitle` フォールバックに合流する）。

### Actor の `type`（3 値）

| 値 | 意味 | 主用途 |
| :-- | :-- | :-- |
| `"local"` | 自分 | 自分の音声字幕用 |
| `"friend"` | Discord ボイスの友人 | Discord User ID マッピング、複数話者対応 |
| `"wipe"` | AI コメンテーター（Wipe AI） | LLM 生成コメントの宛先。**ループ防止フィルタの判定キー** |

`type` の値は `MultiPortWebSocketServer.cs:206-217` で挙動分岐に使われる重要キー。値を変えるとループ防止が壊れる。

## 配信・演出まわり

### Wipe / ワイプ

配信画面の **ワイプ枠**。元来は配信者本人の顔出し用の小窓。zagaroid では「ワイプ枠に AI キャラを表示する」発想から転じて以下の語が派生している。

### Wipe AI / ワイプ AI

ワイプ枠に表示する AI コメンテーター（キャラクター）。実体は `MenZ-GeminiCLI` で動く LLM ベースのコメント生成エージェント。配信のワイプ枠で字幕を読み、ガヤ（後述）を入れる役。

zagaroid 側ではこれを `ActorConfig.type = "wipe"` で扱う。

### ガヤ

配信におけるバラエティ番組風の **不謹慎・茶々入れ的なコメント**を指す業界寄りの語。`MenZ-GeminiCLI` の `system_prompt` の中で「ワンパターンを避けて不謹慎なコメントを返せ」という指示として実装されている。

### 入店音

配信中に視聴者がコメントを **その配信で初めて投稿したとき**に 1 回だけ鳴らす効果音。`Assets/Scripts/CentralManager.cs:27` の `entranceSound` で指定し、`speakComment()` 内で `usersProfile` に未登録のユーザーに対して再生する。

### コメントスクロール（ニコ風）

Twitch コメント等を画面右から左に流す表示形式。Main Canvas 上で `Assets/Scripts/Canvases/CanvasController.cs` が描画する。zagaroid 内では「ニコニコ風」「Canvas コメント」と呼ばれることもある。

### コメント meme

特定キーワードや話者に反応して画像・動画を一定時間表示する演出機能（README 記載の構想）。`Assets/Scripts/Player/VideoPlayerController.cs` が一部基盤を持つ。

### NDI 配信

`Klak NDI`（`jp.keijiro.klak.ndi`）を使って Main Canvas を NDI 経由で OBS に送る方式。同一 PC 内のローレイテンシ映像転送。zagaroid のメインシーンには **NDI カメラ**が居て、Main Canvas を `RenderTexture` 経由で NDI に流している。

## 字幕パイプラインまわり

### チャネル（字幕チャネル）

zagaroid 内部での「同じ枠に表示する字幕の単位」。**実体は OBS の字幕ソース名**。命名規約は `{actorName}_subtitle`。例: `me_subtitle` / `zagan_subtitle`。

`SubtitleController` 内では `Dictionary<string, ...>` のキーとしてこの文字列がそのまま使われる。

### 英語字幕チャネル

`{actorName}_subtitle_en` で固定。zagaroid 側で `subtitle + "_en"` を組み立てる（`Assets/Scripts/CentralManager.cs:866`）。OBS 側にこの命名のテキストソースを用意しない限り、英訳は表示されない。

### 結合表示

字幕表示中に次の字幕が来たとき、改行 `\n` で繋いで同じ枠に並べる挙動。1 度結合した字幕にさらに新規をぶつけると、それはキューに積まれる（3 行以上にはならない）。詳細は `docs/features/subtitle-pipeline.md` § 5.1。

## STT・モードまわり

### STT モード（旧称: MenZ モード）

Discord ボイスの音声認識を **ローカル STT 兄弟アプリ**（`MenZ-Whisper` 等）で行うモード。`PlayerPrefs["DiscordSubtitleMethodStr"] = "STT"`。

> **注意**: コード上には `s_isMenZMode` / `IsMenZMode()` という旧命名が残っている（`Assets/Scripts/Discord/DiscordBotClient.cs:103-104`）。意味は「STT モードかどうか」で、Whisper 以外の MenZ-\* も含む。命名統一は将来 TODO。

### WitAI モード

Discord ボイスの音声認識を **Wit.AI（HTTPS REST）** で行うモード。`DiscordSubtitleMethodStr = "WitAI"`。長期的には STT モードに統合廃止予定。

### `recognize_audio` ブロードキャスト問題

zagaroid → STT は **ID 無し通知**で `/` の全クライアントにブロードキャストされる仕様（`Assets/Scripts/WebSockets/MultiPortWebSocketServer.cs:373-419`）。複数 STT が同時接続していると重複認識が起こる。発話とテキストの紐付けは `params.speaker` のみ。詳細は `docs/companion-apps.md` § 3.4。

## 通信プロトコルまわり

### MCP / MCP 風 JSON-RPC

zagaroid と兄弟アプリ間の独自プロトコルで、コード上 `[MCP]` というログプレフィックスや `IsMcpFormat()` といった命名で登場する。

> **重要**: これは **Anthropic の公式 Model Context Protocol（MCP）とは別物**。実体は単に **JSON-RPC 2.0** に独自メソッド名（`notifications/subtitle` / `recognize_audio` / `translate_text`）を載せたもの。命名衝突しているため、特に AI に説明するときは「MCP 風 JSON-RPC」「JSON-RPC 2.0 ベースの独自規約」と書き分けると混乱しない。

### ハートビート（Heartbeat）

Discord Gateway / Voice Gateway を維持するための定期 PING。`System.Timers.Timer` で送る。落ちると Discord 側に切断される。`DiscordNetworkManager.cs` / `DiscordVoiceGatewayManager.cs` の各 `_heartbeatTimer`。

### Identify

Discord Gateway 接続後に Bot トークン・インテント等を送るペイロード（op=2）。`DiscordPayloadHelper.CreateIdentifyPayload()`。

### IP Discovery

Discord Voice UDP 確立時に、Bot 側の外部 IP/ポートを Discord サーバから教えてもらう手順。74 バイトの特殊パケットを送って応答を待つ。`DiscordVoiceUdpManager.PerformUdpDiscovery()`。

### SSRC（同期ソース識別子）

RTP プロトコルの送信源 ID。Discord ボイスでは **発話者ごとに割り当てられる**。zagaroid 側は `SSRC → discordUserId → actorName` の 3 段マッピングで話者を特定する（`Assets/Scripts/Discord/DiscordBotClient.cs:540-555`、`DiscordVoiceUdpManager.SetSSRCMapping()`）。

### 暗号化モード

Discord Voice UDP の音声パケット暗号化方式。zagaroid は次の 4 種類に対応（優先順）:

1. `aead_aes256_gcm_rtpsize`
2. `aead_xchacha20_poly1305_rtpsize`
3. `xsalsa20_poly1305_suffix`
4. `xsalsa20_poly1305`（既定）

`DiscordVoiceUdpManager.SUPPORTED_ENCRYPTION_MODES`。Discord 側のサポートは時期によって変わるため、固定すると壊れる。

### Opus

Discord ボイスで使う音声コーデック。zagaroid では `Concentus`（Pure C# 実装）でデコードする。`DiscordBotClient.DecodeOpusToPcm()`。フレームサイズ自動検出 ＋ FEC（Forward Error Correction）対応。

### FEC（Forward Error Correction）

Opus デコード時、前のパケットが欠落した分を後続パケットから復元する機能。zagaroid では通常デコード失敗時のフォールバックとして 1 度試行する（`DiscordBotClient.cs:802-818`）。

## 翻訳まわり

### NMT

Neural Machine Translation の略。zagaroid では **`MenZ-FuguMT`** 経由のローカル翻訳を指す。`PlayerPrefs["TranslationMode"] = "NMT"`。

### DeepL

`https://api-free.deepl.com/v2/translate` を直接叩く翻訳経路。`PlayerPrefs["TranslationMode"] = "deepl"`（既定）。

### 翻訳フォールバック

`TranslationController.Translate()` の挙動。優先側（DeepL or NMT）が失敗したらもう片方に切り替える。両方失敗時は **元のテキスト**（日本語のまま）を英語字幕枠に入れる仕様。詳細は `docs/features/subtitle-pipeline.md` § 6。

### ksk 翻訳 / 自分翻訳用 AI / コメント翻訳用 AI

`README.md` のロードマップに登場する旧構想の名称。「複数 LLM を並列で叩いて、自分用／友人（ksk）用／コメント用に翻訳を分ける」案。**現在は実装されていない**（DeepL or NMT で 1 本化）。

将来この語が出てきたら、文脈次第で「過去の構想」か「再着手」か確認する必要がある。

## TTS まわり

### TTS

Text To Speech。zagaroid では **VOICEVOX**（`localhost:50021`）への REST 呼び出しを指す。

### 話者 ID（VOICEVOX speakerId）

VOICEVOX の音声キャラクター識別子。Twitch コメント読み上げでは、ユーザーごとに `GetSpeakerRnd()` でランダムに 1 つ選び、`usersProfile` 辞書にキャッシュする（同じ視聴者は常に同じ声）。

## アバター・口パクまわり

### リップシンク

「口パク」と同じ意味。zagaroid では発話の RMS 音量を `[0, 1]` に正規化して `CentralManager.SendLipSyncLevel(level, actorName)` で発火する。`FaceAnimatorController` / `MainCanvasAvatarController` がこれを購読して口の開閉に反映する。

### `avatarShowWithTalk`

`ActorConfig.avatarShowWithTalk = true` のとき、その actor が **発話している間だけ**アバターを表示する。`OnSubtitleEnded` を購読して非表示に戻す。

## 旧用語・廃止語

混乱しやすい古い名前を整理する。新規コードで使ってはいけない。

| 旧 | 新 / 現状 |
| :-- | :-- |
| `MenZMode` / `IsMenZMode()` | **STT モード**（`DiscordSubtitleMethodStr="STT"`）。コードは旧名のままだが意味は「STT モード」 |
| `MySubtitle` / `MyEnglishSubtitle`（PlayerPrefs キー） | `ActorConfig` に統合移行中。フォールバック用途以外では使わない |
| `MyName` / `FriendName`（PlayerPrefs キー） | 同上 |
| ゆかコネ / ゆかりねっと形式 | レガシー字幕プロトコル（平文 or JSON）。互換維持のためサーバ側だけ残っているが、新規実装では使わない |
| `EchoService2`（ポート 50002） | 旧字幕互換。ほぼ未使用 |

## 関連ドキュメント

- 全体像 → [`architecture.md`](architecture.md)
- 兄弟アプリとの契約 → [`companion-apps.md`](companion-apps.md)
- 字幕パイプライン詳細 → [`features/subtitle-pipeline.md`](features/subtitle-pipeline.md)
- Discord 自前実装の事故防止 → [`integrations/discord.md`](integrations/discord.md)
