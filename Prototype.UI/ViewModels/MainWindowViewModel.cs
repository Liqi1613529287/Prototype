using System;
using System.Collections.Generic;
using System.Linq;
using Prototype.Hosting;
using Prototype.Storage;

namespace Prototype.UI.ViewModels;

//主窗口的视图模型。它不认识任何具体通道、也不认识任何具体图表
//装配清单里有几张图，Charts 就有几项，界面照着一页一页建就行
//加一页图表、换一条通道，这个文件一个字都不用改
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly List<ChartViewModel> _charts = new();

    //本次试验会话，历史查询从这里读。为 null 表示预览环境
    private readonly TestRunContext? _run;

    //每条通道一份链路读数。为 null 表示预览环境
    private readonly IReadOnlyList<PipelineStats> _stats;

    public MainWindowViewModel(
        IReadOnlyList<ChartConsumer>? charts,
        TestRunContext? run,
        IReadOnlyList<PipelineStats>? stats)
    {
        _run = run;

        if (charts is not null)
        {
            foreach (ChartConsumer chart in charts)
            {
                _charts.Add(new ChartViewModel(chart, run));
            }
        }

        _stats = stats ?? [];
    }

    //所有图表。界面遍历它建标签页——装配几张就出来几页
    public IReadOnlyList<ChartViewModel> Charts => _charts;

    public bool HasCharts => _charts.Count > 0;

    public string Title => _charts.Count == 0
        ? "预览环境 · 无数据源"
        : $"{_charts.Count} 张图 / {_charts.Sum(c => c.Curves.Count)} 条曲线";

    //试验开始时刻（本地）。历史查询的时间输入以这一天的零点为基准
    public DateTime RunStartLocal => _run?.StartTimeLocal ?? DateTime.Now;

    //本次试验建出来的所有曲线库名，用于状态栏
    public string StoreSummary => _charts.Count == 0
        ? "（尚未建库）"
        : string.Join(" ｜ ", _charts.Select(c => c.StoreSummary).Distinct());

    //界面底部的诊断行（帧间隔 / 取数 / 渲染 / 数据滞后），带上当前看的是哪张图
    //做成普通属性 + 绑定，而不是让视图去点 TextBlock 的命名字段：
    //Avalonia 的命名控件字段由源生成器在内存里产出、不落盘，IDE 分析器有时解析不到会误报
    private string _perfText = "诊断中…";

    public string PerfText
    {
        get => _perfText;
        set => SetProperty(ref _perfText, value);
    }

    //链路拥堵一句话，每条通道一段。关键看源积压——接近 0 说明分发器跟得上采集，卡的在消费端
    //一路涨到 10 万就说明是某个消费者把整条链堵住了，再看后面哪条下游队列在涨
    public string PipelineText => _stats.Count == 0
        ? "积压 无（预览环境）"
        : string.Join("　｜　", _stats.Select(s => s.Describe()));
}
