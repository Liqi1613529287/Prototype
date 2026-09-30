using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Prototype.Hosting;
using Prototype.UI.ViewModels;

namespace Prototype.UI.Views;

//主窗口：照图表清单建标签页，用一个定时器刷当前可见的那一页
//"页"这个概念由装配清单决定，这个文件里没有一处写死图表名或窗口长度
public partial class MainWindow : Window
{
    //单帧超过这个耗时才值得记进日志，避免把日志刷爆
    private const double SlowFrameMs = 150;

    private readonly MainWindowViewModel _viewModel;

    private readonly List<ChartPanel> _panels = new();

    private readonly DispatcherTimer _timer;

    //当前是否停在历史视图上。为 true 时实时定时器不刷
    private bool _historyMode;

    private bool _hasQuery;

    private DateTime _lastFromLocal;
    private DateTime _lastToLocal;

    //查询代数号。每次发起查询加一，回来的旧结果对不上就直接丢掉
    //没有它的话，连点查询或连续切页会出现"后发的结果被先发的覆盖"
    private int _queryGeneration;

    // ---- 诊断用：自己量自己，别再靠猜 ----
    private long _lastTickTimestamp;

    private double _emaTickMs;
    private double _emaBuildMs;
    private double _emaRefreshMs;

    private long _lastSlowLogTimestamp;
    private long _lastHeartbeatTimestamp;
    private int _framesSinceHeartbeat;

    //仅供 XAML 预览器使用，运行时走下面那个由 DI 注入的构造函数
    public MainWindow()
        : this(new MainWindowViewModel(null, null, null))
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // 照图表清单建页。清单里有几张图就建几页——这个文件不认识任何具体图表
        foreach (ChartViewModel chart in _viewModel.Charts)
        {
            var panel = new ChartPanel { DataContext = chart };

            _panels.Add(panel);
            ChartTabs.Items.Add(new TabItem { Header = chart.Title, Content = panel });
        }

        if (ChartTabs.Items.Count > 0)
        {
            ChartTabs.SelectedIndex = 0;
        }

        ChartTabs.SelectionChanged += (_, _) => OnTabChanged();

        // 时间框默认填"试验开始 → 现在"，点一下就能用
        FromBox.Text = _viewModel.RunStartLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ToBox.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        StatusText.Text = $"库 {_viewModel.StoreSummary}";

        QueryButton.Click += (_, _) => RunQuery();
        LiveButton.Click += (_, _) => BackToLive();

        _lastTickTimestamp = Stopwatch.GetTimestamp();

        // 全局只有一个定时器，而且只刷当前可见的那一页
        // 切走的页：消费者照跑、环形缓冲照攒，只是不渲染，切回来数据立刻就在
        // 这是标签页方案能省 CPU 的唯一正确姿势——省的是渲染，不是采集
        // 要是切页时把消费者停掉或清空，切回来就是断档，用户会以为"刚才没录上"
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();

        StartupLog.Write(
            $"MainWindow 就绪：{_panels.Count} 页图表 / "
            + $"{_viewModel.Charts.Sum(c => c.Curves.Count)} 条曲线 / 定时器 10ms（只刷当前页）");
    }

    //当前选中那一页的图表面板
    private ChartPanel? CurrentPanel
        => ChartTabs.SelectedItem is TabItem { Content: ChartPanel panel }
            ? panel
            : _panels.FirstOrDefault();

    private async void OnTabChanged()
    {
        if (_historyMode)
        {
            // 历史模式下切页：把同一个区间套到新这一页上，
            // 免得出现"这页是实时、那页是历史"的精神分裂
            await ApplyHistoryAsync(CurrentPanel);
            return;
        }

        // 实时模式切页：立刻补一帧，不然新页上停的是切走之前的旧画面
        CurrentPanel?.RefreshLive();
    }

    private void RefreshLive()
    {
        if (_historyMode)
        {
            return;
        }

        ChartPanel? panel = CurrentPanel;
        ChartViewModel? chart = panel?.Chart;

        if (panel is null || chart is null)
        {
            return;
        }

        long t0 = Stopwatch.GetTimestamp();

        double tickMs = Stopwatch.GetElapsedTime(_lastTickTimestamp, t0).TotalMilliseconds;
        _lastTickTimestamp = t0;

        var (points, worstLag) = panel.RefreshLive();

        long t2 = Stopwatch.GetTimestamp();

        double frameMs = Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds;
        double buildMs = panel.LastBuildMs;
        double refreshMs = panel.LastRenderMs;

        _emaTickMs = _emaTickMs == 0 ? tickMs : _emaTickMs * 0.9 + tickMs * 0.1;
        _emaBuildMs = _emaBuildMs == 0 ? buildMs : _emaBuildMs * 0.9 + buildMs * 0.1;
        _emaRefreshMs = _emaRefreshMs == 0 ? refreshMs : _emaRefreshMs * 0.9 + refreshMs * 0.1;

        _viewModel.PerfText =
            $"图「{chart.Title}」帧 {_emaTickMs:F1}ms = 取数 {_emaBuildMs:F2} + 渲染 {_emaRefreshMs:F2}ms"
            + $" · 绘制 {points} 点（{chart.Curves.Count} 条） · {chart.WindowText}"
            + $" · 数据滞后 {worstLag:F2}s";

        // 慢帧记进日志（每秒最多一条），下次"卡顿"就有原始证据，不用再靠人眼描述
        if (frameMs > SlowFrameMs
            && Stopwatch.GetElapsedTime(_lastSlowLogTimestamp, t2).TotalSeconds >= 1)
        {
            _lastSlowLogTimestamp = t2;

            StartupLog.Write(
                $"★慢帧 {frameMs:F0}ms（图「{chart.Title}」帧间隔 {tickMs:F0} · 取数 {buildMs:F1} · 渲染 {refreshMs:F1}）"
                + $"绘制 {points} 点，数据滞后 {worstLag:F2}s");
        }

        // 每 2 秒留一条心跳：证明"界面在刷"，也让帧率有据可查
        _framesSinceHeartbeat++;

        if (Stopwatch.GetElapsedTime(_lastHeartbeatTimestamp, t2).TotalSeconds >= 2)
        {
            double seconds = Stopwatch.GetElapsedTime(_lastHeartbeatTimestamp, t2).TotalSeconds;

            if (_lastHeartbeatTimestamp != 0)
            {
                StartupLog.Write(
                    $"心跳 {_framesSinceHeartbeat} 帧/{seconds:F1}s = {_framesSinceHeartbeat / seconds:F1} fps"
                    + $"（图「{chart.Title}」帧间隔 {_emaTickMs:F1} · 取数 {_emaBuildMs:F2} · 渲染 {_emaRefreshMs:F2}"
                    + $" · 滞后 {worstLag:F2}s · {_viewModel.PipelineText}）");
            }

            _framesSinceHeartbeat = 0;
            _lastHeartbeatTimestamp = t2;
        }
    }

    //查历史：区间以"试验开始那天"为基准，输入 HH:mm:ss。作用于当前这一页
    //读库丢到后台线程，贴图回 UI 线程——查询期间界面不冻
    private async void RunQuery()
    {
        if (!TryParseTime(FromBox.Text, out TimeSpan from) || !TryParseTime(ToBox.Text, out TimeSpan to))
        {
            StatusText.Text = "时间格式应为 HH:mm:ss";
            return;
        }

        DateTime date = _viewModel.RunStartLocal.Date;
        DateTime fromLocal = date + from;
        DateTime toLocal = date + to;

        if (toLocal <= fromLocal)
        {
            StatusText.Text = "结束时间必须晚于起始时间";
            return;
        }

        _historyMode = true;
        _hasQuery = true;
        _lastFromLocal = fromLocal;
        _lastToLocal = toLocal;

        _timer.Stop();

        await ApplyHistoryAsync(CurrentPanel);
    }

    //把上一次查询的区间套到某一页上
    private async Task ApplyHistoryAsync(ChartPanel? panel)
    {
        if (!_hasQuery || panel?.Chart is null)
        {
            return;
        }

        string title = panel.Chart.Title;

        // 在 UI 线程上抓一份曲线模型快照：后台线程不该摸面板内部那个装配期建起来的列表
        CurveViewModel[] models = panel.SnapshotCurves();

        int generation = ++_queryGeneration;

        // 把区间抓成局部变量再交给后台：不要在后台线程上读字段，免得和 UI 线程写撞
        DateTime fromLocal = _lastFromLocal;
        DateTime toLocal = _lastToLocal;

        StatusText.Text = $"查询中…「{title}」{fromLocal:HH:mm:ss} → {toLocal:HH:mm:ss}";

        var sw = Stopwatch.StartNew();

        ChartPanel.HistorySeries[] series;

        try
        {
            series = await Task.Run(
                () => ChartPanel.ReadHistory(models, fromLocal, toLocal, ChartPanel.HistoryCapacity));
        }
        catch (Exception ex)
        {
            // async void 会把异常直接吞掉：定时器已经停了、_historyMode 还是 true，
            // 界面就永远停在"查询中…"且不再重绘——看上去就是"图都不画了"。
            // 所以这里必须自己接住、自己说清楚，并且把实时恢复回去
            StartupLog.Write($"历史查询「{title}」失败：{ex}");

            if (generation == _queryGeneration)
            {
                BackToLive();
                StatusText.Text = $"历史查询失败：{ex.Message}（已切回实时）";
            }

            return;
        }

        sw.Stop();

        // 期间又发起了查询（连点/连续切页），或已经切回实时：这次的结果作废
        if (generation != _queryGeneration || !_historyMode)
        {
            return;
        }

        var (curves, points, spanSeconds) = panel.ApplyHistory(series);

        if (curves == 0)
        {
            StatusText.Text = $"该区间在「{title}」上没有数据（先跑一会儿再查）";
            return;
        }

        StartupLog.Write(
            $"历史查询「{title}」：{curves} 条曲线 / {points} 点，覆盖 {spanSeconds:F2}s，"
            + $"读取耗时 {sw.ElapsedMilliseconds} ms");

        StatusText.Text =
            $"历史「{title}」{curves} 条 / {points} 点 · {fromLocal:HH:mm:ss} → {toLocal:HH:mm:ss}"
            + $" · 读取 {sw.ElapsedMilliseconds} ms";
    }

    private void BackToLive()
    {
        _historyMode = false;

        // 在途的历史查询结果作废：不然它回来时会覆盖掉刚切回来的实时图
        _queryGeneration++;

        // 每一页都切回实时显示，不只是当前页——否则切到别的页会看到上一次的历史残留
        foreach (ChartPanel panel in _panels)
        {
            panel.BackToLive();
        }

        _lastTickTimestamp = Stopwatch.GetTimestamp();
        _emaTickMs = 0;

        CurrentPanel?.RefreshLive();
        _timer.Start();

        StatusText.Text = $"实时 · 库 {_viewModel.StoreSummary}";
    }

    private static bool TryParseTime(string? text, out TimeSpan value)
        => TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value)
           && value >= TimeSpan.Zero
           && value < TimeSpan.FromDays(1);
}
