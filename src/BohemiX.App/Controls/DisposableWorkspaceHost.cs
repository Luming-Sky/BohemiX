using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace BohemiX.App.Controls;

public enum WorkspaceKind
{
    Home,
    Mods,
    Saves,
    Settings,
    Laboratory
}

public sealed class DisposableWorkspaceHost : ContentControl, IDisposable
{
    public static readonly StyledProperty<WorkspaceKind> KindProperty =
        AvaloniaProperty.Register<DisposableWorkspaceHost, WorkspaceKind>(nameof(Kind));

    public static readonly StyledProperty<IDataTemplate?> LazyContentTemplateProperty =
        AvaloniaProperty.Register<DisposableWorkspaceHost, IDataTemplate?>(nameof(LazyContentTemplate));

    public WorkspaceKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IDataTemplate? LazyContentTemplate
    {
        get => GetValue(LazyContentTemplateProperty);
        set => SetValue(LazyContentTemplateProperty, value);
    }

    internal bool IsActive => Content is not null;

    internal bool Activate(object dataContext)
    {
        ArgumentNullException.ThrowIfNull(dataContext);
        if (ReferenceEquals(Content, dataContext))
        {
            return false;
        }

        Content = dataContext;
        ContentTemplate = LazyContentTemplate;
        return true;
    }

    internal bool Deactivate()
    {
        if (Content is null)
        {
            return false;
        }

        ContentTemplate = null;
        Content = null;
        return true;
    }

    public void Dispose() => Deactivate();
}
