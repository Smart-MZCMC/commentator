using Newtonsoft.Json;

namespace CommentatorApp.Models;

/// <summary>
/// 切台状态。
///
/// 导播端每次切台都同时上报「当前播送」和「即将切台」，而不是分两次发
/// 「预告」和「已切」。解说端因此不需要自己推断正在播的是什么——
/// 有 <see cref="Next"/> 就显示「即将播送」，没有就显示「正在播送」。
/// </summary>
public sealed class ShotState
{
    /// <summary>当前正在播送的机位。</summary>
    [JsonProperty("current")]
    public string Current { get; set; } = "";

    /// <summary>
    /// 即将切过去的机位。空串表示导播已确认切完，画面就是 <see cref="Current"/>。
    /// </summary>
    [JsonProperty("next")]
    public string Next { get; set; } = "";

    /// <summary>是否还有待切的机位。</summary>
    [JsonIgnore]
    public bool HasPending => !string.IsNullOrEmpty(Next);

    /// <summary>窗口上方那行字：带中文冒号，下面换行显示机位名。</summary>
    [JsonIgnore]
    public string Label => HasPending ? "即将播送：" : "正在播送：";

    /// <summary>要显示的机位名。待切时优先显示待切项。</summary>
    [JsonIgnore]
    public string Program => HasPending ? Next : Current;
}
