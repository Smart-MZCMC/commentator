# smart-mzcmc-commentator

解说端 —— 给解说员看的**大字提示屏**。

解说员看的是芯象 PGM 画面，存在物理时延，而且导播不会提前告知下一步切什么。这个应用常驻副屏，用超大字号把导播的预判指令显示出来，让解说能提前组织语言。

**C# / .NET 10 WPF** 桌面应用（`net10.0-windows`），无需登录，启动时读配置订阅项目。

## 界面

深色背景 + 超大字号。**只有一块主显示区**，按后端指令在两种状态间切换：

```
┌────────────────────────────┐
│      即将播送：            │  ← 有待切机位时，琥珀红
│      （节目名）            │
└────────────────────────────┘

┌────────────────────────────┐
│      正在播送：            │  ← 已确认切完，绿色
│      （节目名）            │
└────────────────────────────┘
```

右下角一个连接状态小圆点：绿=已连接，红=断开重连中。

状态流转（完全由后端下发的 `shot_state` 驱动）：

1. 导播滑动确认 → `shot_state` 的 `next` 非空 → 显示「即将播送：XXX」
2. 导播点「确认已切」→ `shot_state` 的 `next` 为空 → 切换为「正在播送：XXX」

本端不做任何状态推断，也没有本地过期计时器。

## 配置

`config.json`（随构建复制到输出目录，**运行期读取，改完重启 exe 即可生效，不需要重新编译**）：

```json
{
  "ServerUrl": "http://zhdb.647382.xyz",
  "WsUrl": "ws://zhdb.647382.xyz/ws",
  "ProjectId": 1,
  "Role": "commentator"
}
```

| 字段 | 说明 |
| :--- | :--- |
| `WsUrl` | **实际生效**的地址。反代部署下是 `ws://<域名>/ws` |
| `ProjectId` | 目标项目 id |
| `Role` | 固定 `commentator` |
| `ServerUrl` | **当前没有任何代码使用**——本应用只通过 WebSocket 通信，不发 HTTP 请求。留着备用 |

本应用**不使用 HTTP API**，所以只需要 WebSocket 能连通。

::: danger 启用 HTTPS 后必须把 `ws://` 改成 `wss://`
原生应用不受「混合内容」限制，但服务端若只在 443 提供 TLS，`ws://` 仍然连不上。
:::

::: tip 反代与直连的区别
- **直连**（本地开发）：`ws://<服务器IP>:3002/ws`
- **反代**（生产）：`ws://<域名>/ws`，nginx 把 `/ws` 转到 3002

两种形态都由后端首页自动识别并显示，配置照着首页「接入地址」区块填即可。
:::

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
| `shot_state` | 读 `payload.current` / `payload.next`：`next` 非空显示「即将播送：」，否则显示「正在播送：」 |
| `chat` | 内部消息，显示在底部状态栏 |
| `system` | 系统提示 |

连接后周期性发送心跳（`chat` + `payload.message = "heartbeat"`）保活。
后端在入库前就会丢弃心跳，所以它不会进日志、也不计入消息统计。断线自动重连。

## 部署

发布后把 `publish/` 整个目录（含 `config.json`）拷到解说员副屏机器上，双击 exe 即可。改服务器地址直接编辑 `config.json`，无需重新编译。
