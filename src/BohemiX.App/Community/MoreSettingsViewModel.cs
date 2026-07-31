using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BohemiX.App.Controls;
using BohemiX.App.Services;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.App.Community;

public sealed class LegalProjectNotice
{
    public LegalProjectNotice(
        string name,
        string description,
        string license,
        string projectUrl,
        string licenseUrl,
        string projectButtonText,
        string licenseButtonText,
        Action<string> openUrl)
    {
        Name = name;
        Description = description;
        License = license;
        ProjectUrl = projectUrl;
        LicenseUrl = licenseUrl;
        ProjectButtonText = projectButtonText;
        LicenseButtonText = licenseButtonText;
        OpenProjectCommand = new RelayCommand(() => openUrl(ProjectUrl));
        OpenLicenseCommand = new RelayCommand(() => openUrl(LicenseUrl));
    }

    public string Name { get; }
    public string Description { get; }
    public string License { get; }
    public string ProjectUrl { get; }
    public string LicenseUrl { get; }
    public string ProjectButtonText { get; }
    public string LicenseButtonText { get; }
    public IRelayCommand OpenProjectCommand { get; }
    public IRelayCommand OpenLicenseCommand { get; }
}

public enum SponsorAppearanceAccessLevel
{
    None,
    Static,
    Dynamic
}

public sealed partial class MoreSettingsViewModel : ObservableObject, IDisposable
{
    private const string ApplicationNameValue = "BohemiX";
    private const string GitHubRepositoryUrl = "https://github.com/Luming-Sky/BohemiX";
    private const int SubjectMaximumLength = 80;
    private const int MessageMaximumLength = 2000;
    private const int ContactMaximumLength = 120;
    private const int SupporterIdentifierMaximumLength = 120;
    private static readonly TimeSpan EchoCaveVerificationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(20);
    private const string DefaultBackgroundUri = "avares://BohemiX.App/Assets/kcd2-cover-bg.jpg";
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".avi", ".mov", ".wmv", ".m4v"];

    private readonly ICommunityHubService communityHubService;
    private readonly IApplicationUpdateCheckService updateCheckService;
    private readonly IAppSettingsService? appSettingsService;
    private readonly IGamePathPickerService pathPickerService;
    private readonly ILogger logger;
    private bool hasInitialized;
    private string sponsorUrl = string.Empty;
    private bool useEnglish;
    private CancellationTokenSource? echoAnimationCancellation;
    private CancellationTokenSource? contentLoadCancellation;
    private readonly SemaphoreSlim appearanceSaveGate = new(1, 1);
    private string echoPreviewLineOneText = string.Empty;
    private string echoPreviewLineTwoText = string.Empty;
    private string echoPreviewLineThreeText = string.Empty;
    private int echoPreviewIndex;
    private bool isApplyingAppearanceSettings;
    private bool areVisualResourcesActive;
    private bool disposed;
    private long contentLoadGeneration;
    private ApplicationUpdateCheckResult? lastUpdateCheckResult;
    private string verifiedEchoCavePlanName = string.Empty;

    public MoreSettingsViewModel(
        ICommunityHubService communityHubService,
        IAppSettingsService appSettingsService,
        IGamePathPickerService pathPickerService,
        IApplicationUpdateCheckService updateCheckService,
        ILogger logger)
    {
        this.communityHubService = communityHubService;
        this.appSettingsService = appSettingsService;
        this.pathPickerService = pathPickerService;
        this.updateCheckService = updateCheckService;
        this.logger = logger.ForContext<MoreSettingsViewModel>();
        ApplyLanguage(useEnglish: false);
    }

    public MoreSettingsViewModel(
        ICommunityHubService communityHubService,
        IGamePathPickerService pathPickerService,
        IApplicationUpdateCheckService updateCheckService,
        ILogger logger)
    {
        this.communityHubService = communityHubService;
        this.pathPickerService = pathPickerService;
        this.updateCheckService = updateCheckService;
        this.logger = logger.ForContext<MoreSettingsViewModel>();
        ApplyLanguage(useEnglish: false);
    }

    public MoreSettingsViewModel(
        ICommunityHubService communityHubService,
        IGamePathPickerService pathPickerService,
        ILogger logger)
        : this(
            communityHubService,
            pathPickerService,
            UnavailableApplicationUpdateCheckService.Instance,
            logger)
    {
    }

    public ObservableCollection<CommunityPerson> SpecialThanks { get; } = [];

    public ObservableCollection<CommunityPerson> Sponsors { get; } = [];

    public ObservableCollection<LegalProjectNotice> LegalProjects { get; } = [];

    public string ApplicationName => ApplicationNameValue;

    public string ApplicationVersion { get; } = GetApplicationVersion();

    public bool CanCheckForUpdates => !IsCheckingForUpdates;

    public bool HasUpdateCheckError =>
        HasUpdateCheckResult && !IsUpdateCheckSuccessful;

    public bool HasSpecialThanks => SpecialThanks.Count > 0;

    public bool HasSponsors => Sponsors.Count > 0;

    public bool HasAnyCommunityContent => HasSpecialThanks || HasSponsors;

    public bool IsSponsorLinkAvailable => !string.IsNullOrWhiteSpace(sponsorUrl);

    public bool CanSubmitFeedback =>
        IsFeedbackConfigured
        && (!IsEchoCaveAccessRestricted || IsEchoCaveAccessGranted)
        && !IsSubmittingFeedback
        && FeedbackMessage.Trim().Length >= 10;

    public bool CanUseStaticAppearance =>
        !IsEchoCaveAccessRestricted || AppearanceAccessLevel >= SponsorAppearanceAccessLevel.Static;

    public bool CanUseDynamicAppearance =>
        !IsEchoCaveAccessRestricted || AppearanceAccessLevel >= SponsorAppearanceAccessLevel.Dynamic;

    public bool CanUseSponsorFeatures => CanUseStaticAppearance;

    public bool IsSponsorFeatureLocked => !CanUseSponsorFeatures;

    public bool IsDynamicAppearanceUpgradeRequired =>
        CanUseStaticAppearance && !CanUseDynamicAppearance;

    public bool IsEchoCaveFeatureLocked =>
        IsEchoCaveAccessRestricted && !IsEchoCaveAccessGranted;

    public double SponsorFeatureContentOpacity => IsSponsorFeatureLocked ? 0.18 : 1;

    public double EchoCaveContentOpacity => IsEchoCaveFeatureLocked ? 0.16 : 1;

    public bool CanVerifyEchoCaveAccess =>
        IsEchoCaveAccessRestricted
        && !IsVerifyingEchoCaveAccess
        && !string.IsNullOrWhiteSpace(AfdianSupporterIdentifier);

    public string SubjectCountText => $"{FeedbackSubject.Length}/{SubjectMaximumLength}";

    public string MessageCountText => $"{FeedbackMessage.Length}/{MessageMaximumLength}";

    [ObservableProperty]
    private bool isLoadingContent;

    [ObservableProperty]
    private bool isContentConfigured;

    [ObservableProperty]
    private bool hasContentLoadError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmitFeedback))]
    [NotifyCanExecuteChangedFor(nameof(SubmitFeedbackCommand))]
    private bool isFeedbackConfigured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmitFeedback))]
    [NotifyPropertyChangedFor(nameof(CanVerifyEchoCaveAccess))]
    [NotifyPropertyChangedFor(nameof(CanUseStaticAppearance), nameof(CanUseDynamicAppearance), nameof(CanUseSponsorFeatures), nameof(IsSponsorFeatureLocked), nameof(IsDynamicAppearanceUpgradeRequired), nameof(IsEchoCaveFeatureLocked), nameof(SponsorFeatureContentOpacity), nameof(EchoCaveContentOpacity), nameof(HasSelectedBackground), nameof(ShowStaticBackground), nameof(ShowDynamicBackground), nameof(ShowDynamicImageBackground), nameof(ShowDynamicVideoBackground), nameof(ShowDefaultBackground), nameof(ShowBackdropReflection), nameof(EffectiveBackdropReflectionSource), nameof(EffectiveCardBackgroundUri))]
    [NotifyCanExecuteChangedFor(nameof(VerifyEchoCaveAccessCommand))]
    private bool isEchoCaveAccessRestricted = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmitFeedback))]
    [NotifyPropertyChangedFor(nameof(IsEchoCaveFeatureLocked), nameof(EchoCaveContentOpacity))]
    private bool isEchoCaveAccessGranted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseStaticAppearance), nameof(CanUseDynamicAppearance), nameof(CanUseSponsorFeatures), nameof(IsSponsorFeatureLocked), nameof(IsDynamicAppearanceUpgradeRequired), nameof(SponsorFeatureContentOpacity), nameof(HasSelectedBackground), nameof(ShowStaticBackground), nameof(ShowDynamicBackground), nameof(ShowDynamicImageBackground), nameof(ShowDynamicVideoBackground), nameof(ShowDefaultBackground), nameof(ShowBackdropReflection), nameof(EffectiveBackdropReflectionSource), nameof(EffectiveCardBackgroundUri))]
    private SponsorAppearanceAccessLevel appearanceAccessLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanVerifyEchoCaveAccess))]
    [NotifyCanExecuteChangedFor(nameof(VerifyEchoCaveAccessCommand))]
    private bool isVerifyingEchoCaveAccess;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanVerifyEchoCaveAccess))]
    [NotifyCanExecuteChangedFor(nameof(VerifyEchoCaveAccessCommand))]
    private string afdianSupporterIdentifier = string.Empty;

    [ObservableProperty]
    private string echoCaveAccessStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmitFeedback))]
    [NotifyCanExecuteChangedFor(nameof(SubmitFeedbackCommand))]
    private bool isSubmittingFeedback;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubjectCountText))]
    [NotifyPropertyChangedFor(nameof(CanSubmitFeedback))]
    [NotifyCanExecuteChangedFor(nameof(SubmitFeedbackCommand))]
    private string feedbackSubject = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageCountText))]
    [NotifyPropertyChangedFor(nameof(CanSubmitFeedback))]
    [NotifyCanExecuteChangedFor(nameof(SubmitFeedbackCommand))]
    private string feedbackMessage = string.Empty;

    [ObservableProperty]
    private string feedbackContact = string.Empty;

    [ObservableProperty]
    private string contentStatusText = string.Empty;

    [ObservableProperty]
    private string feedbackStatusText = string.Empty;

    [ObservableProperty]
    private bool isFeedbackSuccess;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool isCheckingForUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateCheckError))]
    private bool hasUpdateCheckResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateCheckError))]
    private bool isUpdateCheckSuccessful;

    [ObservableProperty]
    private string softwareDetailsDescriptionText = string.Empty;

    [ObservableProperty]
    private string checkForUpdatesText = string.Empty;

    [ObservableProperty]
    private string openGitHubText = string.Empty;

    [ObservableProperty]
    private string updateCheckStatusText = string.Empty;

    [ObservableProperty]
    private string specialThanksTitle = string.Empty;

    [ObservableProperty]
    private string specialThanksDescription = string.Empty;

    [ObservableProperty]
    private string sponsorsTitle = string.Empty;

    [ObservableProperty]
    private string sponsorsDescription = string.Empty;

    [ObservableProperty]
    private string sponsorButtonText = string.Empty;

    [ObservableProperty]
    private string echoCaveTitle = string.Empty;

    [ObservableProperty]
    private string echoCaveLockedTitleText = string.Empty;

    [ObservableProperty]
    private string echoCaveLockedDescriptionText = string.Empty;

    [ObservableProperty]
    private string afdianSupporterIdentifierLabel = string.Empty;

    [ObservableProperty]
    private string afdianSupporterIdentifierWatermark = string.Empty;

    [ObservableProperty]
    private string verifyEchoCaveAccessText = string.Empty;

    [ObservableProperty]
    private string echoPreviewLabel = string.Empty;

    [ObservableProperty]
    private string echoPreviewLineOne = string.Empty;

    [ObservableProperty]
    private bool isEchoLineOneCaretVisible;

    [ObservableProperty]
    private string feedbackSubjectLabel = string.Empty;

    [ObservableProperty]
    private string feedbackSubjectWatermark = string.Empty;

    [ObservableProperty]
    private string feedbackMessageLabel = string.Empty;

    [ObservableProperty]
    private string feedbackMessageWatermark = string.Empty;

    [ObservableProperty]
    private string feedbackContactLabel = string.Empty;

    [ObservableProperty]
    private string feedbackContactWatermark = string.Empty;

    [ObservableProperty]
    private string feedbackPrivacyText = string.Empty;

    [ObservableProperty]
    private string submitFeedbackText = string.Empty;

    [ObservableProperty]
    private string retryText = string.Empty;

    [ObservableProperty]
    private string emptySpecialThanksText = string.Empty;

    [ObservableProperty]
    private string emptySponsorsText = string.Empty;

    [ObservableProperty]
    private string legalTitleText = string.Empty;

    [ObservableProperty]
    private string legalDescriptionText = string.Empty;

    [ObservableProperty]
    private string legalProjectsTitleText = string.Empty;

    [ObservableProperty]
    private string legalProjectsDescriptionText = string.Empty;

    [ObservableProperty]
    private string legalProjectButtonText = string.Empty;

    [ObservableProperty]
    private string legalLicenseButtonText = string.Empty;

    [ObservableProperty]
    private string appearanceTitleText = string.Empty;

    [ObservableProperty]
    private string appearanceDescriptionText = string.Empty;

    [ObservableProperty]
    private string appearanceSponsorLockedTitleText = string.Empty;

    [ObservableProperty]
    private string appearanceSponsorLockedDescriptionText = string.Empty;

    [ObservableProperty]
    private string dynamicAppearanceUpgradeText = string.Empty;

    [ObservableProperty]
    private string windowBackgroundText = string.Empty;

    [ObservableProperty]
    private string cardTextureText = string.Empty;

    [ObservableProperty]
    private string staticBackgroundText = string.Empty;

    [ObservableProperty]
    private string dynamicBackgroundText = string.Empty;

    [ObservableProperty]
    private string cardBackgroundText = string.Empty;

    [ObservableProperty]
    private string dynamicCardBackgroundText = string.Empty;

    [ObservableProperty]
    private string scrollingCardBackgroundText = string.Empty;

    [ObservableProperty]
    private string chooseMediaText = string.Empty;

    [ObservableProperty]
    private string restoreDefaultText = string.Empty;

    [ObservableProperty]
    private string backgroundCropText = string.Empty;

    [ObservableProperty]
    private string cropCenterText = string.Empty;

    [ObservableProperty]
    private string cropTopText = string.Empty;

    [ObservableProperty]
    private string cropBottomText = string.Empty;

    [ObservableProperty]
    private string cropLeftText = string.Empty;

    [ObservableProperty]
    private string cropRightText = string.Empty;

    [ObservableProperty]
    private string restoreBackgroundText = string.Empty;

    [ObservableProperty]
    private string cropBackgroundText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStaticBackground), nameof(HasSelectedBackground), nameof(ShowStaticBackground), nameof(ShowDefaultBackground), nameof(ShowBackdropReflection), nameof(EffectiveBackdropReflectionSource), nameof(StaticBackgroundFileText), nameof(WindowBackgroundFileText))]
    private string? staticBackgroundPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDynamicBackground), nameof(HasSelectedBackground), nameof(ShowDynamicBackground), nameof(ShowDynamicImageBackground), nameof(ShowDynamicVideoBackground), nameof(ShowDefaultBackground), nameof(ShowBackdropReflection), nameof(EffectiveBackdropReflectionSource), nameof(DynamicBackgroundFileText), nameof(WindowBackgroundFileText))]
    private string? dynamicBackgroundPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStaticBackgroundSelected), nameof(IsDynamicBackgroundSelected), nameof(HasSelectedBackground), nameof(ShowStaticBackground), nameof(ShowDynamicBackground), nameof(ShowDynamicImageBackground), nameof(ShowDynamicVideoBackground), nameof(ShowDefaultBackground), nameof(ShowBackdropReflection), nameof(EffectiveBackdropReflectionSource), nameof(WindowBackgroundMode), nameof(WindowBackgroundFileText))]
    private bool useDynamicBackground;

    [ObservableProperty]
    private int backgroundCropMode;

    [ObservableProperty]
    private double backgroundCropX;

    [ObservableProperty]
    private double backgroundCropY;

    [ObservableProperty]
    private double backgroundCropWidth = 1;

    [ObservableProperty]
    private double backgroundCropHeight = 1;

    public event EventHandler? BackgroundCropRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCardBackground), nameof(CardBackgroundFileText), nameof(HasStaticCardBackground), nameof(StaticCardBackgroundFileText), nameof(EffectiveCardBackgroundUri), nameof(SelectedCardTextureFileText))]
    private string? cardBackgroundPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDynamicCardBackground), nameof(DynamicCardBackgroundFileText), nameof(EffectiveCardBackgroundUri), nameof(SelectedCardTextureFileText))]
    private string? dynamicCardBackgroundPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveCardBackgroundUri), nameof(CardTextureMode), nameof(SelectedCardTextureFileText))]
    private bool useDynamicCardBackground;

    public bool HasStaticBackground => !string.IsNullOrWhiteSpace(StaticBackgroundPath) && File.Exists(StaticBackgroundPath);

    public bool HasDynamicBackground => !string.IsNullOrWhiteSpace(DynamicBackgroundPath) && File.Exists(DynamicBackgroundPath);

    public bool HasSelectedBackground =>
        UseDynamicBackground
            ? CanUseDynamicAppearance && HasDynamicBackground
            : CanUseStaticAppearance && HasStaticBackground;

    public bool HasCardBackground => !string.IsNullOrWhiteSpace(CardBackgroundPath) && File.Exists(CardBackgroundPath);

    public bool HasStaticCardBackground => HasCardBackground;

    public bool HasDynamicCardBackground => !string.IsNullOrWhiteSpace(DynamicCardBackgroundPath) && File.Exists(DynamicCardBackgroundPath);

    public bool IsStaticBackgroundSelected => !UseDynamicBackground;

    public bool IsDynamicBackgroundSelected => UseDynamicBackground;

    public int WindowBackgroundMode
    {
        get => UseDynamicBackground ? 1 : 0;
        set
        {
            if ((value == 0 && CanUseStaticAppearance)
                || (value == 1 && CanUseDynamicAppearance))
            {
                UseDynamicBackground = value == 1;
            }
        }
    }

    public int CardTextureMode
    {
        get => UseDynamicCardBackground ? 1 : 0;
        set
        {
            if ((value == 0 && CanUseStaticAppearance)
                || (value == 1 && CanUseDynamicAppearance))
            {
                UseDynamicCardBackground = value == 1;
            }
        }
    }

    public bool ShowStaticBackground =>
        CanUseStaticAppearance && !UseDynamicBackground && HasStaticBackground;

    public bool ShowDynamicBackground =>
        CanUseDynamicAppearance && UseDynamicBackground && HasDynamicBackground;

    public bool ShowDynamicImageBackground => ShowDynamicBackground && !IsVideoPath(DynamicBackgroundPath);

    public bool ShowDynamicVideoBackground => ShowDynamicBackground && IsVideoPath(DynamicBackgroundPath);

    public bool ShowDefaultBackground => !ShowDynamicVideoBackground;

    // Reflections are bitmap snapshots. Let user-selected media show through directly so
    // crop settings, animated frames, and video stay aligned with the real backdrop.
    public bool ShowBackdropReflection => !HasSelectedBackground;

    public string EffectiveBackdropReflectionSource => ShowStaticBackground
        ? StaticBackgroundPath!
        : ShowDynamicImageBackground
            ? DynamicBackgroundPath!
            : DefaultBackgroundUri;

    public string StaticCardBackgroundFileText => CardBackgroundFileText;

    public string DynamicCardBackgroundFileText => HasDynamicCardBackground
        ? Path.GetFileName(DynamicCardBackgroundPath) ?? string.Empty
        : L("No scrolling texture selected", "未选择滚动纹理");

    public string StaticBackgroundFileText => HasStaticBackground
        ? Path.GetFileName(StaticBackgroundPath) ?? string.Empty
        : L("No image selected", "未选择图片");

    public string DynamicBackgroundFileText => HasDynamicBackground
        ? Path.GetFileName(DynamicBackgroundPath) ?? string.Empty
        : L("No animated image selected", "未选择动态图片");

    public string WindowBackgroundFileText => UseDynamicBackground
        ? DynamicBackgroundFileText
        : StaticBackgroundFileText;

    public string CardBackgroundFileText => HasCardBackground
        ? Path.GetFileName(CardBackgroundPath) ?? string.Empty
        : L("Using built-in texture", "使用内置纹理");

    public string SelectedCardTextureFileText => UseDynamicCardBackground
        ? DynamicCardBackgroundFileText
        : CardBackgroundFileText;

    public string? StaticBackgroundUri => HasStaticBackground ? StaticBackgroundPath : null;

    public string? DynamicBackgroundUri => HasDynamicBackground
        ? new Uri(Path.GetFullPath(DynamicBackgroundPath!), UriKind.Absolute).AbsoluteUri
        : null;

    public string? EffectiveCardBackgroundUri => UseDynamicCardBackground
        ? CanUseDynamicAppearance && HasDynamicCardBackground
            ? DynamicCardBackgroundPath
            : null
        : CanUseStaticAppearance && HasStaticCardBackground
            ? CardBackgroundPath
            : null;

    [RelayCommand]
    private async Task ChooseStaticBackgroundAsync()
    {
        if (!CanUseStaticAppearance)
        {
            return;
        }

        var path = await pathPickerService.PickAppearanceMediaAsync(animated: false);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        StaticBackgroundPath = path;
        UseDynamicBackground = false;
        await PersistAppearanceSettingsAsync();
    }

    [RelayCommand]
    private async Task ChooseDynamicBackgroundAsync()
    {
        if (!CanUseDynamicAppearance)
        {
            return;
        }

        var path = await pathPickerService.PickAppearanceMediaAsync(animated: true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        DynamicBackgroundPath = path;
        UseDynamicBackground = true;
        await PersistAppearanceSettingsAsync();
    }

    [RelayCommand]
    private async Task ChooseStaticCardBackgroundAsync()
    {
        if (!CanUseStaticAppearance)
        {
            return;
        }

        var path = await pathPickerService.PickAppearanceMediaAsync(animated: false);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        CardBackgroundPath = path;
        UseDynamicCardBackground = false;
        await PersistAppearanceSettingsAsync();
    }

    [RelayCommand]
    private async Task ChooseDynamicCardBackgroundAsync()
    {
        if (!CanUseDynamicAppearance)
        {
            return;
        }

        var path = await pathPickerService.PickAppearanceMediaAsync(animated: false);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        DynamicCardBackgroundPath = path;
        UseDynamicCardBackground = true;
        await PersistAppearanceSettingsAsync();
    }

    [RelayCommand]
    private Task ChooseWindowBackgroundAsync() => UseDynamicBackground
        ? ChooseDynamicBackgroundAsync()
        : ChooseStaticBackgroundAsync();

    [RelayCommand]
    private Task ChooseCardTextureAsync() => UseDynamicCardBackground
        ? ChooseDynamicCardBackgroundAsync()
        : ChooseStaticCardBackgroundAsync();

    [RelayCommand]
    private void OpenBackgroundCrop()
    {
        if (HasSelectedBackground)
        {
            BackgroundCropRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SetBackgroundCropAsync(double x, double y, double width, double height)
    {
        if (!HasSelectedBackground)
        {
            return;
        }

        BackgroundCropWidth = Math.Clamp(width, 0.01, 1);
        BackgroundCropHeight = Math.Clamp(height, 0.01, 1);
        BackgroundCropX = Math.Clamp(x, 0, 1 - BackgroundCropWidth);
        BackgroundCropY = Math.Clamp(y, 0, 1 - BackgroundCropHeight);
        BackgroundCropMode = 0;
        await PersistAppearanceSettingsAsync();
    }

    [RelayCommand]
    private async Task ResetWindowBackgroundAsync()
    {
        if (!CanUseStaticAppearance)
        {
            return;
        }

        StaticBackgroundPath = null;
        DynamicBackgroundPath = null;
        UseDynamicBackground = false;
        BackgroundCropMode = 0;
        BackgroundCropX = 0;
        BackgroundCropY = 0;
        BackgroundCropWidth = 1;
        BackgroundCropHeight = 1;
        await PersistAppearanceSettingsAsync();
    }

    [RelayCommand]
    private async Task ResetCardTextureAsync()
    {
        if (!CanUseStaticAppearance)
        {
            return;
        }

        CardBackgroundPath = null;
        DynamicCardBackgroundPath = null;
        UseDynamicCardBackground = false;
        await PersistAppearanceSettingsAsync();
    }

    [RelayCommand]
    private async Task ResetAppearanceAsync()
    {
        if (!CanUseStaticAppearance)
        {
            return;
        }

        StaticBackgroundPath = null;
        DynamicBackgroundPath = null;
        CardBackgroundPath = null;
        DynamicCardBackgroundPath = null;
        UseDynamicBackground = false;
        BackgroundCropMode = 0;
        BackgroundCropX = 0;
        BackgroundCropY = 0;
        BackgroundCropWidth = 1;
        BackgroundCropHeight = 1;
        UseDynamicCardBackground = false;
        await PersistAppearanceSettingsAsync();
    }

    partial void OnUseDynamicBackgroundChanged(bool value) => SaveAppearanceSettingsInBackground();

    partial void OnIsEchoCaveAccessRestrictedChanged(bool value) => ApplyCardAppearance();

    partial void OnIsEchoCaveAccessGrantedChanged(bool value) => ApplyCardAppearance();

    partial void OnAppearanceAccessLevelChanged(SponsorAppearanceAccessLevel value)
    {
        if (value == SponsorAppearanceAccessLevel.Static)
        {
            UseDynamicBackground = false;
            UseDynamicCardBackground = false;
        }

        ApplyCardAppearance();
    }

    partial void OnBackgroundCropModeChanged(int value)
    {
        if (value is < 0 or > 4)
        {
            BackgroundCropMode = 0;
            return;
        }

        if (!isApplyingAppearanceSettings)
        {
            SaveAppearanceSettingsInBackground();
        }
    }

    partial void OnUseDynamicCardBackgroundChanged(bool value)
    {
        ApplyCardAppearance();
        SaveAppearanceSettingsInBackground();
    }

    partial void OnStaticBackgroundPathChanged(string? value)
    {
        OnPropertyChanged(nameof(StaticBackgroundUri));
        SaveAppearanceSettingsInBackground();
    }

    partial void OnDynamicBackgroundPathChanged(string? value)
    {
        OnPropertyChanged(nameof(DynamicBackgroundUri));
        SaveAppearanceSettingsInBackground();
    }

    partial void OnCardBackgroundPathChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveCardBackgroundUri));
        ApplyCardAppearance();
        SaveAppearanceSettingsInBackground();
    }

    partial void OnDynamicCardBackgroundPathChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveCardBackgroundUri));
        ApplyCardAppearance();
        SaveAppearanceSettingsInBackground();
    }

    public void ApplyAppearanceSettings(AppSettings settings)
    {
        isApplyingAppearanceSettings = true;
        try
        {
            StaticBackgroundPath = ExistingFileOrNull(settings.StaticBackgroundPath);
            DynamicBackgroundPath = ExistingFileOrNull(settings.DynamicBackgroundPath);
            UseDynamicBackground = settings.UseDynamicBackground && HasDynamicBackground;
            BackgroundCropMode = Math.Clamp(settings.BackgroundCropMode, 0, 4);
            BackgroundCropX = Math.Clamp(settings.BackgroundCropX, 0, 1);
            BackgroundCropY = Math.Clamp(settings.BackgroundCropY, 0, 1);
            BackgroundCropWidth = Math.Clamp(settings.BackgroundCropWidth, 0.01, 1);
            BackgroundCropHeight = Math.Clamp(settings.BackgroundCropHeight, 0.01, 1);
            CardBackgroundPath = ExistingFileOrNull(settings.StaticCardBackgroundPath) ?? ExistingFileOrNull(settings.CardBackgroundPath);
            DynamicCardBackgroundPath = ExistingFileOrNull(settings.DynamicCardBackgroundPath);
            UseDynamicCardBackground = settings.UseDynamicCardBackground;
        }
        finally
        {
            isApplyingAppearanceSettings = false;
        }

        ApplyCardAppearance();
    }

    private void ApplyCardAppearance() =>
        StripedCardBorder.SetGlobalAppearance(EffectiveCardBackgroundUri, UseDynamicCardBackground && HasDynamicCardBackground);

    private void SaveAppearanceSettingsInBackground()
    {
        if (!isApplyingAppearanceSettings)
        {
            _ = PersistAppearanceSettingsAsync();
        }
    }

    private async Task PersistAppearanceSettingsAsync()
    {
        if (isApplyingAppearanceSettings)
        {
            return;
        }

        if (appSettingsService is null)
        {
            return;
        }

        await appearanceSaveGate.WaitAsync();
        try
        {
            var settings = await appSettingsService.LoadAsync();
            await appSettingsService.SaveAsync(settings with
            {
                StaticBackgroundPath = StaticBackgroundPath,
                DynamicBackgroundPath = DynamicBackgroundPath,
                UseDynamicBackground = UseDynamicBackground,
                BackgroundCropMode = BackgroundCropMode,
                BackgroundCropX = BackgroundCropX,
                BackgroundCropY = BackgroundCropY,
                BackgroundCropWidth = BackgroundCropWidth,
                BackgroundCropHeight = BackgroundCropHeight,
                CardBackgroundPath = CardBackgroundPath,
                UseDynamicCardBackground = UseDynamicCardBackground,
                StaticCardBackgroundPath = CardBackgroundPath,
                DynamicCardBackgroundPath = DynamicCardBackgroundPath
            });
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to save appearance media settings");
        }
        finally
        {
            appearanceSaveGate.Release();
        }
    }

    private static string? ExistingFileOrNull(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    public static bool IsVideoFile(string? path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension)
            && Array.Exists(VideoExtensions, candidate => extension.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVideoPath(string? path) => IsVideoFile(path);

    public void ApplyLanguage(bool useEnglish)
    {
        StopEchoPreviewAnimation();
        this.useEnglish = useEnglish;
        SpecialThanksTitle = L("Special Honors", "特别荣誉");
        SpecialThanksDescription = L(
            "Sponsors whose support has earned a place of special honor.",
            "向给予重要支持的赞助者致以特别荣誉。");
        SponsorsTitle = L("Sponsors", "赞助者");
        SponsorsDescription = L(
            "Supporters who help sustain development and maintenance.",
            "感谢为开发与维护提供支持的赞助者。");
        SponsorButtonText = L("Become a Sponsor", "成为赞助者");
        EchoCaveTitle = L("Echo Cave Submission", "\u56de\u58f0\u6d1e\u6295\u7a3f");
        EchoCaveLockedTitleText = L("Sponsor access required", "\u9700\u8981\u8d5e\u52a9\u6743\u76ca");
        EchoCaveLockedDescriptionText = L(
            "Sponsor Smoked Cheese or Savior Schnapps, then verify your supporter ID or nickname to submit.",
            "\u8d5e\u52a9\u201c\u718f\u5976\u916a\u201d\u6216\u201c\u6551\u4e16\u5e72\u9152\u201d\u65b9\u6848\u540e\uff0c\u9a8c\u8bc1\u8d5e\u52a9\u8005 ID \u6216\u6635\u79f0\u5373\u53ef\u6295\u7a3f\u3002");
        EchoPreviewLabel = L("Voices in the cave", "洞内回声");
        AfdianSupporterIdentifierLabel = L("Afdian supporter ID or nickname", "\u7231\u53d1\u7535\u8d5e\u52a9\u8005 ID \u6216\u6635\u79f0");
        AfdianSupporterIdentifierWatermark = L("Enter the ID or nickname used for your support", "\u8f93\u5165\u8d5e\u52a9\u65f6\u4f7f\u7528\u7684 ID \u6216\u6635\u79f0");
        VerifyEchoCaveAccessText = L("Verify access", "\u9a8c\u8bc1\u8d44\u683c");
        RefreshEchoCaveAccessStatusText();
        echoPreviewLineOneText = L(
            "May every launch bring less friction and more immersion.",
            "愿每一次启动，都少一点折腾，多一点沉浸。");
        echoPreviewLineTwoText = L(
            "Good ideas do not disappear. They echo into the next update.",
            "好的建议不会消失，它会在下一次更新里回响。");
        echoPreviewLineThreeText = L(
            "Thank you for leaving your voice here.",
            "谢谢你把声音留在这里。");
        echoPreviewIndex = 0;
        ShowCompleteEchoPreview();
        FeedbackSubjectLabel = L("Subject", "主题");
        FeedbackSubjectWatermark = L("Summarize your feedback", "简要概括你的反馈");
        FeedbackMessageLabel = L("Message", "内容");
        FeedbackMessageWatermark = L("Describe what happened or what you would like to see", "描述遇到的问题或希望增加的功能");
        FeedbackContactLabel = L("Contact (optional)", "联系方式（可选）");
        FeedbackContactWatermark = L("Email, Discord, or another way to reply", "邮箱、Discord 或其他回复方式");
        FeedbackPrivacyText = L(
            "Only the text you enter and the app version are submitted.",
            "仅提交你填写的内容和应用版本，不会自动收集玩家资料或设备标识。");
        SubmitFeedbackText = L("Send Feedback", "发送反馈");
        RetryText = L("Retry", "重试");
        EmptySpecialThanksText = L("No special honors have been published yet.", "暂未发布特别荣誉名单。");
        EmptySponsorsText = L("No sponsors have been published yet.", "暂未发布赞助者名单。");
        AppearanceTitleText = L("Appearance media", "外观背景");
        AppearanceDescriptionText = L(
            "Choose a still image or animated image for the window, then set the card texture to stay still or flow.",
            "为窗口选择静态图片或动态图片，并设置卡片纹理为静态或流动效果。");
        AppearanceSponsorLockedTitleText = L(
            "Sponsor access required",
            "需要赞助权益");
        AppearanceSponsorLockedDescriptionText = L(
            "Bread unlocks still backgrounds. Smoked Cheese and Savior Schnapps also unlock animated window and card backgrounds.",
            "“面包”方案可解锁静态自定义背景；“熏奶酪”和“救世干酒”还可解锁动态窗口与动态卡片背景。");
        DynamicAppearanceUpgradeText = L(
            "Animated backgrounds require Smoked Cheese or Savior Schnapps.",
            "动态背景需要“熏奶酪”或“救世干酒”方案");
        WindowBackgroundText = L("Window background", "窗口背景");
        CardTextureText = L("Card texture", "卡片纹理");
        StaticBackgroundText = L("Still window background", "静态窗口背景");
        DynamicBackgroundText = L("Animated image background", "动态图片窗口背景");
        CardBackgroundText = L("Still card texture", "静态卡片纹理");
        DynamicCardBackgroundText = L("Scrolling card texture", "滚动卡片纹理");
        ScrollingCardBackgroundText = L("Scroll card texture", "滚动卡片背景");
        ChooseMediaText = L("Choose", "选择文件");
        RestoreDefaultText = L("Restore defaults", "恢复默认");
        BackgroundCropText = L("Crop focus", "裁剪范围");
        CropCenterText = L("Center", "居中");
        CropTopText = L("Top", "顶部");
        CropBottomText = L("Bottom", "底部");
        CropLeftText = L("Left", "左侧");
        CropRightText = L("Right", "右侧");
        RestoreBackgroundText = L("Restore background", "恢复背景默认");
        CropBackgroundText = L("Free crop", "自由裁剪");
        SoftwareDetailsDescriptionText = L(
            "Launcher and companion tools for Kingdom Come: Deliverance II.",
            "《天国：拯救 II》启动器与游戏辅助工具。");
        CheckForUpdatesText = L("Check for updates", "检查更新");
        OpenGitHubText = L("Open GitHub", "打开 GitHub");
        LegalTitleText = L("Copyright & legal", "\u7248\u6743\u4e0e\u6cd5\u5f8b\u58f0\u660e");
        LegalDescriptionText = L(
            "Copyright (c) 2026 Lume_Sky. BohemiX is provided under the MIT License, without warranty of any kind. Third-party names and trademarks belong to their respective owners.",
            "Copyright (c) 2026 Lume_Sky\u3002BohemiX \u4f9d MIT \u8bb8\u53ef\u8bc1\u63d0\u4f9b\uff0c\u4e0d\u9644\u5e26\u4efb\u4f55\u660e\u793a\u6216\u9ed8\u793a\u62c5\u4fdd\u3002\u7b2c\u4e09\u65b9\u9879\u76ee\u540d\u79f0\u53ca\u5546\u6807\u5f52\u5404\u81ea\u6743\u5229\u4eba\u6240\u6709\u3002");
        LegalProjectsTitleText = L("Projects & attributions", "\u5f15\u7528\u9879\u76ee\u4e0e\u5f52\u5c5e");
        LegalProjectsDescriptionText = L(
            "BohemiX is built with the following projects and attributed assets. Open a project or its license for the full terms.",
            "BohemiX \u4f7f\u7528\u4e86\u4ee5\u4e0b\u9879\u76ee\u4e0e\u5df2\u6807\u6ce8\u7d20\u6750\u3002\u53ef\u8df3\u8f6c\u81f3\u9879\u76ee\u6216\u8bb8\u53ef\u8bc1\u9875\u9762\u67e5\u770b\u5b8c\u6574\u6761\u6b3e\u3002");
        LegalProjectButtonText = L("Project", "\u9879\u76ee");
        LegalLicenseButtonText = L("License", "\u534f\u8bae");
        RefreshLegalProjects();
        RefreshUpdateCheckStatusText();
        RefreshContentStatusText();
        if (!IsSubmittingFeedback && !IsFeedbackSuccess)
        {
            FeedbackStatusText = IsFeedbackConfigured
                ? L("Ready for your feedback.", "欢迎留下你的反馈。")
                : L("The feedback endpoint has not been configured yet.", "回声洞提交接口尚未配置。");
        }
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        areVisualResourcesActive = true;
        if (hasInitialized)
        {
            return;
        }

        hasInitialized = true;
        await LoadContentAsync(cancellationToken);
    }

    public Task ActivateVisualResourcesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        areVisualResourcesActive = true;
        return EnsureInitializedAsync(cancellationToken);
    }

    public void DeactivateVisualResources()
    {
        areVisualResourcesActive = false;
        hasInitialized = false;
        contentLoadGeneration++;
        var cancellation = contentLoadCancellation;
        contentLoadCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        StopEchoPreviewAnimation();
        ClearCommunityContent();
    }

    public async Task PlayEchoPreviewAsync()
    {
        StopEchoPreviewAnimation();

        var animation = new CancellationTokenSource();
        echoAnimationCancellation = animation;
        var cancellationToken = animation.Token;
        var previewText = GetEchoPreviewText();

        EchoPreviewLineOne = string.Empty;

        try
        {
            await Task.Delay(180, cancellationToken);
            await TypeEchoLineAsync(previewText, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(echoAnimationCancellation, animation))
            {
                echoAnimationCancellation = null;
                IsEchoLineOneCaretVisible = false;
            }

            animation.Dispose();
        }
    }

    [RelayCommand]
    private Task NextEchoPreviewAsync()
    {
        echoPreviewIndex = (echoPreviewIndex + 1) % 3;
        return PlayEchoPreviewAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        IsCheckingForUpdates = true;
        HasUpdateCheckResult = false;
        IsUpdateCheckSuccessful = false;
        UpdateCheckStatusText = L("Checking for updates...", "正在检查更新……");
        using var updateCheckCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        updateCheckCancellation.CancelAfter(UpdateCheckTimeout);

        try
        {
            lastUpdateCheckResult = await updateCheckService.CheckAsync(
                ApplicationVersion,
                updateCheckCancellation.Token);
            IsUpdateCheckSuccessful = true;
            HasUpdateCheckResult = true;
            IsCheckingForUpdates = false;
            RefreshUpdateCheckStatusText();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lastUpdateCheckResult = null;
            UpdateCheckStatusText = L("Update check was cancelled.", "已取消检查更新。");
        }
        catch (OperationCanceledException) when (updateCheckCancellation.IsCancellationRequested)
        {
            logger.Warning("BohemiX update check timed out after {TimeoutSeconds} seconds", UpdateCheckTimeout.TotalSeconds);
            lastUpdateCheckResult = null;
            IsUpdateCheckSuccessful = false;
            HasUpdateCheckResult = true;
            UpdateCheckStatusText = L(
                "Update check timed out. Try again later.",
                "检查更新超时，请稍后重试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            logger.Warning(ex, "Unable to check for a BohemiX update on GitHub");
            lastUpdateCheckResult = null;
            IsUpdateCheckSuccessful = false;
            HasUpdateCheckResult = true;
            UpdateCheckStatusText = L(
                "Could not check for updates. Try again later.",
                "检查更新失败，请稍后重试。");
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GitHubRepositoryUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            logger.Warning(ex, "Unable to open the BohemiX GitHub repository");
            UpdateCheckStatusText = L(
                "Could not open GitHub.",
                "无法打开 GitHub 页面。");
        }
    }

    private void OpenLegalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            logger.Warning("Refusing to open an invalid legal notice URL: {Url}", url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            logger.Warning(ex, "Unable to open legal notice URL {Url}", uri);
        }
    }

    private void RefreshLegalProjects()
    {
        LegalProjects.Clear();

        AddLegalProject(
            "BohemiX",
            L("Launcher and companion tools for Kingdom Come: Deliverance II.", "\u300a\u5929\u56fd\uff1a\u62ef\u6551 II\u300b\u542f\u52a8\u5668\u4e0e\u6e38\u620f\u8f85\u52a9\u5de5\u5177\u3002"),
            "MIT",
            GitHubRepositoryUrl,
            "https://opensource.org/license/mit");
        AddLegalProject("Avalonia UI", L("Cross-platform UI framework.", "\u8de8\u5e73\u53f0 UI \u6846\u67b6\u3002"), "MIT", "https://github.com/AvaloniaUI/Avalonia", "https://opensource.org/license/mit");
        AddLegalProject("Semi.Avalonia", L("Avalonia control theme and styles.", "Avalonia \u63a7\u4ef6\u4e3b\u9898\u4e0e\u6837\u5f0f\u3002"), "MIT", "https://github.com/irihitech/Semi.Avalonia", "https://opensource.org/license/mit");
        AddLegalProject("AnimatedImage.Avalonia / Native", L("Animated image playback and native WebP decoding.", "\u52a8\u56fe\u64ad\u653e\u4e0e\u539f\u751f WebP \u89e3\u7801\u3002"), "Apache-2.0 / BSD-3-Clause", "https://github.com/whistyun/AnimatedImage.Avalonia", "https://github.com/whistyun/AnimatedImage.Avalonia/tree/master/licenses");
        AddLegalProject("CommunityToolkit.Mvvm", L("MVVM source generators and application primitives.", "MVVM \u6e90\u751f\u6210\u5668\u4e0e\u5e94\u7528\u57fa\u7840\u7ec4\u4ef6\u3002"), "MIT", "https://github.com/CommunityToolkit/dotnet", "https://opensource.org/license/mit");
        AddLegalProject("LibVLCSharp / libVLC", L("Video playback used by animated backgrounds.", "\u52a8\u6001\u80cc\u666f\u4f7f\u7528\u7684\u89c6\u9891\u64ad\u653e\u7ec4\u4ef6\u3002"), "LGPL-2.1-or-later", "https://code.videolan.org/videolan/LibVLCSharp", "https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html");
        AddLegalProject("Serilog", L("Structured application logging.", "\u7ed3\u6784\u5316\u5e94\u7528\u65e5\u5fd7\u3002"), "Apache-2.0", "https://github.com/serilog/serilog", "https://www.apache.org/licenses/LICENSE-2.0");
        AddLegalProject("SkiaSharp", L("2D graphics and image rendering.", "2D \u56fe\u5f62\u4e0e\u56fe\u50cf\u6e32\u67d3\u3002"), "MIT", "https://github.com/mono/SkiaSharp", "https://opensource.org/license/mit");
        AddLegalProject("NAudio / NAudio.Vorbis", L("Audio playback and Ogg Vorbis support.", "\u97f3\u9891\u64ad\u653e\u4e0e Ogg Vorbis \u652f\u6301\u3002"), "MIT", "https://github.com/naudio/NAudio", "https://opensource.org/license/mit");
        AddLegalProject("Silk.NET", L("OpenGL bindings used by the forge module.", "\u953b\u9020\u6a21\u5757\u4f7f\u7528\u7684 OpenGL \u7ed1\u5b9a\u3002"), "MIT", "https://github.com/dotnet/Silk.NET", "https://opensource.org/license/mit");
        AddLegalProject("BCnEncoder.NET", L("GPU texture encoding and decoding.", "GPU \u7eb9\u7406\u7f16\u89e3\u7801\u3002"), "MIT OR Unlicense", "https://github.com/Nominom/BCnEncoder.NET", "https://github.com/Nominom/BCnEncoder.NET/blob/master/LICENSE");
        AddLegalProject("SharpGLTF", L("glTF model reading for the forge module.", "\u953b\u9020\u6a21\u5757\u7684 glTF \u6a21\u578b\u8bfb\u53d6\u3002"), "MIT", "https://github.com/vpenades/SharpGLTF", "https://opensource.org/license/mit");
        AddLegalProject("Dapper", L("Lightweight database object mapping.", "\u8f7b\u91cf\u6570\u636e\u5e93\u5bf9\u8c61\u6620\u5c04\u3002"), "Apache-2.0", "https://github.com/DapperLib/Dapper", "https://www.apache.org/licenses/LICENSE-2.0");
        AddLegalProject("Facepunch.Steamworks", L("Steam client integration.", "Steam \u5ba2\u6237\u7aef\u96c6\u6210\u3002"), "MIT", "https://github.com/Facepunch/Facepunch.Steamworks", "https://opensource.org/license/mit");
        AddLegalProject("Microsoft.Data.Sqlite", L("SQLite data provider.", "SQLite \u6570\u636e\u63d0\u4f9b\u7a0b\u5e8f\u3002"), "MIT", "https://github.com/dotnet/efcore", "https://opensource.org/license/mit");
        AddLegalProject("Microsoft.Extensions", L("Dependency injection and platform services.", "\u4f9d\u8d56\u6ce8\u5165\u4e0e\u5e73\u53f0\u670d\u52a1\u3002"), "MIT", "https://github.com/dotnet/runtime", "https://opensource.org/license/mit");
        AddLegalProject("Microsoft Edge WebView2", L("Embedded web content runtime integration.", "\u5185\u5d4c Web \u5185\u5bb9\u8fd0\u884c\u65f6\u96c6\u6210\u3002"), "Microsoft WebView2 SDK License", "https://developer.microsoft.com/microsoft-edge/webview2", "https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4022.49/license");
        AddLegalProject("SharpCompress", L("Archive reading and extraction.", "\u538b\u7f29\u5305\u8bfb\u53d6\u4e0e\u89e3\u538b\u3002"), "MIT", "https://github.com/adamhathcock/sharpcompress", "https://opensource.org/license/mit");
        AddLegalProject("usvfs", L("User-space virtual file system. Copyright (c) 2015-2024 Sebastian Herbord, ModOrganizer2 Team.", "\u7528\u6237\u7a7a\u95f4\u865a\u62df\u6587\u4ef6\u7cfb\u7edf\u3002Copyright (c) 2015-2024 Sebastian Herbord, ModOrganizer2 Team\u3002"), "GPL-3.0-or-later + Section 7 permissions", "https://github.com/ModOrganizer2/usvfs", "https://github.com/ModOrganizer2/usvfs/blob/master/LICENSE");
        AddLegalProject("Material Icons", L("Source glyphs adapted for the application icon library.", "\u5e94\u7528\u56fe\u6807\u5e93\u6539\u7f16\u6240\u4f7f\u7528\u7684\u6e90\u56fe\u5f62\u3002"), "Apache-2.0", "https://fonts.google.com/icons", "https://www.apache.org/licenses/LICENSE-2.0");
        AddLegalProject("Freesound / Kenney audio", L("CC0 sounds by liambratchford, beskhu, RossJuterbock, lensson, Vrymaa, NickTayloe, and Kenney.", "\u7531 liambratchford\u3001beskhu\u3001RossJuterbock\u3001lensson\u3001Vrymaa\u3001NickTayloe \u4e0e Kenney \u63d0\u4f9b\u7684 CC0 \u97f3\u6548\u3002"), "CC0-1.0", "https://freesound.org/help/faq/#licenses-0", "https://creativecommons.org/publicdomain/zero/1.0/");
    }

    private void AddLegalProject(
        string name,
        string description,
        string license,
        string projectUrl,
        string licenseUrl) =>
        LegalProjects.Add(new LegalProjectNotice(
            name,
            description,
            license,
            projectUrl,
            licenseUrl,
            LegalProjectButtonText,
            LegalLicenseButtonText,
            OpenLegalUrl));

    [RelayCommand]
    private async Task LoadContentAsync(CancellationToken cancellationToken = default)
    {
        var generation = ++contentLoadGeneration;
        contentLoadCancellation?.Cancel();
        contentLoadCancellation?.Dispose();
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        contentLoadCancellation = loadCancellation;
        IsLoadingContent = true;
        ContentStatusText = L("Loading community content...", "正在加载社区内容……");

        try
        {
            var result = await communityHubService.LoadAsync(loadCancellation.Token);
            if (generation != contentLoadGeneration
                || loadCancellation.IsCancellationRequested
                || !areVisualResourcesActive)
            {
                DisposeCommunityPeople(result.Content.SpecialThanks);
                DisposeCommunityPeople(result.Content.Sponsors);
                return;
            }

            Replace(SpecialThanks, result.Content.SpecialThanks);
            Replace(Sponsors, result.Content.Sponsors);
            sponsorUrl = result.Content.SponsorUrl;
            IsContentConfigured = result.IsContentConfigured;
            IsFeedbackConfigured = result.IsFeedbackConfigured;
            IsEchoCaveAccessRestricted = result.IsEchoCaveAccessRestricted;
            IsEchoCaveAccessGranted = false;
            AppearanceAccessLevel = SponsorAppearanceAccessLevel.None;
            verifiedEchoCavePlanName = string.Empty;
            RefreshEchoCaveAccessStatusText();
            HasContentLoadError = result.RemoteLoadFailed;
            OnPropertyChanged(nameof(HasSpecialThanks));
            OnPropertyChanged(nameof(HasSponsors));
            OnPropertyChanged(nameof(HasAnyCommunityContent));
            OnPropertyChanged(nameof(IsSponsorLinkAvailable));
            RefreshContentStatusText();
            FeedbackStatusText = IsFeedbackConfigured
                ? L("Ready for your feedback.", "欢迎留下你的反馈。")
                : L("The feedback endpoint has not been configured yet.", "回声洞提交接口尚未配置。");
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to initialize the More settings page");
            HasContentLoadError = true;
            ContentStatusText = L(
                "Community content could not be loaded. Try again later.",
                "社区内容加载失败，请稍后重试。");
        }
        finally
        {
            if (ReferenceEquals(contentLoadCancellation, loadCancellation))
            {
                contentLoadCancellation = null;
                IsLoadingContent = false;
            }

            loadCancellation.Dispose();
        }
    }

    [RelayCommand]
    private void OpenSponsorPage()
    {
        if (!IsSponsorLinkAvailable)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = sponsorUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            logger.Warning(ex, "Unable to open sponsor page");
            ContentStatusText = L(
                "The sponsor page could not be opened.",
                "无法打开赞助页面。");
        }
    }

    [RelayCommand(CanExecute = nameof(CanVerifyEchoCaveAccess))]
    private async Task VerifyEchoCaveAccessAsync(CancellationToken cancellationToken)
    {
        IsVerifyingEchoCaveAccess = true;
        IsEchoCaveAccessGranted = false;
        AppearanceAccessLevel = SponsorAppearanceAccessLevel.None;
        verifiedEchoCavePlanName = string.Empty;
        RefreshEchoCaveAccessStatusText();
        using var verificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        verificationCancellation.CancelAfter(EchoCaveVerificationTimeout);

        try
        {
            var access = await communityHubService.VerifyEchoCaveAccessAsync(
                AfdianSupporterIdentifier.Trim(),
                verificationCancellation.Token);
            IsEchoCaveAccessGranted = access.IsEligible;
            AppearanceAccessLevel = ResolveAppearanceAccessLevel(access.PlanName, access.IsEligible);
            verifiedEchoCavePlanName = access.PlanName;
            if (!access.IsEligible && !string.IsNullOrWhiteSpace(access.SponsorName))
            {
                EchoCaveAccessStatusText = L(
                    $"{access.SponsorName} is not on an eligible Afdian plan.",
                    $"{access.SponsorName} \u5f53\u524d\u4e0d\u5728\u53ef\u4f7f\u7528\u7684\u7231\u53d1\u7535\u65b9\u6848\u4e2d\u3002");
            }
            else
            {
                RefreshEchoCaveAccessStatusText();
            }

            FeedbackStatusText = IsEchoCaveAccessGranted
                ? L("Echo cave access verified. You can leave a message.", "\u5df2\u9a8c\u8bc1\u56de\u58f0\u6d1e\u8d44\u683c\uff0c\u73b0\u5728\u53ef\u4ee5\u7559\u8a00\u3002")
                : EchoCaveAccessStatusText;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EchoCaveAccessStatusText = L("Afdian access verification was cancelled.", "\u5df2\u53d6\u6d88\u7231\u53d1\u7535\u8d44\u683c\u9a8c\u8bc1\u3002");
        }
        catch (OperationCanceledException) when (verificationCancellation.IsCancellationRequested)
        {
            EchoCaveAccessStatusText = L(
                "Afdian access verification timed out. Try again later.",
                "\u7231\u53d1\u7535\u8d44\u683c\u9a8c\u8bc1\u8d85\u65f6\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5\u3002");
            FeedbackStatusText = EchoCaveAccessStatusText;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            logger.Warning(ex, "Unable to verify Afdian echo cave access");
            EchoCaveAccessStatusText = L(
                "Afdian access could not be verified. Try again later.",
                "\u65e0\u6cd5\u9a8c\u8bc1\u7231\u53d1\u7535\u8d44\u683c\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5\u3002");
            FeedbackStatusText = EchoCaveAccessStatusText;
        }
        finally
        {
            IsVerifyingEchoCaveAccess = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmitFeedback))]
    private async Task SubmitFeedbackAsync(CancellationToken cancellationToken)
    {
        IsSubmittingFeedback = true;
        IsFeedbackSuccess = false;
        FeedbackStatusText = L("Sending feedback...", "正在发送反馈……");

        try
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            await communityHubService.SubmitEchoAsync(
                new EchoCaveSubmission(
                    string.IsNullOrWhiteSpace(FeedbackSubject)
                        ? L("Echo Cave feedback", "\u56de\u58f0\u6d1e\u7559\u8a00")
                        : FeedbackSubject.Trim(),
                    FeedbackMessage.Trim(),
                    string.Empty,
                    version,
                    useEnglish ? "en-US" : "zh-CN",
                    AfdianSupporterIdentifier.Trim()),
                cancellationToken);

            FeedbackSubject = string.Empty;
            FeedbackMessage = string.Empty;
            IsFeedbackSuccess = true;
            FeedbackStatusText = L(
                "Feedback sent. Thank you for helping improve BohemiX.",
                "反馈已发送，感谢你帮助改进 BohemiX。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FeedbackStatusText = L("Feedback sending was cancelled.", "反馈发送已取消。");
        }
        catch (UnauthorizedAccessException)
        {
            IsEchoCaveAccessGranted = false;
            AppearanceAccessLevel = SponsorAppearanceAccessLevel.None;
            verifiedEchoCavePlanName = string.Empty;
            RefreshEchoCaveAccessStatusText();
            FeedbackStatusText = EchoCaveAccessStatusText;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            logger.Warning(ex, "Unable to submit echo cave feedback");
            FeedbackStatusText = L(
                "Feedback could not be sent. Check your connection and try again.",
                "反馈发送失败，请检查网络后重试。");
        }
        finally
        {
            IsSubmittingFeedback = false;
        }
    }

    private void RefreshContentStatusText()
    {
        ContentStatusText = HasContentLoadError
            ? L(
                "Remote content is unavailable. Showing the built-in fallback.",
                "远程内容暂不可用，当前显示内置备用内容。")
            : !IsContentConfigured
                ? L(
                    "The community data source has not been configured yet.",
                    "社区数据源尚未配置。")
                : HasAnyCommunityContent
                    ? L("Community content is up to date.", "社区内容已更新。")
                    : L("The community list is currently empty.", "社区名单当前为空。");
    }

    private void RefreshUpdateCheckStatusText()
    {
        if (IsCheckingForUpdates)
        {
            UpdateCheckStatusText = L("Checking for updates...", "正在检查更新……");
            return;
        }

        if (lastUpdateCheckResult is null)
        {
            UpdateCheckStatusText = L(
                "Check GitHub for the latest released version.",
                "从 GitHub 检查是否有新版本。");
            return;
        }

        UpdateCheckStatusText = lastUpdateCheckResult.Availability switch
        {
            ApplicationUpdateAvailability.UpdateAvailable => L(
                $"A new version is available: {lastUpdateCheckResult.LatestVersion}.",
                $"发现新版本 {lastUpdateCheckResult.LatestVersion}。"),
            ApplicationUpdateAvailability.UpToDate => L(
                "You are using the latest version.",
                "当前已是最新版本。"),
            ApplicationUpdateAvailability.NoPublishedVersion => L(
                "No released version is available on GitHub yet.",
                "GitHub 暂未发布可供检查的版本。"),
            _ => L("Could not check for updates. Try again later.", "检查更新失败，请稍后重试。")
        };
    }

    private static string GetApplicationVersion()
    {
        var assembly = typeof(MoreSettingsViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informationalVersion.Split('+', 2)[0];
        return $"v{version}";
    }

    private async Task TypeEchoLineAsync(string text, CancellationToken cancellationToken)
    {
        using var caretCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var caretTask = BlinkEchoCaretAsync(caretCancellation.Token);

        try
        {
            for (var index = 1; index <= text.Length; index++)
            {
                EchoPreviewLineOne = text[..index];
                await Task.Delay(GetEchoTypingDelay(text[index - 1]), cancellationToken);
            }
        }
        finally
        {
            caretCancellation.Cancel();
            try
            {
                await caretTask;
            }
            catch (OperationCanceledException) when (caretCancellation.IsCancellationRequested)
            {
            }

            IsEchoLineOneCaretVisible = false;
        }
    }

    private async Task BlinkEchoCaretAsync(CancellationToken cancellationToken)
    {
        var isVisible = true;
        while (true)
        {
            IsEchoLineOneCaretVisible = isVisible;
            isVisible = !isVisible;
            await Task.Delay(170, cancellationToken);
        }
    }

    private void StopEchoPreviewAnimation()
    {
        var animation = echoAnimationCancellation;
        echoAnimationCancellation = null;
        animation?.Cancel();
        IsEchoLineOneCaretVisible = false;
    }

    private void ShowCompleteEchoPreview()
    {
        EchoPreviewLineOne = GetEchoPreviewText();
    }

    private string GetEchoPreviewText()
    {
        return echoPreviewIndex switch
        {
            1 => echoPreviewLineTwoText,
            2 => echoPreviewLineThreeText,
            _ => echoPreviewLineOneText
        };
    }

    private static int GetEchoTypingDelay(char character)
    {
        if (character is '.' or '!' or '?' or ',' or '。' or '！' or '？' or '，')
        {
            return 110;
        }

        return character <= 127 ? 24 : 52;
    }

    partial void OnAfdianSupporterIdentifierChanged(string value)
    {
        if (!IsEchoCaveAccessGranted
            && AppearanceAccessLevel == SponsorAppearanceAccessLevel.None
            && string.IsNullOrEmpty(verifiedEchoCavePlanName))
        {
            return;
        }

        IsEchoCaveAccessGranted = false;
        AppearanceAccessLevel = SponsorAppearanceAccessLevel.None;
        verifiedEchoCavePlanName = string.Empty;
        RefreshEchoCaveAccessStatusText();
    }

    private void RefreshEchoCaveAccessStatusText()
    {
        if (!IsEchoCaveAccessRestricted)
        {
            EchoCaveAccessStatusText = L(
                "Echo cave access is available.",
                "\u56de\u58f0\u6d1e\u7559\u8a00\u5df2\u5f00\u653e\u3002");
            return;
        }

        if (IsVerifyingEchoCaveAccess)
        {
            EchoCaveAccessStatusText = L(
                "Checking your Afdian support...",
                "\u6b63\u5728\u9a8c\u8bc1\u60a8\u7684\u7231\u53d1\u7535\u8d5e\u52a9\u2026");
            return;
        }

        if (IsEchoCaveAccessGranted)
        {
            EchoCaveAccessStatusText = string.IsNullOrWhiteSpace(verifiedEchoCavePlanName)
                ? L("Afdian access verified.", "\u5df2\u901a\u8fc7\u7231\u53d1\u7535\u8d44\u683c\u9a8c\u8bc1\u3002")
                : L(
                    $"Afdian access verified through {verifiedEchoCavePlanName}.",
                    $"\u5df2\u901a\u8fc7 {verifiedEchoCavePlanName} \u65b9\u6848\u5b8c\u6210\u7231\u53d1\u7535\u8d44\u683c\u9a8c\u8bc1\u3002");
            return;
        }

        EchoCaveAccessStatusText = L(
            "Sponsor an eligible Afdian plan, then verify your supporter ID or nickname.",
            "\u8d5e\u52a9\u6307\u5b9a\u7684\u7231\u53d1\u7535\u65b9\u6848\u540e\uff0c\u8bf7\u9a8c\u8bc1\u60a8\u7684\u8d5e\u52a9\u8005 ID \u6216\u6635\u79f0\u3002");
    }

    private string L(string english, string simplifiedChinese)
    {
        return useEnglish ? english : simplifiedChinese;
    }

    private static SponsorAppearanceAccessLevel ResolveAppearanceAccessLevel(
        string? planName,
        bool hasEchoCaveAccess)
    {
        if (hasEchoCaveAccess)
        {
            return SponsorAppearanceAccessLevel.Dynamic;
        }

        var normalizedPlanName = planName?.Trim();
        if (normalizedPlanName is null)
        {
            return SponsorAppearanceAccessLevel.None;
        }

        if (normalizedPlanName.Equals("熏奶酪", StringComparison.OrdinalIgnoreCase)
            || normalizedPlanName.Equals("救世干酒", StringComparison.OrdinalIgnoreCase))
        {
            return SponsorAppearanceAccessLevel.Dynamic;
        }

        return normalizedPlanName.Equals("面包", StringComparison.OrdinalIgnoreCase)
            ? SponsorAppearanceAccessLevel.Static
            : SponsorAppearanceAccessLevel.None;
    }

    private static void Replace(
        ObservableCollection<CommunityPerson> target,
        IReadOnlyList<CommunityPerson> source)
    {
        foreach (var item in target)
        {
            item.Dispose();
        }

        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void ClearCommunityContent()
    {
        DisposeCommunityPeople(SpecialThanks);
        DisposeCommunityPeople(Sponsors);
        SpecialThanks.Clear();
        Sponsors.Clear();
        sponsorUrl = string.Empty;
        OnPropertyChanged(nameof(HasSpecialThanks));
        OnPropertyChanged(nameof(HasSponsors));
        OnPropertyChanged(nameof(HasAnyCommunityContent));
        OnPropertyChanged(nameof(IsSponsorLinkAvailable));
    }

    private static void DisposeCommunityPeople(IEnumerable<CommunityPerson> people)
    {
        foreach (var person in people)
        {
            person.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DeactivateVisualResources();
        appearanceSaveGate.Dispose();
    }

    private sealed class UnavailableApplicationUpdateCheckService : IApplicationUpdateCheckService
    {
        public static UnavailableApplicationUpdateCheckService Instance { get; } = new();

        public Task<ApplicationUpdateCheckResult> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ApplicationUpdateCheckResult>(
                new HttpRequestException("The update check service is unavailable."));
    }
}
