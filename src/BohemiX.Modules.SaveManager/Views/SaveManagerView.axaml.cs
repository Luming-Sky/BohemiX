using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BohemiX.Modules.SaveManager.ViewModels;

namespace BohemiX.Modules.SaveManager.Views;

public partial class SaveManagerView : UserControl
{
    public SaveManagerView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        SizeChanged += (_, args) =>
        {
            if (DataContext is SaveManagerViewModel viewModel)
            {
                viewModel.ShowSecondaryColumns = args.NewSize.Width >= 1100;
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is not SaveManagerViewModel viewModel)
        {
            return;
        }

        viewModel.ShowSecondaryColumns = Bounds.Width >= 1100;
        viewModel.RequestTextAsync = ShowTextDialogAsync;
        viewModel.RequestConfirmationAsync = ShowConfirmationDialogAsync;
        viewModel.RequestImportPathAsync = PickImportPathAsync;
        viewModel.RequestExportPathAsync = PickExportPathAsync;
        viewModel.RequestExistingSavePathsAsync = PickExistingSavePathsAsync;
        await viewModel.InitializeAsync();
    }

    private async Task<string?> PickImportPathAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import BohemiX save package",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("BohemiX save package") { Patterns = ["*.bohemix-save.zip", "*.zip"] }
            ]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickExportPathAsync(string suggestedFileName)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export BohemiX save package",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "zip",
            FileTypeChoices =
            [
                new FilePickerFileType("BohemiX save package") { Patterns = ["*.bohemix-save.zip"] }
            ]
        });
        return file?.TryGetLocalPath();
    }

    private async Task<IReadOnlyList<string>> PickExistingSavePathsAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select existing game saves to protect",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("KCD2 save files") { Patterns = ["*.whs"] }
            ]
        });
        return files.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToList();
    }

    private async Task<string?> ShowTextDialogAsync(string title, string message, string currentValue)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            return null;
        }

        var viewModel = DataContext as SaveManagerViewModel;
        var input = new TextBox
        {
            Text = currentValue,
            MinWidth = 340,
            Classes = { "ModOpsTextBox" }
        };
        var dialog = CreateDialog(title);
        string? result = null;
        var cancel = new Button { Content = viewModel?.Text.Cancel ?? "Cancel", Classes = { "ModOpsFlatButton" } };
        var save = new Button { Content = viewModel?.Text.Save ?? "Save", Classes = { "ModOpsPrimaryButton" } };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            result = input.Text;
            dialog.Close();
        };

        dialog.Content = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A4ABB7") },
                input,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, save }
                }
            }
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        await dialog.ShowDialog(owner);
        return result;
    }

    private async Task<bool> ShowConfirmationDialogAsync(string title, string message, string actionText)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            return false;
        }

        var viewModel = DataContext as SaveManagerViewModel;
        var dialog = CreateDialog(title);
        var result = false;
        var cancel = new Button { Content = viewModel?.Text.Cancel ?? "Cancel", Classes = { "ModOpsFlatButton" } };
        var confirm = new Button { Content = actionText, Classes = { "ModOpsDangerButton" } };
        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        dialog.Content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#D9DDE4") },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm }
                }
            }
        };
        await dialog.ShowDialog(owner);
        return result;
    }

    private static Window CreateDialog(string title) => new()
    {
        Title = title,
        Width = 470,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Brush.Parse("#17191F"),
        Padding = new Thickness(18)
    };
}
