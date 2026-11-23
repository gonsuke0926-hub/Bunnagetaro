# スクショはローカルAIにぶん投げろ！ v1.2.1

画面上の好きな範囲をキャプチャ枠で囲み、ローカルの Ollama（AI モデル）や内蔵 OCR（PP-OCRv6）を使って翻訳・文字起こし・数式・表の読み取りを行う Windows 向けツールです。  
画像は外部サーバーへ送信せず、すべて手元の PC 内で処理します。

## 導入手順

### 1. zip を展開して起動する

1. [Releases](https://github.com/gonsuke0926-hub/Bunnagetaro/releases/latest) から `Bunnagetaro-v1.2.1-win-x64.zip` をダウンロードします。
2. zip を右クリックし、「すべて展開」で任意のフォルダへ展開します。
3. 展開した中の `Bunnagetaro` フォルダを開き、`Bunnagetaro.exe` を起動します。
   - `Bunnagetaro.exe` だけを別の場所へ移動すると動作しません（同じフォルダの DLL や `Models` フォルダが必要です）。デスクトップなどから起動したい場合はショートカットを作成してください。
   - 初回起動時に Windows SmartScreen の警告が出た場合は、「詳細情報」→「実行」を選択してください。

※ プリセット「OCR」（内蔵 OCR のみ）を使う場合は、下の 2〜3（Ollama の導入）は不要です。

### 2. Ollama をインストールする（翻訳・数式・表を使う場合）

1. [Ollama 公式サイト](https://ollama.com/download) から Windows 版をインストールします。
2. インストール後、Ollama を起動します（タスクトレイにアイコンが表示されていれば起動中です）。

### 3. モデルを取得する

スタートメニューで `cmd` と検索してコマンドプロンプトを開き、使いたいプリセットのモデルを取得します。

- **翻訳**（PLaMo 3 Translate）
  ```bat
  ollama pull hf.co/pfnet/plamo-3-translate-0.8b-GGUF:Q4_K_M
  ```
- **数式**（GLM-OCR）
  ```bat
  ollama pull glm-ocr
  ```
- **表**（PaddleOCR-VL 1.6）
  ```bat
  ollama pull hf.co/PaddlePaddle/PaddleOCR-VL-1.6-GGUF
  ```

別の Ollama モデル（`gemma4`、`qwen3-vl` 等）を使う場合は、設定ページのプリセット欄でモデルと指示を変更してください。

### 4. 接続を確認して使う

1. 「設定」→「AI接続」で「接続テスト」「モデル一覧を取得」を押します。
2. 「キャプチャ窓を開く」を押し、読みたい範囲に枠を合わせてバーの「ぶん投げる」を押します。

## 既定プリセット

| プリセット | ぶん投げ方（既定） | 想定モデル | Ollama |
| :--- | :--- | :--- | :--- |
| 翻訳 | 文字起こし → 内蔵OCR（`PP-OCRv6 small`） | `hf.co/pfnet/plamo-3-translate-0.8b-GGUF:Q4_K_M` | 要 |
| OCR | 内蔵OCRのみ（`PP-OCRv6 small`） | 同梱（`PP-OCRv6 small` / `medium`） | 不要 |
| 数式 | 画像そのまま | `glm-ocr` | 要 |
| 表 | 画像そのまま | `hf.co/PaddlePaddle/PaddleOCR-VL-1.6-GGUF` | 要 |

設定ページの「ぶん投げ方」から、プリセットごとに `文字起こし`（`内蔵OCR` / `VLM`）・`画像そのまま`・`内蔵OCRのみ` を切り替えられます。  
数式・表は解答欄の右クリックからコピーできます。OBS 等の配信・録画に枠や解答欄を映したい場合は、設定ページの「配信・録画に映す」を ON にしてください（既定 OFF）。

## 動作環境

- OS: Windows 10 / 11 (64bit)
- Ollama: ローカルで起動していること（既定 URL: `http://localhost:11434`。内蔵 OCR のみ使う場合は不要）
- Microsoft Edge WebView2 Runtime: Windows 10 / 11 には通常標準搭載されています（数式・表の描画に使用）

## 設定・ログの場所とアンインストール

- 設定とログは `%APPDATA%\Bunnagetaro5` に保存されます。
- アンインストール時は、展開したフォルダと `%APPDATA%\Bunnagetaro5` を削除してください。

## ライセンス

- 本ソフトウェアはフリーソフトウェアです。利用・再配布の条件と免責は [LICENSE.txt](LICENSE.txt) をご覧ください。
- 同梱のサードパーティソフトウェア（PP-OCRv6 / OpenVINO / ONNX Runtime 等）のライセンス表記は `THIRD_PARTY_NOTICES.txt`（およびアプリ内「バージョン情報」）をご確認ください。
- Ollama で別途取得する AI モデルは、各モデル配布元のライセンス条件に従ってください。
