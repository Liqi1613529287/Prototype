using Prototype.Core;

namespace Prototype.Hosting;

//一条曲线在图表中的身份与显示信息
public sealed class ChartCurve
{
    //逻辑键。图表引用通道一律用它，不用经过 Sanitize 的目录名
    public required ChannelKey Key { get; init; }
    public required string DeviceId { get; init; }
    public required string ChannelId { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }
    //实时曲线颜色。通道定默认，图表可覆盖
    public required string ColorHex { get; init; }
    //历史曲线颜色
    public required string HistoryColorHex { get; init; }
    //图例/图元标题，例如 "Net05 · CH3 · 温度信息 (°C)"
    public string Title => $"{DeviceId} · CH{ChannelId} · {Name} ({Unit})";
    //这条曲线在历史库里的目录名，例如 Net05_3_温度信息
    public string StoreName => Prototype.Storage.CurveNaming.StoreName(DeviceId, ChannelId, Name);
}

//一条订阅：图表向某条通道要数据，装配层为它建了一条私有队列
public sealed record ChartSubscription(DataChannel<float> Channel, ChartCurve Curve);

//一帧里一条曲线的落点：调用方自己的数组 + 本帧结果
//刻不返回新数组：这个结构每 10ms 被填一次，逐帧分配就是每秒几十 MB 垃圾
public sealed class ChartFrame
{
    //OADate，直接跟 ScottPlot 的日期时间刻度对齐。由调用方分配、终身复用
    public required double[] Times { get; init; }
    //纵轴数组。由调用方分配、终身复用
    public required double[] Values { get; init; }
    //本帧实际画出的点数
    public int Count { get; internal set; }
    //"数据滞后"只能以它为准，不能取"画出来的最后一个点"
    public double NewestUnix { get; internal set; } = -1;
}

//图表消费端：一张图一个实例，内部管这张图订阅的每一条通道
//同屏多条曲线各自刷新会错开一个刷新间隔，做相位对比时那点错位会变成假相位
//所以这里改成"一次锁内快照全部曲线"
public class ChartConsumer : IDataConsumer<float>
{
    //抽稀后的目标点密度（点/秒）。屏幕就 900px 宽，再密也是白画
    private const int TargetPointsPerSecond = 1600;

    //环形缓冲下限：窗口很短时也得留点余量，不然曲线会被裁成一条短横线
    private const int MinCapacity = 1024;

    //环形缓冲上限，防止有人配个"10 分钟窗口"把内存吃掉
    private const int MaxCapacity = 1_000_000;

    //缓冲相对窗口的余量。窗口边界上正好对齐，留两成免得边缘抖
    private const double CapacityHeadroom = 1.25;

    //1970-01-01 对应的 OADate（= 25569.0）。逐点转换就靠这个常数，不做日历运算
    private const double OaDateAtUnixEpoch = 25569.0;

    //一条订阅在运行期的全部状态
    private sealed class Entry
    {
        public required ChartSubscription Subscription { get; init; }

        //抽稀倍数：每隔这么多点取一个
        public required int Decimation { get; init; }

        //环形缓冲：时间戳（Unix 秒）
        public required double[] Times { get; init; }

        //环形缓冲：数值
        public required double[] Values { get; init; }

        //环里有效元素个数（≤ Times.Length）
        public int Count;

        //下一个写入位置
        public int Next;
    }

    private readonly Entry[] _entries;

    private readonly object _sync = new();

    public ChartConsumer(ChartSpec spec, IReadOnlyList<ChartSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(subscriptions);

        if (subscriptions.Count == 0)
        {
            throw new ArgumentException($"图表「{spec.Title}」没有订阅任何通道。", nameof(subscriptions));
        }

        Title = spec.Title;
        WindowSeconds = spec.WindowSeconds;
        MaxPlotPoints = spec.MaxPlotPoints;
        YMin = spec.YMin;
        YMax = spec.YMax;

        _entries = new Entry[subscriptions.Count];

        for (int i = 0; i < subscriptions.Count; i++)
        {
            ChartSubscription sub = subscriptions[i];
            DataChannel<float> channel = sub.Channel;

            // 抽稀倍数按采样率算。硬编码 16 的话，挂一条 100Hz 的通道上去只剩 6 个点，曲线直接消失
            int decimation = Math.Max(1, (int)Math.Round(channel.SampleRateHertz / (double)TargetPointsPerSecond));

            // 抽稀之后的实际点密度，用它算缓冲容量：窗口 × 密度 × 余量
            // 这样细节图只占自己该占的内存，而不是按总览图的窗口分配
            double effectiveRate = channel.SampleRateHertz / (double)decimation;
            int capacity = (int)Math.Clamp(
                Math.Ceiling(spec.WindowSeconds * effectiveRate * CapacityHeadroom),
                MinCapacity,
                MaxCapacity);

            _entries[i] = new Entry
            {
                Subscription = sub,
                Decimation = decimation,
                Times = new double[capacity],
                Values = new double[capacity],
            };
        }
    }

    //标签页标题
    public string Title { get; }

    //这张图的横轴窗口（秒）
    public double WindowSeconds { get; }

    //这张图一帧最多画多少个点
    public int MaxPlotPoints { get; }

    //Y 轴下限
    public double YMin { get; }

    //Y 轴上限
    public double YMax { get; }

    //这张图订阅的曲线，按图例顺序
    public IReadOnlyList<ChartCurve> Curves => _entries.Select(e => e.Subscription.Curve).ToArray();

    //这张图的私有队列，给链路读数用
    public IReadOnlyList<DataChannel<float>> Queues => _entries.Select(e => e.Subscription.Channel).ToArray();

    //每条订阅一个读取任务，最后合成一个 Task。装配层只管 await 这一个
    public Task Start(CancellationToken cancellationToken = default)
    {
        var tasks = new Task[_entries.Length];

        for (int i = 0; i < _entries.Length; i++)
        {
            tasks[i] = RunEntry(i, cancellationToken);
        }

        return Task.WhenAll(tasks);
    }

    private async Task RunEntry(int index, CancellationToken cancellationToken)
    {
        Entry entry = _entries[index];

        long seen = 0;

        try
        {
            await foreach (var point in entry.Subscription.Channel
                               .ReadAllAsync()
                               .WithCancellation(cancellationToken))
            {
                if (seen++ % entry.Decimation != 0)
                {
                    continue;
                }

                double seconds = point.Timestamp / 1_000_000_000.0;

                lock (_sync)
                {
                    entry.Times[entry.Next] = seconds;
                    entry.Values[entry.Next] = point.Value;

                    entry.Next = entry.Next + 1 == entry.Times.Length ? 0 : entry.Next + 1;

                    if (entry.Count < entry.Times.Length)
                    {
                        entry.Count++;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    //把每条曲线"最近 WindowSeconds 秒"的点抽稀后写进各自的目标数组
    //必须一次锁内做完全部曲线：各自加锁取数的话，两条线的最新时刻会差一个取数间隔
    //抽稀两条规矩（踩过坑，不许改）：
    //  1. stride 向上取整 ⇒ 抽稀后点数 ≤ 点数上限，末尾一段永不被截掉
    //  2. 抽稀网格从最新点往回排 ⇒ 最后画出的一定是最新点
    //旧写法 stride = available / 4000 向下取整，48000 掉到 47999 时 stride 由 12 变 11，
    //抽稀后点数从 4000 炸到 4364，超出视口的正好是最新的 268 个点 = 1.84 秒
    public void CopyWindow(double fromUnix, IReadOnlyList<ChartFrame> frames)
    {
        if (frames.Count != _entries.Length)
        {
            throw new ArgumentException(
                $"图表「{Title}」有 {_entries.Length} 条曲线，但给了 {frames.Count} 个落点数组。",
                nameof(frames));
        }

        lock (_sync)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                CopyOne(_entries[i], frames[i], fromUnix);
            }
        }
    }

    //调用方必须已持有 _sync
    private void CopyOne(Entry entry, ChartFrame frame, double fromUnix)
    {
        frame.Count = 0;
        frame.NewestUnix = -1;

        int capacity = entry.Times.Length;

        if (entry.Count == 0)
        {
            return;
        }

        // 滞后读数取缓冲里真实最新的那一点，与怎么抽稀、怎么画彻底解耦
        frame.NewestUnix = entry.Times[(entry.Next - 1 + capacity) % capacity];

        // 逻辑第 0 个元素在环形缓冲里的物理下标
        int oldest = entry.Next - entry.Count;
        if (oldest < 0)
        {
            oldest += capacity;
        }

        // 逻辑下标上的时间戳单调递增，二分找第一个 ≥ fromUnix 的点
        int lo = 0;
        int hi = entry.Count;

        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;

            if (entry.Times[(oldest + mid) % capacity] < fromUnix)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        int available = entry.Count - lo;

        if (available <= 0)
        {
            return;
        }

        int cap = Math.Min(Math.Min(frame.Times.Length, frame.Values.Length), MaxPlotPoints);

        if (cap < 1)
        {
            return;
        }

        // 向上取整：保证 ceil(available / stride) ≤ cap，不存在"算出来再多砍掉"这回事
        int stride = Math.Max(1, (available + cap - 1) / cap);
        int count = Math.Min(cap, (available + stride - 1) / stride);

        if (count < 1)
        {
            return;
        }

        int newestLogical = entry.Count - 1;

        // 从最新一点往前填，这样下标 count-1 上放的一定是最新的那个采样点
        for (int i = count - 1; i >= 0; i--)
        {
            int logical = newestLogical - (count - 1 - i) * stride;
            int p = (oldest + logical) % capacity;

            // 一次乘法加法替代 DateTime 的日历运算：OADate = Unix 秒 / 86400 + 25569
            frame.Times[i] = entry.Times[p] / 86400.0 + OaDateAtUnixEpoch;
            frame.Values[i] = entry.Values[p];
        }

        frame.Count = count;
    }
}
