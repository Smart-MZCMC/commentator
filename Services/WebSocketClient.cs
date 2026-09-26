using System.IO;
using System.Net.WebSockets;
using System.Text;
using CommentatorApp.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CommentatorApp.Services;

public class WebSocketClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Timer? _heartbeatTimer;
    private Timer? _reconnectTimer;
    private bool _intentionalClose;
    private int _reconnectAttempts;
    private AppConfig _config;

    public event Action<bool>? ConnectionChanged;

    /// <summary>收到切台状态。参数为 (是否待切, 显示的机位名, 标签文字)。</summary>
    public event Action<bool, string, string>? ShotStateReceived;

    /// <summary>收到内部消息（chat）。</summary>
    public event Action<string>? ChatReceived;

    public event Action<string>? SystemMessage;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public WebSocketClient(AppConfig config)
    {
        _config = config;
    }

    public async Task ConnectAsync()
    {
        _intentionalClose = false;
        await DoConnectAsync();
    }

    private async Task DoConnectAsync()
    {
        try
        {
            _cts?.Cancel();
            _ws?.Dispose();

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            var url = $"{_config.WsUrl}?project_id={_config.ProjectId}&role={_config.Role}";
            await _ws.ConnectAsync(new Uri(url), _cts.Token);

            _reconnectAttempts = 0;
            ConnectionChanged?.Invoke(true);

            _ = ReceiveLoopAsync();
            StartHeartbeat();
        }
        catch
        {
            ConnectionChanged?.Invoke(false);
            ScheduleReconnect();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (_ws?.State == WebSocketState.Open && !_cts?.Token.IsCancellationRequested == true)
            {
                // 一条 WS 消息可能被拆成多帧，必须按 EndOfMessage 拼完再解析，
                // 否则 JSON 会被截断，shot_state 这类消息会被静默丢掉。
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts!.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ConnectionChanged?.Invoke(false);
                        if (!_intentionalClose) ScheduleReconnect();
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        frame.Write(buffer, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (frame.Length > 0)
                {
                    HandleMessage(Encoding.UTF8.GetString(frame.ToArray()));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)
        {
            ConnectionChanged?.Invoke(false);
            if (!_intentionalClose) ScheduleReconnect();
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.ToString() ?? "";
            var payload = msg["payload"] as JObject;

            switch (type)
            {
                case "shot_state":
                    // 导播端每次切台都下发完整状态：current=当前播送，next=即将切台。
                    var state = payload?.ToObject<ShotState>() ?? new ShotState();
                    ShotStateReceived?.Invoke(state.HasPending, state.Program, state.Label);
                    break;
                case "chat":
                    var chatMsg = payload?["message"]?.ToString() ?? "";
                    // 心跳只是保活信号，后端不会再转发，这里只是兜底跳过。
                    if (chatMsg == "heartbeat") break;
                    ChatReceived?.Invoke(chatMsg);
                    break;
                case "system":
                    SystemMessage?.Invoke(payload?["message"]?.ToString()
                        ?? payload?["error"]?.ToString()
                        ?? "");
                    break;
            }
        }
        catch { }
    }

    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(_ =>
        {
            if (_ws?.State == WebSocketState.Open)
            {
                try
                {
                    var msg = JsonConvert.SerializeObject(new
                    {
                        type = "chat",
                        project_id = _config.ProjectId,
                        payload = new { message = "heartbeat" }
                    });
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    _ws?.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { }
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private void ScheduleReconnect()
    {
        _reconnectAttempts++;
        var delay = TimeSpan.FromSeconds(Math.Min(1 * (1 << (_reconnectAttempts - 1)), 10));
        _reconnectTimer?.Dispose();
        _reconnectTimer = new Timer(async _ => await DoConnectAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    public void Disconnect()
    {
        _intentionalClose = true;
        _heartbeatTimer?.Dispose();
        _reconnectTimer?.Dispose();
        try { _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        ConnectionChanged?.Invoke(false);
    }

    public void Dispose()
    {
        Disconnect();
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
