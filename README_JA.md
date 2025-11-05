# Unity Agent Client

[![GitHub license](https://img.shields.io/github/license/nuskey8/UnityAgentClient)](./LICENSE)
![Unity 2021.3+](https://img.shields.io/badge/unity-2021.3+-000.svg)

[English](README.md) | 日本語

Provides integration of any AI agent (Gemini CLI, Claude Code, Codex CLI, etc.) with the Unity editor using Agent Client Protocol(ACP).

![demo](/docs/images/img-demo.gif)

## 概要

Unity Agent Clientは、Zedの提唱するAgent Client Protocol(ACP)を用いて、任意のAIエージェントをUnityエディタ上で動かすためのエディタ拡張です。

### 特徴

- Unityエディタに任意のAIエージェントを組み込み可能
- ACPに対応した全てのAIエージェント(Gemini CLI, Claude Code, Codex CLI, etc.)に対応
- アセットやエディタの情報をコンテキストとして利用可能
- 組み込みのMCPサーバーを提供

### What's Agent Client Protocol?

<img src="https://camo.githubusercontent.com/7de78d0f4d0f9755d0ed1aef979e0758dc64790f9c14831d0445d92dc6f36666/68747470733a2f2f7a65642e6465762f696d672f6163702f62616e6e65722d6461726b2e77656270">

[Agent Client Protocol](https://agentclientprotocol.com)はZedが提唱する、AIエージェントとコードエディタを繋ぐための新たなプロトコルです。JSON-RPCをベースとしたプロトコルであり、MCP(Model Context Protocol)との連携を念頭に設計されています。

ZedはACPを用いて外部のAIエージェントをエディタに組み込むことが可能です。Unity Agent ClientはZedと同様のアプローチを採用し、エディタ拡張としてACP Clientを実装することで、任意のAIエージェントをUnityエディタ上で動かすことを可能にします。

現在ではGemini CLIが実験的なサポート(`--experimental-acp`)を提供しているほか、Zedの提供するアダプタを用いることでClaude Code、Codex CLIなどをACPに対応させることが可能です。ACPをサポートするAIエージェントの一覧は以下のページで確認できます。

https://agentclientprotocol.com/overview/agents

### Why not Unity AI?

Unity 6.2以降では、公式が提供する[Unity AI](https://unity.com/products/ai)を利用できます。では、なぜUnity Agent Clientを利用するのでしょうか？

Unity AIはUnityが提供するモデルを利用するため、都度最適なモデルをユーザーが選択することができません。さらに、Unity AIを利用するには専用のトークン(ポイント)を必要とし、そのためUnityプロジェクトをUnity Cloudに接続することが必須となります。

Unity Agent Clientは特定のLLMプロバイダやエージェントに依存しないため、ユーザーがそれぞれ利用しているAIエージェントをエディタに組み込むことが可能です。さらに、ACPはMCPとの連携をサポートしているため、ユーザー側で任意のMCPサーバーを接続することも可能です。

## セットアップ

Unity Agent Clientを利用するにはUnity 2021.3以上およびNode.jsランタイムが必要です。

### 1. AgentClientProtocolパッケージのインストール

Unity Agent Clientは[AgentClientProtocol](https://www.nuget.org/packages/AgentClientProtocol)パッケージに依存しているため、NuGetからこれをインストールする必要があります。

インストールには[NugetForUnity](https://github.com/GlitchEnzo/NuGetForUnity)(推奨)を利用するか、全ての依存パッケージをdllとしてプロジェクトに追加してください。

### 2. UnityAgentClientパッケージのインストール

Package Managerを開き、`+ > Add package from git URL...`に以下のURLを入力します。
```
https://github.com/nuskey8/UnityAgentClient.git?path=Assets/UnityAgentClient
```

### 3. 利用するAgentのセットアップ

`Project Settings > Unity Agent Client`を開き、利用したいAIエージェントに応じて設定を埋めます。

> [!NOTE]
> macOSの場合、zshでPATH解決がうまく行われないことがあります。もしエラーが起こった場合は、`which`コマンドでバイナリのフルパスを確認し、Commandにそれを入力してください。

> [!WARNING] 
> 設定の内容はプロジェクトのUserSettingsフォルダに保存されます。UserSettingsフォルダは通常`.gitignore`で除外されますが、APIキーなどを誤ってアップロードしないように十分注意してください。

<details>

<summary>Gemini CLI</summary>

Gemini CLIは現在実験的なACPサポートを提供しています。これは`--experimental-acp`オプションを指定することで実行できます。

| Command  | Arguments            |
| -------- | -------------------- |
| `gemini` | `--experimental-acp` |

ログインにAPIキーを利用する場合は、Environment Variablesに`GEMINI_API_KEY`を追加してください。

</details>

<details>

<summary>Claude Code</summary>

Claude Code自体はACPをサポートしていないため、Zedが提供するアダプタを利用します。以下のリポジトリのREADMEに従ってclaude-code-acpを導入してください。

https://github.com/zed-industries/claude-code-acp

| Command           | Arguments |
| ----------------- | --------- |
| `claude-code-acp` | -         |

</details>

<details>

<summary>Codex CLI</summary>

Codex CLI自体はACPをサポートしていないため、Zedが提供するアダプタを利用します。以下のリポジトリのREADMEに従ってcodex-acpを導入してください。

https://github.com/zed-industries/codex-acp

| Command     | Arguments |
| ----------- | --------- |
| `codex-acp` | -         |

</details>

<details>

<summary>opencode (推奨)</summary>

opencodeはCLI上で動作するオープンソースのAIエージェントであり、任意のLLMプロバイダを利用できるほか、MCPやACPにも標準で対応しています。

https://opencode.ai/

| Command    | Arguments |
| ---------- | --------- |
| `opencode` | `acp`     |

</details>

<details>

<summary>Goose</summary>

GooseはCLI/デスクトップで動作するオープンソースのAIエージェントであり、任意のLLMプロバイダを利用できるほか、MCPやACPにも標準で対応しています。

https://block.github.io/goose/

| Command    | Arguments |
| ---------- | --------- |
| `goose` | `acp`     |

</details>

## 使い方

`Window > Unity Agent Client > AI Agent`からAIエージェントを開くと、自動でセッションへの接続が行われます。

![](/docs/images/img-agent-window.png)

- フィールドにプロンプトを入力し、Sendを押すことで送信ができます。
- アセットをウィンドウにドラッグ&ドロップすることで、コンテキストとしてアタッチすることが可能です。
- ツールを実行する場合、エージェント側から許可を求められることがあります。(許可を求めるかどうかはエージェント側の設定に依存します)
- 利用中のエージェントがサポートしている場合に限り、送信時にモードやモデルの切り替えを行うことが可能です。

## ベストプラクティス

Unity Agent Clientはエディタ上のAIエージェントを用いてコーディングを行うことを**推奨していません。** Unityの制約上、C#スクリプトを編集するとDomain Reloadが発生し、セッションが切断されます。また、そもそもUnityエディタはAIが編集したコードを確認することに向いていません。これには、IDEやコードエディタに組み込まれたAIエージェントを利用すべきです。

Unity Agent Clientは、AIエージェントを利用してUnityプロジェクト全体をドキュメントとして活用することに焦点を当てています。エディタ全体をコンテキストとしたAIを利用することで、開発メンバーがプロジェクトへの理解を深めるのに役立てたり、不具合やボトルネックの特定を補助するツールとして使うことができます。

従来のMCPを用いたアプローチではエディタ外のツールを利用する必要がありましたが、Unity Agent Clientはエディタ内で動作するため、都度ウィンドウを切り替える必要はありません。

## ライセンス

このライブラリは[MIT LICENSE](LICENSE)の下で提供されています。