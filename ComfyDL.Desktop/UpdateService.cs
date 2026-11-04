using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDL;

/// <summary>远端发布的一个版本</summary>
public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public long Size { get; set; }
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";

    public string SizeText => Size > 0 ? Fmt.Human(Size) : "";
    public string Summary => string.Join(" · ",
        new[] { Version, Date, SizeText }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>
/// 联网更新检查。
/// 读一个 version.json（可以放 GitHub raw / jsDelivr / Gitee raw 任意静态地址），
/// 格式：{ "version": "2.4.0", "date": "2026-11-06", "size": 62464828,
///        "url": "https://…/极速下载器.exe", "notes": "修了什么" }
/// 地址为空时整个功能静默跳过。
/// </summary>
public static class UpdateService
{
    public static Version Current =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentText
    {
        get
        {
            var v = Current;
            return $"v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    /// <summary>有更新返回信息，没有 / 未配置 / 失败都返回 null</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        var feed = (App.Settings.UpdateFeed ?? "").Trim();
        if (feed.Length == 0) return null;

        try
        {
            var json = await ComfyNet.Http.GetStringAsync(feed, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ver = root.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
            if (ver.Length == 0) return null;
            if (!Version.TryParse(ver.TrimStart('v', 'V'), out var remote)) return null;

            // 只比 Major.Minor.Build，避免 2.3.0 与 2.3.0.0 被当成不同版本
            var a = new Version(remote.Major, remote.Minor, Math.Max(0, remote.Build));
            var b = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
            if (a <= b) return null;

            return new UpdateInfo
            {
                Version = ver,
                Date = root.TryGetProperty("date", out var d) ? (d.GetString() ?? "") : "",
                Size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0,
                Url = root.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "",
                Notes = root.TryGetProperty("notes", out var n) ? (n.GetString() ?? "") : "",
            };
        }
        catch
        {
            return null;   // 网络不通不该打扰用户
        }
    }
}
