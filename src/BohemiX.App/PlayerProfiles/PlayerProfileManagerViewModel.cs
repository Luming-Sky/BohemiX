using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using BohemiX.App.Services;
using BohemiX.App.ViewModels;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace BohemiX.App.PlayerProfiles;

public sealed partial class PlayerProfileManagerViewModel : ObservableObject, IDisposable
{
    private const double AvatarCropPreviewSize = 260d;
    private const int AvatarEditorDecodeWidth = 640;

    private readonly IPlayerService playerService;
    private readonly IGamePathPickerService pathPickerService;
    private readonly IAvatarCropService avatarCropService;
    private readonly ILogger logger;
    private readonly IPlayerSteamAccountBindingService? steamAccountBindingService;
    private readonly IWorkshopService? workshopService;
    private readonly INexusAccountService? nexusAccountService;
    private Bitmap? editorAvatarImage;
    private Bitmap? editorAvatarCroppedImage;
    private readonly SemaphoreSlim editorAutoSaveGate = new(1, 1);
    private CancellationTokenSource? editorAutoSaveCancellation;
    private CancellationTokenSource? avatarCropCancellation;
    private CancellationTokenSource? nexusAccountBindingCancellation;
    private string? editorAvatarSourcePath;
    private string? editorAvatarCropPath;
    private bool suppressEditorAutoSave;
    private bool suppressAvatarCropUpdate;
    private bool isCreating;
    private bool isFirstProfileSetup;
    private bool isInitializing;

    public PlayerProfileManagerViewModel(
        IPlayerService playerService,
        IGamePathPickerService pathPickerService,
        IAvatarCropService avatarCropService,
        ILogger logger,
        IPlayerSteamAccountBindingService? steamAccountBindingService = null,
        IWorkshopService? workshopService = null,
        INexusAccountService? nexusAccountService = null)
    {
        this.playerService = playerService;
        this.pathPickerService = pathPickerService;
        this.avatarCropService = avatarCropService;
        this.logger = logger.ForContext<PlayerProfileManagerViewModel>();
        this.steamAccountBindingService = steamAccountBindingService;
        this.workshopService = workshopService;
        this.nexusAccountService = nexusAccountService;
        playerService.CurrentPlayerChanged += OnCurrentPlayerChanged;
    }

    public PlayerProfileManagerViewModel(
        IPlayerService playerService,
        IGamePathPickerService pathPickerService,
        ILogger logger)
        : this(playerService, pathPickerService, new AvatarCropService(), logger)
    {
    }

    [Obsolete("Game installation detection is managed by game environments, not player profiles.")]
    public PlayerProfileManagerViewModel(
        IPlayerService playerService,
        ILocalGameInstallationDetector gameInstallationDetector,
        IGamePathPickerService pathPickerService,
        ILogger logger)
        : this(playerService, pathPickerService, logger)
    {
        ArgumentNullException.ThrowIfNull(gameInstallationDetector);
    }

    public event EventHandler<PlayerChangedEventArgs>? PlayerChanged;

    public event EventHandler? SettingsRequested;

    public ObservableCollection<PlayerProfileItemViewModel> Players { get; } = [];

    // Compatibility bindings for the existing host visual. Game-platform controls are hidden
    // while game environments move to their own management surface.
    public IReadOnlyList<PlayerPlatformOption> PlatformOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentPlayer))]
    private PlayerProfileItemViewModel? currentPlayer;

    [ObservableProperty]
    private PlayerProfileItemViewModel? selectedPlayer;

    [ObservableProperty]
    private bool isPlayerMenuOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    private bool isInformationOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    [NotifyPropertyChangedFor(nameof(IsProfileEditorContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsAvatarCropSheetVisible))]
    private bool isEditorOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    private bool isSwitchPlayerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    private bool isDeleteConfirmationOpen;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string editorTitle = "创建玩家";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorInitial))]
    [NotifyPropertyChangedFor(nameof(EditorPreviewName))]
    private string editorDisplayName = string.Empty;

    [ObservableProperty]
    private string editorBio = string.Empty;

    [ObservableProperty]
    private PlayerPlatformOption selectedPlatform = new(string.Empty, string.Empty);

    public bool IsEditorSteamPlatform => false;

    public bool IsEditorGogPlatform => false;

    [ObservableProperty]
    private string? editorAvatarPath;

    // Crop controls are a follow-up to selecting a new source image, rather than a permanent profile setting.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileEditorContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsAvatarCropSheetVisible))]
    private bool isAvatarCropAdjustmentOpen;

    [ObservableProperty]
    private double editorAvatarCropZoom = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorAvatarPreviewOffsetX))]
    [NotifyPropertyChangedFor(nameof(EditorAvatarCropPreviewOffsetX))]
    private double editorAvatarCropHorizontalOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorAvatarPreviewOffsetY))]
    [NotifyPropertyChangedFor(nameof(EditorAvatarCropPreviewOffsetY))]
    private double editorAvatarCropVerticalOffset;

    [ObservableProperty]
    private string editorProviderName = "Local Account";

    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private string editorSaveStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBindSteamAccount))]
    [NotifyPropertyChangedFor(nameof(CanUnbindSteamAccount))]
    private bool isSteamBindingBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBindSteamAccount))]
    [NotifyPropertyChangedFor(nameof(CanUnbindSteamAccount))]
    private bool hasSteamBinding;

    [ObservableProperty]
    private string steamBindingStateText = "未绑定";

    [ObservableProperty]
    private string steamBindingPersonaName = string.Empty;

    [ObservableProperty]
    private string steamBindingSteamIdText = string.Empty;

    [ObservableProperty]
    private string steamBindingMessage = "尚未将此离线账号绑定到 Steam。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBindNexusAccount))]
    [NotifyPropertyChangedFor(nameof(CanUnbindNexusAccount))]
    private bool isNexusAccountBinding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBindNexusAccount))]
    [NotifyPropertyChangedFor(nameof(CanUnbindNexusAccount))]
    private bool hasNexusBinding;

    [ObservableProperty]
    private string nexusBindingStateText = "未绑定";

    [ObservableProperty]
    private string nexusBindingAccountName = string.Empty;

    [ObservableProperty]
    private string nexusBindingTierText = string.Empty;

    [ObservableProperty]
    private string nexusBindingMessage = "绑定 Nexus Mods 账号后即可搜索和下载 Nexus 来源的 Mod。";

    [ObservableProperty]
    private bool isPlayerSetupRequired;

    public bool HasCurrentPlayer => CurrentPlayer is not null;

    public bool CanBindSteamAccount =>
        HasCurrentPlayer
        && !HasSteamBinding
        && !IsSteamBindingBusy
        && steamAccountBindingService is not null
        && workshopService is not null;

    public bool CanUnbindSteamAccount =>
        HasCurrentPlayer
        && HasSteamBinding
        && !IsSteamBindingBusy
        && steamAccountBindingService is not null;

    public bool CanBindNexusAccount =>
        HasCurrentPlayer
        && !HasNexusBinding
        && !IsNexusAccountBinding
        && nexusAccountService is not null;

    public bool CanUnbindNexusAccount =>
        HasCurrentPlayer
        && HasNexusBinding
        && !IsNexusAccountBinding
        && nexusAccountService is not null;

    public bool IsAnyModalOpen =>
        IsInformationOpen || IsEditorOpen || IsSwitchPlayerOpen || IsDeleteConfirmationOpen;

    public bool CanCloseEditor => !isFirstProfileSetup;

    public bool IsProfileEditorContentVisible => IsEditorOpen && !IsAvatarCropAdjustmentOpen;

    public bool IsAvatarCropSheetVisible => IsEditorOpen && IsAvatarCropAdjustmentOpen;

    public string EditorInitial => string.IsNullOrWhiteSpace(EditorDisplayName)
        ? "?"
        : char.ToUpperInvariant(EditorDisplayName.Trim()[0]).ToString();

    public string EditorPreviewName => string.IsNullOrWhiteSpace(EditorDisplayName)
        ? "新玩家"
        : EditorDisplayName.Trim();

    public Bitmap? EditorAvatarImage
    {
        get => editorAvatarImage;
        private set
        {
            if (ReferenceEquals(editorAvatarImage, value))
            {
                return;
            }

            editorAvatarImage?.Dispose();
            editorAvatarImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEditorAvatar));
            NotifyAvatarCropPreviewGeometryChanged();
        }
    }

    public bool HasEditorAvatar => EditorAvatarImage is not null;

    public Bitmap? EditorAvatarCroppedImage
    {
        get => editorAvatarCroppedImage;
        private set
        {
            if (ReferenceEquals(editorAvatarCroppedImage, value))
            {
                return;
            }

            editorAvatarCroppedImage?.Dispose();
            editorAvatarCroppedImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEditorCroppedAvatar));
        }
    }

    public bool HasEditorCroppedAvatar => EditorAvatarCroppedImage is not null;

    public double EditorAvatarPreviewOffsetX => -EditorAvatarCropHorizontalOffset * 44;

    public double EditorAvatarPreviewOffsetY => -EditorAvatarCropVerticalOffset * 44;

    public double EditorAvatarCropPreviewImageWidth => GetAvatarCropPreviewImageSize().Width;

    public double EditorAvatarCropPreviewImageHeight => GetAvatarCropPreviewImageSize().Height;

    public double EditorAvatarCropPreviewMaxOffsetX =>
        Math.Max(0d, (EditorAvatarCropPreviewImageWidth - AvatarCropPreviewSize) / 2d);

    public double EditorAvatarCropPreviewMaxOffsetY =>
        Math.Max(0d, (EditorAvatarCropPreviewImageHeight - AvatarCropPreviewSize) / 2d);

    public double EditorAvatarCropPreviewOffsetX =>
        -EditorAvatarCropHorizontalOffset * EditorAvatarCropPreviewMaxOffsetX;

    public double EditorAvatarCropPreviewOffsetY =>
        -EditorAvatarCropVerticalOffset * EditorAvatarCropPreviewMaxOffsetY;

    partial void OnEditorDisplayNameChanged(string value)
    {
        QueueEditorAutoSave();
    }

    partial void OnEditorBioChanged(string value)
    {
        QueueEditorAutoSave();
    }

    partial void OnEditorAvatarPathChanged(string? value)
    {
        QueueEditorAutoSave();
    }

    partial void OnEditorAvatarCropZoomChanged(double value)
    {
        NotifyAvatarCropPreviewGeometryChanged();
        QueueAvatarCropUpdate();
    }

    partial void OnEditorAvatarCropHorizontalOffsetChanged(double value) => QueueAvatarCropUpdate();

    partial void OnEditorAvatarCropVerticalOffsetChanged(double value) => QueueAvatarCropUpdate();

    private (double Width, double Height) GetAvatarCropPreviewImageSize()
    {
        var pixelSize = EditorAvatarImage?.PixelSize;
        if (pixelSize is null || pixelSize.Value.Width <= 0 || pixelSize.Value.Height <= 0)
        {
            return (AvatarCropPreviewSize, AvatarCropPreviewSize);
        }

        var shortestEdge = Math.Min(pixelSize.Value.Width, pixelSize.Value.Height);
        var scale = AvatarCropPreviewSize * Math.Clamp(EditorAvatarCropZoom, 1d, 3d) / shortestEdge;
        return (pixelSize.Value.Width * scale, pixelSize.Value.Height * scale);
    }

    private void NotifyAvatarCropPreviewGeometryChanged()
    {
        OnPropertyChanged(nameof(EditorAvatarCropPreviewImageWidth));
        OnPropertyChanged(nameof(EditorAvatarCropPreviewImageHeight));
        OnPropertyChanged(nameof(EditorAvatarCropPreviewMaxOffsetX));
        OnPropertyChanged(nameof(EditorAvatarCropPreviewMaxOffsetY));
        OnPropertyChanged(nameof(EditorAvatarCropPreviewOffsetX));
        OnPropertyChanged(nameof(EditorAvatarCropPreviewOffsetY));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        isInitializing = true;
        try
        {
            await playerService.InitializeAsync(cancellationToken);
            ReloadPlayers();
            await RefreshSteamBindingAsync();
            IsPlayerSetupRequired = playerService.CurrentPlayer is null;
            if (IsPlayerSetupRequired)
            {
                BeginCreate(isFirstProfile: true);
            }
        }
        finally
        {
            isInitializing = false;
        }
    }

    [RelayCommand]
    private void TogglePlayerMenu()
    {
        if (!HasCurrentPlayer)
        {
            return;
        }

        IsPlayerMenuOpen = !IsPlayerMenuOpen;
    }

    [RelayCommand]
    private void ClosePlayerMenu()
    {
        IsPlayerMenuOpen = false;
    }

    [RelayCommand]
    private void OpenInformation()
    {
        IsPlayerMenuOpen = false;
        if (CurrentPlayer is not null)
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void OpenSwitchPlayer()
    {
        IsPlayerMenuOpen = false;
        SelectedPlayer = CurrentPlayer;
        IsSwitchPlayerOpen = true;
    }

    [RelayCommand]
    private void OpenCreatePlayer()
    {
        IsPlayerMenuOpen = false;
        BeginCreate(isFirstProfile: false);
    }

    [RelayCommand]
    private void OpenEditProfile()
    {
        IsPlayerMenuOpen = false;
        var current = playerService.CurrentPlayer;
        if (current is null)
        {
            return;
        }

        CloseAllModals();
        isCreating = false;
        isFirstProfileSetup = false;
        ClearAvatarCropFile();
        EditorTitle = "编辑玩家资料";
        EditorDisplayName = current.DisplayName;
        EditorBio = current.Bio ?? string.Empty;
        EditorAvatarPath = current.Avatar;
        editorAvatarSourcePath = current.Avatar;
        EditorAvatarImage = TryLoadBitmap(current.Avatar);
        EditorAvatarCroppedImage = TryLoadBitmap(current.Avatar);
        ResetAvatarCropSettings();
        IsAvatarCropAdjustmentOpen = false;
        EditorProviderName = GetProviderDisplayName(current.Provider);
        ErrorMessage = string.Empty;
        EditorSaveStatus = "修改会自动保存";
        OnPropertyChanged(nameof(CanCloseEditor));
        IsEditorOpen = true;
    }

    [RelayCommand]
    private async Task ChooseAvatarAsync()
    {
        var path = await pathPickerService.PickAvatarAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ClearAvatarCropFile();
        editorAvatarSourcePath = path;
        EditorAvatarImage = TryLoadBitmap(path);
        ResetAvatarCropSettings();
        IsAvatarCropAdjustmentOpen = true;
        await ApplyAvatarCropAsync(path, CancellationToken.None);
    }

    [RelayCommand]
    private async Task CompleteAvatarCropAdjustmentAsync()
    {
        avatarCropCancellation?.Cancel();
        if (!string.IsNullOrWhiteSpace(editorAvatarSourcePath))
        {
            await ApplyAvatarCropAsync(editorAvatarSourcePath, CancellationToken.None);
        }

        IsAvatarCropAdjustmentOpen = false;
    }

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var displayName = EditorDisplayName.Trim();
        if (displayName.Length is < 1 or > 64)
        {
            ErrorMessage = "玩家昵称需为 1 到 64 个字符。";
            return;
        }

        if (EditorBio.Length > 280)
        {
            ErrorMessage = "个人简介最多 280 个字符。";
            return;
        }

        editorAutoSaveCancellation?.Cancel();
        editorAutoSaveCancellation?.Dispose();
        IsBusy = true;
            ErrorMessage = string.Empty;
            EditorSaveStatus = string.Empty;
        try
        {
            if (isCreating)
            {
                var localProvider = playerService.Providers.FirstOrDefault(provider => !provider.RequiresNetwork)
                    ?? throw new InvalidOperationException("No local player provider is registered.");
                await playerService.CreateAsync(new PlayerProfileDraft(
                    displayName,
                    EditorAvatarPath,
                    localProvider.Id,
                    NullIfWhiteSpace(EditorBio)));
            }
            else
            {
                var current = playerService.CurrentPlayer
                    ?? throw new InvalidOperationException("No current player is available.");
                await playerService.UpdateAsync(current.Id, new PlayerProfileUpdate(
                    displayName,
                    EditorAvatarPath,
                    NullIfWhiteSpace(EditorBio)));
            }

            IsEditorOpen = false;
            IsPlayerSetupRequired = false;
            isFirstProfileSetup = false;
            ClearAvatarCropFile();
            OnPropertyChanged(nameof(CanCloseEditor));
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to save player profile");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SwitchPlayerAsync(PlayerProfileItemViewModel? player)
    {
        if (player is null || IsBusy || player.IsCurrent)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await playerService.SwitchAsync(player.Id);
            IsSwitchPlayerOpen = false;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to switch player profile");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestDeletePlayer()
    {
        IsPlayerMenuOpen = false;
        IsDeleteConfirmationOpen = CurrentPlayer is not null;
    }

    [RelayCommand]
    private async Task ConfirmDeletePlayerAsync()
    {
        var current = playerService.CurrentPlayer;
        if (current is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await playerService.DeleteAsync(current.Id);
            IsDeleteConfirmationOpen = false;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to delete player profile");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsPlayerMenuOpen = false;
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task RefreshSteamBindingAsync()
    {
        var current = playerService.CurrentPlayer;
        if (current is null || steamAccountBindingService is null)
        {
            ApplySteamBinding(null);
            SteamBindingMessage = current is null
                ? "请先创建或选择一个离线账号。"
                : "Steam 绑定服务当前不可用。";
            return;
        }

        IsSteamBindingBusy = true;
        SteamBindingMessage = "正在读取 Steam 绑定状态...";
        try
        {
            var binding = await steamAccountBindingService.GetBindingByPlayerIdAsync(current.Id);
            ApplySteamBinding(binding);
            SteamBindingMessage = binding is null
                ? "尚未将此离线账号绑定到 Steam。"
                : "已保存绑定。进入 Steam 创意工坊时会再次确认登录账号是否拥有游戏。";
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to load Steam binding for player {PlayerId}", current.Id);
            ApplySteamBinding(null);
            SteamBindingMessage = $"无法读取 Steam 绑定：{ex.Message}";
        }
        finally
        {
            IsSteamBindingBusy = false;
            NotifySteamBindingCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanBindSteamAccount))]
    private async Task BindSteamAccountAsync()
    {
        var current = playerService.CurrentPlayer;
        if (current is null || steamAccountBindingService is null || workshopService is null)
        {
            return;
        }

        IsSteamBindingBusy = true;
        SteamBindingMessage = "正在检测 Steam 登录账号...";
        NotifySteamBindingCommands();
        try
        {
            var progress = new SteamAccountDetectionProgressGate(value =>
            {
                SteamBindingMessage = value.Stage switch
                {
                    SteamAccountDetectionStage.StartingClient => "正在启动 Steam...",
                    SteamAccountDetectionStage.WaitingForLogin => "正在等待你登录 Steam，登录后将自动继续...",
                    SteamAccountDetectionStage.ValidatingAccount => "正在确认 Steam 账号是否拥有游戏...",
                    _ => SteamBindingMessage
                };
            });
            SteamAccountIdentity identity;
            try
            {
                identity = await workshopService.GetCurrentSteamAccountAsync(progress);
            }
            finally
            {
                progress.Complete();
            }

            var binding = await steamAccountBindingService.BindAsync(current.Id, identity);
            ApplySteamBinding(binding);
            SteamBindingMessage = identity.OwnsKcd2
                ? "Steam 账号绑定成功，可以使用 Steam 创意工坊。"
                : "Steam 账号绑定成功，但当前账号未拥有《天国：拯救 II》。";
        }
        catch (SteamAccountBindingException ex)
        {
            logger.Warning(ex, "Steam binding conflict for player {PlayerId}", current.Id);
            SteamBindingMessage = ex.FailureKind switch
            {
                SteamAccountBindingFailureKind.PlayerAlreadyBoundToDifferentSteamAccount =>
                    "此离线账号已绑定其他 Steam 账号，请先解绑。",
                SteamAccountBindingFailureKind.SteamAccountAlreadyBoundToDifferentPlayer =>
                    "当前 Steam 账号已绑定到其他离线账号。",
                _ => ex.Message
            };
        }
        catch (WorkshopException ex)
        {
            logger.Warning(ex, "Unable to detect Steam account for player {PlayerId}", current.Id);
            SteamBindingMessage = ex.FailureKind switch
            {
                WorkshopFailureKind.SteamUnavailable => "Steam 不可用，请启动 Steam 后重试。",
                WorkshopFailureKind.NotLoggedIn => "Steam 尚未登录，请登录后重试。",
                WorkshopFailureKind.GameNotOwned => "当前 Steam 账号未拥有《天国：拯救 II》，因此无法打开该游戏的创意工坊。",
                _ => $"Steam 账号检测失败：{ex.Message}"
            };
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to bind Steam account for player {PlayerId}", current.Id);
            SteamBindingMessage = $"Steam 账号绑定失败：{ex.Message}";
        }
        finally
        {
            IsSteamBindingBusy = false;
            NotifySteamBindingCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnbindSteamAccount))]
    private async Task UnbindSteamAccountAsync()
    {
        var current = playerService.CurrentPlayer;
        if (current is null || steamAccountBindingService is null)
        {
            return;
        }

        IsSteamBindingBusy = true;
        SteamBindingMessage = "正在解除 Steam 账号绑定...";
        NotifySteamBindingCommands();
        try
        {
            await steamAccountBindingService.UnbindAsync(current.Id);
            ApplySteamBinding(null);
            SteamBindingMessage = "已解除 Steam 账号绑定。";
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to unbind Steam account for player {PlayerId}", current.Id);
            SteamBindingMessage = $"解除 Steam 账号绑定失败：{ex.Message}";
        }
        finally
        {
            IsSteamBindingBusy = false;
            NotifySteamBindingCommands();
        }
    }

    public async Task<bool> EnsureNexusAccountBoundAsync(CancellationToken cancellationToken = default)
    {
        if (nexusAccountService is null)
        {
            NexusBindingMessage = "Nexus 账号绑定服务当前不可用。";
            return false;
        }

        var account = await nexusAccountService.GetBoundAccountAsync(cancellationToken);
        if (account is not null)
        {
            ApplyNexusBinding(account);
            return true;
        }

        return await BindNexusAccountCoreAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task RefreshNexusAccountAsync(CancellationToken cancellationToken = default)
    {
        if (nexusAccountService is null)
        {
            ApplyNexusBinding(null);
            NexusBindingMessage = "Nexus 账号绑定服务当前不可用。";
            return;
        }

        try
        {
            var account = await nexusAccountService.GetBoundAccountAsync(cancellationToken);
            ApplyNexusBinding(account);
            NexusBindingMessage = account is null
                ? "绑定 Nexus Mods 账号后即可搜索和下载 Nexus 来源的 Mod。"
                : "已保存 Nexus Mods 账号绑定，可直接用于 Nexus 下载。";
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to load Nexus account binding");
            ApplyNexusBinding(null);
            NexusBindingMessage = $"无法读取 Nexus 账号绑定：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanBindNexusAccount))]
    private async Task BindNexusAccountAsync() => await BindNexusAccountCoreAsync();

    private async Task<bool> BindNexusAccountCoreAsync(CancellationToken cancellationToken = default)
    {
        if (nexusAccountService is null || IsNexusAccountBinding)
        {
            return false;
        }

        nexusAccountBindingCancellation?.Cancel();
        nexusAccountBindingCancellation?.Dispose();
        nexusAccountBindingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IsNexusAccountBinding = true;
        NexusBindingStateText = "绑定中";
        NexusBindingMessage = "正在打开 Nexus Mods 授权页面，完成授权后会自动继续。";
        NotifyNexusBindingCommands();
        try
        {
            var account = await nexusAccountService.BindAsync(nexusAccountBindingCancellation.Token);
            ApplyNexusBinding(account);
            NexusBindingMessage = $"已绑定 Nexus Mods 账号 {account.Name}，现在可以下载 Nexus 来源的 Mod。";
            return true;
        }
        catch (OperationCanceledException)
        {
            ApplyNexusBinding(null);
            NexusBindingMessage = "已取消 Nexus 账号绑定。";
            return false;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to bind Nexus account");
            ApplyNexusBinding(null);
            NexusBindingMessage = $"Nexus 账号绑定失败：{ex.Message}";
            return false;
        }
        finally
        {
            IsNexusAccountBinding = false;
            nexusAccountBindingCancellation?.Dispose();
            nexusAccountBindingCancellation = null;
            NotifyNexusBindingCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnbindNexusAccount))]
    private async Task UnbindNexusAccountAsync()
    {
        if (nexusAccountService is null)
        {
            return;
        }

        IsNexusAccountBinding = true;
        NexusBindingMessage = "正在解除 Nexus 账号绑定...";
        NotifyNexusBindingCommands();
        try
        {
            await nexusAccountService.UnbindAsync();
            ApplyNexusBinding(null);
            NexusBindingMessage = "已解除 Nexus 账号绑定。";
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to unbind Nexus account");
            NexusBindingMessage = $"解除 Nexus 账号绑定失败：{ex.Message}";
        }
        finally
        {
            IsNexusAccountBinding = false;
            NotifyNexusBindingCommands();
        }
    }

    [RelayCommand]
    private void CancelNexusAccountBinding() => nexusAccountBindingCancellation?.Cancel();

    public void CancelNexusAccountBindingRequest() => nexusAccountBindingCancellation?.Cancel();

    public async Task<bool> BindNexusAccountWithApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (nexusAccountService is null)
        {
            NexusBindingMessage = "Nexus 账号绑定服务当前不可用。";
            return false;
        }

        IsNexusAccountBinding = true;
        NexusBindingStateText = "检查中";
        NexusBindingMessage = "正在检查 Nexus 个人密钥...";
        NotifyNexusBindingCommands();
        try
        {
            var account = await nexusAccountService.BindWithApiKeyAsync(apiKey, cancellationToken);
            ApplyNexusBinding(account);
            NexusBindingMessage = $"已绑定 Nexus Mods 账号 {account.Name}，现在可以下载 Nexus 来源的 Mod。";
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to bind Nexus account with a personal API key");
            ApplyNexusBinding(null);
            NexusBindingMessage = $"Nexus 个人密钥无效或无法使用：{ex.Message}";
            return false;
        }
        finally
        {
            IsNexusAccountBinding = false;
            NotifyNexusBindingCommands();
        }
    }

    [RelayCommand]
    private void CloseModal()
    {
        if (IsAvatarCropAdjustmentOpen)
        {
            IsAvatarCropAdjustmentOpen = false;
            return;
        }

        if (IsEditorOpen && !CanCloseEditor)
        {
            return;
        }

        CloseAllModals();
        ErrorMessage = string.Empty;
    }

    private void BeginCreate(bool isFirstProfile)
    {
        CloseAllModals();
        isCreating = true;
        isFirstProfileSetup = isFirstProfile;
        ClearAvatarCropFile();
        EditorTitle = isFirstProfile ? "创建你的玩家档案" : "创建玩家";
        EditorDisplayName = string.Empty;
        EditorBio = string.Empty;
        EditorAvatarPath = null;
        editorAvatarSourcePath = null;
        EditorAvatarImage = null;
        ResetAvatarCropSettings();
        IsAvatarCropAdjustmentOpen = false;
        EditorProviderName = playerService.Providers.FirstOrDefault(provider => !provider.RequiresNetwork)?.DisplayName
            ?? "Local Account";
        ErrorMessage = string.Empty;
        EditorSaveStatus = string.Empty;
        OnPropertyChanged(nameof(CanCloseEditor));
        IsEditorOpen = true;
    }

    private void CloseAllModals()
    {
        IsInformationOpen = false;
        IsEditorOpen = false;
        IsSwitchPlayerOpen = false;
        IsDeleteConfirmationOpen = false;
    }

    private void OnCurrentPlayerChanged(object? sender, PlayerChangedEventArgs e)
    {
        ReloadPlayers();
        _ = RefreshSteamBindingAsync();
        if (!isInitializing)
        {
            // Nexus browser cookies are intentionally probed only from the
            // settings and download workflows. Loading WebView2 for a player
            // switch would otherwise keep its native runtime resident on the
            // Dashboard even when Nexus features are never opened.
            ApplyNexusBinding(null);
        }
        IsPlayerMenuOpen = false;
        IsPlayerSetupRequired = e.CurrentPlayer is null;
        if (e.CurrentPlayer is null)
        {
            BeginCreate(isFirstProfile: true);
        }

        PlayerChanged?.Invoke(this, e);
    }

    private void ReloadPlayers()
    {
        foreach (var player in Players)
        {
            player.Dispose();
        }

        Players.Clear();
        var currentId = playerService.CurrentPlayer?.Id;
        foreach (var profile in playerService.Profiles)
        {
            Players.Add(new PlayerProfileItemViewModel(
                profile,
                profile.Id == currentId,
                GetProviderDisplayName(profile.Provider)));
        }

        CurrentPlayer = Players.FirstOrDefault(player => player.Id == currentId);
        SelectedPlayer = CurrentPlayer;
        OnPropertyChanged(nameof(CanBindSteamAccount));
        OnPropertyChanged(nameof(CanUnbindSteamAccount));
        OnPropertyChanged(nameof(CanBindNexusAccount));
        OnPropertyChanged(nameof(CanUnbindNexusAccount));
    }

    private void ApplySteamBinding(SteamAccountBinding? binding)
    {
        HasSteamBinding = binding is not null;
        SteamBindingStateText = binding is null ? "未绑定" : "已绑定";
        SteamBindingPersonaName = binding?.PersonaName ?? "未检测";
        SteamBindingSteamIdText = binding is null ? string.Empty : $"SteamID: {binding.SteamId}";
        NotifySteamBindingCommands();
    }

    private void NotifySteamBindingCommands()
    {
        BindSteamAccountCommand.NotifyCanExecuteChanged();
        UnbindSteamAccountCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanBindSteamAccount));
        OnPropertyChanged(nameof(CanUnbindSteamAccount));
    }

    private void ApplyNexusBinding(NexusAccountBinding? binding)
    {
        HasNexusBinding = binding is not null;
        NexusBindingStateText = binding is null ? "未绑定" : "已绑定";
        NexusBindingAccountName = binding?.Name ?? string.Empty;
        NexusBindingTierText = binding is null
            ? string.Empty
            : binding.UserId == 0
                ? "已通过 Nexus 登录授权，可用于浏览和下载。"
            : binding.IsPremium
                ? "高级会员，可使用 Nexus 直接下载。"
                : binding.IsSupporter
                    ? "支持者账号；下载遵循 Nexus 账号限制。"
                    : "免费账号；下载遵循 Nexus 账号限制。";
        NotifyNexusBindingCommands();
    }

    private void NotifyNexusBindingCommands()
    {
        BindNexusAccountCommand.NotifyCanExecuteChanged();
        UnbindNexusAccountCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanBindNexusAccount));
        OnPropertyChanged(nameof(CanUnbindNexusAccount));
    }

    private static Bitmap? TryLoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, AvatarEditorDecodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string GetProviderDisplayName(string providerId) =>
        playerService.Providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? providerId;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ResetAvatarCropSettings()
    {
        suppressAvatarCropUpdate = true;
        try
        {
            EditorAvatarCropZoom = 1;
            EditorAvatarCropHorizontalOffset = 0;
            EditorAvatarCropVerticalOffset = 0;
        }
        finally
        {
            suppressAvatarCropUpdate = false;
        }
    }

    private void QueueAvatarCropUpdate()
    {
        if (suppressAvatarCropUpdate || !IsEditorOpen || string.IsNullOrWhiteSpace(editorAvatarSourcePath))
        {
            return;
        }

        avatarCropCancellation?.Cancel();
        avatarCropCancellation = new CancellationTokenSource();
        _ = ApplyAvatarCropAfterDelayAsync(editorAvatarSourcePath, avatarCropCancellation);
    }

    private async Task ApplyAvatarCropAfterDelayAsync(string sourcePath, CancellationTokenSource cancellationSource)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationSource.Token);
            await ApplyAvatarCropAsync(sourcePath, cancellationSource.Token);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(avatarCropCancellation, cancellationSource))
            {
                avatarCropCancellation = null;
            }

            cancellationSource.Dispose();
        }
    }

    private async Task ApplyAvatarCropAsync(string sourcePath, CancellationToken cancellationToken)
    {
        try
        {
            var croppedPath = await avatarCropService.CreateSquareAvatarAsync(
                new AvatarCropRequest(
                    sourcePath,
                    EditorAvatarCropZoom,
                    EditorAvatarCropHorizontalOffset,
                    EditorAvatarCropVerticalOffset),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || !string.Equals(editorAvatarSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                DeleteFile(croppedPath);
                return;
            }

            ClearAvatarCropFile();
            editorAvatarCropPath = croppedPath;
            EditorAvatarCroppedImage = TryLoadBitmap(croppedPath);
            suppressEditorAutoSave = true;
            try
            {
                EditorAvatarPath = croppedPath;
            }
            finally
            {
                suppressEditorAutoSave = false;
            }

            QueueEditorAutoSave();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to crop player avatar");
            ErrorMessage = ex.Message;
        }
    }

    private void ClearAvatarCropFile()
    {
        EditorAvatarCroppedImage = null;
        if (editorAvatarCropPath is not null)
        {
            DeleteFile(editorAvatarCropPath);
            editorAvatarCropPath = null;
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void QueueEditorAutoSave()
    {
        if (suppressEditorAutoSave || isCreating || !IsEditorOpen)
        {
            return;
        }

        editorAutoSaveCancellation?.Cancel();
        editorAutoSaveCancellation?.Dispose();
        editorAutoSaveCancellation = new CancellationTokenSource();
        EditorSaveStatus = "存在未保存的更改";
        _ = AutoSaveEditorAsync(editorAutoSaveCancellation);
    }

    private async Task AutoSaveEditorAsync(CancellationTokenSource cancellationSource)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationSource.Token);
            var displayName = EditorDisplayName.Trim();
            if (displayName.Length is < 1 or > 64)
            {
                EditorSaveStatus = "请输入 1 到 64 个字符后保存。";
                return;
            }

            if (EditorBio.Length > 280)
            {
                EditorSaveStatus = "个人简介最多 280 个字符。";
                return;
            }

            var update = new PlayerProfileUpdate(
                displayName,
                EditorAvatarPath,
                NullIfWhiteSpace(EditorBio));

            await editorAutoSaveGate.WaitAsync(cancellationSource.Token);
            try
            {
                cancellationSource.Token.ThrowIfCancellationRequested();
                var current = playerService.CurrentPlayer
                    ?? throw new InvalidOperationException("No current player is available.");
                EditorSaveStatus = "正在保存...";
                var updated = await playerService.UpdateAsync(current.Id, update, cancellationSource.Token);

                suppressEditorAutoSave = true;
                try
                {
                    EditorAvatarPath = updated.Avatar;
                }
                finally
                {
                    suppressEditorAutoSave = false;
                }

                ErrorMessage = string.Empty;
                EditorSaveStatus = $"已保存 {DateTimeOffset.Now:HH:mm:ss}";
            }
            finally
            {
                editorAutoSaveGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to auto-save player profile");
            ErrorMessage = ex.Message;
            EditorSaveStatus = "自动保存失败";
        }
        finally
        {
            if (ReferenceEquals(editorAutoSaveCancellation, cancellationSource))
            {
                editorAutoSaveCancellation = null;
            }

            cancellationSource.Dispose();
        }
    }

    public void Dispose()
    {
        playerService.CurrentPlayerChanged -= OnCurrentPlayerChanged;
        editorAutoSaveCancellation?.Cancel();
        editorAutoSaveCancellation?.Dispose();
        avatarCropCancellation?.Cancel();
        avatarCropCancellation?.Dispose();
        nexusAccountBindingCancellation?.Cancel();
        nexusAccountBindingCancellation?.Dispose();
        ClearAvatarCropFile();
        EditorAvatarImage = null;
        foreach (var player in Players)
        {
            player.Dispose();
        }

        Players.Clear();
    }
}

public sealed record PlayerPlatformOption(string Id, string DisplayName)
{
    public bool IsSteam => false;

    public bool IsGog => false;
}
