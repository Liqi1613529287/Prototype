using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Prototype.UI.Views;

namespace Prototype.UI;

//Avalonia 应用入口。主窗口从 DI 取，其它没什么
public partial class App : Application
{
    public static IServiceProvider ServiceProvider { get; set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow =
                ServiceProvider.GetRequiredService<MainWindow>();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
