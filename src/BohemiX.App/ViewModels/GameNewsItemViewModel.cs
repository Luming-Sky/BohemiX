using System;
using Avalonia.Media.Imaging;
using BohemiX.App.Services;
using BohemiX.Core.Models;

namespace BohemiX.App.ViewModels;

public sealed class GameNewsItemViewModel : ViewModelBase, IDisposable
{
    private const int ImageDecodeWidth = 720;
    private BitmapLease? imageLease;
    private Bitmap? image;
    private bool visualResourcesActive;

    public GameNewsItemViewModel(GameNewsItem item)
    {
        Item = item;
    }

    public GameNewsItem Item { get; }

    public string Title => Item.Title;

    public string Summary => Item.Summary;

    public string Url => Item.Url;

    public string Source => Item.Source;

    public string Author => Item.Author;

    public Bitmap? Image
    {
        get => image;
        private set
        {
            if (!SetProperty(ref image, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(HasNoImage));
        }
    }

    public bool HasImage => Image is not null;

    public bool HasNoImage => !HasImage;

    public string PublishedText => Item.PublishedAtUtc == DateTimeOffset.MinValue
        ? string.Empty
        : Item.PublishedAtUtc.ToLocalTime().ToString("yyyy-MM-dd");

    internal bool AreVisualResourcesActive => visualResourcesActive;

    public void ActivateVisualResources()
    {
        visualResourcesActive = true;
        if (imageLease is not null || string.IsNullOrWhiteSpace(Item.ImagePath))
        {
            return;
        }

        imageLease = SharedBitmapLeaseCache.Instance.Acquire(Item.ImagePath, ImageDecodeWidth);
        Image = imageLease?.Bitmap;
    }

    public void DeactivateVisualResources()
    {
        visualResourcesActive = false;
        Image = null;
        imageLease?.Dispose();
        imageLease = null;
    }

    public void Dispose() => DeactivateVisualResources();
}
