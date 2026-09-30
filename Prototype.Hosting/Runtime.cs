using System.Diagnostics;
using Prototype.Core;
using Prototype.Devices.DataSources;
using Prototype.Devices.Device.Abstractions;
using Prototype.Storage;

namespace Prototype.Hosting;

//运行时：读两份清单（ChannelSpec 通道 / ChartSpec 图表），把整条管线接起来
//装配两遍：
//  第一遍 · 每条通道：源队列 → 分发器 →（存储 / 附加消费者）
//  第二遍 · 每张图  ：图表 × 它订阅的通道，交叉处建一条私有队列 → 一个 ChartConsumer
//队列条数 = 订阅线的条数，不是"通道数 × 图表数"
//加一页图表 = 往 BuildChartPlan() 加一条，界面和历史查询自动跟上
public class Runtime
{
    //装配清单没指定颜色时按这个顺序轮转分配
    private static readonly string[] Palette =
    [
        "#0F6E56", "#D64545", "#3B7DD8", "#C2703A", "#7A5AF8", "#2C9E9E",
    ];
    //装配清单没给历史颜色时的兜底色（赭色）
    private const string FallbackHistoryColor = "#C2703A";
    //等待消费链收尾的上限。超过说明还有消费者在写库，强行 Dispose 会与它抢 ZoneTree 的锁
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(10);
    //落盘并释放的上限。超时就放弃等待——WAL 已兜住数据，而"进程永远不退"严重得多
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(20);

    //一条通道运行期的装配结果
    //图表支路要在第二遍里往 Targets 上补队列，链路读数要等两遍都走完才能定稿
    private sealed class ChannelRuntime
    {
        public required ChannelSpec Spec { get; init; }
        public required DataChannel<float> Source { get; init; }
        public required DataDistributionService<float> Distributor { get; init; }
        //这条通道的默认曲线色（清单指定，或按调色板轮转）。图表可以覆盖它
        public required string DefaultColor { get; init; }
        //所有下游队列，带标签，供链路读数显示
        public List<PipelineStats.TargetReading> Targets { get; } = [];
    }

    private readonly Dictionary<ChannelKey, ChannelRuntime> _byKey = [];

    //还没实例化的采集源：装配时只记下"哪条 spec 配哪条源队列"，真正 new 出来放在 Start()
    private readonly List<(ChannelSpec Spec, DataChannel<float> Channel)> _sourceSlots = [];
    private readonly List<IDataSource<float>> _sources = [];
    private readonly List<DataDistributionService<float>> _distributors = [];
    private readonly List<IDataConsumer<float>> _consumers = [];
    private readonly List<Task> _tasks = [];
    private readonly CancellationTokenSource _cancellation = new();
    //本次运行的统一时基。装配期是 0，Start() 那一刻才定
    private long _epochNs;
    private bool _stopped;
    //通道清单本身，也暴露出去——界面靠它知道"这次跑的是哪几条通道"
    public IReadOnlyList<ChannelSpec> Channels { get; }
    //图表清单。装配几条，界面上就有几个标签页
    public IReadOnlyList<ChartSpec> ChartPlan { get; }
    //所有图表消费者，一张图一个（不是一条通道一个）
    //界面遍历它建标签页，所以装配几张就画几张，界面代码里没有一处写死图表名
    public IReadOnlyList<ChartConsumer> Charts { get; }
    //本次试验会话。UI 靠它查历史
    public TestRunContext Run { get; }
    //每条通道一份链路读数（源队列 + 该通道全部下游队列）
    public IReadOnlyList<PipelineStats> Stats { get; }

    public Runtime(StorageOptions options) : this(options, null, null) { }

    //带清单注入的构造函数。两份清单传 null 就用 BuildPlan() / BuildChartPlan() 的默认清单
    //开口子的原因：装配逻辑（笛卡尔积 + 校验）是这个类真正容易错的部分，
    //能注入清单之后，无头探针就能拿故意写错的清单去验"校验到底拦不拦得住"
    public Runtime(StorageOptions options, IReadOnlyList<ChannelSpec>? channelPlan, IReadOnlyList<ChartSpec>? chartPlan)
    {
        // 日志要尽早起来，后面每一步都往里面写字
        StartupLog.Init(options.RootDirectory);
        // 一次试验 = 一个会话目录 + 每曲线一个 ZoneTree 库
        Run = new TestRunContext(options);
        Channels = channelPlan ?? BuildPlan();
        ChartPlan = chartPlan ?? BuildChartPlan();
        // 先把两份清单查个底朝天，再动任何资源
        // 清单写错要在这里一次报完，而不是"运行到一半某条曲线不出现"
        Validate();

        // ---------- 第一遍：每条通道 ----------
        for (int i = 0; i < Channels.Count; i++)
        {
            ChannelSpec spec = Channels[i];

            // 源队列：采集源往这里塞点，分发器从这里读
            var sourceChannel = new DataChannel<float>(
                spec.DeviceId, spec.ChannelId, spec.Name, spec.Unit, spec.SampleRate);

            var distributor = new DataDistributionService<float>(sourceChannel);

            var channel = new ChannelRuntime
            {
                Spec = spec,
                Source = sourceChannel,
                Distributor = distributor,
                DefaultColor = spec.ColorHex ?? Palette[i % Palette.Length],
            };

            // 存储支路。一曲线一库，库名由通道元数据拼出来，落盘抽稀倍数来自配置
            // 分发器给每个消费者一条私有队列，所以某一路消费慢只填满自己的队列
            if (spec.Store)
            {
                DataChannel<float> storageChannel = NewPrivateQueue(spec);
                distributor.AddTarget(storageChannel);

                ZoneTreeCurveStore store = Run.GetCurveStore(
                    spec.DeviceId, spec.ChannelId, spec.Name, spec.Unit);

                _consumers.Add(new StorageConsumer(storageChannel, new ZoneTreeStorage(store, options.StoreDivisor)));
                channel.Targets.Add(new PipelineStats.TargetReading("存储", storageChannel));
            }
            
            // 附加消费者：报警 / 导出 / 转发……加这类东西只改清单，Runtime 一个字不动
            for (int e = 0; e < spec.ExtraConsumers.Count; e++)
            {
                DataChannel<float> extraChannel = NewPrivateQueue(spec);
                distributor.AddTarget(extraChannel);

                _consumers.Add(spec.ExtraConsumers[e](BuildContext(spec, extraChannel, options)));
                channel.Targets.Add(new PipelineStats.TargetReading($"附加{e + 1}", extraChannel));
            }

            _byKey[spec.Key] = channel;
            _distributors.Add(distributor);
            _sourceSlots.Add((spec, sourceChannel));
        }

        // ---------- 第二遍：每张图 ----------
        // 「图表 × 订阅」的笛卡尔积。交叉处各建一条私有队列——
        // 队列条数 = 订阅线条数。同一条通道被两张图订阅，就有两条互相独立的队列：
        // 细节图刷不过来只填满细节图那条，总览图一点不受影响
        var charts = new List<ChartConsumer>(ChartPlan.Count);

        foreach (ChartSpec chartSpec in ChartPlan)
        {
            var subscriptions = new List<ChartSubscription>(chartSpec.Subscriptions.Count);

            foreach (ChannelKey key in chartSpec.Subscriptions)
            {
                ChannelRuntime channel = _byKey[key];

                DataChannel<float> chartChannel = NewPrivateQueue(channel.Spec);
                channel.Distributor.AddTarget(chartChannel);

                string color = chartSpec.ColorOverrides.TryGetValue(key, out string? overridden)
                    ? overridden
                    : channel.DefaultColor;

                subscriptions.Add(new ChartSubscription(chartChannel, new ChartCurve
                {
                    Key = key,
                    DeviceId = channel.Spec.DeviceId,
                    ChannelId = channel.Spec.ChannelId,
                    Name = channel.Spec.Name,
                    Unit = channel.Spec.Unit,
                    ColorHex = color,
                    HistoryColorHex = channel.Spec.HistoryColorHex ?? FallbackHistoryColor,
                }));

                channel.Targets.Add(new PipelineStats.TargetReading($"图·{chartSpec.Title}", chartChannel));
            }

            var consumer = new ChartConsumer(chartSpec, subscriptions);

            charts.Add(consumer);
            _consumers.Add(consumer);
        }

        Charts = charts;

        // 两遍都走完了，链路读数才能定稿（它要看到全部下游队列）
        var stats = new List<PipelineStats>(Channels.Count);

        foreach (ChannelSpec spec in Channels)
        {
            ChannelRuntime channel = _byKey[spec.Key];

            stats.Add(new PipelineStats(spec.Key, spec.Name, channel.Source, channel.Distributor, channel.Targets));
        }

        Stats = stats;

        int chartQueues = charts.Sum(c => c.Queues.Count);

        StartupLog.Write(
            $"Runtime 装配完成：{Channels.Count} 条通道 / {ChartPlan.Count} 张图（{chartQueues} 条图表队列） / "
            + $"{_consumers.Count} 个消费者 / {Run.Curves.Count} 个库");
    }

    //通道清单。要加一条数据管路，只改这里
    //注意这里不再有"要不要画"：一条通道画不画、画在哪张图上，由图表清单决定
    private static List<ChannelSpec> BuildPlan() =>
    [
        new ChannelSpec
        {
            DeviceId = "Net05",
            ChannelId = "2",
            Name = "PT",
            Unit = "bar",
            SampleRate = 25_600,
            ColorHex = "#0F6E56",
            HistoryColorHex = "#C2703A",
            SourceFactory = (channel, epochNs) => new MockSource(channel, 25_600, 10.0, 2.0, 0.0, epochNs),
        },
        new ChannelSpec
        {
            DeviceId = "Net05",
            ChannelId = "3",
            Name = "温度信息",
            Unit = "°C",
            SampleRate = 25_600,
            ColorHex = "#D64545",
            HistoryColorHex = "#8B3A3A",
            SourceFactory = (channel, epochNs) => new MockSource(channel, 25_600, 5.0, 4.0, 0.0, epochNs),
        },
        new ChannelSpec
        {
            DeviceId = "Net05",
            ChannelId = "15",
            Name = "温度信息",
            Unit = "°C",
            SampleRate = 25_600,
            ColorHex = "#000000",
            HistoryColorHex = "#8B3A3A",
            SourceFactory = (channel, epochNs) => new MockSource(channel, 25_60, 7.0, 8.0, 0.0, epochNs),
        },
    ];

    //图表清单。要加一页图表，只改这里——界面、历史查询、链路读数全自动跟上
    //每张图自己决定：看哪几条通道、窗口多长、最多画多少点、Y 量程多少、颜色要不要覆盖
    private static List<ChartSpec> BuildChartPlan() =>
    [
        // 总览：两条曲线同屏，30 秒窗口，用来看全局和对比相位
        new ChartSpec
        {
            Title = "总览 30s",
            Subscriptions =
            [
                new ChannelKey("Net05", "2"),
                new ChannelKey("Net05", "3"),
                new ChannelKey("Net05", "15"),
            ],
            WindowSeconds = 30,
            MaxPlotPoints = 4000,
            YMin = -12,
            YMax = 12,
        },

        // 压力细节：窗口缩到 3 秒，同一周期看一个完整波形
        // 颜色覆盖成蓝色——通道定默认色、图表可覆盖：PT 在总览里是墨绿，在这里是蓝的
        new ChartSpec
        {
            Title = "压力细节 3s",
            Subscriptions = [
                new ChannelKey("Net05", "2"),
                new ChannelKey("Net05", "3"),
            ],
            WindowSeconds = 3,
            MaxPlotPoints = 2000,
            YMin = -12,
            YMax = 12,
            ColorOverrides = new Dictionary<ChannelKey, string>
            {
                [new ChannelKey("Net05", "2")] = "#3B7DD8",
            },
        },

        // 温度细节：窗口 1 秒、Y 量程收到 ±6（温度幅值只有 5）
        // "同一条通道被两张图订阅"就是这里——温度既在总览里也在这里，两条链路各有各的队列
        new ChartSpec
        {
            Title = "温度细节 1s",
            Subscriptions = [new ChannelKey("Net05", "3")],
            WindowSeconds = 1,
            MaxPlotPoints = 2000,
            YMin = -6,
            YMax = 6,
        },
        new ChartSpec
        {
            Title = "总曲线 100s",
            Subscriptions =
            [
                new ChannelKey("Net05", "2"),
                new ChannelKey("Net05", "3"),
                new ChannelKey("Net05", "15"),
            ],
            WindowSeconds = 100,
            MaxPlotPoints = 2000,
            YMin = -12,
            YMax = 12,
        },
    ];

    private static DataChannel<float> NewPrivateQueue(ChannelSpec spec)
        => new(spec.DeviceId, spec.ChannelId, spec.Name, spec.Unit, spec.SampleRate);

    private ConsumerContext BuildContext(ChannelSpec spec, DataChannel<float> channel, StorageOptions options)
        => new()
        {
            Key = spec.Key,
            DeviceId = spec.DeviceId,
            ChannelId = spec.ChannelId,
            Name = spec.Name,
            Unit = spec.Unit,
            SampleRate = spec.SampleRate,
            Channel = channel,
            Run = Run,
            Storage = options,
            EpochNs = () => Interlocked.Read(ref _epochNs),
        };

    //装配期校验：把两份清单所有能静态查出的矛盾一次报完
    //非要有这一步：以前清单写错的后果是"某条曲线静默不出现"，得靠肉眼在图上找缺了谁
    //在分配任何资源之前调用：库目录都建好了才报错，既浪费又不干净
    //显示名（Name）故意不查重：它是展示值，两个测点同名是正常的，区分靠 ChannelKey
    //要盯的是拼完之后的库名唯不唯一——那是下面 storeNames 那条在做的事
    private void Validate()
    {
        var problems = new List<string>();
        var channelKeys = new HashSet<ChannelKey>();
        var storeNames = new Dictionary<string, ChannelKey>(StringComparer.Ordinal);

        foreach (ChannelSpec spec in Channels)
        {
            if (!channelKeys.Add(spec.Key))
            {
                problems.Add($"通道身份重复：{spec.Key}。设备号+通道号是通道的唯一身份，" + "重复了会让两条通道共用同一个库和同一条曲线身份。");
            }
            if (!storeNames.TryAdd(spec.StoreName, spec.Key))
            {
                problems.Add($"库名冲突：{spec.Key} 与 {storeNames[spec.StoreName]} 都会生成目录 " + $"'{spec.StoreName}'（命名会替换非法字符，不同的名字可能替换成同一个）。");
            }
            if (spec.SampleRate <= 0)
            {
                problems.Add($"通道 {spec.Key} 的采样率非法：{spec.SampleRate}。" + "抽稀倍数和积压折算都以它为分母。");
            }
            if (spec.SourceFactory is null)
            {
                problems.Add($"通道 {spec.Key} 没配采集源工厂。");
            }
        }
        
        if (Channels.Count == 0)
        {
            problems.Add("通道清单是空的。");
        }
        var chartTitles = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChartSpec chart in ChartPlan)
        {
            string title = string.IsNullOrWhiteSpace(chart.Title) ? "(无标题)" : chart.Title;
            if (!chartTitles.Add(title))
            {
                problems.Add($"图表标题重复：'{title}'。标签页会重名，链路读数里也分不清是哪张图的队列。");
            }
            if (chart.WindowSeconds <= 0)
            {
                problems.Add($"图表「{title}」的窗口不是正数：{chart.WindowSeconds}。");
            }
            if (chart.MaxPlotPoints < 1)
            {
                problems.Add($"图表「{title}」的点数上限小于 1：{chart.MaxPlotPoints}。");
            }
            if (chart.YMax <= chart.YMin)
            {
                problems.Add($"图表「{title}」的 Y 量程颠倒：{chart.YMin} → {chart.YMax}。");
            }
            if (chart.Subscriptions.Count == 0)
            {
                problems.Add($"图表「{title}」没有订阅任何通道，画不出来。");
            }
            var seen = new HashSet<ChannelKey>();
            foreach (ChannelKey key in chart.Subscriptions)
            {
                if (!channelKeys.Contains(key))
                {
                    problems.Add($"图表「{title}」订阅了不存在的通道 {key}。" + $"当前通道清单里只有：{string.Join("、", channelKeys)}。");
                }
                if (!seen.Add(key))
                {
                    problems.Add($"图表「{title}」重复订阅了 {key}。同一条曲线在一张图上画两遍没有意义。");
                }
            }
            
            foreach (ChannelKey key in chart.ColorOverrides.Keys)
            {
                if (!channelKeys.Contains(key))
                {
                    problems.Add($"图表「{title}」的颜色覆盖指向不存在的通道 {key}。");
                }
            }
        }

        if (problems.Count == 0)
        {
            StartupLog.Write($"装配校验通过：{Channels.Count} 条通道 / {ChartPlan.Count} 张图 / " + $"{ChartPlan.Sum(c => c.Subscriptions.Count)} 条订阅线");
            return;
        }
        foreach (string problem in problems)
        {
            StartupLog.Write($"★装配校验不通过：{problem}");
        }
        throw new InvalidOperationException($"装配清单不合法，共 {problems.Count} 处：" + Environment.NewLine+ string.Join(Environment.NewLine, problems.Select(p => "  · " + p)));
    }

    public void Start()
    {
        // 本次运行的统一时基：所有通道共用，通道间的相位才有可比性
        // 必须取在这里而不是装配时：采样点时间戳是 base + t 算出来的，base 就是"第一条点的时刻"
        // 取在构造期的话，从构造到真正开始采集之间隔着建库开销（~170ms），
        // 第一条点的时间戳就凭空早那么多，曲线右端永远落后同一截，"数据滞后"常年显示 0.2s
        Interlocked.Exchange(ref _epochNs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);
        // 源、分发器、消费者统一收进一个任务列表——Stop 时一起等，没有谁能被漏掉
        foreach ((ChannelSpec spec, DataChannel<float> channel) in _sourceSlots)
        {
            IDataSource<float> source = spec.SourceFactory(channel, Interlocked.Read(ref _epochNs));
            _sources.Add(source);
            _tasks.Add(source.RunAsync(_cancellation.Token));
        }
        foreach (DataDistributionService<float> distributor in _distributors)
        {
            _tasks.Add(distributor.Start(_cancellation.Token));
        }
        foreach (IDataConsumer<float> consumer in _consumers)
        {
            _tasks.Add(consumer.Start(_cancellation.Token));
        }
        StartupLog.Write($"Runtime.Start 时基 = {Interlocked.Read(ref _epochNs)}，已启动 {_tasks.Count} 个任务，" + $"运行目录 = {Run.RunDirectory}");
    }
    
    //停采集 → 等消费链把队列里的点写完 → 逐库落盘并释放
    //顺序不能反：先落盘再等消费者，等于把还在队列里的数据判死刑
    public void Stop()
    {
        if (_stopped)
        {
            StartupLog.Write("Runtime.Stop 重复调用，忽略");
            return;
        }

        _stopped = true;

        StartupLog.Write("Runtime.Stop 开始：取消令牌");

        var sw = Stopwatch.StartNew();

        _cancellation.Cancel();

        Task[] tasks = _tasks.ToArray();

        bool drained;

        try
        {
            // 超时的返回值必须看。忽略它的话，超时后仍去 Dispose，
            // 而消费者还在往同一个库里 Upsert——一边写一边释放，ZoneTree 内部互锁
            drained = Task.WaitAll(tasks, StopDrainTimeout);
        }
        catch (Exception ex)
        {
            drained = false;
            StartupLog.Write($"Runtime.Stop 等待退出时出错: {ex.GetType().Name}: {ex.Message}");
        }

        StartupLog.Write(
            $"Runtime.Stop 消费链等待完成，结果 = {(drained ? "全部结束" : "★超时未结束")}（{sw.ElapsedMilliseconds} ms）");

        if (!drained)
        {
            int alive = tasks.Count(t => !t.IsCompleted);

            StartupLog.Write(
                $"Runtime.Stop ★跳过落盘与释放：仍有 {alive} 个任务在跑。"
                + "强行 Dispose 会与写入争 ZoneTree 的锁，宁可交给 WAL 兜底。");

            // 元数据还是要写的：run.json 是以后重建索引的唯一线索
            try
            {
                Run.SaveMetadata();
            }
            catch (Exception ex)
            {
                StartupLog.Write($"Runtime.Stop 写 run.json 失败: {ex.Message}");
            }

            StartupLog.Write("Runtime.Stop 结束（未落盘）");
            return;
        }

        sw.Restart();

        bool released = TryReleaseRun(ReleaseTimeout);

        StartupLog.Write(released
            ? $"Runtime.Stop 落盘并释放完成（{sw.ElapsedMilliseconds} ms）"
            : $"Runtime.Stop ★落盘并释放超时（{sw.ElapsedMilliseconds} ms），放弃等待，交给 WAL 兜底");

        StartupLog.Write("Runtime.Stop 结束");
    }

    //用有时限的后台任务去跑 Run.Dispose()
    //必须有时限：ZoneTree 的维护器在释放时会 Join 后台合并线程，这一步实测会在真实 GUI 进程里永久阻塞
    //（托管栈实拍 2026-09-22：Stop → TestRunContext.Dispose → ZoneTreeCurveStore.Flush
    //  → ZoneTreeMaintainer.WaitForBackgroundThreads → Task.Wait）
    //卡住的表现就是"关窗后进程还在运行、bin 被锁"
    //Task.Run 跑在线程池上，线程池线程是后台线程，不阻止进程退出，所以超时也能让进程干净走掉
    private bool TryReleaseRun(TimeSpan timeout)
    {
        try
        {
            Task task = Task.Run(() =>
            {
                try
                {
                    Run.Dispose();
                }
                catch (Exception ex)
                {
                    StartupLog.Write($"Runtime.Stop 释放时异常: {ex.GetType().Name}: {ex.Message}");
                }
            });

            return task.Wait(timeout);
        }
        catch (Exception ex)
        {
            StartupLog.Write($"Runtime.Stop 等待释放时异常: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
