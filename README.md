# smart-mzcmc-commentator

解说端 —— 给解说员看的**大字提示屏**。

解说员看的是芯象 PGM 画面，存在物理时延，而且导播不会提前告知下一步切什么。这个应用常驻副屏，用超大字号把导播的预判指令显示出来，让解说能提前组织语言。

**C# / .NET 10 WPF** 桌面应用（`net10.0-windows`），无需登录，启动时读配置订阅项目。

## 界面

深色背景 + 超大字号，分上下两半：

```
┌────────────────────────────┐
│        正在播送            │
│     （当前节目名/内容）     │
├────────────────────────────┤
│        即将播送            │
│   （导播推送的下一项）      │
└────────────────────────────┘
```

右下角一个连接状态小圆点：绿=已连接，红=断开重连中。

状态流转：

1. 导播推送「下一项」→ **下半屏** 显示「即将播送: XXX」
2. 导播点「确认已切」→ **上半屏** 更新为「正在播送: XXX」，并清空下半屏

## 配置

`config.json`（随构建复制到输出目录，可直接改）：

```json
{
  "ServerUrl": "http://127.0.0.1:3000",
  "WsUrl": "ws://127.0.0.1:3002/ws",
  "ProjectId": 1,
  "Role": "commentator"
}
```

> 注意 WebSocket 服务跑在 **3002** 端口，而 HTTP API 在 3000。

## 构建与运行

需要 **.NET 10 SDK**（Windows）。

```bash
dotnet restore
dotnet build --configuration Release
dotnet run                        # 直接运行

dotnet publish --configuration Release --output publish
```

## 目录

```
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs   # 双卡片界面与状态更新
├── Models/AppConfig.cs                    # 配置模型
├── Services/WebSocketClient.cs            # 连接、心跳、断线重连、消息分发
├── config.json
└── CommentatorApp.csproj
```

## 通信

只接收，不发送业务指令。处理的消息类型：

| type | 行为 |
| :--- | :--- |
| `next_shot` | 更新下半屏「即将播送」 |
| `confirm_switch` | 上半屏切换为「正在播送」，清空下半屏 |
| `chat` | 忽略 `payload.message == "heartbeat"` 的心跳，其余可显示 |
| `system` | 系统提示 |

连接后周期性发送心跳（`chat` + `payload.message = "heartbeat"`）保活，断线自动重连。

## 部署

发布后把 `publish/` 整个目录（含 `config.json`）拷到解说员副屏机器上，双击 exe 即可。改服务器地址直接编辑 `config.json`，无需重新编译。
