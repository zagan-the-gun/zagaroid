# zagaroid ドキュメント

このディレクトリには zagaroid 本体（Unityアプリ）の設計・運用ドキュメントを集約します。
人間の開発者と AI コーディングアシスタント（Cursor 等）の双方が、コードを読み始める前に全体像を素早く掴めることを目的とします。

## 読み始めガイド

AIエージェント・新規参画者は、まず以下を上から順に読めば全体像を把握できます。

1. 本ファイル（目次）
2. [`architecture.md`](architecture.md) ⭐ — 全体アーキテクチャ・データフロー・スレッドモデル
3. [`glossary.md`](glossary.md) ⭐ — ドメイン用語集（用語の意味を固定する。短時間で読める）
4. [`companion-apps.md`](companion-apps.md) — `MenZ-*` 兄弟リポジトリ群との関係 / JSON-RPC 契約
5. [`features/subtitle-pipeline.md`](features/subtitle-pipeline.md) — 字幕パイプラインの縦串（4 入力経路 → キュー／結合 → 翻訳 → OBS 送出）
6. [`integrations/discord.md`](integrations/discord.md) — Discord 自前実装の事故防止ガイド（Gateway / Voice UDP / 暗号化 / Opus）

## 文書の方針

- **書く前に「無くてもAIが推論できるか？」を自問する**: コードから素直に分かることはドキュメントに書かない。コードから分からない**意図・契約・用語・事故ポイント**だけを書く
- **AI フレンドリー**: ファイルパスは `Assets/Scripts/...` のフル相対パスで書く（`grep` しやすい）。図は Mermaid で書く
- **更新ルール**: コードと同じコミットでドキュメントも更新する。維持できないならそのドキュメントは作らない（**腐ったドキュメントは無いより悪い**）
- **粒度**: 機能ドキュメントは「概要 / ユーザーから見た振る舞い / 関連スクリプト / データフロー / 外部依存 / 既知の制約」の節構成で揃える

## ドキュメントを増やすかどうかの判断

新しいドキュメントは、以下のいずれかに当てはまる場合のみ追加する:

- そのドキュメントが無いと、AI が同じミスを繰り返している
- 用語や規約が曖昧で、人間／AI 間の解釈にズレが起きている
- 複数ファイル／リポジトリにまたがる契約があり、grep で繋げない
- コードを読めば分かる内容なら、書かない

人間向けに必要になったタイミング（新規参画者の onboarding、CI 整備、リリース手順の標準化）で追加するもの:

- `development.md` — 開発環境セットアップ
- `build-and-release.md` — ビルド・配布
- `testing.md` — EditMode テスト方針

これらは現時点では未作成。必要になったら書く。

## 関連リポジトリ

zagaroid は以下の兄弟リポジトリとローカル WebSocket 経由で連携します（詳細は [`companion-apps.md`](companion-apps.md)）。

| リポジトリ | 役割 | zagaroid との接続 |
| :-- | :-- | :-- |
| `MenZ-Whisper` | 音声認識（STT、Whisper 系） | `ws://localhost:50001/` |
| `MenZ-ReazonSpeech` | 音声認識（STT、ReazonSpeech / NeMo） | 同上 |
| `MenZ-Moonshine` | 音声認識（STT、軽量） | 同上 |
| `MenZ-FuguMT` | 翻訳（NMT） | `ws://localhost:50001/translate_text` |
| `MenZ-GeminiCLI` | ワイプ AI（コメント生成 LLM） | `ws://localhost:50001/` |

zagaroid 側は **WebSocket サーバ**として待ち受けます（`Assets/Scripts/WebSockets/MultiPortWebSocketServer.cs`）。
