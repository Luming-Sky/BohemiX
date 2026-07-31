using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BohemiX.Core.Models;

namespace BohemiX.App.Views;

public partial class ErrorDialogWindow : Window
{
    private ApplicationErrorReport? report;
    private string? logTargetPath;

    public ErrorDialogWindow()
    {
        InitializeComponent();
    }

    public ErrorDialogWindow(ApplicationErrorReport report)
        : this()
    {
        this.report = report ?? throw new ArgumentNullException(nameof(report));
        TitleTextBlock.Text = report.Title;
        MessageTextBlock.Text = report.Message;
        LocationTextBlock.Text = report.Location;
        SourceTextBlock.Text = report.Source;
        TimeTextBlock.Text = report.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        DetailsTextBlock.Text = report.TechnicalDetails;
        LogPathTextBlock.Text = string.IsNullOrWhiteSpace(report.LogPath)
            ? "日志目录：不可用"
            : $"日志目录：{report.LogPath}";
        logTargetPath = ResolveLogTarget(report.LogPath);
        OpenLogButton.IsEnabled = logTargetPath is not null;
    }

    private async void CopyDetails_OnClick(object? sender, RoutedEventArgs e)
    {
        if (report is null || Clipboard is null)
        {
            return;
        }

        await Clipboard.SetTextAsync(report.ToClipboardText());
    }

    private void OpenLog_OnClick(object? sender, RoutedEventArgs e)
    {
        if (logTargetPath is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = logTargetPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogPathTextBlock.Text = $"无法打开日志：{ex.Message}\n{report?.LogPath}";
        }
    }

    internal static string? ResolveLogTarget(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(logPath);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            if (!Directory.Exists(fullPath))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(fullPath, "bohemix-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
                ?? fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
