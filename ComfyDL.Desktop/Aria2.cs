using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDL;

/// <summary>aria2c 引擎：16 线程分段 + 断点续传 + 进度解析</summary>
public static class Aria2
{
    private static string? _cached;

    public static string? Find() => FindInternal(useCustom: true);

    /// <summary>忽略用户自定义路径，只做自动检测（"恢复自动检测"时用）</summary>
    public static string? AutoFind() => FindInternal(useCustom: false);

    /// <summary>改了引擎设置后必须调一下，否则 _cached 还指向旧文件</summary>
    public static void ResetCache() => _cached = null;

    /// <summary>给设置页用的一句话来源说明</summary>
    public static string Describe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return "未找到";
        if (string.Equals(Path.GetDirectoryName(exe)?.TrimEnd('\\', '/'),
                AppContext.BaseDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return "程序同目录（整个文件夹可直接拷走）";
        var custom = App.Settings?.Aria2Path;
        if (!string.IsNullOrWhiteSpace(custom) &&
            string.Equals(custom, exe, StringComparison.OrdinalIgnoreCase))
            return "自定义路径";
        if (exe.Contains(@"WinGet\Packages", StringComparison.OrdinalIgnoreCase))
            return "winget 安装（路径随版本变化）";
        if (exe.Contains(@"Program Files", StringComparison.OrdinalIgnoreCase))
            return "系统安装目录";
        return "环境变量 PATH";
    }

    private static string? FindInternal(bool useCustom)
    {
        if (useCustom && _cached != null && File.Exists(_cached)) return _cached;

        // 优先级 1：用户在设置里手动指定的引擎
        if (useCustom)
        {
            var custom = App.Settings?.Aria2Path;
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            {
                _cached = custom;
                return custom;
            }
        }

        var candidates = new List<string>
        {
            // 优先级 2：exe 同目录。把 aria2c.exe 放在旁边即可便携使用
            Path.Combine(AppContext.BaseDirectory, "aria2c.exe")
        };

        // winget 安装位置（版本号不固定，扫一遍）
        try
        {
            var winget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WinGet\Packages");
            if (Directory.Exists(winget))
            {
                foreach (var d in Directory.GetDirectories(winget, "aria2*"))
                {
                    candidates.AddRange(Directory.GetFiles(d, "aria2c.exe", SearchOption.AllDirectories));
                }
            }
        }
        catch { }

        candidates.Add(@"C:\Program Files\aria2\aria2c.exe");
        candidates.Add(@"C:\Program Files (x86)\aria2\aria2c.exe");

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                _cached = c;
                return c;
            }
        }

        // PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "aria2c.exe");
                if (File.Exists(exe))
                {
                    _cached = exe;
                    return exe;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// 按站点决定并发数。
    /// 实测 ModelScope 的 CDN 对同一 IP 的并发连接有惩罚性限速：
    /// 同一个 27GB 分片，16 线程只有 ~100 KB/s，4 并发 ~2.5 MB/s，单连接能跑到 13.6 MB/s。
    /// 所以手动模式走 ModelScope 时强制单连接，线程越多反而越慢。
    /// 自动模式（autoTuned）由 ComfyNet.PickConnectionsAsync 实测决定，不走这里。
    /// </summary>
    private static int ConnectionsForHost(string url, int configured, Action<string> log)
    {
        string host;
        try { host = new Uri(url).Host.ToLowerInvariant(); }
        catch { return configured; }

        if (host.Contains("modelscope"))
        {
            if (configured > 1)
                log($"    {host} 会限制并发，已把 {configured} 线程改为单连接（更快）");
            return 1;
        }
        return configured;
    }

    /// <summary>解析 aria2 的 .aria2 控制文件，得到 (已下载, 总大小)</summary>
    public static (long Done, long Total) ReadCtrl(string ctrlPath, long expectTotal)
    {
        var b = File.ReadAllBytes(ctrlPath);
        int o = 2;
        uint el = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        o += 4 + (int)el;
        uint ihl = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        o += 4 + (int)ihl;
        uint pieceLen = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        o += 4;
        ulong total = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));
        o += 16; // total + uploaded
        uint blen = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        o += 4;

        if (total == 0) total = (ulong)Math.Max(0, expectTotal);

        int avail = Math.Max(0, Math.Min((int)blen, b.Length - o));
        long done = 0;
        for (int i = 0; i < avail; i++) done += System.Numerics.BitOperations.PopCount(b[o + i]);

        if (pieceLen > 0)
        {
            long npieces = (long)Math.Ceiling(total / (double)pieceLen);
            if (npieces > 0) done = Math.Min(done, npieces);
            return (Math.Min(done * pieceLen, (long)total), (long)total);
        }
        return (0, (long)total);
    }

    /// <summary>限速与代理：所有下载（含 BT）共用，读当前设置</summary>
    private static List<string> CommonArgs()
    {
        var list = new List<string>();
        var s = App.Settings;

        if (s.SpeedLimitMBps > 0)
        {
            long bytes = (long)Math.Round(s.SpeedLimitMBps * 1024 * 1024);
            list.Add($"--max-overall-download-limit={bytes}");
        }

        var proxy = (s.ProxyUrl ?? "").Trim();
        if (proxy.Length > 0)
        {
            list.Add($"--all-proxy={proxy}");
        }
        return list;
    }

    /// <summary>
    /// 下载单个文件。返回 true 表示文件已完整落盘。
    /// 取消时抛 OperationCanceledException。
    /// </summary>
    public static async Task<bool> DownloadAsync(
        DownloadTask task, string url, long totalHint, int connections, Action<string> log, CancellationToken ct,
        bool autoTuned = false)
    {
        var aria = Find() ?? throw new InvalidOperationException(
            "找不到 aria2c.exe，请先安装：winget install aria2.aria2");

        var destDir = Path.GetDirectoryName(task.DestPath)!;
        Directory.CreateDirectory(destDir);
        var ctrl = task.DestPath + ".aria2";

        connections = Math.Clamp(connections, 1, 32);
        // autoTuned：调用方已实测过各并发档位，直接采用，不再套用站点硬规则
        if (!autoTuned) connections = ConnectionsForHost(url, connections, log);
        var psi = new ProcessStartInfo(aria)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = destDir,
        };
        var commonArgs = CommonArgs();

        foreach (var a in new[]
                 {
                     $"-x{connections}", $"-s{connections}", "-k1M",
                     $"--max-connection-per-server={connections}", "--min-split-size=1M",
                     "--disable-ipv6=true",
                     "--check-certificate=false",
                     "--file-allocation=none",
                     "-c",
                     "--timeout=20", "--connect-timeout=20",
                     "--max-tries=0", "--retry-wait=2",
                     "--auto-save-interval=2",
                     "--console-log-level=warn", "--summary-interval=0",
                     "-d", destDir, "-o", task.FileName, url
                 }.Concat(commonArgs))
        {
            psi.ArgumentList.Add(a);
        }

        int attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            task.Status = attempts == 1 ? TaskStatus.Connecting : TaskStatus.Downloading;
            task.SpeedBps = 0;

            Process proc;
            try
            {
                proc = Process.Start(psi) ?? throw new InvalidOperationException("aria2c 启动失败");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("aria2c 启动失败：" + ex.Message, ex);
            }

            task.Proc = proc;
            long lastDone = 0, prev = -1;
            var lastT = DateTime.UtcNow;
            double spd = 0;

            try
            {
                while (!proc.HasExited)
                {
                    await Task.Delay(400, ct);
                    ct.ThrowIfCancellationRequested();

                    if (!File.Exists(ctrl)) continue;

                    long done, total;
                    try
                    {
                        (done, total) = ReadCtrl(ctrl, totalHint);
                    }
                    catch
                    {
                        continue; // 正在重写控制文件
                    }

                    if (total <= 0) continue;
                    task.Total = total;
                    if (done == prev) continue;
                    prev = done;

                    var now = DateTime.UtcNow;
                    var dt = (now - lastT).TotalSeconds;
                    if (dt > 0.2)
                    {
                        var inst = (done - lastDone) / dt;
                        spd = spd <= 0 ? inst : spd * 0.55 + inst * 0.45;
                        lastDone = done;
                        lastT = now;
                    }

                    task.Status = TaskStatus.Downloading;
                    task.Done = done;
                    task.SpeedBps = spd;
                    task.PushSample(spd);
                }
            }
            catch (OperationCanceledException)
            {
                Kill(proc);
                throw;
            }

            await Task.Delay(300, ct);

            if (!File.Exists(ctrl))
            {
                long size = File.Exists(task.DestPath) ? new FileInfo(task.DestPath).Length : 0;
                if (totalHint > 0 && size < totalHint * 0.999)
                {
                    log($"[FAIL] 只拿到 {Fmt.Human(size)} / {Fmt.Human(totalHint)}，aria2 提前退出");
                    task.Done = size;
                    return false;
                }
                task.Done = size > 0 ? size : task.Done;
                task.Total = task.Total > 0 ? task.Total : size;
                task.SpeedBps = 0;
                task.PushSample(0);
                return true;
            }

            if (attempts >= 8)
            {
                log($"[FAIL] 连续 {attempts} 次未完成，下次运行会续传");
                return false;
            }

            log($"[重试 {attempts}] 连接中断，正在续传 ...");
            await Task.Delay(2000, ct);
        }
    }

    // ==================================================================
    // BT / 磁力：aria2c 原生 BitTorrent 模式（DHT + tracker）
    // ==================================================================
    private static readonly Regex BtProgress = new(
        @"\[#\w+\s+([\d.]+)([KMGT]?i?B)/([\d.]+)([KMGT]?i?B)\((\d+)%\)(?:.*?DL:([\d.]+)([KMGT]?i?B))?",
        RegexOptions.Compiled);

    private static long ToBytes(string num, string unit)
    {
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return 0;
        return unit switch
        {
            "KiB" or "KB" => (long)(v * 1024),
            "MiB" or "MB" => (long)(v * 1048576),
            "GiB" or "GB" => (long)(v * 1073741824),
            "TiB" or "TB" => (long)(v * 1099511627776),
            _ => (long)v
        };
    }

    /// <summary>磁力 / .torrent 下载。aria2 每秒打印一行进度，解析后回填任务。</summary>
    public static async Task<bool> DownloadTorrentAsync(
        DownloadTask task, string source, string saveDir, Action<string> log, CancellationToken ct)
    {
        var aria = Find() ?? throw new InvalidOperationException(
            "找不到 aria2c.exe，请先安装：winget install aria2.aria2");

        Directory.CreateDirectory(saveDir);

        var psi = new ProcessStartInfo(aria)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = saveDir,
        };
        foreach (var a in new[]
                 {
                     "-d", saveDir,
                     "--seed-time=0",              // 下完不做种
                     "--bt-stop-timeout=0",
                     "--summary-interval=1",       // 每秒一行进度
                     "--console-log-level=notice",
                     "--file-allocation=none",
                     "--max-concurrent-downloads=1",
                     "--bt-max-peers=64",
                     "--enable-dht=true",
                     "--dht-listen-port=6881-6999",
                     "--listen-port=6881-6999",
                     "--disable-ipv6=true",
                     "--check-certificate=false",
                     "--bt-save-metadata=true",
                     source
                 }.Concat(CommonArgs()))
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };

        proc.OutputDataReceived += (_, ev) =>
        {
            var line = ev.Data;
            if (string.IsNullOrEmpty(line)) return;
            var m = BtProgress.Match(line);
            if (!m.Success) return;

            long done = ToBytes(m.Groups[1].Value, m.Groups[2].Value);
            long total = ToBytes(m.Groups[3].Value, m.Groups[4].Value);
            double speed = m.Groups[6].Success ? ToBytes(m.Groups[6].Value, m.Groups[7].Value) : 0;

            if (total > 0) task.Total = total;
            task.Done = done;
            if (speed > 0)
            {
                task.SpeedBps = speed;
                task.PushSample(speed);
            }
        };

        proc.ErrorDataReceived += (_, ev) =>
        {
            var line = ev.Data;
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                log("    " + line.Trim());
        };

        task.Status = TaskStatus.Downloading;
        if (!proc.Start()) throw new InvalidOperationException("aria2c 启动失败");
        task.Proc = proc;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 停止时立刻杀进程，不在这里等退出（避免卡 UI）
        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });
        await proc.WaitForExitAsync(ct);

        task.SpeedBps = 0;
        task.PushSample(0);
        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode != 0)
        {
            log($"    aria2c 退出码 {proc.ExitCode}");
            return false;
        }
        return true;
    }

    public static void Kill(Process? p)
    {
        try
        {
            if (p != null && !p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch { }
    }
}
