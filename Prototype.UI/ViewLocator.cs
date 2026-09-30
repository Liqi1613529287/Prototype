using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Prototype.UI.ViewModels;

namespace Prototype.UI;

//按约定"ViewModel"→"View"反射找视图。Avalonia 模板自带，没动过
[RequiresUnreferencedCode("Default implementation of ViewLocator involves reflection which may be trimmed away.", Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
