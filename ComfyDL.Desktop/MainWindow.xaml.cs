using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ComfyDL;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadTask> _tasks = new();
    private readonly ObservableCollection<LogEntry> _logs = new();
    private readonly Dictionary<string, CancellationTokenSource> _cts = new();
    private readonly ICollectionView _view;
    private readonly ListCollectionView _historyView;
    private readonly DispatcherTimer _speedTimer;
    private readonly DispatcherTimer _detectTimer;
    private bool _pumping;
    private string _tab = "all";
    private bool _navBusy;
    private bool _ready;   // XAML 加载完成前，IsChecked 事件会先触发
    private bool _historyDirty;
    private readonly DispatcherTimer _saveTimer;
    private DownloadTask? _btTask;
    private CancellationTokenSource? _btCts;
    private bool _drawerOpen;
    private bool _logOpen;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _logs;

        // 版本号只认 csproj 里的 <Version>，避免界面和程序集版本各写一份
        VersionBadge.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.0.0");

        // 小屏幕上别让窗口超出桌面
        var wa = SystemParameters.WorkArea;
        if (Height > wa.Height - 16) Height = Math.Max(MinHeight, wa.Height - 16);
        if (Width > wa.Width - 16) Width = Math.Max(MinWidth, wa.Width - 16);

        // 恢复上次退出前的任务记录（没有 history.json 时会自动补录本地已有文件）
        foreach (var r in TaskStore.Load())
        {
            var t = new DownloadTask
            {
                Url = r.Url,
                Host = r.Host,
                FileName = r.FileName,
                Folder = r.Folder,
                DestPath = NormalizePath(r.DestPath),
                SizeHint = r.SizeHint,
            };
            t.Total = r.Total;
            t.Done = r.Done;
            bool finished = Enum.TryParse<TaskStatus>(r.Status, out var st) &&
                            st is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Canceled;
            t.Status = finished ? st : TaskStatus.Canceled;
            t.Message = finished ? r.Message : "上次退出时未完成，可继续下载";
            t.PropertyChanged += Task_PropertyChanged;
            _tasks.Add(t);
        }

        _view = CollectionViewSource.GetDefaultView(_tasks);
        _view.Filter = o => o is DownloadTask t && TabMatch(t);

        // v8：队列按 进行中 / 等待 / 已完成 分组，组内再按用户选的规则排序
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DownloadTask.QueueGroup)));
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.GroupOrder),
            ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.AddedAt),
            ListSortDirection.Descending));

        // 状态一变就自动换组，不需要手动 Refresh
        if (_view is ListCollectionView lcv)
        {
            lcv.IsLiveGrouping = true;
            lcv.IsLiveSorting = true;
        }

        TaskList.ItemsSource = _view;

        _historyView = new ListCollectionView(_tasks)
        {
            // 完成 / 失败 / 已停止 都属于历史，不再只显示「已完成」
            Filter = o => o is DownloadTask t && t.IsFinished
        };
        // 按天分组：今天 / 昨天 / 具体日期
        _historyView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DownloadTask.DayLabel)));
        HistoryList.ItemsSource = _historyView;

        _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _speedTimer.Tick += (_, _) => UpdateSpeedCard();
        _speedTimer.Start();

        _detectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _detectTimer.Tick += (_, _) =>
        {
            _detectTimer.Stop();
            DetectNow();
        };

        LoadSettingsIntoUi();
        UpdateLimitPill();
        RefreshCounts();
        UpdateEmptyHint();

        _ready = true;

        _navBusy = true;
        NavTasks.IsChecked = true;
        _navBusy = false;
        ShowView("tasks");
        UpdateMaxIcon();
        StateChanged += (_, _) => UpdateMaxIcon();

        SortCombo.SelectedIndex = 0;
        SortCombo.SelectionChanged += (_, _) => ApplySort();

        Log("极速下载器已就绪（C# / WPF 原生客户端）。");

        InitTray();
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            if (_tray == null) return;
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        };

        // 启动 10 秒后静默检查更新（不打扰首次使用）
        if (App.Settings.AutoCheckUpdate && !string.IsNullOrWhiteSpace(App.Settings.UpdateFeed))
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                CheckUpdateAsync(false);
            };
            t.Start();
        }

        // 引擎缺失时提示并支持一键获取（窗口出来之后再做，才能挂 MessageBox）
        Dispatcher.BeginInvoke(new Action(CheckEngineOnStartup),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Log("粘贴下载链接即可：模型文件自动进 models，其他文件进下载目录。");
        Log("支持 huggingface.co / hf-mirror.com / modelscope.cn / civitai / 任意 http 直链。");

        // 进度每 400ms 变一次，用 2 秒节流写盘；退出时强制落盘
        _tasks.CollectionChanged += (_, _) => _historyDirty = true;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) => FlushHistory();
        _saveTimer.Start();
        if (_tasks.Count > 0) Log($"已恢复 {_tasks.Count} 条下载记录。");

        var args = Environment.GetCommandLineArgs();
        var urls = args.Skip(1).Where(a => !a.StartsWith("-")).ToList();
        if (urls.Count > 0)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                foreach (var u in urls)
                {
                    UrlBox.Text = u;
                    await AddTaskAsync();
                }
            }), DispatcherPriority.Background);
        }
    }

    // ==================================================================
    // 设置
    // ==================================================================

    // ==================================================================
    // v2.3 · 下载行为设置
    // ==================================================================
    private void BehaviorCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var s = App.Settings;

        s.MaxConcurrent = ConcurrentCombo.SelectedIndex switch { 0 => 1, 1 => 2, 2 => 3, _ => 0 };
        s.SpeedLimitMBps = SpeedLimitCombo.SelectedIndex switch
        {
            1 => 1,
            2 => 2,
            3 => 5,
            4 => 10,
            _ => 0
        };
        s.ConflictPolicy = ConflictCombo.SelectedIndex switch
        {
            1 => "overwrite",
            2 => "skip",
            3 => "rename",
            _ => "ask"
        };

        s.Save();
        UpdateLimitPill();
    }

    private void ProxyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        App.Settings.ProxyUrl = (ProxyBox.Text ?? "").Trim();
        App.Settings.Save();
    }

    private void DiskCheckSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.CheckDiskSpace = DiskCheckSwitch.IsChecked == true;
        App.Settings.Save();
    }

    /// <summary>状态条上的限速胶囊：只有限速时才出现</summary>
    private void UpdateLimitPill()
    {
        if (LimitPill == null) return;
        double v = App.Settings.SpeedLimitMBps;
        LimitPill.Visibility = v > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (v > 0 && LimitPillText != null)
            LimitPillText.Text = $"限速 {v:0.#} MB/s";
    }


    // ==================================================================
    // v2.3 · 下载引擎：缺失检测 + 自动获取
    // ==================================================================

    private async void CheckEngineOnStartup()
    {
        try
        {
            if (Aria2.Find() != null) return;

            var r = await AskAsync("缺少下载引擎",
                "没有找到 aria2c.exe。它是多线程分段下载的核心，缺少它无法下载任何文件。\n\n" +
                "现在自动获取吗？（压缩包约 1.8 MB，来自 aria2 官方发布页）",
                "自动获取", "手动安装", "稍后");

            if (r == AskResult.Primary) await InstallEngineAsync();
            else Log("[!] 未安装引擎，下载功能不可用。可手动下载后放到程序目录。");
        }
        catch (Exception ex)
        {
            Log("[错误] 引擎检测失败：" + ex.Message);
        }
    }

    private async Task InstallEngineAsync()
    {
        // 官方只发 ZIP，里面才是 aria2c.exe
        const string url = "https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip";
        var target = Path.Combine(AppContext.BaseDirectory, "aria2c.exe");
        var tmp = Path.Combine(Path.GetTempPath(), "aria2-" + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            Log("正在下载下载引擎 aria2 1.37.0 ...");
            using (var resp = await ComfyNet.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs);
            }

            using (var zip = System.IO.Compression.ZipFile.OpenRead(tmp))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals("aria2c.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidOperationException("压缩包里没有 aria2c.exe");

                // 先解压到临时名再替换，避免目标被占用时留下半个文件
                var staging = target + ".new";
                entry.ExtractToFile(staging, overwrite: true);
                if (File.Exists(target)) File.Delete(target);
                File.Move(staging, target);
            }

            Log("引擎已安装：" + target);
            RefreshAriaInfo();

            Ask("下载引擎已就绪", "现在可以正常下载了。", "好");
        }
        catch (Exception ex)
        {
            Log("[错误] 获取引擎失败：" + ex.Message);
            var r = Ask("自动获取失败",
                ex.Message + "\n\n要打开 aria2 官方发布页手动下载吗？下载后把 aria2c.exe 放到程序所在目录即可。",
                "打开发布页", "关闭");
            if (r == AskResult.Primary)
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/aria2/aria2/releases",
                    UseShellExecute = true
                });
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }


    // ==================================================================
    // v2.4 · 托盘常驻 + 完成通知
    // ==================================================================
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _exiting;

    private void InitTray()
    {
        try
        {
            var exe = Environment.ProcessPath;
            var icon = string.IsNullOrEmpty(exe) ? null : System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon == null) return;

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "极速下载器",
                Visible = true,
            };
            _tray.DoubleClick += (_, _) => ShowFromTray();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
            menu.Items.Add("暂停全部", null, (_, _) => PauseAll());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) =>
            {
                _exiting = true;                    // 绕过关闭询问
                if (_tray != null) _tray.Visible = false;
                Close();
            });
            _tray.ContextMenuStrip = menu;
        }
        catch (Exception ex)
        {
            Log("[!] 托盘图标初始化失败：" + ex.Message);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void PauseAll()
    {
        foreach (var t in _tasks.Where(x => x.IsActive).ToList())
        {
            if (_cts.TryGetValue(t.Id, out var c))
            {
                try { c.Cancel(); } catch { }
            }
        }
        Log("已暂停全部进行中的任务。");
    }

    private void NotifyDone(DownloadTask task)
    {
        try
        {
            _tray?.ShowBalloonTip(4000, "下载完成",
                $"{task.FileName}\n{task.TotalText} · 已归类到 {task.FolderLeaf}",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { }
    }

    // ==================================================================
    // v2.4 · 联网更新检查
    // ==================================================================
    private UpdateInfo? _pendingUpdate;

    private async void CheckUpdateAsync(bool manual)
    {
        if (manual) Log("正在检查更新 ...");

        var info = await UpdateService.CheckAsync(CancellationToken.None);

        if (info == null)
        {
            if (UpdateStatusText != null) UpdateStatusText.Text = "已是最新版本";

            if (manual)
                Ask("检查更新", $"当前已是最新版本（{UpdateService.CurrentText}）。", "知道了");
            return;
        }

        _pendingUpdate = info;
        Log($"发现新版本 {info.Version}（当前 {UpdateService.CurrentText}）");
        if (UpdateStatusText != null) UpdateStatusText.Text = $"发现新版本 {info.Version}";

        if (UpdatePill != null)
        {
            UpdatePill.Visibility = Visibility.Visible;
            UpdatePillText.Text = $"{info.Version} 可用";
        }
    }

    private void OpenUpdatePage()
    {
        var url = _pendingUpdate?.Url;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("[错误] 打开下载页失败：" + ex.Message);
        }
    }

    private void UpdatePill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenUpdatePage();

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => CheckUpdateAsync(true);

    private void AutoUpdateSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AutoCheckUpdate = AutoUpdateSwitch.IsChecked == true;
        App.Settings.Save();
    }



    // ==================================================================
    // 关闭行为：收进托盘 / 询问 / 直接退出
    // ==================================================================
    private void CloseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        App.Settings.CloseAction = CloseCombo.SelectedIndex switch
        {
            0 => "tray",
            2 => "exit",
            _ => "ask"
        };
        App.Settings.Save();
    }


    // ==================================================================
    // 通用确认弹层（替代系统 MessageBox，保持界面风格一致）
    // ==================================================================
    private enum AskResult { Primary, Secondary, Cancel }
    private TaskCompletionSource<AskResult>? _askTcs;

    private Task<AskResult> AskAsync(string title, string body, string primary,
        string? secondary = null, string? cancel = null)
    {
        AskTitle.Text = title;
        AskBody.Text = body;
        AskPrimaryBtn.Content = primary;

        AskSecondBtn.Content = secondary ?? "";
        AskSecondBtn.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;

        AskCancelBtn.Content = cancel ?? "";
        AskCancelBtn.Visibility = cancel == null ? Visibility.Collapsed : Visibility.Visible;

        _askTcs = new TaskCompletionSource<AskResult>();
        AskOverlay.Visibility = Visibility.Visible;
        return _askTcs.Task;
    }

    private void FinishAsk(AskResult r)
    {
        AskOverlay.Visibility = Visibility.Collapsed;
        var tcs = _askTcs;
        _askTcs = null;
        tcs?.TrySetResult(r);
    }

    private void AskPrimary_Click(object sender, RoutedEventArgs e) => FinishAsk(AskResult.Primary);
    private void AskSecond_Click(object sender, RoutedEventArgs e) => FinishAsk(AskResult.Secondary);
    private void AskCancel_Click(object sender, RoutedEventArgs e) => FinishAsk(AskResult.Cancel);
    private void AskCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void AskOverlay_Click(object sender, MouseButtonEventArgs e) => FinishAsk(AskResult.Cancel);

    /// <summary>同步版：给非 async 的调用点用</summary>
    private AskResult Ask(string title, string body, string primary,
        string? secondary = null, string? cancel = null)
    {
        var task = AskAsync(title, body, primary, secondary, cancel);
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return task.Result;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_exiting) return;                       // 托盘菜单点了退出

        var action = App.Settings.CloseAction ?? "ask";

        if (action == "exit") return;               // 直接退出

        if (action == "tray")
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // ask：先拦下来，弹自定义询问框，按用户选择再决定
        e.Cancel = true;
        _ = AskCloseAsync();
    }

    private async Task AskCloseAsync()
    {
        int active = _tasks.Count(t => t.IsActive);
        var body = active > 0
            ? $"还有 {active} 个任务正在下载。\n\n收进托盘会继续下载，直接退出则全部中断。"
            : "要收进托盘继续运行，还是直接退出？\n\n这个询问可以在 设置 → 外观 里改掉。";

        var r = await AskAsync("关闭窗口", body, "收进托盘", "直接退出", "取消");

        if (r == AskResult.Primary) HideToTray();
        else if (r == AskResult.Secondary) { _exiting = true; Close(); }
    }

    private void HideToTray()
    {
        Hide();
        _tray?.ShowBalloonTip(2500, "极速下载器仍在运行",
            "双击托盘图标可以重新打开", System.Windows.Forms.ToolTipIcon.Info);
    }

    // ==================================================================
    // 打赏与项目主页：入口在程序里，内容在仓库里
    // 二维码放在 README 而不是打进 exe —— 换码不用发版，也不用担心图被替换
    // ==================================================================
    private const string RepoUrl = "https://github.com/Mandydavi/Download-Tools";

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

    private void OpenDonate_Click(object sender, RoutedEventArgs e) =>
        DonateOverlay.Visibility = Visibility.Visible;

    private void CloseDonate_Click(object sender, RoutedEventArgs e) =>
        DonateOverlay.Visibility = Visibility.Collapsed;

    /// <summary>点遮罩关闭；点卡片本身不关</summary>
    private void DonateOverlay_Click(object sender, MouseButtonEventArgs e) =>
        DonateOverlay.Visibility = Visibility.Collapsed;

    private void DonateSheet_Click(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("[错误] 打开链接失败：" + ex.Message);
        }
    }

    private void LoadSettingsIntoUi()
    {
        var s = App.Settings;
        ModelsDirBox.Text = s.ModelsDir;
        DownloadDirBox.Text = s.DownloadDir;
        UpdateSidePath();
        RaceSwitch.IsChecked = s.RaceSources;
        VerifySwitch.IsChecked = s.AutoVerify;
        ThemeSwitch.IsChecked = s.DarkMode;
        AskFolderSwitch.IsChecked = s.AskFolderOnAdd;

        // v2.3 下载行为
        ConcurrentCombo.SelectedIndex = s.MaxConcurrent switch { 1 => 0, 2 => 1, 3 => 2, _ => 3 };
        SpeedLimitCombo.SelectedIndex = s.SpeedLimitMBps switch
        {
            <= 0 => 0,
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 3,
            _ => 4
        };
        ProxyBox.Text = s.ProxyUrl ?? "";
        ConflictCombo.SelectedIndex = (s.ConflictPolicy ?? "ask") switch
        {
            "overwrite" => 1,
            "skip" => 2,
            "rename" => 3,
            _ => 0
        };
        DiskCheckSwitch.IsChecked = s.CheckDiskSpace;

        // 索引：0 = 自动，1 = 4，2 = 8，3 = 16，4 = 32
        var conns = new[] { 4, 8, 16, 32 };
        int idx = Array.IndexOf(conns, s.Connections);
        ConnCombo.SelectedIndex = s.AutoConnections ? 0 : (idx >= 0 ? idx + 1 : 3);

        VersionLine.Text = "极速下载器 " + UpdateService.CurrentText;
        AutoUpdateSwitch.IsChecked = s.AutoCheckUpdate;
        CloseCombo.SelectedIndex = (s.CloseAction ?? "ask") switch
        {
            "tray" => 0,
            "exit" => 2,
            _ => 1
        };

        RefreshAriaInfo();
    }

    /// <summary>捕获区显示保存位置：Chip 里是目录名，右侧是完整路径</summary>
    private void UpdateSidePath()
    {
        var s = App.Settings;
        var leaf = LeafName(s.ModelsDir);
        if (TargetChipText != null)
            TargetChipText.Text = leaf.Equals("models", StringComparison.OrdinalIgnoreCase)
                ? "自动归类"
                : "models / " + leaf;
        if (CapTargetText != null)
        {
            CapTargetText.Text = s.ModelsDir;
            CapTargetText.ToolTip = $"模型 → {s.ModelsDir}\n其他 → {s.DownloadDir}";
        }
        if (BtDirText != null)
        {
            var dir = string.IsNullOrWhiteSpace(s.DownloadDir) ? AppSettings.DefaultDownloadDir : s.DownloadDir;
            BtDirText.Text = "保存到 " + dir;
            BtDirText.ToolTip = dir;
        }
    }

    private static string LeafName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "默认目录";
        var t = path.TrimEnd('\\', '/');
        int i = t.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 && i < t.Length - 1 ? t[(i + 1)..] : t;
    }

    /// <summary>把 E:/xxx/models\loras\a.safetensors 这类混合斜杠统一成标准 Windows 路径</summary>
    private static string NormalizePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try { return Path.GetFullPath(p); }
        catch { return p.Replace('/', '\\'); }
    }

    private void AskFolderSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AskFolderOnAdd = AskFolderSwitch.IsChecked == true;
        App.Settings.Save();
        Log(App.Settings.AskFolderOnAdd
            ? "新建任务时会询问保存位置。"
            : "新建任务不再询问，按文件类型自动归类。");
    }

    private static string ShortPath(string path, int keep)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= keep) return path;
        return "…" + path[^keep..];
    }

    private void ApplySettingsFromUi()
    {
        var s = App.Settings;
        if (!string.IsNullOrWhiteSpace(ModelsDirBox.Text)) s.ModelsDir = ModelsDirBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(DownloadDirBox.Text)) s.DownloadDir = DownloadDirBox.Text.Trim();
        s.RaceSources = RaceSwitch.IsChecked == true;
        s.AutoVerify = VerifySwitch.IsChecked == true;
        s.AskFolderOnAdd = AskFolderSwitch.IsChecked == true;
        int ci = ConnCombo.SelectedIndex;
        s.AutoConnections = ci <= 0;              // 0 = 自动
        s.Connections = ci switch { 1 => 4, 2 => 8, 4 => 32, _ => 16 };
        s.Save();

        UpdateSidePath();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        Log("设置已保存。");
    }

    /// <summary>开关类设置：改完立即生效，不再需要按保存</summary>
    private void ToggleSetting_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Settings.RaceSources = RaceSwitch.IsChecked == true;
        App.Settings.AutoVerify = VerifySwitch.IsChecked == true;
        App.Settings.Save();
    }

    private void ConnCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        int ci = ConnCombo.SelectedIndex;
        App.Settings.AutoConnections = ci <= 0;
        App.Settings.Connections = ci switch { 1 => 4, 2 => 8, 4 => 32, _ => 16 };
        App.Settings.Save();
    }

    private void ThemeSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool dark = ThemeSwitch.IsChecked == true;
        ThemeManager.Apply(dark);
        App.Settings.DarkMode = dark;
        App.Settings.Save();
        Log(dark ? "已切换到深色主题。" : "已切换到浅色主题。");
    }

    private void BrowseModelsDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择模型文件的保存目录",
            InitialDirectory = Directory.Exists(App.Settings.ModelsDir)
                ? App.Settings.ModelsDir
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true)
        {
            ModelsDirBox.Text = dlg.FolderName;
            ApplySettingsFromUi();
        }
    }

    private void BrowseDownloadDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择其他文件的保存目录",
            InitialDirectory = Directory.Exists(App.Settings.DownloadDir)
                ? App.Settings.DownloadDir
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true)
        {
            DownloadDirBox.Text = dlg.FolderName;
            ApplySettingsFromUi();
        }
    }

    private void OpenModelsDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(App.Settings.DownloadDir)
                ? App.Settings.ModelsDir
                : App.Settings.DownloadDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("[错误] 打不开目录：" + ex.Message);
        }
    }

    // ==================================================================
    // 下载引擎：位置可自定义 / 可随程序携带
    // ==================================================================
    private void RefreshAriaInfo()
    {
        var exe = Aria2.Find();
        AriaText.Text = exe ?? "未找到 aria2c.exe —— 请先安装：winget install aria2.aria2";
        AriaText.ToolTip = AriaText.Text;
        AriaSourceText.Text = "来源：" + Aria2.Describe(exe);
        AriaOpenBtn.IsEnabled = exe != null;
        AriaResetBtn.IsEnabled = !string.IsNullOrWhiteSpace(App.Settings.Aria2Path);

        bool alreadyPortable = exe != null && string.Equals(
            Path.GetDirectoryName(exe)?.TrimEnd('\\', '/'),
            AppContext.BaseDirectory.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
        AriaPortableBtn.IsEnabled = exe != null && !alreadyPortable;
        AriaPortableBtn.ToolTip = alreadyPortable
            ? "引擎已经在软件目录里了，整个文件夹可以直接拷走"
            : "把引擎放到本程序旁边，之后整个文件夹可拷走使用";
    }

    private void AriaOpen_Click(object sender, RoutedEventArgs e)
    {
        var exe = Aria2.Find();
        if (exe == null) { Log("找不到 aria2c.exe。"); return; }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exe}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex) { Log("[错误] 打不开引擎目录：" + ex.Message); }
    }

    private void AriaCopy_Click(object sender, RoutedEventArgs e)
    {
        var exe = Aria2.Find();
        if (exe == null) return;
        try { Clipboard.SetText(exe); Log("引擎路径已复制到剪贴板。"); }
        catch { }
    }

    private void AriaPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 aria2c.exe",
            Filter = "aria2c.exe|aria2c.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        var current = Aria2.Find();
        if (current != null) dlg.InitialDirectory = Path.GetDirectoryName(current) ?? "";
        if (dlg.ShowDialog(this) != true) return;

        if (!string.Equals(Path.GetFileName(dlg.FileName), "aria2c.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            var ok = Ask("更换引擎",
                "选中的文件不叫 aria2c.exe，确定要用它作为下载引擎吗？", "使用它", "取消");
            if (ok != AskResult.Primary) return;
        }

        App.Settings.Aria2Path = dlg.FileName;
        App.Settings.Save();
        Aria2.ResetCache();
        RefreshAriaInfo();
        Log("下载引擎已改为：" + dlg.FileName);
    }

    private void AriaPortable_Click(object sender, RoutedEventArgs e)
    {
        var exe = Aria2.Find();
        if (exe == null) { Log("找不到可用的 aria2c.exe，无法复制。"); return; }

        var target = Path.Combine(AppContext.BaseDirectory, "aria2c.exe");
        if (string.Equals(exe, target, StringComparison.OrdinalIgnoreCase))
        {
            Log("引擎已经在软件目录里了，整个文件夹可以直接拷走。");
            return;
        }
        if (File.Exists(target))
        {
            var ok = Ask("复制到软件目录",
                "软件目录里已经有 aria2c.exe，要覆盖吗？", "覆盖", "取消");
            if (ok != AskResult.Primary) return;
        }

        try
        {
            File.Copy(exe, target, overwrite: true);
            // 清空自定义路径，改走「程序同目录」优先级，这样换电脑后路径不会失效
            App.Settings.Aria2Path = null;
            App.Settings.Save();
            Aria2.ResetCache();
            RefreshAriaInfo();
            Log("引擎已放到软件目录：" + target);
        }
        catch (Exception ex) { Log("[错误] 复制引擎失败：" + ex.Message); }
    }

    private void AriaReset_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Aria2Path = null;
        App.Settings.Save();
        Aria2.ResetCache();
        RefreshAriaInfo();
        var found = Aria2.Find();
        Log(found == null ? "已恢复自动检测，但没找到 aria2c.exe。" : "已恢复自动检测：" + found);
    }

    // ==================================================================
    // 标题栏 / 窗口
    // ==================================================================
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveInTitleBar(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            ToggleMax();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;

        // 最大化状态下拖动标题栏：先还原，并让窗口停在鼠标下方
        if (WindowState == WindowState.Maximized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var inWindow = e.GetPosition(this);
            var screen = PointToScreen(inWindow);
            var ratio = ActualWidth > 0 ? inWindow.X / ActualWidth : 0.5;

            WindowState = WindowState.Normal;
            Left = screen.X / dpi.DpiScaleX - Width * ratio;
            Top = Math.Max(0, screen.Y / dpi.DpiScaleY - 20);
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经抬起或窗口正在拖动，忽略即可
        }
    }

    private static bool IsInteractiveInTitleBar(DependencyObject? source)
    {
        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBox or ComboBox or CheckBox or RadioButton or ListBox)
                return true;
        }
        return false;
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) => ToggleMax();

    private void ToggleMax() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        foreach (var cts in _cts.Values)
        {
            try { cts.Cancel(); } catch { }
        }
        foreach (var t in _tasks) Aria2.Kill(t.Proc);
        App.Settings.Save();
        FlushHistory(force: true);
        base.OnClosing(e);
    }

    /// <summary>把任务列表写进「历史记录」文件夹，force=true 时忽略脏标记</summary>
    private void FlushHistory(bool force = false)
    {
        if (!_historyDirty && !force) return;
        _historyDirty = false;
        TaskStore.Save(_tasks);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.Text)) return;
        var text = (e.Data.GetData(DataFormats.Text) as string)?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        UrlBox.Text = text;
        await AddTaskAsync();
    }

    // ==================================================================
    // 导航 / 标签
    // ==================================================================
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_navBusy || !_ready) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        ShowView(tag);
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e) => OpenSettings("path");

    private void ShowView(string tag)
    {
        ViewTasks.Visibility = tag == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        ViewHistory.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        ViewFolder.Visibility = tag == "folder" ? Visibility.Visible : Visibility.Collapsed;

        // 详情抽屉只在「队列」页有意义，切到别的页面直接收起
        if (tag != "tasks")
        {
            TaskList.SelectedItem = null;
            CloseDrawer(immediate: true);
        }

        if (tag == "folder") RefreshFolders();
        if (tag == "history") RefreshHistoryStats();
    }

    // ==================================================================
    // 设置模态
    // ==================================================================
    private void OpenSettings(string pane)
    {
        CloseLog();
        SettingsOverlay.Visibility = Visibility.Visible;

        var target = pane switch
        {
            "behavior" => SubNavBehavior,
            "engine" => SubNavEngine,
            "look" => SubNavLook,
            _ => SubNavPath
        };
        target.IsChecked = true;
        ApplySubNav(pane);
    }

    private void CloseSettings() => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void SettingsOverlay_Click(object sender, MouseButtonEventArgs e) => CloseSettings();

    /// <summary>点面板内部不该关掉模态，吃掉这次事件</summary>
    private void SettingsSheet_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void PickTargetFolder_Click(object sender, RoutedEventArgs e) => OpenSettings("path");

    private void SubNav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanePath == null) return;                       // XAML 加载完成前会先触发一次
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        ApplySubNav(tag);
    }

    private void ApplySubNav(string tag)
    {
        if (PanePath == null) return;
        PanePath.Visibility = tag == "path" ? Visibility.Visible : Visibility.Collapsed;
        PaneBehavior.Visibility = tag == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        PaneEngine.Visibility = tag == "engine" ? Visibility.Visible : Visibility.Collapsed;
        PaneLook.Visibility = tag == "look" ? Visibility.Visible : Visibility.Collapsed;
        SheetTitle.Text = tag switch
        {
            "behavior" => "下载行为",
            "engine" => "下载引擎",
            "look" => "外观",
            _ => "保存位置"
        };
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeSwitch.IsChecked = ThemeSwitch.IsChecked != true;
        ThemeSwitch_Click(sender, e);
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.ModelsDir = AppSettings.DefaultModelsDir;
        s.DownloadDir = AppSettings.DefaultDownloadDir;
        s.RaceSources = true;
        s.AutoVerify = true;
        s.AskFolderOnAdd = true;
        s.AutoConnections = true;
        s.Save();
        LoadSettingsIntoUi();
        Log("设置已恢复默认。");
    }

    // ==================================================================
    // 磁力 / BT：不再占独立页面，识别到 magnet 时在捕获区展开
    // ==================================================================
    private void BtPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择种子文件",
            Filter = "种子文件 (*.torrent)|*.torrent|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) UrlBox.Text = dlg.FileName;
    }

    private async void BtStart_Click(object sender, RoutedEventArgs e)
    {
        var src = (UrlBox.Text ?? "").Trim().Trim('"');
        if (src.Length == 0)
        {
            Log("请先粘贴磁力链接，或选择 .torrent 文件。");
            return;
        }

        bool isMagnet = src.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
        if (!isMagnet && !File.Exists(src))
        {
            Log("这不是磁力链接，也找不到该种子文件。");
            return;
        }
        if (_btCts != null)
        {
            Log("已有 BT 任务在进行中，先停止再开始新的。");
            return;
        }

        var dir = string.IsNullOrWhiteSpace(App.Settings.DownloadDir)
            ? AppSettings.DefaultDownloadDir
            : App.Settings.DownloadDir;
        Directory.CreateDirectory(dir);

        var task = new DownloadTask
        {
            Url = src,
            Host = "BT",
            FileName = isMagnet ? MagnetName(src) : Path.GetFileNameWithoutExtension(src),
            Folder = dir,
            DestPath = dir,
            Status = TaskStatus.Connecting,
            Message = "正在连接 DHT / tracker ..."
        };
        _btTask = task;
        BtProgressCard.DataContext = task;
        BtProgressCard.Visibility = Visibility.Visible;
        BtStartBtn.IsEnabled = false;
        BtStopBtn.IsEnabled = true;

        var cts = new CancellationTokenSource();
        _btCts = cts;
        Log($"[BT] 开始：{task.FileName}  ->  {dir}");

        try
        {
            bool ok = await Aria2.DownloadTorrentAsync(task, src, dir, Log, cts.Token);
            task.SpeedBps = 0;
            if (ok)
            {
                task.Status = TaskStatus.Completed;
                task.Message = "";
                Log($"[BT 完成] {dir}");
            }
            else
            {
                task.Status = TaskStatus.Failed;
                task.Message = "未完成，重新开始会续传";
                Log("[BT] 下载未完成");
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = TaskStatus.Canceled;
            task.SpeedBps = 0;
            task.Message = "已停止";
            Log("[BT] 已停止");
        }
        catch (Exception ex)
        {
            task.Status = TaskStatus.Failed;
            task.SpeedBps = 0;
            task.Message = ex.Message;
            Log("[BT 错误] " + ex.Message);
        }
        finally
        {
            _btCts = null;
            BtStartBtn.IsEnabled = true;
            BtStopBtn.IsEnabled = false;
        }
    }

    private void BtStop_Click(object sender, RoutedEventArgs e)
    {
        try { _btCts?.Cancel(); } catch { }
    }

    /// <summary>磁力链接里的显示名：优先 dn 参数，否则用 infohash 前 8 位</summary>
    private static string MagnetName(string magnet)
    {
        var dn = Regex.Match(magnet, @"[?&]dn=([^&]+)");
        if (dn.Success)
        {
            try { return Uri.UnescapeDataString(dn.Groups[1].Value.Replace('+', ' ')); } catch { }
        }
        var ih = Regex.Match(magnet, @"btih:([0-9a-fA-F]{40}|[A-Z2-7]{32})");
        return ih.Success ? "BT_" + ih.Groups[1].Value[..8] : "BT_" + DateTime.Now.ToString("HHmmss");
    }

    private void RightTab_Checked(object sender, RoutedEventArgs e)
    {
        // 详情抽屉已合并为单页，保留空实现兼容旧的 XAML 引用
    }

    // ==================================================================
    // 详情抽屉
    // ==================================================================
    private void OpenDrawer()
    {
        _drawerOpen = true;
        ScrimLayer.Visibility = Visibility.Visible;

        if (DetailPanel.Visibility != Visibility.Visible)
        {
            DetailPanel.Visibility = Visibility.Visible;
            DrawerShift.X = 364;
        }

        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        DrawerShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void CloseDrawer(bool immediate = false)
    {
        _drawerOpen = false;
        ScrimLayer.Visibility = Visibility.Collapsed;
        if (DetailPanel.Visibility != Visibility.Visible) return;

        if (immediate)
        {
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, null);
            DrawerShift.X = 364;
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var anim = new DoubleAnimation(364, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            if (!_drawerOpen) DetailPanel.Visibility = Visibility.Collapsed;
        };
        DrawerShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void ScrimLayer_Click(object sender, MouseButtonEventArgs e)
    {
        TaskList.SelectedItem = null;
        CloseDrawer();
    }

    private void CloseDrawer_Click(object sender, RoutedEventArgs e)
    {
        TaskList.SelectedItem = null;
        CloseDrawer();
    }

    // ==================================================================
    // 日志抽屉
    // ==================================================================
    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logOpen) CloseLog();
        else OpenLog();
    }

    private void OpenLog()
    {
        _logOpen = true;
        if (PanelLog.Visibility != Visibility.Visible)
        {
            PanelLog.Visibility = Visibility.Visible;
            LogShift.Y = 252;
        }

        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        LogShift.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void CloseLog()
    {
        _logOpen = false;
        if (PanelLog.Visibility != Visibility.Visible) return;

        var anim = new DoubleAnimation(252, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            if (!_logOpen) PanelLog.Visibility = Visibility.Collapsed;
        };
        LogShift.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void CloseLog_Click(object sender, RoutedEventArgs e) => CloseLog();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (DonateOverlay.Visibility == Visibility.Visible)
        {
            DonateOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            CloseSettings();
            e.Handled = true;
            return;
        }
        if (_logOpen)
        {
            CloseLog();
            e.Handled = true;
            return;
        }
        if (_drawerOpen)
        {
            TaskList.SelectedItem = null;
            CloseDrawer();
            e.Handled = true;
        }
    }

    // ==================================================================
    // 窗口按钮图标
    // ==================================================================
    private void UpdateMaxIcon()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxIcon.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool TabMatch(DownloadTask t) => _tab switch
    {
        "active" => t.IsActive,
        "done" => t.Status == TaskStatus.Completed,
        "paused" => t.Status is TaskStatus.Canceled or TaskStatus.Failed,
        _ => true
    };

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender == TabActive) _tab = "active";
        else if (sender == TabDone) _tab = "done";
        else if (sender == TabPaused) _tab = "paused";
        else _tab = "all";

        _view.Refresh();
        UpdateEmptyHint();
    }

    private void ApplySort()
    {
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();

            // 分组顺序永远优先，用户的排序规则只在组内生效
            _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.GroupOrder),
                ListSortDirection.Ascending));

            switch (SortCombo.SelectedIndex)
            {
                case 1:
                    _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.FileName),
                        ListSortDirection.Ascending));
                    break;
                case 2:
                    _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.Pct),
                        ListSortDirection.Descending));
                    break;
                default:
                    _view.SortDescriptions.Add(new SortDescription(nameof(DownloadTask.AddedAt),
                        ListSortDirection.Descending));
                    break;
            }
        }
    }

    private void RefreshCounts()
    {
        CountAll.Text = _tasks.Count.ToString();
        CountActive.Text = _tasks.Count(t => t.IsActive).ToString();
        CountDone.Text = _tasks.Count(t => t.Status == TaskStatus.Completed).ToString();
        CountPaused.Text = _tasks.Count(t => t.Status is TaskStatus.Canceled or TaskStatus.Failed).ToString();

        int active = _tasks.Count(t => t.IsActive);
        int waiting = _tasks.Count(t => t.Status == TaskStatus.Queued);
        SbRight.Text = $"{active} 进行中 · {waiting} 等待 · 共 {_tasks.Count} 项";
    }

    /// <summary>历史页顶部的三个统计块</summary>
    private void RefreshHistoryStats()
    {
        var finished = _tasks.Where(t => t.IsFinished).ToList();
        StatCount.Text = finished.Count.ToString();

        long bytes = finished.Where(t => t.Status == TaskStatus.Completed).Sum(t => t.Total);
        StatBytes.Text = Fmt.Human(bytes);

        int ok = finished.Count(t => t.Status == TaskStatus.Completed);
        StatRate.Text = finished.Count > 0 ? ok * 100 / finished.Count + "%" : "--";
        HistorySubText.Text = $"完成、失败、已停止的任务都在这里 · 共 {finished.Count} 条";
    }

    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var finished = _tasks.Where(t => t.IsFinished).ToList();
        if (finished.Count == 0)
        {
            Log("历史里还没有记录，没什么可导出的。");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出下载历史",
            FileName = $"ComfyDL-历史-{DateTime.Now:yyyyMMdd}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("文件名,状态,大小(字节),目标目录,来源,添加时间");
            foreach (var t in finished.OrderByDescending(t => t.AddedAt))
            {
                sb.AppendLine(string.Join(",",
                    Q(t.FileName), Q(t.StatusText), t.Total.ToString(),
                    Q(t.DestPath), Q(t.Url), t.AddedAt.ToString("yyyy-MM-dd HH:mm:ss")));
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
            Log("历史已导出：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            Log("[错误] 导出失败：" + ex.Message);
        }

        static string Q(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
    }

    private void UpdateEmptyHint()
    {
        bool empty = !_tasks.Any(TabMatch);
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================
    // 文件管理
    // ==================================================================
    private void RefreshFolders_Click(object sender, RoutedEventArgs e) => RefreshFolders();

    private void RefreshFolders()
    {
        var root = App.Settings.ModelsDir;
        FolderPathText.Text = root;
        var list = new List<ModelFolder>();

        try
        {
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(".")) continue;
                    int count = 0;
                    long size = 0;
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                size += new FileInfo(f).Length;
                                count++;
                            }
                            catch { }
                        }
                    }
                    catch { }
                    list.Add(new ModelFolder(name, dir, count, size));
                }
            }
        }
        catch (Exception ex)
        {
            Log("[错误] 读取 models 目录失败：" + ex.Message);
        }

        var ordered = list.OrderByDescending(f => f.Size).ToList();
        FolderList.ItemsSource = ordered;
        if (ordered.Count > 0) FolderList.SelectedIndex = 0;
        else LoadLibFiles(null);
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadLibFiles(FolderList.SelectedItem as ModelFolder);

    /// <summary>模型库右栏：选中目录里的文件</summary>
    private void LoadLibFiles(ModelFolder? f)
    {
        var files = new List<ModelFile>();
        if (f != null && Directory.Exists(f.FullPath))
        {
            try
            {
                foreach (var p in Directory.EnumerateFiles(f.FullPath, "*", SearchOption.TopDirectoryOnly))
                {
                    try { files.Add(new ModelFile(Path.GetFileName(p), p, new FileInfo(p).Length)); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("[错误] 读取目录失败：" + ex.Message);
            }
        }

        var ordered = files.OrderByDescending(x => x.Size).ToList();
        LibFileList.ItemsSource = ordered;
        LibTitle.Text = f == null ? "文件" : f.Name + " · " + ordered.Count + " 个文件";
        LibSummary.Text = f == null ? "" : Fmt.Human(ordered.Sum(x => x.Size));
    }

    private void LibFileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibFileList.SelectedItem is not ModelFile f) return;
        if (!RevealInExplorer(f.FullPath)) Log("[提示] 找不到这个文件：" + f.FullPath);
    }

    private void FolderList_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedFolder();

    private void OpenFolderRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModelFolder f)
        {
            OpenFolder(f);
            return;
        }
        OpenSelectedFolder();
    }

    private void OpenSelectedFolder()
    {
        if (FolderList.SelectedItem is not ModelFolder f) return;
        OpenFolder(f);
    }

    private void OpenFolder(ModelFolder f)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{f.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("[错误] 打不开目录：" + ex.Message);
        }
    }

    // ==================================================================
    // 链接识别
    // ==================================================================
    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = UrlBox.Text ?? "";
        UrlHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

        // 磁力链接不是独立页面，识别到才在捕获区展开 BT 面板
        bool magnet = text.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                      || text.TrimEnd().EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        MagStrip.Visibility = magnet ? Visibility.Visible : Visibility.Collapsed;

        _detectTimer.Stop();
        _detectTimer.Start();
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = AddTaskAsync();
        }
    }

    private void DetectNow()
    {
        var u = UrlBox.Text.Trim();
        if (u.Length == 0)
        {
            InfoText.Text = "";
            UpdateSidePath();
            return;
        }

        try
        {
            var info = ComfyNet.Parse(u);
            var src = ComfyNet.SourceFor(info.Host).Name;

            // v8：一行说清「哪个源 · 什么文件 · 会存到哪 · 有几个源可选」
            var parts = new List<string>();
            if (info.Kind == UrlKind.Repo)
            {
                parts.Add($"{src} 仓库");
                parts.Add(info.FullName);
                parts.Add("选择文件后入队");
            }
            else if (info.Kind == UrlKind.File)
            {
                parts.Add(src);
                if (!string.IsNullOrWhiteSpace(info.FileName)) parts.Add(info.FileName);
                parts.Add("→ models\\" + (info.Folder ?? "diffusion_models"));

                int cands = ComfyNet.CandidatesFor(info.Url, info).Count;
                if (cands > 1) parts.Add($"{cands} 个源可选");
            }
            else
            {
                parts.Add("认不出这个链接");
            }

            InfoText.Text = string.Join("  ·  ", parts);

            if (info.Kind == UrlKind.File && !string.IsNullOrWhiteSpace(info.Folder))
                TargetChipText.Text = "models / " + info.Folder;
        }
        catch (Exception ex)
        {
            InfoText.Text = "链接解析失败：" + ex.Message;
        }
    }

    // ==================================================================
    // 新建任务
    // ==================================================================
    private async void AddTask_Click(object sender, RoutedEventArgs e) => await AddTaskAsync();

    private async Task AddTaskAsync()
    {
        var raw = UrlBox.Text.Trim();
        if (raw.Length == 0)
        {
            Log("先粘贴一个下载链接。");
            return;
        }

        UrlInfo info;
        try
        {
            info = ComfyNet.Parse(raw);
        }
        catch (Exception ex)
        {
            Log("[错误] " + ex.Message);
            return;
        }

        if (info.Kind == UrlKind.Unknown)
        {
            Log("[错误] 认不出这个链接，请检查是否复制完整。");
            return;
        }

        if (info.Kind == UrlKind.Repo)
        {
            await AddFromRepoAsync(info);
            return;
        }

        if (info.CivitaiVersionId != null)
        {
            Log("查询 civitai 文件信息 ...");
            var (name, folder) = await ComfyNet.LookupCivitaiAsync(info.CivitaiVersionId, CancellationToken.None);
            if (!string.IsNullOrEmpty(name)) info.FileName = name!;
            if (!string.IsNullOrEmpty(folder)) info.Folder = folder;
        }

        var target = ResolveFolderForNewTask(info);
        if (target == null) return;   // 用户取消了

        Enqueue(info, target);
        UrlBox.Clear();
    }

    /// <summary>
    /// 按文件类型决定最终目录：模型文件进 models（再按类型分子目录），
    /// 其他文件（软件 / 压缩包 / 视频 …）进下载目录。绝对路径则原样使用。
    /// </summary>
    private static string ResolveDir(string fileName, string? folder)
    {
        folder = (folder ?? "").Trim().TrimEnd('\\', '/');
        // 统一规范成反斜杠：配置里可能带 /，explorer 的 /select 认不了混合斜杠路径
        if (folder.Length > 0 && Path.IsPathRooted(folder)) return Path.GetFullPath(folder);

        bool isModel = ComfyFolders.IsModelFile(fileName);
        var root = isModel ? App.Settings.ModelsDir : App.Settings.DownloadDir;
        if (string.IsNullOrWhiteSpace(root)) root = AppSettings.DefaultDownloadDir;

        string dir = !isModel
            ? (folder.Length == 0 ? root : Path.Combine(root, folder))
            : Path.Combine(root, folder.Length == 0 ? "diffusion_models" : folder);
        return Path.GetFullPath(dir);
    }

    /// <summary>
    /// 决定新任务存到哪个目录。返回 null 表示用户取消了添加。
    /// 设置了「每次询问」就弹窗；否则按文件类型自动归类。
    /// </summary>
    private string? ResolveFolderForNewTask(UrlInfo info)
    {
        bool isModel = ComfyFolders.IsModelFile(info.FileName);
        var root = isModel ? App.Settings.ModelsDir : App.Settings.DownloadDir;
        var detected = isModel ? (info.Folder ?? "diffusion_models") : null;

        if (!App.Settings.AskFolderOnAdd) return detected;

        var prefill = detected;
        if (string.IsNullOrWhiteSpace(prefill)) prefill = App.Settings.DefaultFolder;

        var dlg = new FolderPickerWindow(info.FileName, root, prefill, info.Host)
        {
            Owner = this
        };
        if (dlg.ShowDialog() != true)
        {
            Log("已取消：没有选择保存位置。");
            return null;
        }

        App.Settings.DefaultFolder = dlg.Folder;
        if (dlg.SetAsDefault)
        {
            App.Settings.AskFolderOnAdd = false;
            App.Settings.Save();
            AskFolderSwitch.IsChecked = false;
            Log($"已设为默认位置：{dlg.Folder}（以后不再询问）");
        }
        else
        {
            App.Settings.Save();
        }

        return dlg.Folder;
    }


    /// <summary>磁盘空间预检：需要 + 512MB 余量</summary>
    private bool EnsureDiskSpace(DownloadTask task, long need)
    {
        if (!App.Settings.CheckDiskSpace || need <= 0) return true;
        try
        {
            var dir = Path.GetDirectoryName(task.DestPath);
            if (string.IsNullOrEmpty(dir)) return true;
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            long free = drive.AvailableFreeSpace;
            const long reserve = 512L * 1024 * 1024;
            if (free >= need + reserve) return true;

            return Dispatcher.Invoke(() => Ask("磁盘空间不足",
                $"{task.FileName}\n\n需要 {Fmt.Human(need)}，{root} 只剩 {Fmt.Human(free)}。\n\n仍然开始下载吗？",
                "仍然下载", "跳过") == AskResult.Primary);
        }
        catch
        {
            return true;   // 检查本身失败不阻断下载
        }
    }

    /// <summary>同名文件处理：destPath 可能被改成重命名后的路径；返回 false 表示放弃</summary>
    private bool ResolveConflict(ref string destPath, out bool skip)
    {
        skip = false;
        if (!File.Exists(destPath)) return true;

        var policy = App.Settings.ConflictPolicy;
        if (string.IsNullOrWhiteSpace(policy)) policy = "ask";

        if (policy == "ask")
        {
            var r = Ask("文件已存在",
                $"目标位置已经有同名文件：\n{Path.GetFileName(destPath)}",
                "覆盖", "重命名保存", "跳过");
            if (r == AskResult.Cancel) { skip = true; return false; }
            policy = r == AskResult.Primary ? "overwrite" : "rename";
        }

        if (policy == "skip") { skip = true; return false; }

        if (policy == "overwrite")
        {
            try
            {
                File.Delete(destPath);
                var ctrl = destPath + ".aria2";
                if (File.Exists(ctrl)) File.Delete(ctrl);
            }
            catch (Exception ex)
            {
                Log("[!] 覆盖旧文件失败：" + ex.Message);
            }
            return true;
        }

        destPath = UniquePath(destPath);
        Log($"重命名为 {Path.GetFileName(destPath)}");
        return true;
    }

    private static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 999; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
        return path;
    }

    private void Enqueue(UrlInfo info, string? folder)
    {
        var dir = ResolveDir(info.FileName, folder);
        folder = (folder ?? "").Trim().TrimEnd('\\', '/');

        var destPath = NormalizePath(Path.Combine(dir, info.FileName));

        // 同名文件：按设置或询问决定 覆盖 / 重命名 / 跳过
        if (!ResolveConflict(ref destPath, out bool skip) || skip)
        {
            Log("[跳过] 目标位置已有同名文件：" + info.FileName);
            return;
        }

        var task = new DownloadTask
        {
            Url = info.Url,
            Host = info.Host,
            FileName = Path.GetFileName(destPath),
            Folder = folder,
            DestPath = destPath,
            Status = TaskStatus.Queued,
        };

        task.PropertyChanged += Task_PropertyChanged;
        _tasks.Add(task);
        TaskList.SelectedItem = task;

        Log($"已加入队列：{info.FileName}  ->  {dir}");
        RefreshCounts();
        UpdateEmptyHint();

        _ = PumpAsync();
    }

    private async Task AddFromRepoAsync(UrlInfo info)
    {
        Log($"读取仓库文件列表：{info.FullName} @ {info.Rev} ...");
        InfoText.Text = "正在读取文件列表 ...";

        IReadOnlyList<RepoFile> files;
        try
        {
            files = await ComfyNet.ListRepoAsync(info, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log("[错误] " + ex.Message);
            InfoText.Text = "";
            return;
        }

        if (files.Count == 0)
        {
            Log("[错误] 没读到文件列表（仓库可能是私有的，或名字不对）。");
            InfoText.Text = "";
            return;
        }

        var models = files.Where(f => ComfyFolders.IsModelFile(f.Path)).ToList();
        var show = (models.Count > 0 ? models : files.ToList())
            .OrderByDescending(f => f.Size)
            .ToList();

        InfoText.Text = "";
        var picker = new RepoPickerWindow(info.FullName, show) { Owner = this };
        if (picker.ShowDialog() != true) return;

        int n = 0;
        foreach (var rf in picker.SelectedFiles)
        {
            var url = ComfyNet.FileUrlFor(info, rf.Path);
            var sub = rf.Path.Split('/').ToList();
            var folder = ComfyFolders.Detect(sub.Take(Math.Max(0, sub.Count - 1)))
                         ?? ComfyFolders.GuessFromName(rf.Name)
                         ?? "diffusion_models";
            var subInfo = ComfyNet.Parse(url);
            subInfo.FileName = rf.Name;
            Enqueue(subInfo, folder);
            n++;
        }
        Log($"已加入 {n} 个文件到队列。");
        UrlBox.Clear();
    }

    // ==================================================================
    // 队列
    // ==================================================================
    /// <summary>已被调度器取走、但还没结束的任务 Id（避免同一任务被并发取两次）</summary>
    private readonly HashSet<string> _claimed = new();

    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            var running = new List<Task>();

            while (true)
            {
                running.RemoveAll(t => t.IsCompleted);

                // 0 = 不限并发，给一个安全上限
                int limit = App.Settings.MaxConcurrent <= 0 ? 32 : App.Settings.MaxConcurrent;

                if (running.Count >= limit)
                {
                    if (!_tasks.Any(t => t.Status == TaskStatus.Queued && !_claimed.Contains(t.Id))) break;
                    await Task.WhenAny(running);
                    continue;
                }

                var next = _tasks.FirstOrDefault(t => t.Status == TaskStatus.Queued && !_claimed.Contains(t.Id));
                if (next == null)
                {
                    if (running.Count == 0) break;
                    await Task.WhenAny(running);
                    continue;
                }

                _claimed.Add(next.Id);
                running.Add(RunClaimedAsync(next));
            }

            await Task.WhenAll(running);
        }
        finally
        {
            _pumping = false;
        }
    }

    private async Task RunClaimedAsync(DownloadTask task)
    {
        try
        {
            await RunTaskAsync(task);
        }
        finally
        {
            _claimed.Remove(task.Id);
        }
    }

    private async Task RunTaskAsync(DownloadTask task)
    {
        var cts = new CancellationTokenSource();
        _cts[task.Id] = cts;
        var ct = cts.Token;

        try
        {
            var info = ComfyNet.Parse(task.Url);
            var url = task.Url;
            task.ClearSamples();
            task.Done = 0;
            task.SpeedBps = 0;

            if (App.Settings.RaceSources)
            {
                task.Status = TaskStatus.Racing;
                var (best, _) = await ComfyNet.RaceAsync(url, info, Log, ct);
                url = best;
            }

            ct.ThrowIfCancellationRequested();

            var (total, _, _) = await ComfyNet.HeadAsync(url, ct);
            task.Total = total;
            task.SizeHint = total;
            if (total > 0)
                Log($"大小：{Fmt.Human(total)}（{total / 1e9:0.00} GB）");

            if (File.Exists(task.DestPath) && !File.Exists(task.DestPath + ".aria2") &&
                total > 0 && new FileInfo(task.DestPath).Length >= total)
            {
                task.Done = total;
                task.Status = TaskStatus.Completed;
                task.SpeedBps = 0;
                Log("[=] 文件已存在且完整，跳过。");
                return;
            }

            // 磁盘空间预检：不够就别开始，省得下到 99% 才失败
            if (!EnsureDiskSpace(task, total))
            {
                task.Status = TaskStatus.Canceled;
                task.Message = "磁盘空间不足，已跳过";
                Log("[!] 磁盘空间不足，跳过：" + task.FileName);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(task.DestPath)!);

            // 连接数：自动模式先实测 1 / 4 / 16，谁快用谁；手动模式直接用设置值
            int conns = App.Settings.Connections;
            bool autoTuned = false;
            if (App.Settings.AutoConnections)
            {
                task.Status = TaskStatus.Connecting;
                conns = await ComfyNet.PickConnectionsAsync(url, total, App.Settings.Connections, Log, ct);
                autoTuned = true;
            }

            task.Status = TaskStatus.Connecting;
            Log($"开始下载：{task.FileName}（{conns} 连接）");

            bool ok = await Aria2.DownloadAsync(task, url, total, conns, Log, ct, autoTuned);
            if (!ok)
            {
                task.Status = TaskStatus.Failed;
                task.Message = "下载未完成（再次开始会续传）";
                return;
            }

            if (App.Settings.AutoVerify &&
                task.FileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            {
                task.Status = TaskStatus.Verifying;
                var (good, msg) = await Task.Run(() => Safetensors.Verify(task.DestPath), ct);
                Log($"完整性校验：{(good ? "PASS" : "FAIL")}  {msg}");
                if (!good)
                {
                    task.Status = TaskStatus.Failed;
                    task.Message = "文件不完整：" + msg;
                    return;
                }
            }

            if (task.Total > 0) task.Done = task.Total;
            task.SpeedBps = 0;
            task.Status = TaskStatus.Completed;
            Log($"[完成] {task.DestPath}");
        }
        catch (OperationCanceledException)
        {
            Aria2.Kill(task.Proc);
            task.SpeedBps = 0;
            task.Status = TaskStatus.Canceled;
            Log($"[已停止] {task.FileName}（再次开始会续传）");
        }
        catch (Exception ex)
        {
            Aria2.Kill(task.Proc);
            task.SpeedBps = 0;
            task.Status = TaskStatus.Failed;
            task.Message = ex.Message;
            Log("[错误] " + ex.Message);
        }
        finally
        {
            _cts.Remove(task.Id);
            RefreshCounts();
        }
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadTask.Status) or nameof(DownloadTask.Total)
            or nameof(DownloadTask.Done) or nameof(DownloadTask.Message)
            or nameof(DownloadTask.DestPath) or nameof(DownloadTask.Url))
            _historyDirty = true;

        if (e.PropertyName == nameof(DownloadTask.Status) && sender is DownloadTask done
            && done.Status == TaskStatus.Completed)
        {
            NotifyDone(done);
        }

        if (e.PropertyName is nameof(DownloadTask.Status) or nameof(DownloadTask.Total))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _view.Refresh();
                _historyView.Refresh();
                RefreshCounts();
                UpdateEmptyHint();
            }), DispatcherPriority.Background);
        }
    }

    // ==================================================================
    // 任务操作
    // ==================================================================
    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSpeedCard();

        if (!_ready) return;
        if (TaskList.SelectedItem == null) CloseDrawer();
        else OpenDrawer();
    }

    private DownloadTask? Current =>
        _tasks.FirstOrDefault(t => t.Status is TaskStatus.Downloading
            or TaskStatus.Connecting or TaskStatus.Racing or TaskStatus.Verifying)
        ?? TaskList.SelectedItem as DownloadTask
        ?? _tasks.FirstOrDefault(t => t.IsActive);

    private void UpdateSpeedCard()
    {
        var t = Current;
        if (t == null)
        {
            SpeedBig.Text = "0.0";
            SpeedUnit.Text = "MB/s";
            EtaText.Text = "--";
            SpeedChart.Points = null;
            return;
        }

        var bps = t.SpeedBps;
        if (bps >= 1048576)
        {
            SpeedBig.Text = (bps / 1048576.0).ToString("0.0");
            SpeedUnit.Text = "MB/s";
        }
        else if (bps >= 1024)
        {
            SpeedBig.Text = (bps / 1024.0).ToString("0.0");
            SpeedUnit.Text = "KB/s";
        }
        else
        {
            SpeedBig.Text = "0.0";
            SpeedUnit.Text = "MB/s";
        }

        EtaText.Text = t.EtaText.Replace("剩余 ", "");
        SpeedChart.Points = t.Samples;
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;
        if (_cts.TryGetValue(t.Id, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        Aria2.Kill(t.Proc);
        Log($"[取消] {t.FileName}");
    }

    private void ResumeTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;
        if (!t.CanResume) return;
        t.Message = "";
        t.Status = TaskStatus.Queued;
        RefreshCounts();
        _ = PumpAsync();
    }

    private void RemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;

        // 正在下载的任务先确认，已结束的直接删
        if (t.IsActive)
        {
            var ok = Ask("删除任务",
                $"「{t.FileName}」正在下载，删除会中断任务。", "删除", "取消");
            if (ok != AskResult.Primary) return;
        }

        if (_cts.TryGetValue(t.Id, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        Aria2.Kill(t.Proc);
        t.PropertyChanged -= Task_PropertyChanged;
        _tasks.Remove(t);
        RefreshCounts();
        UpdateEmptyHint();
        _view.Refresh();
        _historyView.Refresh();
    }

    private void OpenTaskFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;

        // 历史记录里可能是 E:/xxx/models\loras\a.safetensors 这种混合斜杠。
        // File.Exists 认得，但 explorer /select 只接受规范化路径，否则会跳到错误位置。
        string full = NormalizePath(t.DestPath);

        // 文件被搬走过，就在配置的两个根目录里按文件名找一次
        if (full.Length == 0 || !File.Exists(full))
        {
            var moved = LocateMovedFile(t);
            if (moved != null) full = moved;
        }

        try
        {
            if (File.Exists(full))
            {
                // Shell API 直接让资源管理器定位并选中文件，比 explorer.exe /select 命令行可靠
                // （后者遇到含空格 / 中文 / 混合斜杠的路径经常跳到别处）
                if (!RevealInExplorer(full))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")
                        { UseShellExecute = true });
                Log("已定位文件：" + full);
                return;
            }

            // 绝不 Directory.CreateDirectory：原来那样会凭空造一个空目录，
            // 用户看到的自然不可能是文件所在位置
            var dir = string.IsNullOrEmpty(full) ? null : Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);

            if (!string.IsNullOrEmpty(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    { UseShellExecute = true });
                Log($"文件不在原位置，已打开最近的目录：{dir}");
            }
            else
            {
                Log("找不到文件，也找不到保存目录：" + (t.DestPath ?? "(空)"));
            }
        }
        catch (Exception ex)
        {
            Log("[错误] 打不开目录：" + ex.Message);
        }
    }

    /// <summary>
    /// 用 Shell API 在资源管理器里定位并选中文件。
    /// 返回 false 时调用方会退回 explorer.exe /select 命令行。
    /// </summary>
    private static bool RevealInExplorer(string fullPath)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(fullPath, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return false;
            return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
        }
        catch { return false; }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc,
        out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl,
        IntPtr[]? apidl, uint dwFlags);

    /// <summary>文件不在记录的路径时，在模型目录 / 下载目录里按文件名找一次</summary>
    private static string? LocateMovedFile(DownloadTask t)
    {
        if (string.IsNullOrWhiteSpace(t.FileName)) return null;
        var roots = new[] { App.Settings.ModelsDir, App.Settings.DownloadDir }
            .Where(s => !string.IsNullOrWhiteSpace(s) && Directory.Exists(s))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            try
            {
                var hit = Directory.EnumerateFiles(root, t.FileName,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MaxRecursionDepth = 6,
                        ReturnSpecialDirectories = false
                    }).FirstOrDefault();
                if (hit != null) return Path.GetFullPath(hit);
            }
            catch { }
        }
        return null;
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask t) return;
        try
        {
            Clipboard.SetText(t.Url);
            Log("链接已复制到剪贴板。");
        }
        catch { }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var done = _tasks.Where(t => t.IsFinished).ToList();
        if (done.Count == 0)
        {
            Log("没有可清空的历史记录。");
            return;
        }

        var ok = Ask("清空历史",
            $"将删除 {done.Count} 条历史记录，此操作不可撤销。", "清空", "取消");
        if (ok != AskResult.Primary) return;

        foreach (var t in done)
        {
            t.PropertyChanged -= Task_PropertyChanged;
            _tasks.Remove(t);
        }
        RefreshCounts();
        UpdateEmptyHint();
        _view.Refresh();
        _historyView.Refresh();
        Log($"已清空 {done.Count} 条历史记录。");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        LogEmpty.Visibility = Visibility.Visible;
    }

    // ==================================================================
    // 日志
    // ==================================================================
    private void Log(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Log(text)));
            return;
        }

        if (_logs.Count >= 500) _logs.RemoveAt(0);
        var entry = new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text };
        _logs.Add(entry);
        LogEmpty.Visibility = Visibility.Collapsed;

        // 等布局更新后再滚，保证始终停在最新一条
        Dispatcher.BeginInvoke(new Action(() => LogList.ScrollIntoView(entry)),
            DispatcherPriority.Background);
    }
}

/// <summary>右侧日志面板的一行</summary>
public sealed class LogEntry
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
}
