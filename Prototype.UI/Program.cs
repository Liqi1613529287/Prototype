using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using Prototype.Hosting;
using Prototype.Storage;
using Prototype.UI.ViewModels;
using Prototype.UI.Views;

namespace Prototype.UI;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var services = new ServiceCollection();

        ConfigureServices(services);

        var serviceProvider = services.BuildServiceProvider();

        App.ServiceProvider = serviceProvider;

        var runtime = serviceProvider.GetRequiredService<Runtime>();

        try
        {
            runtime.Start();

            StartupLog.Write("消息循环开始（窗口即将出现）");

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            StartupLog.Write("消息循环已退出");
        }
        catch (Exception ex)
        {
            StartupLog.Write($"消息循环异常退出: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            // 必须走 finally：正常关窗、崩溃、Alt+F4 都要保证落盘
            // 日志会把这一步的进出都记下来——如果只有"进入 finally"没有"已返回"，
            // 就说明进程是卡在退出流程里，而不是"窗口没关掉"
            StartupLog.Write("进入 finally：开始 runtime.Stop()");

            try
            {
                runtime.Stop();
            }
            catch (Exception ex)
            {
                StartupLog.Write($"runtime.Stop() 抛出异常: {ex.GetType().Name}: {ex.Message}");
            }

            StartupLog.Write("runtime.Stop() 已返回，Main 结束");
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 落盘抽稀倍数在这里改：1 = 全量；2 = 存一半；4 = 存 1/4；8 = 存 1/8
        services.AddSingleton(new StorageOptions
        {
            RootDirectory = ResolveStorageRoot(),
            StoreDivisor = 1,
            FlushIntervalSeconds = 5,
        });

        services.AddSingleton<Runtime>();

        // Hosting 组装出来的东西，UI 只取需要的
        // 注意这里给的是清单而不是单个对象：装配清单里有几条曲线，界面就拿到几条
        // 界面代码里没有任何一处写死通道名
        services.AddSingleton<TestRunContext>(sp => sp.GetRequiredService<Runtime>().Run);
        services.AddSingleton<IReadOnlyList<ChartConsumer>>(sp => sp.GetRequiredService<Runtime>().Charts);
        services.AddSingleton<IReadOnlyList<PipelineStats>>(sp => sp.GetRequiredService<Runtime>().Stats);

        // UI
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();
    }

    //数据目录放在仓库根目录下的 StorageData，而不是 bin 里——
    //bin 一执行"清理"数据就陪葬，没人想在排查问题时发现数据被编译器删了
    private static string ResolveStorageRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Prototype.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "StorageData");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
