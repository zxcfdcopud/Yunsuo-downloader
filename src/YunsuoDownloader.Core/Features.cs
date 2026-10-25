namespace YunsuoDownloader.Core;

/// <summary>
/// 功能开关（编译期常量，发布包按需裁剪）。
///
/// 网盘解析（百度 / 阿里 / 夸克 / 123）代码已经完整实现并测过，
/// 但 1.0 版本**先不公开**：入口隐藏、粘网盘链接会看到「暂未开放」提示。
/// 下个版本把 <see cref="PanEnabled"/> 改成 true 即可完整启用，
/// 不需要恢复任何代码。
/// </summary>
public static class Features
{
    /// <summary>
    /// 网盘分享链接解析（含提取码、Cookie 登录态、网盘账号设置页）。
    /// 1.0 版本 = false（暂未开放）。
    /// </summary>
    public const bool PanEnabled = false;

    /// <summary>网盘功能未开放时给用户看的统一提示。</summary>
    public const string PanDisabledMessage =
        "网盘分享解析（百度 / 阿里云盘 / 夸克 / 123）暂未开放，将在下个版本推出。\n" +
        "当前版本支持：普通直链、磁力链、迅雷链、m3u8/HLS 直播与点播，\n" +
        "以及 B站 / YouTube / 抖音 / 快手 / 小红书 等视频页面（可从浏览器插件一键发送）。";
}
