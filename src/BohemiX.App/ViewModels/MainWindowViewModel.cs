using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BohemiX.App.Community;
using BohemiX.App.Models;
using BohemiX.App.PlayerProfiles;
using BohemiX.App.Services;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Modules.Alchemy.ViewModels;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.ViewModels;
using BohemiX.Modules.SaveManager.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public enum LaunchWindowAction
{
    Minimize,
    Close
}

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Raised when a top-level navigation button (the top bar) triggers a real
    /// destination change, so the View can replay the entrance ("pop in") animation.
    /// Left sub-navigation buttons navigate via the *Silent command variants, which
    /// never raise this event, so clicking a sidebar item does not re-trigger the
    /// entrance animation.
    /// </summary>
    public event Action? NavigationTransitionRequested;

    /// <summary>
    /// Raised when the save workspace is opened from a silent navigation entry.
    /// The view uses this to animate the workspace without replaying the sidebar.
    /// </summary>
    public event Action? SaveWorkspaceTransitionRequested;

    public event Action<LaunchWindowAction>? LaunchWindowActionRequested;

    private const string GameTitle = "Kingdom Come: Deliverance II";
    private const int ModSearchPageSize = 40;
    private const int ModCoverCacheConcurrency = 6;
    private const int ModPackCoverCacheConcurrency = 16;
    private const string SimplifiedChineseLanguage = "简体中文";
    private const string LegacySimplifiedChineseLanguage = "Simplified Chinese";
    private const string EnglishLanguage = "English";
    private const string DownloadingQueueSection = "Downloading";
    private const string DownloadedQueueSection = "Downloaded";
    private const int ModRecommendationCandidateCount = 30;
    private const int ModRecommendationMaxResults = 12;
    private const int ModRecommendationMaxPerCategory = 4;
    private const int ModRecommendationQueryConcurrency = 3;
    private const int ModRecommendationPageRetryCount = 3;
    private const int DailyModCoverRetryCount = 3;
    private const int ModSearchAutoSearchDelayMilliseconds = 450;
    public const double SidebarCollapsedWidth = 60;
    public const double SidebarComfortWidth = 184;
    private const string NavigationTargetDashboard = "Dashboard";
    private const string NavigationTargetMods = "Mods";
    private const string NavigationTargetSaves = "Saves";
    private const string NavigationTargetSettings = "Settings";

    private readonly IAppStartupService appStartupService;
    private readonly IApplicationPathService applicationPathService;
    private readonly IGameInstallationStore gameInstallationStore;
    private readonly IGameDiscoveryService gameDiscoveryService;
    private readonly IGameLauncherService gameLauncherService;
    private readonly IGameNewsService gameNewsService;
    private readonly IGameRuntimeMonitorService gameRuntimeMonitorService;
    private readonly IExternalStoreService externalStoreService;
    private readonly IGamePathPickerService gamePathPickerService;
    private readonly IModCoverCacheService modCoverCacheService;
    private readonly IModCatalogService modCatalogService;
    private readonly IModPackCatalogService modPackCatalogService;
    private readonly IModPackInstallService modPackInstallService;
    private readonly IModPackImportService modPackImportService;
    private readonly IModConflictAnalyzer modConflictAnalyzer;
    private readonly IModConflictReviewService modConflictReviewService;
    private readonly INexusModService nexusModService;
    private readonly IModTranslationService modTranslationService;
    private readonly IWorkshopService workshopService;
    private readonly IPlayerSteamAccountBindingService playerSteamAccountBindingService;
    private readonly IModDownloader modDownloader;
    private readonly IModPackageInstaller modPackageInstaller;
    private readonly INexusApiKeyStore nexusApiKeyStore;
    private readonly INexusAccountService nexusAccountService;
    private readonly IAppSettingsService appSettingsService;
    private readonly ForgeGraphicsSettings forgeGraphicsSettings;
    private readonly IPlayerStatisticsService playerStatisticsService;
    private readonly IAdventureProfileDataService adventureProfileDataService;
    private readonly IModLaunchPreflightService modLaunchPreflightService;
    private readonly IModLoadOrderPlanner modLoadOrderPlanner;
    private readonly IModManagementSnapshotService modManagementSnapshotService;
    private readonly ITrackerDiagnosticsService trackerDiagnosticsService;
    private readonly ITrackerModHealthService trackerModHealthService;
    private readonly ITrackerModInstallService trackerModInstallService;
    private readonly ITrackerModPackageService trackerModPackageService;
    private readonly ITrackerRuntimeService trackerRuntimeService;
    private readonly IVfsSessionService vfsSessionService;
    private readonly IApplicationErrorReporter applicationErrorReporter;
    private readonly IMainWindowTextCatalog textCatalog;
    private readonly SemaphoreSlim playerRuntimeGate = new(1, 1);
    private bool isPlayerProfileManagerInitialized;
    private bool isGameRuntimeInitialized;
    internal bool IsInitializationReady { get; private set; }
    private long adventureProfileRefreshVersion;
    private readonly Lazy<AlchemyWorkshopViewModel> alchemyWorkshop;
    private readonly Lazy<ForgeWorkshopViewModel> forgeWorkshop;
    private readonly SemaphoreSlim labModuleInitializationGate = new(1, 1);
    private long labPlayerStateVersion;
    private long alchemyPlayerStateVersion = -1;
    private long forgePlayerStateVersion = -1;
    private bool labNavigationInitialized;

    /// <summary>The independent alchemy simulation module mounted in the Lab workspace.</summary>
    public AlchemyWorkshopViewModel AlchemyWorkshop => alchemyWorkshop.Value;

    internal bool IsAlchemyWorkshopCreated => alchemyWorkshop.IsValueCreated;

    internal AlchemyWorkshopViewModel? CreatedAlchemyWorkshop =>
        alchemyWorkshop.IsValueCreated ? alchemyWorkshop.Value : null;

    /// <summary>The independent forging simulation module mounted in the Lab workspace.</summary>
    public ForgeWorkshopViewModel ForgeWorkshop => forgeWorkshop.Value;

    internal ForgeWorkshopViewModel? CreatedForgeWorkshop =>
        forgeWorkshop.IsValueCreated ? forgeWorkshop.Value : null;


    /// <summary>The KCD2 save isolation module mounted into the Saves workspace.</summary>
    public SaveManagerViewModel SaveManager { get; }

    /// <summary>The spoiler-free KCD2 player summary shown on the launcher dashboard.</summary>
    public AdventureProfileViewModel AdventureProfile { get; }

    public PlayerProfileManagerViewModel PlayerProfiles { get; }

    public MoreSettingsViewModel MoreSettings { get; }

    private readonly SemaphoreSlim modCoverCacheThrottle = new(ModCoverCacheConcurrency);
    private readonly SemaphoreSlim modPackCoverCacheThrottle = new(ModPackCoverCacheConcurrency);
    private readonly Dictionary<string, string> installedModGroupNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> installedModGroupAssignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> shownModRecommendationIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dismissedCanceledDownloadKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> requestedModPackDownloadPresentationModIds = [];
    private readonly ModDownloadSearchOperationGate modDownloadSearchOperationGate = new();
    private readonly ConcurrentDictionary<int, IReadOnlyList<NexusModRequirement>> installedModRequirementCache = new();

    private DiscoveredGame? selectedGame;
    private bool isTrackerRuntimeSubscribed;
    private CancellationTokenSource? gameDiscoveryCancellation;
    private CancellationTokenSource? modCoverCacheCancellation;
    private CancellationTokenSource? installedModCoverCancellation;
    private CancellationTokenSource? installedModRequirementsCancellation;
    private CancellationTokenSource? modPackCoverCacheCancellation;
    private CancellationTokenSource? dailyModCoverCancellation;
    private readonly CancellationTokenSource downloadCoverCancellation = new();
    private readonly CancellationTokenSource shutdownCancellation = new();
    private readonly object shutdownPreparationGate = new();
    private Task? shutdownPreparationTask;
    private int isShuttingDown;
    private IReadOnlyList<ModManifest> currentModManifests = [];
    private IReadOnlyList<ModConflict> currentModConflicts = [];
    private IReadOnlyList<ModPackCatalogEntry> currentModPackCatalog = [];
    private IReadOnlyList<ModPackCatalogEntry> currentFilteredModPackCatalog = [];
    private int nextModPackBatchOffset;
    private bool hasLoadedModPackCatalog;
    private IReadOnlyDictionary<string, ModConflictReview>? currentModConflictReviews;
    private string currentSettingsCategory = "Navigation";
    private string activeModSearchSourceKey = "Nexus";
    private string? activeModSearchQuery;
    private string activeModSearchCategoryKey = "All";
    private WorkshopSortOrder activeWorkshopSortOrder = WorkshopSortOrder.Hot;
    private int modSearchTotalCount;
    private int nextNexusModSearchOffset;
    private int nextSteamWorkshopSearchPage = 1;
    private int modRecommendationRefreshPage;
    private string? activeModRecommendationSignature;
    private long modCoverCacheGeneration;
    private long dailyModCoverGeneration;
    private CancellationTokenSource? modSearchAutoSearchCancellation;
    private bool hasPendingModSearchAutoSearch;
    private bool isModDownloadSourceTransitionLoading;
    private string? lastSubmittedModSearchSignature;
    private long nexusModFilePickerRequestId;
    private long installedModCoverGeneration;
    private long nexusAccountBindingDialogRequestId;
    private long steamAccountBindingDialogRequestId;
    private CancellationTokenSource? steamAccountBindingDetectionCancellation;
    private bool suppressSelectedModDownloadSourceChanged;
    private string approvedModDownloadSourceKey = ModDownloadSourceOptionViewModel.NexusKey;
    private SteamAccountIdentity? pendingSteamAccountIdentity;
    private SteamAccountBinding? pendingPlayerSteamAccountBinding;
    private SteamAccountBinding? pendingSteamAccountBindingConflict;
    private PlayerProfile? pendingSteamBindingPlayerProfile;
    private string? pendingSteamBindingConflictPlayerName;
    private ModPackRowViewModel? pendingSteamModPackInstall;
    private ModPackRowViewModel? pendingNexusModPackInstall;
    private ModPackInstallPlan? pendingModPackInstallPlan;
    private NexusModSearchRowViewModel? pendingNexusModDownload;
    private string? editingInstalledModGroupKey;
    private InstalledModGroupViewModel? editingInstalledModGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmNexusAccountBinding))]
    [NotifyPropertyChangedFor(nameof(CanBindNexusAccountWithApiKey))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmNexusAccountBindingCommand))]
    [NotifyCanExecuteChangedFor(nameof(BindNexusAccountWithApiKeyCommand))]
    private bool isNexusAccountBindingDialogOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmNexusAccountBinding))]
    [NotifyPropertyChangedFor(nameof(CanBindNexusAccountWithApiKey))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmNexusAccountBindingCommand))]
    [NotifyCanExecuteChangedFor(nameof(BindNexusAccountWithApiKeyCommand))]
    private bool isNexusAccountBindingDialogBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBindNexusAccountWithApiKey))]
    [NotifyCanExecuteChangedFor(nameof(BindNexusAccountWithApiKeyCommand))]
    private string nexusAccountBindingDialogApiKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmNexusAccountBinding))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmNexusAccountBindingCommand))]
    private bool isNexusApiKeyRequiredForPendingAction;

    [ObservableProperty]
    private string nexusAccountBindingDialogStatusText = "绑定 Nexus Mods 账号后即可浏览和下载 Nexus 来源的 Mod。";

    public bool CanConfirmNexusAccountBinding =>
        IsNexusAccountBindingDialogOpen
        && !IsNexusAccountBindingDialogBusy;

    public bool CanBindNexusAccountWithApiKey =>
        IsNexusAccountBindingDialogOpen
        && !IsNexusAccountBindingDialogBusy
        && !string.IsNullOrWhiteSpace(NexusAccountBindingDialogApiKey);

    private enum SteamAccountBindingDialogState
    {
        Hidden,
        Detecting,
        StartingSteam,
        WaitingForSteamLogin,
        ValidatingSteamAccount,
        ReadyToBind,
        AlreadyBound,
        PlayerBoundToDifferentSteamAccount,
        SteamAccountBoundToDifferentPlayer,
        NoCurrentPlayer,
        SteamUnavailable,
        LoginRequired,
        GameNotOwned,
        Failed
    }

    private SteamAccountBindingDialogState steamAccountBindingDialogState;

    public MainWindowViewModel(
        IAppStartupService appStartupService,
        IApplicationPathService applicationPathService,
        IGameInstallationStore gameInstallationStore,
        IGameDiscoveryService gameDiscoveryService,
        IGameLauncherService gameLauncherService,
        IGameNewsService gameNewsService,
        IGameRuntimeMonitorService gameRuntimeMonitorService,
        IExternalStoreService externalStoreService,
        IGamePathPickerService gamePathPickerService,
        IModCoverCacheService modCoverCacheService,
        IModCatalogService modCatalogService,
        IModPackCatalogService modPackCatalogService,
        IModPackInstallService modPackInstallService,
        IModPackImportService modPackImportService,
        IModConflictAnalyzer modConflictAnalyzer,
        IModConflictReviewService modConflictReviewService,
        INexusModService nexusModService,
        IModTranslationService modTranslationService,
        IWorkshopService workshopService,
        IPlayerSteamAccountBindingService playerSteamAccountBindingService,
        IModDownloader modDownloader,
        IModPackageInstaller modPackageInstaller,
        INexusApiKeyStore nexusApiKeyStore,
        INexusAccountService nexusAccountService,
        IAppSettingsService appSettingsService,
        IPlayerStatisticsService playerStatisticsService,
        IAdventureProfileDataService adventureProfileDataService,
        IModLaunchPreflightService modLaunchPreflightService,
        IModLoadOrderPlanner modLoadOrderPlanner,
        IModManagementSnapshotService modManagementSnapshotService,
        ITrackerDiagnosticsService trackerDiagnosticsService,
        ITrackerModHealthService trackerModHealthService,
        ITrackerModInstallService trackerModInstallService,
        ITrackerModPackageService trackerModPackageService,
        ITrackerRuntimeService trackerRuntimeService,
        IVfsSessionService vfsSessionService,
        IApplicationErrorReporter applicationErrorReporter,
        IMainWindowTextCatalog textCatalog,
        PlayerProfileManagerViewModel playerProfiles,
        MoreSettingsViewModel moreSettings,
        SaveManagerViewModel saveManager,
        ForgeGraphicsSettings forgeGraphicsSettings,
        Lazy<AlchemyWorkshopViewModel> alchemyWorkshop,
        Lazy<ForgeWorkshopViewModel> forgeWorkshop)
    {
        this.appStartupService = appStartupService;
        this.applicationPathService = applicationPathService;
        this.gameInstallationStore = gameInstallationStore;
        this.gameDiscoveryService = gameDiscoveryService;
        this.gameLauncherService = gameLauncherService;
        this.gameNewsService = gameNewsService;
        this.gameRuntimeMonitorService = gameRuntimeMonitorService;
        this.gameRuntimeMonitorService.Exited += OnGameRuntimeExited;
        this.externalStoreService = externalStoreService;
        this.gamePathPickerService = gamePathPickerService;
        this.modCoverCacheService = modCoverCacheService;
        this.modCatalogService = modCatalogService;
        this.modPackCatalogService = modPackCatalogService;
        this.modPackInstallService = modPackInstallService;
        this.modPackImportService = modPackImportService;
        this.modConflictAnalyzer = modConflictAnalyzer;
        this.modConflictReviewService = modConflictReviewService;
        this.nexusModService = nexusModService;
        this.modTranslationService = modTranslationService;
        this.workshopService = workshopService;
        this.playerSteamAccountBindingService = playerSteamAccountBindingService;
        this.modDownloader = modDownloader;
        this.modPackageInstaller = modPackageInstaller;
        this.nexusApiKeyStore = nexusApiKeyStore;
        this.nexusAccountService = nexusAccountService;
        this.appSettingsService = appSettingsService;
        this.playerStatisticsService = playerStatisticsService;
        this.adventureProfileDataService = adventureProfileDataService;
        this.modLaunchPreflightService = modLaunchPreflightService;
        this.modLoadOrderPlanner = modLoadOrderPlanner;
        this.modManagementSnapshotService = modManagementSnapshotService;
        this.trackerDiagnosticsService = trackerDiagnosticsService;
        this.trackerModHealthService = trackerModHealthService;
        this.trackerModInstallService = trackerModInstallService;
        this.trackerModPackageService = trackerModPackageService;
        this.trackerRuntimeService = trackerRuntimeService;
        this.vfsSessionService = vfsSessionService;
        this.applicationErrorReporter = applicationErrorReporter;
        this.textCatalog = textCatalog;
        PlayerProfiles = playerProfiles;
        PlayerProfiles.PlayerChanged += OnPlayerChanged;
        PlayerProfiles.SettingsRequested += OnPlayerSettingsRequested;
        MoreSettings = moreSettings;
        SaveManager = saveManager;
        this.forgeGraphicsSettings = forgeGraphicsSettings;
        AdventureProfile = AdventureProfileViewModel.CreateEmpty();
        SaveManager.PropertyChanged += OnSaveManagerPropertyChanged;
        SyncAdventureSaveOptions();
        SaveManager.RequestLaunchGameAsync = LaunchGameCoreAsync;
        this.alchemyWorkshop = alchemyWorkshop;
        this.forgeWorkshop = forgeWorkshop;

        Sections =
        [
            new ShellSectionViewModel("Launcher", "KCD2 command center", "Check, launch, and view the running state of Kingdom Come: Deliverance II from one page."),
            new ShellSectionViewModel("Installation", "Find the game", "Look in Steam, Epic, and local folders for a usable KingdomCome.exe."),
            new ShellSectionViewModel("Mods", "Installed mods", "Review installed mods, file conflicts, and whether they can load correctly."),
            new ShellSectionViewModel("Saves", "Save monitoring", "Track save changes and achievement rule inputs."),
            new ShellSectionViewModel("Settings", "Launcher preferences", "Runtime paths, logs, storage, and launch behavior."),
            new ShellSectionViewModel("Lab", "KCD2 gameplay lab", "Simulate and recreate Kingdom Come: Deliverance II gameplay systems: alchemy, forging, and swordsmanship.")
        ];

        SelectedSection = Sections[0];
        Kcd2InstallPath = "Not scanned";
        Kcd2ExecutablePath = "Not verified";
        SourceText = "Unknown";
        VerificationText = "Scan required";
        SelectedModDownloadSource = ModDownloadSources[0];
        approvedModDownloadSourceKey = GetModDownloadSourceKey(SelectedModDownloadSource);
        var applicationPaths = applicationPathService.GetPaths();
        DataDirectory = applicationPaths.DataDirectory;
        DatabasePath = applicationPaths.DatabasePath;
        ApplyLanguage();
        SelectSettingsCategory("Navigation");
        StatusText = T("LauncherReady");
        LastPlayedText = T("Ready");
        LastActionText = T("NoActionYet");
    }

    public ObservableCollection<ShellSectionViewModel> Sections { get; }

    public ObservableCollection<string> AvailableLanguages { get; } =
    [
        SimplifiedChineseLanguage,
        EnglishLanguage
    ];

    public ObservableCollection<string> ForgeQualityOptions { get; } =
    [
        "低",
        "中",
        "高"
    ];

    public ObservableCollection<string> DefaultNavigationTargets { get; } =
    [
        NavigationTargetDashboard,
        NavigationTargetMods,
        NavigationTargetSaves,
        NavigationTargetSettings
    ];

    [ObservableProperty]
    private ShellSectionViewModel? selectedSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarExpanded))]
    private double sidebarWidth = SidebarComfortWidth;

    public bool IsSidebarExpanded => SidebarWidth > SidebarCollapsedWidth;

    [ObservableProperty]
    private string sidebarCollapseText = "Collapse sidebar";

    [ObservableProperty]
    private string sidebarExpandText = "Expand sidebar";

    [ObservableProperty]
    private string sidebarToggleText = "Toggle sidebar";

    [ObservableProperty]
    private string selectedTopNav = "Home";

    [ObservableProperty]
    private string statusText = "KCD2 launcher ready. Verify the local installation to enable launch.";

    [ObservableProperty]
    private string workspaceTitle = "KCD2 Launcher";

    [ObservableProperty]
    private string workspaceDescription = "Single-game workspace for Kingdom Come: Deliverance II.";

    [ObservableProperty]
    private string primaryActionLabel = "Launch Game";

    [ObservableProperty]
    private string kcd2InstallPath;

    [ObservableProperty]
    private string kcd2ExecutablePath;

    [ObservableProperty]
    private string sourceText;

    [ObservableProperty]
    private string verificationText;

    [ObservableProperty]
    private string lastPlayedText = "Ready";

    [ObservableProperty]
    private string lastActionText = "No action yet";

    [ObservableProperty]
    private int gameCount;

    [ObservableProperty]
    private int verifiedGameCount;

    [ObservableProperty]
    private int modCount;

    [ObservableProperty]
    private int enabledModCount;

    [ObservableProperty]
    private int disabledModCount;

    [ObservableProperty]
    private int conflictCount;

    [ObservableProperty]
    private string dataDirectory = string.Empty;

    [ObservableProperty]
    private string databasePath = string.Empty;

    [ObservableProperty]
    private string modsDirectory = string.Empty;

    [ObservableProperty]
    private string storageText = "Runtime storage";

    [ObservableProperty]
    private string vfsStateText = "Idle";

    [ObservableProperty]
    private string trackerBridgePath = string.Empty;

    [ObservableProperty]
    private string trackerModPackagePath = string.Empty;

    [ObservableProperty]
    private string trackerModStatusText = "Tracker mod package not prepared";

    [ObservableProperty]
    private string trackerModInstallStatusText = "Verify KCD2 install before installing Tracker mod";

    [ObservableProperty]
    private string trackerDiagnosticsStatusText = "Bridge self-test not run";

    [ObservableProperty]
    private string trackerModHealthStatusText = "Tracker mod health not checked";

    [ObservableProperty]
    private string trackerBridgeLastWriteText = "Bridge last write: never";

    [ObservableProperty]
    private int trackerEventCount;

    [ObservableProperty]
    private int trackerVisibleEntityCount;

    [ObservableProperty]
    private int trackerActiveQuestCount;

    [ObservableProperty]
    private int trackerCompletedEntityCount;

    [ObservableProperty]
    private int trackerUnconfirmedSessionCount;

    [ObservableProperty]
    private string trackerLastEventText = "No tracker events processed";

    [ObservableProperty]
    private string trackerPositionText = "Awaiting position";

    [ObservableProperty]
    private double trackerMapMarkerLeft = 152;

    [ObservableProperty]
    private double trackerMapMarkerTop = 82;

    [ObservableProperty]
    private bool isTrackerPositionVisible;

    [ObservableProperty]
    private string trackerMapStatusText = "Waiting for the game to send its position";

    [ObservableProperty]
    private int trackerAchievementUnlockedCount;

    [ObservableProperty]
    private int trackerAchievementTotalCount;

    public ObservableCollection<string> TrackerEntityRows { get; } = [];

    public ObservableCollection<string> TrackerRecentEventRows { get; } = [];

    public ObservableCollection<string> TrackerAchievementRows { get; } = [];

    public ObservableCollection<string> LabAlchemyFeatures { get; } = [];

    public ObservableCollection<string> LabForgingFeatures { get; } = [];

    public ObservableCollection<LabModuleCardViewModel> LabModuleCards { get; } = [];

    public ObservableCollection<ModListItemViewModel> ModRows { get; } = [];

    public ObservableCollection<InstalledModGroupViewModel> InstalledModGroups { get; } = [];

    public ObservableCollection<InstalledModGroupOptionViewModel> InstalledModGroupOptions { get; } = [];

    public ObservableCollection<ModConflictRowViewModel> ModConflictRows { get; } = [];

    public ObservableCollection<ModConflictRowViewModel> DashboardConflictRows { get; } = [];

    public ObservableCollection<GameNewsItemViewModel> SecondaryGameNewsItems { get; } = [];

    [ObservableProperty]
    private GameNewsItemViewModel? featuredGameNews;

    private bool launcherDetailsVisualResourcesActive;

    [ObservableProperty]
    private ModListItemViewModel? dashboardFeaturedMod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDailyModRecommendation))]
    [NotifyPropertyChangedFor(nameof(HasNoDailyModRecommendation))]
    private ModRecommendationRowViewModel? dailyModRecommendation;

    public bool HasDailyModRecommendation => DailyModRecommendation is not null;

    public bool HasNoDailyModRecommendation => !HasDailyModRecommendation;

    [ObservableProperty]
    private bool isDailyModRecommendationLoading;

    [ObservableProperty]
    private string dailyModRecommendationTitleText = "Daily Mod pick";

    [ObservableProperty]
    private string dailyModRecommendationDateText = string.Empty;

    [ObservableProperty]
    private string dailyModRecommendationStatusText = "Connect to Nexus Mods to load today's pick";

    [ObservableProperty]
    private string dailyModRecommendationOpenText = "View Mod";

    [ObservableProperty]
    private string dailyModRecommendationBrowseText = "All recommendations";

    [ObservableProperty]
    private bool hasGameNews;

    [ObservableProperty]
    private bool isGameNewsLoading;

    [ObservableProperty]
    private bool hasGameNewsError;

    [ObservableProperty]
    private bool isGameNewsUsingCache;

    [ObservableProperty]
    private bool isTrackerHealthy;

    [ObservableProperty]
    private string dashboardOverviewTitleText = "Player overview";

    [ObservableProperty]
    private string dashboardUpdatedText = "Updated just now";

    [ObservableProperty]
    private string dashboardRecentSavesText = "Recent saves";

    [ObservableProperty]
    private string dashboardNoRecentSavesText = "No managed saves yet";

    [ObservableProperty]
    private string dashboardManageSavesText = "Manage saves";

    [ObservableProperty]
    private string dashboardModStatusText = "Mod status";

    [ObservableProperty]
    private string dashboardModSummaryText = "No pending conflicts";

    [ObservableProperty]
    private string dashboardNoConflictsText = "No pending conflicts";

    [ObservableProperty]
    private string dashboardReviewConflictsText = "Review conflicts";

    [ObservableProperty]
    private string dashboardManageModsText = "All mods";

    [ObservableProperty]
    private string dashboardNoModsText = "No installed mods";

    [ObservableProperty]
    private string dashboardOfficialNewsText = "Official updates";

    [ObservableProperty]
    private string dashboardNewsStatusText = "Loading Steam announcements";

    [ObservableProperty]
    private string dashboardOpenArticleText = "Open article";

    public ObservableCollection<ModConflictDetailViewModel> SelectedModConflictDetails { get; } = [];

    public ObservableCollection<NexusModSearchRowViewModel> NexusModSearchRows { get; } = [];

    public ObservableCollection<ModPackRowViewModel> ModPackRows { get; } = [];

    public ObservableCollection<NexusModFileOptionViewModel> NexusModFileOptions { get; } = [];

    public ObservableCollection<NexusModDownloadPresetViewModel> NexusModDownloadPresets { get; } = [];

    public ObservableCollection<NexusModRequirementViewModel> NexusModRequirementOptions { get; } = [];

    public ObservableCollection<ModRecommendationRowViewModel> ModRecommendationRows { get; } = [];

    public ObservableCollection<ModDownloadCategoryFilterViewModel> ModRecommendationStrategyFilters { get; } = [];

    public ObservableCollection<ModRecommendationQualifierViewModel> ModRecommendationQualifiers { get; } = [];

    public ObservableCollection<ModRecommendationQualifierViewModel> AvailableModRecommendationQualifiers { get; } = [];

    public ObservableCollection<ModDownloadQueueRowViewModel> ModDownloadQueueRows { get; } = [];

    public ObservableCollection<ModDownloadQueueGroupViewModel> ModDownloadQueueGroups { get; } = [];

    public ObservableCollection<ModDownloadQueueRowViewModel> VisibleModDownloadQueueRows { get; } = [];

    public ObservableCollection<ModDownloadQueueGroupViewModel> VisibleModDownloadQueueGroups { get; } = [];

    public ObservableCollection<SaveSlotRowViewModel> SaveSlotRows { get; } = [];

    [ObservableProperty]
    private SaveSlotRowViewModel? selectedSaveSlot;

    [ObservableProperty]
    private string officialSavePath = string.Empty;

    [ObservableProperty]
    private string saveVaultPath = string.Empty;

    [ObservableProperty]
    private string saveDisplayNameInput = string.Empty;

    [ObservableProperty]
    private string saveManagerStatusText = "还没有检查存档。";

    [ObservableProperty]
    private string saveSafetyStatusText = "等待检查";

    [ObservableProperty]
    private string saveMountedSlotText = "还没有选择使用哪个备份";

    [ObservableProperty]
    private string saveSlotCountText = "0 个备份";

    [ObservableProperty]
    private int saveSlotCount;

    [ObservableProperty]
    private string saveLastParsedText = "-";

    [ObservableProperty]
    private string savePhysicalModeText = "快速切换";

    [ObservableProperty]
    private string saveImportCurrentText = "备份当前存档";

    [ObservableProperty]
    private string saveVaultText = "备份文件夹";

    [ObservableProperty]
    private string saveVaultRootText = "备份保存位置";

    [ObservableProperty]
    private string saveImportDisplayNameText = "备份名称";

    [ObservableProperty]
    private string saveDisplayNameWatermarkText = "例如：主线前、打 Boss 前";

    [ObservableProperty]
    private string saveSafetyLockLabelText = "切换前检查";

    [ObservableProperty]
    private string saveVaultSlotsText = "存档备份";

    [ObservableProperty]
    private string saveMountedJunctionTargetText = "当前游戏会使用这个备份";

    [ObservableProperty]
    private string saveMountSelectedText = "使用这个备份";

    [ObservableProperty]
    private string saveUnmountJunctionText = "取消当前切换";

    [ObservableProperty]
    private string saveOpenVaultText = "打开备份文件夹";

    [ObservableProperty]
    private string saveSafetyNoticeText = "BohemiX 不会改写存档内容，只负责保存备份和帮你选择要使用的那一份。";

    [ObservableProperty]
    private string saveMountedText = "使用中";

    [ObservableProperty]
    private string trackerEventsLabelText = "Events";

    [ObservableProperty]
    private string trackerVisibleLabelText = "Visible";

    [ObservableProperty]
    private string trackerActiveLabelText = "Active";

    [ObservableProperty]
    private string trackerDoneLabelText = "Done";

    [ObservableProperty]
    private string trackerUnconfirmedLabelText = "Unconfirmed";

    [ObservableProperty]
    private string trackerPositionLabelText = "Position";

    [ObservableProperty]
    private string trackerBridgeProjectionText = "Game position sync";

    [ObservableProperty]
    private string prepareText = "Prepare";

    [ObservableProperty]
    private string installText = "Install";

    [ObservableProperty]
    private string selfTestText = "Self-test";

    [ObservableProperty]
    private string healthText = "Health";

    [ObservableProperty]
    private string trackerAchievementsText = "Tracker achievements";

    [ObservableProperty]
    private string trackerVisibleEntitiesText = "Visible entities";

    [ObservableProperty]
    private string trackerRecentEventsText = "Recent events";

    [ObservableProperty]
    private string saveRouterText = "存档切换";

    [ObservableProperty]
    private string savePathText = "存档位置";

    [ObservableProperty]
    private string officialSaveEntryText = "游戏原本的存档位置";

    [ObservableProperty]
    private string savePhysicalModeDescriptionText = "切换时更快，不用来回复制";

    [ObservableProperty]
    private string saveVaultSlotsDescriptionText = "每个备份都保存在本机，名字可以随时改。";

    [ObservableProperty]
    private string displayNameText = "备份名称";

    [ObservableProperty]
    private string playTimeText = "游戏时间";

    [ObservableProperty]
    private string lastSavedText = "保存时间";

    [ObservableProperty]
    private string physicalFolderText = "文件位置";

    [ObservableProperty]
    private string selectedSlotText = "选中的备份";

    [ObservableProperty]
    private string saveSwitchingBlockedNoticeText = "游戏运行中或正在写入存档时，不能切换。请先退出游戏。";

    [ObservableProperty]
    private string junctionRoutingNoticeText = "切换后，游戏会使用你选中的这份备份。";

    [ObservableProperty]
    private bool hasSaveSlots;

    [ObservableProperty]
    private bool isSaveSlotSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartSaveImport))]
    private bool canUseSaveManager;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartSaveImport))]
    private bool isSaveImporting;

    [ObservableProperty]
    private double saveImportProgressPercent;

    [ObservableProperty]
    private string saveImportProgressText = "等待备份";

    [ObservableProperty]
    private string saveImportProgressPercentText = "0%";

    public bool CanStartSaveImport => CanUseSaveManager && !IsSaveImporting;

    partial void OnSidebarWidthChanged(double value)
    {
        var clampedValue = Math.Clamp(value, SidebarCollapsedWidth, SidebarComfortWidth);
        if (Math.Abs(clampedValue - value) > 0.1)
        {
            SidebarWidth = clampedValue;
        }
    }

    partial void OnShowNavigationTooltipsChanged(bool value)
    {
        NotifyNavigationTooltipPropertiesChanged();
    }

    partial void OnCompactSidebarChanged(bool value)
    {
        SidebarWidth = value ? SidebarCollapsedWidth : SidebarComfortWidth;
    }

    partial void OnMinimizeOnLaunchChanged(bool value)
    {
        if (value && CloseAfterLaunch)
        {
            CloseAfterLaunch = false;
        }
    }

    partial void OnCloseAfterLaunchChanged(bool value)
    {
        if (value && MinimizeOnLaunch)
        {
            MinimizeOnLaunch = false;
        }
    }

    partial void OnBackgroundDimLevelChanged(double value)
    {
        var clampedValue = Math.Clamp(value, 30, 90);
        if (Math.Abs(clampedValue - value) > 0.1)
        {
            BackgroundDimLevel = clampedValue;
        }
    }

    public ObservableCollection<ModDownloadSourceOptionViewModel> ModDownloadSources { get; } =
    [
        new(ModDownloadSourceOptionViewModel.NexusKey, "Nexus Mods"),
        new(ModDownloadSourceOptionViewModel.SteamWorkshopKey, "Steam Workshop"),
        new(ModDownloadSourceOptionViewModel.ModPackKey, "Mod Packs")
    ];

    public ObservableCollection<string> ModDownloadCategories { get; } =
    [
        "All",
        "Gameplay",
        "Visuals",
        "Interface",
        "Items",
        "Utilities",
        "Updated"
    ];

    public ObservableCollection<ModDownloadCategoryFilterViewModel> ModDownloadCategoryFilters { get; } = [];

    [ObservableProperty]
    private ModListItemViewModel? selectedMod;

    [ObservableProperty]
    private string modCatalogStatusText = "No local mod catalog loaded";

    [ObservableProperty]
    private string selectedModDetailText = "Select a mod to inspect its local package path.";

    [ObservableProperty]
    private string selectedModConflictText = "Conflict status will appear after a catalog refresh.";

    [ObservableProperty]
    private string selectedModConflictHeadingText = "Conflict status";

    [ObservableProperty]
    private IBrush selectedModConflictIndicatorBrush = new SolidColorBrush(0xFF64748B);

    [ObservableProperty]
    private bool isSelectedModConflictStatusVisible = true;

    [ObservableProperty]
    private string conflictDetailsText = "Conflict details";

    [ObservableProperty]
    private string conflictDetailsDescriptionText = "File paths where the selected mod overlaps another enabled mod.";

    [ObservableProperty]
    private string autoFixSelectedModConflictsText = "Make selected mod active";

    [ObservableProperty]
    private string autoFixSelectedModConflictsDescriptionText = "Move this mod after the mods it conflicts with so the game reads its files first.";

    [ObservableProperty]
    private string autoFixSelectedModConflictsHintText = "Adjusts load order only. Mod files are not deleted or rewritten.";

    [ObservableProperty]
    private string autoFixAllModConflictsText = "Fix all";

    [ObservableProperty]
    private string autoFixAllModConflictsHintText = "Keeps the current load order and confirms all pending overlaps.";

    [ObservableProperty]
    private bool hasSelectedModConflictDetails;

    [ObservableProperty]
    private bool hasSelectedModConflictRepairOpportunity;

    [ObservableProperty]
    private bool hasNoSelectedModConflictDetails = true;

    [ObservableProperty]
    private string selectedModConflictDetailsEmptyText = "No file-level conflicts for the selected mod.";

    [ObservableProperty]
    private bool hasMods;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoModConflicts))]
    private bool hasModConflicts;

    public bool HasNoModConflicts => !HasModConflicts;

    [ObservableProperty]
    private bool isDashboardActive = true;

    [ObservableProperty]
    private bool isModsActive;

    [ObservableProperty]
    private bool isStoreActive;

    [ObservableProperty]
    private bool isSettingsActive;

    [ObservableProperty]
    private bool isSavesActive;

    [ObservableProperty]
    private bool isLabActive;

    [ObservableProperty]
    private bool isLabView;

    [ObservableProperty]
    private bool isLabAlchemyActive = true;

    [ObservableProperty]
    private bool isLabForgingActive;

    [ObservableProperty]
    private string labNavText = "Lab";

    [ObservableProperty]
    private string labModuleDescription = "KCD2 gameplay simulations and toolkits live here.";

    [ObservableProperty]
    private string labAlchemyText = "Alchemy";

    [ObservableProperty]
    private string labForgingText = "Forging";

    [ObservableProperty]
    private string labAlchemyLongDescription = "Recreate the KCD2 alchemy bench: brew potions, distil spirits, and experiment with herb and ingredient combinations.";

    [ObservableProperty]
    private string labForgingLongDescription = "Recreate the KCD2 smithy: shape, temper, and sharpen blades with the historical forging mini-game.";

    [ObservableProperty]
    private string labPlannedFeaturesText = "Planned features";

    [ObservableProperty]
    private string labPlaceholderText = "Framework ready";

    [ObservableProperty]
    private string labStatusPrototypeText = "Prototype";

    [ObservableProperty]
    private string labStatusPlannedText = "Planned";

    [ObservableProperty]
    private bool isLauncherView = true;

    [ObservableProperty]
    private bool isSettingsView;

    [ObservableProperty]
    private bool isLauncherDashboardVisible = true;

    [ObservableProperty]
    private bool isModManagerVisible;

    [ObservableProperty]
    private string modManagerLoadingText = "Mods are loading...";

    [ObservableProperty]
    private bool isLauncherNavigationVisible = true;

    [ObservableProperty]
    private bool isModListPageActive = true;

    [ObservableProperty]
    private bool isModDownloadPageActive;

    [ObservableProperty]
    private bool isModRecommendationPageActive;

    [ObservableProperty]
    private bool isModListPageVisible;

    [ObservableProperty]
    private bool isModDownloadPageVisible;

    [ObservableProperty]
    private bool isModRecommendationPageVisible;

    [ObservableProperty]
    private string modManagerPageTitle = "Mod List";

    [ObservableProperty]
    private string modManagerPageDescription = "Review installed mods, load order, conflicts, and local package state.";

    [ObservableProperty]
    private string modManagerExitText = "Exit Mod Manager";

    [ObservableProperty]
    private string modManagerHeaderText = "MOD MANAGER";

    [ObservableProperty]
    private string modManagerTitleText = "Mod List";

    [ObservableProperty]
    private string modListPageText = "Mod List";

    [ObservableProperty]
    private string modDownloadPageText = "Mod Download";

    [ObservableProperty]
    private string modRecommendationPageText = "Mod Recommendations";

    [ObservableProperty]
    private string addLocalModTooltipText = "Add local mod";

    [ObservableProperty]
    private string deleteSelectedModTooltipText = "Delete selected mod";

    [ObservableProperty]
    private string renameModGroupTooltipText = "Rename group";

    [ObservableProperty]
    private string createModGroupText = "New group";

    [ObservableProperty]
    private string deleteModGroupTooltipText = "Delete group";

    [ObservableProperty]
    private string addModToGroupText = "Add mod";

    [ObservableProperty]
    private string dragModToGroupTooltipText = "Drag to a custom group";

    [ObservableProperty]
    private string customGroupBadgeText = "Custom group";

    [ObservableProperty]
    private string returnToAutomaticGroupTooltipText = "Return to automatic grouping";

    [ObservableProperty]
    private string emptyModGroupDropText = "Drag a mod card here";

    [ObservableProperty]
    private string noModsAvailableForGroupText = "All installed mods are already in this group.";

    [ObservableProperty]
    private string modGroupAssignmentLabelText = "Group";

    [ObservableProperty]
    private string automaticModGroupText = "Automatic";

    [ObservableProperty]
    private string moveToModGroupText = "Move";

    [ObservableProperty]
    private string modGroupNameEditorTitleText = "Rename group";

    [ObservableProperty]
    private string modGroupNameLabelText = "Group name";

    [ObservableProperty]
    private string modGroupNameWatermarkText = "Enter a group name";

    [ObservableProperty]
    private string restoreDefaultModGroupNameText = "Restore default";

    [ObservableProperty]
    private string saveModGroupNameText = "Save name";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveModGroupNameCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedModToGroupCommand))]
    private bool isModGroupNameSaving;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveModGroupNameCommand))]
    private string modGroupNameDraft = string.Empty;

    [ObservableProperty]
    private InstalledModGroupOptionViewModel? selectedModGroupOption;

    [ObservableProperty]
    private string addText = "Add";

    [ObservableProperty]
    private string directoryText = "Directory";

    [ObservableProperty]
    private string refreshText = "Refresh";

    [ObservableProperty]
    private string deleteText = "Delete";

    [ObservableProperty]
    private string enableAllText = "Enable all";

    [ObservableProperty]
    private string disableAllText = "Disable all";

    [ObservableProperty]
    private string installedText = "Installed";

    [ObservableProperty]
    private string enabledWithSeparatorText = "Enabled /";

    [ObservableProperty]
    private string disabledText = "Disabled";

    [ObservableProperty]
    private string orderText = "Order";

    [ObservableProperty]
    private string stateText = "State";

    [ObservableProperty]
    private string nameText = "Name";

    [ObservableProperty]
    private string versionText = "Version";

    [ObservableProperty]
    private string idText = "ID";

    [ObservableProperty]
    private string actionsText = "Actions";

    [ObservableProperty]
    private string progressColumnText = "Progress";

    [ObservableProperty]
    private string sizeColumnText = "Size";

    [ObservableProperty]
    private string speedColumnText = "Speed";

    [ObservableProperty]
    private string enableText = "Enable";

    [ObservableProperty]
    private string detailsText = "Details";

    [ObservableProperty]
    private string builtInText = "Built-in";

    [ObservableProperty]
    private string openDirectoryText = "Open Folder";

    [ObservableProperty]
    private string modSearchWatermarkText = "Search name, author, or tag";

    [ObservableProperty]
    private string localPackageText = "Local package";

    [ObservableProperty]
    private string searchText = "Search";

    [ObservableProperty]
    private string downloadSourceText = "Download sources";

    [ObservableProperty]
    private string onlineSearchImportText = "Online search and import";

    [ObservableProperty]
    private string connectText = "Connect";

    [ObservableProperty]
    private string localArchiveText = "Local archive";

    [ObservableProperty]
    private string chooseFileImportText = "Choose a file and import";

    [ObservableProperty]
    private string browseText = "Browse";

    [ObservableProperty]
    private string downloadQueueText = "Download queue";

    [ObservableProperty]
    private string currentDownloadText = "Current download";

    [ObservableProperty]
    private string downloadCenterEmptyText = "Select a download from the queue.";

    [ObservableProperty]
    private string downloadCenterLauncherTooltipText = "Open download queue";

    [ObservableProperty]
    private string downloadingQueueText = "Active downloads";

    [ObservableProperty]
    private string downloadedQueueText = "View downloaded mods";

    [ObservableProperty]
    private string activeDownloadQueueEmptyText = "No active downloads.";

    [ObservableProperty]
    private string completedDownloadQueueEmptyText = "Completed downloads will appear here.";

    [ObservableProperty]
    private string visibleDownloadQueueEmptyText = "No active downloads.";

    [ObservableProperty]
    private string activeDownloadQueueDescriptionText = "Downloads that are queued, running, paused, or need attention.";

    [ObservableProperty]
    private string completedDownloadQueueDescriptionText = "Finished mod downloads ready to review, install, or open.";

    [ObservableProperty]
    private string noTasksText = "No tasks";

    [ObservableProperty]
    private string nexusApiKey = string.Empty;

    [ObservableProperty]
    private string nexusApiKeyWatermarkText = "Nexus personal key";

    [ObservableProperty]
    private bool isNexusCookieConsentDialogOpen;

    [ObservableProperty]
    private bool rememberNexusCookieConsent;

    [ObservableProperty]
    private bool allowNexusBrowserCookieAuth;

    [ObservableProperty]
    private string nexusCookieConsentTitleText = "Use browser sign-in for Nexus Mods downloads?";

    [ObservableProperty]
    private string nexusCookieConsentIntroText = "BohemiX can use your Nexus browser sign-in when the normal download flow needs it.";

    [ObservableProperty]
    private string nexusCookieConsentReadText = "BohemiX checks supported browsers only for a valid Nexus Mods sign-in.";

    [ObservableProperty]
    private string nexusCookieConsentProtectText = "Your Nexus login information stays on this device and is used only for Nexus Mods requests.";

    [ObservableProperty]
    private string nexusCookieConsentRevokeText = "You can disconnect this account at any time in Settings or by signing out of Nexus Mods.";

    [ObservableProperty]
    private string nexusCookieConsentLegalReviewText = "Draft privacy copy: legal/compliance review required before release.";

    [ObservableProperty]
    private string nexusCookieConsentAgreeText = "Agree";

    [ObservableProperty]
    private string nexusCookieConsentDeclineText = "Decline";

    [ObservableProperty]
    private string nexusCookieConsentRememberText = "Do not ask again";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmSteamAccountBinding))]
    private bool isSteamAccountBindingDialogOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmSteamAccountBinding))]
    private bool isSteamAccountBindingBusy;

    [ObservableProperty]
    private string steamAccountBindingDialogTitleText = "Bind Steam account";

    [ObservableProperty]
    private string steamAccountBindingDialogDescriptionText = "Bind the current offline profile to the active Steam account before browsing Steam Workshop mods.";

    [ObservableProperty]
    private string steamAccountBindingOfflineAccountLabelText = "Offline account";

    [ObservableProperty]
    private string steamAccountBindingOfflineAccountNameText = string.Empty;

    [ObservableProperty]
    private string steamAccountBindingSteamAccountLabelText = "Steam account";

    [ObservableProperty]
    private string steamAccountBindingSteamAccountNameText = string.Empty;

    [ObservableProperty]
    private string steamAccountBindingSteamAccountIdText = string.Empty;

    [ObservableProperty]
    private string steamAccountBindingStatusText = string.Empty;

    [ObservableProperty]
    private string steamAccountBindingConfirmText = "Bind and continue";

    [ObservableProperty]
    private string steamAccountBindingCancelText = "Cancel";

    [ObservableProperty]
    private bool isSteamAccountBindingConfirmEnabled;

    public bool CanConfirmSteamAccountBinding =>
        IsSteamAccountBindingDialogOpen && IsSteamAccountBindingConfirmEnabled && !IsSteamAccountBindingBusy;

    [ObservableProperty]
    private string nexusSearchQuery = string.Empty;

    [ObservableProperty]
    private bool hasModSearchQuery;

    [ObservableProperty]
    private bool canSearchModDownloads = true;

    [ObservableProperty]
    private ModDownloadSourceOptionViewModel? selectedModDownloadSource;

    public bool IsModPackSourceSelected => SelectedModDownloadSource?.IsModPack == true;
    public bool IsModDownloadLoading => IsSearchingNexusMods || isModDownloadSourceTransitionLoading;

    public bool IsStandardModSourceSelected => !IsModPackSourceSelected;

    public bool IsStandardModSearchEmpty => IsStandardModSourceSelected && IsNexusModSearchEmpty;

    public bool IsVisibleModPackSearchEmpty => IsModPackSourceSelected && IsModPackSearchEmpty;

    public bool HasVisibleModDownloadSearchResults => IsModPackSourceSelected
        ? HasModPackResults
        : HasNexusModSearchResults;

    [ObservableProperty]
    private string selectedModDownloadCategory = "All";

    [ObservableProperty]
    private bool isModDownloadCardView = true;

    [ObservableProperty]
    private bool isModDownloadListView;

    [ObservableProperty]
    private bool isModDownloadDetailVisible;

    [ObservableProperty]
    private bool isModPackDetailVisible;

    [ObservableProperty]
    private bool hasNexusModSearchResults;

    [ObservableProperty]
    private bool isNexusModSearchEmpty = true;

    [ObservableProperty]
    private bool hasModPackResults;

    [ObservableProperty]
    private bool isModPackSearchEmpty = true;

    [ObservableProperty]
    private bool isDownloadCenterVisible;

    [ObservableProperty]
    private bool isDownloadLauncherVisible;

    [ObservableProperty]
    private bool hasSelectedDownloadQueueItem;

    [ObservableProperty]
    private ModDownloadQueueRowViewModel? selectedDownloadQueueItem;

    [ObservableProperty]
    private bool hasModDownloadQueueItems;

    [ObservableProperty]
    private bool isModDownloadQueueEmpty = true;

    [ObservableProperty]
    private bool hasVisibleModDownloadQueueItems;

    [ObservableProperty]
    private bool isVisibleModDownloadQueueEmpty = true;

    [ObservableProperty]
    private string activeModDownloadQueueCountText = "0";

    [ObservableProperty]
    private string downloadedModDownloadQueueCountText = "0";

    [ObservableProperty]
    private string totalModDownloadQueueCountText = "0";

    [ObservableProperty]
    private string modDownloadQueueSummaryText = "No queued downloads";

    [ObservableProperty]
    private string modDownloadQueueThroughputText = "-";

    [ObservableProperty]
    private string modDownloadQueueSizeText = "-";

    [ObservableProperty]
    private string visibleDownloadQueueSectionTitleText = "Active downloads";

    [ObservableProperty]
    private string visibleDownloadQueueSectionDescriptionText = "Downloads that are queued, running, paused, or need attention.";

    [ObservableProperty]
    private bool isDownloadingQueueSectionActive = true;

    [ObservableProperty]
    private bool isDownloadedQueueSectionActive;

    [ObservableProperty]
    private string selectedModDownloadQueueSection = DownloadingQueueSection;

    [ObservableProperty]
    private bool canStartModDownloadQueue;

    [ObservableProperty]
    private bool canPauseModDownloadQueue;

    [ObservableProperty]
    private bool canResumeModDownloadQueue;

    [ObservableProperty]
    private bool canCancelModDownloadQueue;

    [ObservableProperty]
    private bool canClearModDownloadQueue;

    [ObservableProperty]
    private bool canOpenModDownloadDirectory;

    [ObservableProperty]
    private NexusModSearchRowViewModel? selectedNexusMod;

    [ObservableProperty]
    private ModPackRowViewModel? selectedModPack;

    public ObservableCollection<ModPackInstallOptionViewModel> ModPackInstallOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmModPackInstall))]
    private bool isModPackPreflightOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmModPackInstall))]
    private bool isModPackPreflightBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmModPackInstall))]
    private bool isModPackAdultContentConfirmed;

    [ObservableProperty]
    private string modPackPreflightTitleText = string.Empty;

    [ObservableProperty]
    private string modPackPreflightSummaryText = string.Empty;

    [ObservableProperty]
    private string modPackPreflightCompatibilityText = string.Empty;

    [ObservableProperty]
    private string modPackPreflightCountsText = string.Empty;

    [ObservableProperty]
    private string modPackPreflightSizeText = string.Empty;

    [ObservableProperty]
    private string modPackPreflightStatusText = string.Empty;

    [ObservableProperty]
    private bool modPackPreflightContainsAdultContent;

    [ObservableProperty]
    private double modPackInstallProgressPercent;

    [ObservableProperty]
    private string modPackInstallProgressText = string.Empty;

    public bool CanConfirmModPackInstall =>
        IsModPackPreflightOpen
        && !IsModPackPreflightBusy
        && pendingModPackInstallPlan is not null
        && (!ModPackPreflightContainsAdultContent || IsModPackAdultContentConfirmed);

    [ObservableProperty]
    private NexusModDownloadPresetViewModel? selectedNexusModDownloadPreset;

    [ObservableProperty]
    private NexusModSearchRowViewModel? nexusModFilePickerMod;

    [ObservableProperty]
    private bool isNexusModFilePickerVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmNexusModFileDownloadCommand))]
    private bool isLoadingNexusModFileOptions;

    [ObservableProperty]
    private string nexusModFilePickerStatusText = string.Empty;

    [ObservableProperty]
    private string nexusModFilePickerTitleText = "Choose a file to download";

    [ObservableProperty]
    private string nexusModFilePickerDescriptionText = "Choose the version or optional file you want to add to the download queue.";

    [ObservableProperty]
    private string nexusModFilePickerLoadingText = "Getting available files...";

    [ObservableProperty]
    private string nexusModFilePickerEmptyText = "No downloadable files are available for this mod.";

    [ObservableProperty]
    private string nexusModFilePickerPrimaryText = "Main file";

    [ObservableProperty]
    private string nexusModFilePickerFileLabelText = "File";

    [ObservableProperty]
    private string nexusModFilePickerUploadedText = "Uploaded";

    [ObservableProperty]
    private string nexusModFilePickerConfirmText = "Add to queue";

    [ObservableProperty]
    private string nexusModFilePickerCancelText = "Cancel";

    [ObservableProperty]
    private string nexusModFilePickerPresetsText = "Quick setups";

    [ObservableProperty]
    private string nexusModFilePickerAdvancedText = "Advanced: adjust files";

    [ObservableProperty]
    private string nexusModFilePickerSelectionText = "No files selected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNexusModRequirements))]
    private bool autoDownloadNexusModRequirements = true;

    public bool HasNexusModRequirements => NexusModRequirementOptions.Count > 0;

    [ObservableProperty]
    private string nexusModRequirementsSummaryText = string.Empty;

    [ObservableProperty]
    private string nexusModRequirementsTitleText = "Prerequisite Mods";

    [ObservableProperty]
    private string nexusModRequirementsAutoDownloadText = "Automatically download available prerequisite mods";

    [ObservableProperty]
    private string nexusModRequirementDetailsText = "Open details";

    [ObservableProperty]
    private bool isNexusModFileAdvancedSelectionVisible;

    private bool isApplyingNexusModDownloadPreset;

    [ObservableProperty]
    private string cardViewText = "Cards";

    [ObservableProperty]
    private string listViewText = "List";

    [ObservableProperty]
    private string modCategoryText = "Category";

    [ObservableProperty]
    private string downloadProviderText = "Source";

    [ObservableProperty]
    private string modDetailText = "Details";

    [ObservableProperty]
    private string modSummaryText = "Mod summary";

    [ObservableProperty]
    private string viewOriginalPageText = "View original page";

    [ObservableProperty]
    private string searchResultsText = "Results";

    [ObservableProperty]
    private string downloadText = "Download";

    [ObservableProperty]
    private string startQueueText = "Start";

    [ObservableProperty]
    private string pauseText = "Pause";

    [ObservableProperty]
    private string resumeText = "Resume";

    [ObservableProperty]
    private string cancelText = "Cancel";

    [ObservableProperty]
    private string clearText = "Clear";

    [ObservableProperty]
    private string retryText = "Retry";

    [ObservableProperty]
    private string openFolderText = "Open folder";

    [ObservableProperty]
    private string openDownloadDirectoryText = "Open downloads";

    [ObservableProperty]
    private string loadMoreModSearchResultsText = "Load more";

    [ObservableProperty]
    private string nexusStatusText = "Ready";

    [ObservableProperty]
    private string nexusResultEmptyText = "Search Nexus Mods to list compatible KCD2 packages.";

    [ObservableProperty]
    private string nexusDownloadQueueEmptyText = "Queued Nexus downloads will appear here.";

    [ObservableProperty]
    private bool isSearchingNexusMods;

    [ObservableProperty]
    private bool isLoadingMoreModSearchResults;

    [ObservableProperty]
    private bool hasMoreModSearchResults;

    [ObservableProperty]
    private bool isDownloadingMods;

    [ObservableProperty]
    private string popularText = "Popular";

    [ObservableProperty]
    private string stableText = "Stable";

    [ObservableProperty]
    private string visualsText = "Visuals";

    [ObservableProperty]
    private string starterPackText = "Starter Pack";

    [ObservableProperty]
    private string firstInstallRecommendationText = "Curated essentials to get started";

    [ObservableProperty]
    private string visualOptimizationText = "Visual Optimization";

    [ObservableProperty]
    private string textureLightingUiEnhancementText = "Higher-fidelity visuals with minimal cost";

    [ObservableProperty]
    private string compatibilityFirstText = "Compatibility First";

    [ObservableProperty]
    private string lowConflictCombinationText = "Plays well together, fewer overrides";

    [ObservableProperty]
    private string recommendationListText = "Recommendation List";

    [ObservableProperty]
    private string inventoryInfoOptimizationText = "Inventory Info Optimization";

    [ObservableProperty]
    private string interfaceText = "Interface";

    [ObservableProperty]
    private string stablePerformancePresetText = "Stable Performance Preset";

    [ObservableProperty]
    private string performanceText = "Performance";

    [ObservableProperty]
    private string recommendationBasisText = "Recommendation Basis";

    [ObservableProperty]
    private string recommendationBasisDescriptionText = "Heat-ranked by compatibility, popularity and freshness";

    [ObservableProperty]
    private string modRecommendationStatusText = "Recommendations are ready.";

    [ObservableProperty]
    private string modRecommendationAlgorithmText = "Hybrid recommendation algorithm";

    [ObservableProperty]
    private string modRecommendationAlgorithmDescriptionText = "Heat blends content fit, popularity, quality and freshness.";

    [ObservableProperty]
    private string selectedModRecommendationStrategy = "PopularityQuality";

    [ObservableProperty]
    private bool isModRecommendationCardView = true;

    [ObservableProperty]
    private bool isModRecommendationListView;

    [ObservableProperty]
    private string contentRecommendationText = "Content-based";

    [ObservableProperty]
    private string popularityQualityRecommendationText = "Popular + quality";

    [ObservableProperty]
    private string starterRecommendationText = "Starter-safe";

    [ObservableProperty]
    private string refreshRecommendationsText = "Refresh recommendations";

    [ObservableProperty]
    private string recommendationQualifierInput = string.Empty;

    [ObservableProperty]
    private string recommendationQualifierText = "Recommendation filters";

    [ObservableProperty]
    private string recommendationQualifierWatermarkText = "Add keyword...";

    [ObservableProperty]
    private string recommendationQualifierHintText = "Use words like performance, UI, Chinese, combat, visual, save.";

    [ObservableProperty]
    private string recommendationQualifierEmptyText = "No custom filters yet.";

    [ObservableProperty]
    private bool hasRecommendationQualifiers;

    [ObservableProperty]
    private string recommendationScoreText = "Heat";

    [ObservableProperty]
    private string recommendationMatchText = "Match";

    [ObservableProperty]
    private string recommendationQualityText = "Quality";

    [ObservableProperty]
    private string recommendationReasonText = "Recommendation reason";

    [ObservableProperty]
    private bool hasModRecommendations;

    [ObservableProperty]
    private bool isModRecommendationEmpty = true;

    [ObservableProperty]
    private bool isLoadingModRecommendations;

    [ObservableProperty]
    private ModRecommendationRowViewModel? selectedModRecommendation;

    [ObservableProperty]
    private string checkConflictsBeforeLaunchText = "Check conflicts before launch";

    [ObservableProperty]
    private string checkConflictsBeforeLaunchDescriptionText = "Check whether multiple mods change the same file before starting the game.";

    [ObservableProperty]
    private string nexusBrowserCookieAuthText = "Nexus Mods account";

    [ObservableProperty]
    private string nexusBrowserCookieAuthDescriptionText = "Sign in to Nexus Mods and allow BohemiX access. A backup personal key stays on this computer.";

    [ObservableProperty]
    private string revokeNexusCookieAuthText = "Unbind";

    [ObservableProperty]
    private string nexusSignInText = "Bind Account";

    [ObservableProperty]
    private string testNexusCookieAuthText = "Refresh";

    [ObservableProperty]
    private string installedModsText = "Installed mods";

    [ObservableProperty]
    private string registeredModsText = "Registered mods";

    [ObservableProperty]
    private string detectedConflictsText = "Detected conflicts";

    [ObservableProperty]
    private string settingsCategoryTitle = "Navigation";

    [ObservableProperty]
    private string settingsCategoryDescription = "Set language and navigation behavior for the launcher shell.";

    [ObservableProperty]
    private bool isSettingsNavigationActive = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsConfigurationActive))]
    private bool isSettingsPlayerProfilesActive;

    [ObservableProperty]
    private bool isSettingsLaunchActive;

    [ObservableProperty]
    private bool isSettingsModsActive;

    [ObservableProperty]
    private bool isSettingsAppearanceActive;

    [ObservableProperty]
    private bool isSettingsGraphicsActive;

    [ObservableProperty]
    private bool isSettingsStorageActive;

    [ObservableProperty]
    private bool isSettingsAdvancedActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsConfigurationActive))]
    private bool isSettingsMoreActive;

    public bool IsSettingsConfigurationActive => !IsSettingsPlayerProfilesActive && !IsSettingsMoreActive;

    [ObservableProperty]
    private string settingsPlayerProfilesText = "Player Profiles";

    [ObservableProperty]
    private string settingsWorkspaceTitle = "Launcher Settings";

    [ObservableProperty]
    private bool autoDiscoverOnStartup = true;

    [ObservableProperty]
    private bool enableVfsBeforeLaunch = true;

    [ObservableProperty]
    private bool checkModConflictsBeforeLaunch = true;

    [ObservableProperty]
    private bool minimizeOnLaunch;

    [ObservableProperty]
    private bool closeAfterLaunch;

    [ObservableProperty]
    private bool useSteamProtocol;

    [ObservableProperty]
    private bool enableAcrylicEffects = true;

    [ObservableProperty]
    private bool reduceMotion;

    [ObservableProperty]
    private bool isLaunchReady;

    [ObservableProperty]
    private bool compactSidebar;

    [ObservableProperty]
    private bool showNavigationTooltips = true;

    [ObservableProperty]
    private bool keepTopNavigationVisible = true;

    [ObservableProperty]
    private bool showRuntimeCardInSidebar = true;

    [ObservableProperty]
    private string selectedLanguage = SimplifiedChineseLanguage;

    [ObservableProperty]
    private string defaultNavigationTarget = "Dashboard";

    [ObservableProperty]
    private string navigationHeaderText = "NAVIGATION";

    [ObservableProperty]
    private string settingsHeaderText = "SETTINGS";

    [ObservableProperty]
    private string dashboardNavText = "Dashboard";

    [ObservableProperty]
    private string modsNavText = "Mods";

    [ObservableProperty]
    private string savesNavText = "Saves";

    [ObservableProperty]
    private string settingsNavigationText = "Navigation";

    [ObservableProperty]
    private string settingsLaunchText = "Launch";

    [ObservableProperty]
    private string settingsAppearanceText = "Appearance";

    [ObservableProperty]
    private string settingsGraphicsText = "Graphics";

    [ObservableProperty]
    private string settingsStorageText = "Storage";

    [ObservableProperty]
    private string settingsAdvancedText = "Advanced";

    [ObservableProperty]
    private string settingsMoreText = "More";

    [ObservableProperty]
    private int forgeRenderQualityIndex = 1;

    [ObservableProperty]
    private int forgeTextureQualityIndex = 1;

    [ObservableProperty]
    private string forgeRenderQualityText = "Forge render quality";

    [ObservableProperty]
    private string forgeRenderQualityDescriptionText = "Controls resolution scale, shadows, and particle density.";

    [ObservableProperty]
    private string forgeTextureQualityText = "Forge texture quality";

    [ObservableProperty]
    private string forgeTextureQualityDescriptionText = "Controls material texture resolution and memory usage.";

    [ObservableProperty]
    private string forgeSettingsApplyNextEntryText = "Changes take effect the next time Forge opens.";

    [ObservableProperty]
    private string storeNavText = "Store";

    [ObservableProperty]
    private string launcherSettingsTitleText = "Launcher Settings";

    [ObservableProperty]
    private string resetButtonText = "Reset";

    [ObservableProperty]
    private string applyButtonText = "Apply";

    [ObservableProperty]
    private string languageSettingText = "Language";

    [ObservableProperty]
    private string languageSettingDescriptionText = "Select the launcher interface language for this session.";

    [ObservableProperty]
    private string defaultPageSettingText = "Default page";

    [ObservableProperty]
    private string defaultPageSettingDescriptionText = "Choose which page BohemiX should open after startup.";

    [ObservableProperty]
    private string topNavigationTooltipsText = "Top navigation tooltips";

    [ObservableProperty]
    private string topNavigationTooltipsDescriptionText = "Show short labels when hovering over the top navigation icons.";

    [ObservableProperty]
    private string keepTopNavigationVisibleText = "Keep top navigation visible";

    [ObservableProperty]
    private string keepTopNavigationVisibleDescriptionText = "Keep the floating icon island available while switching setting categories.";

    [ObservableProperty]
    private string sidebarRuntimeCardText = "Sidebar game status card";

    [ObservableProperty]
    private string sidebarRuntimeCardDescriptionText = "Keep a compact KCD2 game status card below the left navigation.";

    [ObservableProperty]
    private string scanOnStartupText = "Scan KCD2 on startup";

    [ObservableProperty]
    private string scanOnStartupDescriptionText = "Look for KCD2 in Steam and Epic when BohemiX opens.";

    [ObservableProperty]
    private string prepareVfsText = "Prepare mods before launch";

    [ObservableProperty]
    private string prepareVfsDescriptionText = "Prepare enabled mods before starting KingdomCome.exe so they can load in the game.";

    [ObservableProperty]
    private string launchArgumentsText = "Launch arguments";

    [ObservableProperty]
    private string launchArgumentsDescriptionText = "Optional session arguments passed to the launcher flow.";

    [ObservableProperty]
    private string useSteamProtocolText = "Use Steam protocol";

    [ObservableProperty]
    private string windowBehaviorText = "Window behavior after launch";

    [ObservableProperty]
    private string windowBehaviorDescriptionText = "Choose whether BohemiX stays visible after the game starts.";

    [ObservableProperty]
    private string minimizeText = "Minimize";

    [ObservableProperty]
    private string closeText = "Close";

    [ObservableProperty]
    private string gameGenreText = "Medieval RPG";

    [ObservableProperty]
    private string launcherHeroDescriptionText = "Single-game launcher for KCD2. Verify your local installation, manage mods and start Henry's journey from one clean command center.";

    [ObservableProperty]
    private string verifyInstallText = "Verify Install";

    [ObservableProperty]
    private string launcherActivityTitleText = "Launcher Activity";

    [ObservableProperty]
    private string statusLabelText = "Status";

    [ObservableProperty]
    private string currentLauncherStateText = "Current launcher state";

    [ObservableProperty]
    private string installLabelText = "Install";

    [ObservableProperty]
    private string lastLabelText = "Last";

    [ObservableProperty]
    private string kcd2InstallTitleText = "KCD2 Install";

    [ObservableProperty]
    private string executableLabelText = "Game launcher file";

    [ObservableProperty]
    private string kcd2InstallsLabelText = "KCD2 installs";

    [ObservableProperty]
    private string conflictsLabelText = "Conflicts";

    [ObservableProperty]
    private string kcd2RuntimeTitleText = "KCD2 Runtime";

    [ObservableProperty]
    private string installSourceLabelText = "Install source";

    [ObservableProperty]
    private string vfsStateLabelText = "Mod loading status";

    [ObservableProperty]
    private string databaseLabelText = "Program data";

    [ObservableProperty]
    private string quickActionsTitleText = "Quick Actions";

    [ObservableProperty]
    private string quickLaunchSubtitleText = "Kingdom Come II";

    [ObservableProperty]
    private string manageModsText = "Manage Mods";

    [ObservableProperty]
    private string saveMonitorText = "Save Monitor";

    [ObservableProperty]
    private string openSaveToolsText = "Open save tools";

    [ObservableProperty]
    private string runtimePathsText = "Program folders";

    [ObservableProperty]
    private string verifyShortText = "Verify";

    [ObservableProperty]
    private string playNowText = "Play Now";

    [ObservableProperty]
    private string kcd2OnlyText = "KCD2 ONLY";

    [ObservableProperty]
    private string runtimeLabelText = "Game status";

    [ObservableProperty]
    private string scrollForDetailsText = "Scroll down for launcher details";

    [ObservableProperty]
    private string settingsCategorySelectedPrefixText = "Settings category selected";

    [ObservableProperty]
    private string scaleText = "Scale";

    [ObservableProperty]
    private string acrylicGlassEffectsText = "Acrylic glass effects";

    [ObservableProperty]
    private string acrylicGlassEffectsDescriptionText = "Keep the Nebula Glass material and soft glow surfaces enabled.";

    [ObservableProperty]
    private string reduceMotionText = "Reduce motion";

    [ObservableProperty]
    private string reduceMotionDescriptionText = "Use calmer transitions for hover and page changes.";

    [ObservableProperty]
    private string backdropDimStrengthText = "Backdrop dim strength";

    [ObservableProperty]
    private string backdropDimStrengthDescriptionText = "Tune how strongly the background image recedes behind controls.";

    [ObservableProperty]
    private string compactLeftNavigationText = "Compact left navigation";

    [ObservableProperty]
    private string compactLeftNavigationDescriptionText = "Reserve more width for settings and launcher content.";

    [ObservableProperty]
    private string dataDirectoryText = "Data directory";

    [ObservableProperty]
    private string modsDirectoryText = "Mods directory";

    [ObservableProperty]
    private string openText = "Open";

    [ObservableProperty]
    private string runtimeStateText = "Program status";

    [ObservableProperty]
    private string kcd2ExecutableText = "KCD2 game launcher file";

    [ObservableProperty]
    private string lastActionLabelText = "Last action";

    [ObservableProperty]
    private string resetKcd2GameDirectoryText = "Reset";

    [ObservableProperty]
    private string verificationDialogTitleText = "Verify installation";

    [ObservableProperty]
    private string verificationDialogQuestionText = "Do you own Kingdom Come: Deliverance II?";

    [ObservableProperty]
    private string verificationDialogIntroText = "Confirm ownership, then locate your local game files.";

    [ObservableProperty]
    private string automaticSearchTitleText = "Automatic detection";

    [ObservableProperty]
    private string deepSearchButtonText = "Deep search";

    [ObservableProperty]
    private string manualSelectionTitleText = "Or choose the game files manually";

    [ObservableProperty]
    private string verificationDialogExitText = "Exit";

    [ObservableProperty]
    private string verificationDialogPurchaseText = "Buy on Steam";

    [ObservableProperty]
    private string verificationDialogOwnedText = "I own it, continue";

    [ObservableProperty]
    private string manualGamePathWatermarkText = "KingdomCome.exe or installation folder";

    [ObservableProperty]
    private string gameDirectoryText = "Directory";

    [ObservableProperty]
    private string addPathText = "Add path";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundDimOpacity))]
    private double backgroundDimLevel = 68;

    [ObservableProperty]
    private string launchArguments = string.Empty;

    public double BackgroundDimOpacity => Math.Clamp(BackgroundDimLevel / 100d, 0.3d, 0.9d);

    public string? DashboardNavTooltipText => ShowNavigationTooltips ? DashboardNavText : null;

    public string? ModsNavTooltipText => ShowNavigationTooltips ? ModsNavText : null;

    public string? SavesNavTooltipText => ShowNavigationTooltips ? SavesNavText : null;

    public string? StoreNavTooltipText => ShowNavigationTooltips ? StoreNavText : null;

    public string? LabNavTooltipText => ShowNavigationTooltips ? LabNavText : null;

    public string? SettingsNavTooltipText => ShowNavigationTooltips ? SettingsHeaderText : null;

    public string? SettingsPlayerProfilesTooltipText => ShowNavigationTooltips ? SettingsPlayerProfilesText : null;

    public string? SettingsNavigationTooltipText => ShowNavigationTooltips ? SettingsNavigationText : null;

    public string? SettingsLaunchTooltipText => ShowNavigationTooltips ? SettingsLaunchText : null;

    public string? SettingsAppearanceTooltipText => ShowNavigationTooltips ? SettingsAppearanceText : null;

    public string? SettingsGraphicsTooltipText => ShowNavigationTooltips ? SettingsGraphicsText : null;

    public string? SettingsStorageTooltipText => ShowNavigationTooltips ? SettingsStorageText : null;

    public string? SettingsAdvancedTooltipText => ShowNavigationTooltips ? SettingsAdvancedText : null;

    public string? SettingsMoreTooltipText => ShowNavigationTooltips ? SettingsMoreText : null;

    [ObservableProperty]
    private bool isInstallVerificationDialogOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOwnershipChoiceVisible))]
    private bool isOwnedGameOptionsVisible;

    public bool IsOwnershipChoiceVisible => !IsOwnedGameOptionsVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyManualGamePathCommand))]
    private string manualGamePath = string.Empty;

    [ObservableProperty]
    private string verificationDialogMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoSearchInProgress))]
    private bool isAutoSearchButtonEnabled = true;

    public bool IsAutoSearchInProgress => !IsAutoSearchButtonEnabled;

    [ObservableProperty]
    private bool isDeepGameSearchAvailable;

    [ObservableProperty]
    private string autoSearchButtonText = "自动搜索";

    partial void OnSelectedLanguageChanged(string value)
    {
        if (value == LegacySimplifiedChineseLanguage)
        {
            SelectedLanguage = SimplifiedChineseLanguage;
            return;
        }

        if (!AvailableLanguages.Contains(value))
        {
            SelectedLanguage = SimplifiedChineseLanguage;
            return;
        }

        ApplyLanguage();
        SaveManager.UseLanguage(value);
        SelectSettingsCategory(currentSettingsCategory);
        StatusText = $"{T("LanguageChanged")}: {textCatalog.FormatLanguageDisplayName(value)}";
        LastActionText = StatusText;
    }

    partial void OnSourceTextChanged(string value)
    {
        var platform = FormatAdventurePlatform(value);
        if (platform is not null)
        {
            AdventureProfile.Platform = platform;
        }
    }

    partial void OnEnabledModCountChanged(int value)
    {
        AdventureProfile.UpdateInstalledMods(value);
    }

    partial void OnSelectedModChanged(ModListItemViewModel? value)
    {
        foreach (var row in ModRows)
        {
            row.IsSelected = ReferenceEquals(row, value);
        }

        RefreshSelectedModText(value);
        RefreshSelectedModConflictDetails(value);
        RefreshSelectedModGroupOption(value);
        MoveSelectedModToGroupCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModGroupOptionChanged(InstalledModGroupOptionViewModel? value)
    {
        MoveSelectedModToGroupCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedNexusModChanged(NexusModSearchRowViewModel? oldValue, NexusModSearchRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    partial void OnSelectedModPackChanged(ModPackRowViewModel? oldValue, ModPackRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    partial void OnIsDownloadCenterVisibleChanged(bool value)
    {
        RefreshDownloadLauncherVisibility();
    }

    partial void OnSelectedDownloadQueueItemChanged(ModDownloadQueueRowViewModel? value)
    {
        HasSelectedDownloadQueueItem = value is not null;
        foreach (var group in ModDownloadQueueGroups)
        {
            group.SyncSelectedItem(value);
        }

        foreach (var group in VisibleModDownloadQueueGroups)
        {
            group.SyncSelectedItem(value);
        }
    }

    partial void OnSelectedModRecommendationChanged(ModRecommendationRowViewModel? oldValue, ModRecommendationRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
            SelectedNexusMod = newValue.Mod;
            IsModDownloadDetailVisible = true;
        }
    }

    private void RefreshSelectedModText(ModListItemViewModel? value)
    {
        IsSelectedModConflictStatusVisible = value is null || !value.HasOverrides || value.IsConflicted;
        SelectedModDetailText = value is null
            ? T("SelectModInspectPath")
            : $"{value.DisplayName} ({value.Id}) at {value.RootPath}";
        SelectedModConflictHeadingText = value is null
            ? T("ConflictStatusHeading")
            : !value.IsEnabled
                ? T("ModCheckStatusHeading")
                : value.IsConflicted
                    ? T("PendingConflictHeading")
                    : value.HasOverrides
                        ? T("HandledOverlapHeading")
                        : T("NoConflictHeading");
        SelectedModConflictText = value is null
            ? T("ConflictStatusAfterRefresh")
            : value.IsEnabled
                ? value.HasOverrides && value.IsConflicted
                    ? $"{value.ConflictSummary} {value.ConflictOutcomeSummary}"
                    : value.HasOverrides
                        ? value.ConflictOutcomeSummary
                    : T("SelectedModNoConflicts")
                : T("DisabledExcludedFromConflicts");
        SelectedModConflictIndicatorBrush = value is null || !value.IsEnabled
            ? new SolidColorBrush(0xFF64748B)
            : value.IsConflicted
                ? new SolidColorBrush(0xFFFBBF24)
                : new SolidColorBrush(0xFF22C55E);
    }

    private void RefreshSelectedModConflictDetails(ModListItemViewModel? value)
    {
        SelectedModConflictDetails.Clear();

        if (value is null || !value.IsEnabled)
        {
            HasSelectedModConflictDetails = false;
            HasSelectedModConflictRepairOpportunity = false;
            HasNoSelectedModConflictDetails = true;
            SelectedModConflictDetailsEmptyText = value is null
                ? T("SelectModConflictDetailsPrompt")
                : T("DisabledExcludedFromConflicts");
            return;
        }

        var allSelectedConflicts = currentModConflicts
            .Where(conflict => conflict.ModIds.Contains(value.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var selectedConflicts = allSelectedConflicts
            .Where(conflict => !IsConflictReviewed(conflict, currentModConflictReviews))
            .OrderBy(conflict => conflict.NormalizedVirtualPath, StringComparer.OrdinalIgnoreCase);

        foreach (var conflict in selectedConflicts)
        {
            ModConflictReview? review = null;
            currentModConflictReviews?.TryGetValue(conflict.Fingerprint, out review);
            SelectedModConflictDetails.Add(new ModConflictDetailViewModel(conflict, review, value.Id, T));
        }

        HasSelectedModConflictDetails = SelectedModConflictDetails.Count > 0;
        HasSelectedModConflictRepairOpportunity = selectedConflicts.Any(conflict =>
            !string.Equals(conflict.WinningModId, value.Id, StringComparison.OrdinalIgnoreCase));
        HasNoSelectedModConflictDetails = !HasSelectedModConflictDetails;
        SelectedModConflictDetailsEmptyText = HasSelectedModConflictDetails
            ? string.Empty
            : allSelectedConflicts.Any(conflict => IsConflictReviewed(conflict, currentModConflictReviews))
                ? T("SelectedModConflictDetailsHandled")
                : T("NoSelectedModConflictDetails");
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        IsInitializationReady = false;
        try
        {
            await PlayerProfiles.InitializeAsync();
            isPlayerProfileManagerInitialized = true;
            ApplyAdventureProfileAccountIdentity();
            if (!PlayerProfiles.HasCurrentPlayer)
            {
                StorageText = "Create a local player profile to initialize BohemiX.";
                StatusText = "Player profile setup required";
                LastActionText = StatusText;
                IsInitializationReady = true;
                return;
            }

            await InitializeCurrentPlayerRuntimeAsync();
            isGameRuntimeInitialized = true;
            IsInitializationReady = true;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(T("InitializationFailed"), ex.Message);
            LastActionText = StatusText;
        }
    }

    private async Task InitializeCurrentPlayerRuntimeAsync()
    {
        await playerRuntimeGate.WaitAsync();
        try
        {
            await StopPlayerScopedBackgroundOperationsAsync();
            ResetPlayerScopedViewState();
            var paths = await appStartupService.InitializeAsync();
            var playerStatistics = await playerStatisticsService.GetAllAsync();
            AdventureProfile.ApplyPlayerStatistics(playerStatistics);
            var playerStateVersion = Interlocked.Increment(ref labPlayerStateVersion);
            await RefreshCreatedLabModulesAsync(playerStateVersion);
            var settings = await appSettingsService.LoadAsync();
            DataDirectory = paths.DataDirectory;
            DatabasePath = paths.DatabasePath;
            ModsDirectory = string.IsNullOrWhiteSpace(settings.ModsDirectory)
                ? paths.ModsDirectory
                : settings.ModsDirectory;
            ApplySettingsModel(settings);
            TrackerBridgePath = paths.TrackerBridgeEventsPath;
            StorageText = T("LocalRuntimeInitialized");
            NavigateCore(NavigationTargetToDestination(DefaultNavigationTarget), silent: true, updateStatus: false);

            var cachedGames = await gameInstallationStore.LoadAsync();
            var hasValidCachedInstallation = cachedGames.Any(game =>
                game.IsVerified &&
                !string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                File.Exists(game.ExecutablePath));
            if (AutoDiscoverOnStartup && !hasValidCachedInstallation)
            {
                cachedGames = await gameDiscoveryService.DiscoverInstalledGamesAsync();
            }
            ApplyDiscoveredGames(cachedGames);

            // 并行执行相互独立的 I/O 初始化任务，减少启动等待时间
            var trackerPackageTask = PrepareTrackerModPackageAsync();
            var modsTask = RefreshModsAsync();
            var saveManagerTask = SaveManager.InitializeAsync();
            await Task.WhenAll(trackerPackageTask, modsTask, saveManagerTask);

            RefreshAdventureProfileRuntimeData();

            // 在前置依赖完成后，并行执行第二批独立初始化任务
            var trackerRefreshTask = RefreshTrackerAsync();
            var adventureProfileTask = RefreshAdventureProfileDataAsync();
            var cachedNewsTask = LoadCachedGameNewsAsync();
            await Task.WhenAll(trackerRefreshTask, adventureProfileTask, cachedNewsTask);

            _ = RefreshGameNewsAsync();
            StartTrackerMonitor();

            StatusText = selectedGame is null
                ? T("RuntimeInitializedNoCache")
                : T("RuntimeInitializedCached");
            LastActionText = StatusText;
        }
        finally
        {
            playerRuntimeGate.Release();
        }
    }

    private async void OnPlayerChanged(object? sender, PlayerChangedEventArgs e)
    {
        try
        {
            if (!isPlayerProfileManagerInitialized || IsShuttingDown)
            {
                return;
            }

            if (e.PreviousPlayer?.Id != e.CurrentPlayer?.Id
                && SelectedModDownloadSource?.IsSteamWorkshop == true)
            {
                approvedModDownloadSourceKey = ModDownloadSourceOptionViewModel.NexusKey;
                CloseSteamAccountBindingDialog(restoreApprovedSource: true);
            }

            ApplyAdventureProfileAccountIdentity();

            // Identity changes are intentionally independent from the game environment.
            // Runtime services stay attached to the active game data until an environment switch is introduced.
            if (e.CurrentPlayer is not null && !isGameRuntimeInitialized)
            {
                await InitializeCurrentPlayerRuntimeAsync();
                isGameRuntimeInitialized = true;
            }
        }
        catch (Exception ex)
        {
            StatusText = string.Format(T("InitializationFailed"), ex.Message);
            LastActionText = StatusText;
        }
    }

    private void OnPlayerSettingsRequested(object? sender, EventArgs e)
    {
        NavigateCore(NavigationTargetSettings, silent: false);
        SelectSettingsCategory("PlayerProfiles");
    }

    private async Task StopPlayerScopedBackgroundOperationsAsync()
    {
        if (isTrackerRuntimeSubscribed)
        {
            trackerRuntimeService.Updated -= OnTrackerRuntimeUpdated;
            isTrackerRuntimeSubscribed = false;
        }

        await trackerRuntimeService.StopAsync();
        await gameRuntimeMonitorService.StopAsync();
        modCoverCacheCancellation?.Cancel();
        modCoverCacheCancellation?.Dispose();
        modCoverCacheCancellation = null;
        dailyModCoverCancellation?.Cancel();
        dailyModCoverCancellation?.Dispose();
        dailyModCoverCancellation = null;
    }

    private void ResetPlayerScopedViewState()
    {
        modSearchAutoSearchCancellation?.Cancel();
        modSearchAutoSearchCancellation?.Dispose();
        modSearchAutoSearchCancellation = null;
        hasPendingModSearchAutoSearch = false;
        lastSubmittedModSearchSignature = null;
        nexusModFilePickerRequestId++;
        IsNexusModFilePickerVisible = false;
        IsLoadingNexusModFileOptions = false;
        NexusModFileOptions.Clear();
        NexusModDownloadPresets.Clear();
        SelectedNexusModDownloadPreset = null;
        NexusModFilePickerMod = null;

        foreach (var row in ModRows)
        {
            row.Dispose();
        }

        ModRows.Clear();
        InstalledModGroups.Clear();
        installedModCoverCancellation?.Cancel();
        installedModCoverCancellation?.Dispose();
        installedModCoverCancellation = null;
        SelectedMod = null;
        DashboardFeaturedMod = null;
        ModConflictRows.Clear();
        DashboardConflictRows.Clear();
        SelectedModConflictDetails.Clear();
        currentModManifests = [];
        currentModConflicts = [];
        currentModConflictReviews = null;
        ModCount = 0;
        EnabledModCount = 0;
        DisabledModCount = 0;
        ConflictCount = 0;
        HasMods = false;
        HasModConflicts = false;

        var searchRows = NexusModSearchRows
            .Concat(ModRecommendationRows.Select(item => item.Mod))
            .Append(DailyModRecommendation?.Mod)
            .Where(row => row is not null)
            .Cast<NexusModSearchRowViewModel>()
            .Distinct()
            .ToArray();
        foreach (var row in searchRows)
        {
            row.Dispose();
        }

        NexusModSearchRows.Clear();
        foreach (var row in ModPackRows)
        {
            row.Dispose();
        }

        ModPackRows.Clear();
        ModRecommendationRows.Clear();
        shownModRecommendationIdentities.Clear();
        ModRecommendationQualifiers.Clear();
        SelectedNexusMod = null;
        SelectedModPack = null;
        SelectedModRecommendation = null;
        DailyModRecommendation = null;
        activeModRecommendationSignature = null;
        activeModSearchQuery = null;
        activeModSearchCategoryKey = "All";
        modSearchTotalCount = 0;
        nextNexusModSearchOffset = 0;
        nextSteamWorkshopSearchPage = 1;
        modRecommendationRefreshPage = 0;
        HasNexusModSearchResults = false;
        IsNexusModSearchEmpty = true;
        HasModPackResults = false;
        IsModPackSearchEmpty = true;
        HasMoreModSearchResults = false;
        HasModRecommendations = false;
        IsModRecommendationEmpty = true;
        IsSearchingNexusMods = false;
        IsLoadingMoreModSearchResults = false;
        IsLoadingModRecommendations = false;
        IsDailyModRecommendationLoading = false;

        foreach (var row in ModDownloadQueueRows)
        {
            row.Dispose();
        }

        ModDownloadQueueRows.Clear();
        VisibleModDownloadQueueRows.Clear();
        VisibleModDownloadQueueGroups.Clear();
        dismissedCanceledDownloadKeys.Clear();
        SelectedDownloadQueueItem = null;
        SyncDownloadQueueRows(modDownloader.Queue);

        TrackerEntityRows.Clear();
        TrackerRecentEventRows.Clear();
        TrackerAchievementRows.Clear();
        TrackerEventCount = 0;
        TrackerVisibleEntityCount = 0;
        TrackerActiveQuestCount = 0;
        TrackerCompletedEntityCount = 0;
        TrackerUnconfirmedSessionCount = 0;
        TrackerAchievementUnlockedCount = 0;
        TrackerAchievementTotalCount = 0;
        IsTrackerHealthy = false;
        IsTrackerPositionVisible = false;
        TrackerLastEventText = T("AwaitingBridgeEvents");
        TrackerPositionText = T("AwaitingBridgeEvents");
        TrackerMapStatusText = T("AwaitingBridgeEvents");

        selectedGame = null;
        GameCount = 0;
        VerifiedGameCount = 0;
        RefreshLaunchStateText();
        AdventureProfile.ResetPlayerStatistics();
    }

    [RelayCommand]
    private async Task MoveSelectedModUpAsync()
    {
        await MoveSelectedModAsync(-1);
    }

    [RelayCommand]
    private async Task MoveSelectedModDownAsync()
    {
        await MoveSelectedModAsync(1);
    }

    [RelayCommand]
    private async Task MoveSelectedModToTopAsync()
    {
        await MoveSelectedModToIndexAsync(0);
    }

    [RelayCommand]
    private async Task MoveSelectedModToBottomAsync()
    {
        await MoveSelectedModToIndexAsync(ModRows.Count - 1);
    }

    [RelayCommand]
    private async Task SaveModLoadOrderAsync()
    {
        await SaveCurrentModLoadOrderAsync();
    }

    [RelayCommand]
    private async Task MarkAllModConflictsReviewedAsync()
    {
        await SaveVisibleModConflictReviewsAsync(isReviewed: true);
    }

    [RelayCommand]
    private async Task ClearModConflictReviewsAsync()
    {
        await SaveVisibleModConflictReviewsAsync(isReviewed: false);
    }

    [RelayCommand]
    private async Task AutoFixAllModConflictsAsync()
    {
        var pendingConflicts = currentModConflicts
            .Where(conflict => !IsConflictReviewed(conflict, currentModConflictReviews))
            .ToArray();
        if (pendingConflicts.Length == 0)
        {
            StatusText = T("NoActiveModConflictsToReview");
            LastActionText = StatusText;
            return;
        }

        try
        {
            // With no per-mod compatibility metadata, the current later-mod-wins order is the only
            // deterministic precedence rule. Persist it, then record the pending overlaps as handled.
            await SaveCurrentModLoadOrderAsync();
            await modConflictReviewService.SaveReviewsAsync(
                pendingConflicts.ToDictionary(conflict => conflict.Fingerprint, _ => true, StringComparer.OrdinalIgnoreCase));
            await RefreshModsAsync();

            StatusText = string.Format(T("AutoFixedAllModConflicts"), pendingConflicts.Length);
            LastActionText = StatusText;
            ModCatalogStatusText = StatusText;
            Sections[2].Detail = $"{StatusText} {string.Format(T("CurrentConflictsDetected"), ConflictCount)}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to auto-fix all mod conflicts: {ex}");
            StatusText = string.Format(T("AutoFixAllModConflictsFailed"), ex.Message);
            LastActionText = StatusText;
            ModCatalogStatusText = StatusText;
        }
    }

    [RelayCommand]
    private async Task AutoFixSelectedModConflictsAsync()
    {
        try
        {
            var selectedMod = SelectedMod;
            if (selectedMod is null)
            {
                StatusText = T("SelectModBeforeAutoFixConflicts");
                LastActionText = StatusText;
                return;
            }

            if (!selectedMod.IsEnabled)
            {
                StatusText = T("DisabledModCannotAutoFixConflicts");
                LastActionText = StatusText;
                return;
            }

            var selectedConflicts = currentModConflicts
                .Where(conflict => conflict.ModIds.Contains(selectedMod.Id, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (selectedConflicts.Length == 0)
            {
                StatusText = T("NoSelectedModConflictsToAutoFix");
                LastActionText = StatusText;
                return;
            }

            if (selectedConflicts.All(conflict => string.Equals(conflict.WinningModId, selectedMod.Id, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = string.Format(T("SelectedModConflictsAlreadyFixed"), selectedMod.DisplayName);
                LastActionText = StatusText;
                ModCatalogStatusText = StatusText;
                return;
            }

            var orderedIds = ModRows.Select(row => row.Id).ToArray();
            var repairOrder = BuildSelectedModConflictRepairOrder(orderedIds, selectedMod.Id, selectedConflicts);
            if (repairOrder is null)
            {
                StatusText = string.Format(T("SelectedModConflictsAlreadyFixed"), selectedMod.DisplayName);
                LastActionText = StatusText;
                ModCatalogStatusText = StatusText;
                return;
            }

            ApplyModRowOrder(repairOrder);
            UpdateVisibleLoadOrder();
            await SaveCurrentModLoadOrderAsync();

            var handledConflictReviews = currentModConflicts
                .Where(conflict =>
                    conflict.ModIds.Contains(selectedMod.Id, StringComparer.OrdinalIgnoreCase) &&
                    string.Equals(conflict.WinningModId, selectedMod.Id, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(conflict => conflict.Fingerprint, _ => true, StringComparer.OrdinalIgnoreCase);
            if (handledConflictReviews.Count > 0)
            {
                await modConflictReviewService.SaveReviewsAsync(handledConflictReviews);
                await RefreshModsAsync();
            }

            StatusText = string.Format(T("AutoFixedSelectedModConflicts"), selectedMod.DisplayName, selectedConflicts.Length);
            LastActionText = StatusText;
            ModCatalogStatusText = StatusText;
            Sections[2].Detail = $"{StatusText} {string.Format(T("CurrentConflictsDetected"), ConflictCount)}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to auto-fix selected mod conflicts: {ex}");
            StatusText = string.Format(T("AutoFixSelectedModConflictsFailed"), ex.Message);
            LastActionText = StatusText;
            ModCatalogStatusText = StatusText;
        }
    }

    private void RequestLaunchWindowAction()
    {
        if (CloseAfterLaunch)
        {
            LaunchWindowActionRequested?.Invoke(LaunchWindowAction.Close);
            return;
        }

        if (MinimizeOnLaunch)
        {
            LaunchWindowActionRequested?.Invoke(LaunchWindowAction.Minimize);
        }
    }

    internal static IReadOnlyList<string>? BuildSelectedModConflictRepairOrderForTesting(
        IReadOnlyList<string> orderedModIds,
        string selectedModId,
        IReadOnlyList<ModConflict> selectedConflicts) =>
        BuildSelectedModConflictRepairOrder(orderedModIds, selectedModId, selectedConflicts);

    private static IReadOnlyList<string>? BuildSelectedModConflictRepairOrder(
        IReadOnlyList<string> orderedModIds,
        string selectedModId,
        IReadOnlyList<ModConflict> selectedConflicts)
    {
        var selectedIndex = FindModIndex(orderedModIds, selectedModId);
        if (selectedIndex < 0)
        {
            return null;
        }

        var participantIds = selectedConflicts
            .SelectMany(conflict => conflict.ModIds)
            .Where(modId => !string.Equals(modId, selectedModId, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (participantIds.Count == 0)
        {
            return null;
        }

        var latestParticipantIndex = orderedModIds
            .Select((modId, index) => new { modId, index })
            .Where(entry => participantIds.Contains(entry.modId))
            .Select(entry => entry.index)
            .DefaultIfEmpty(-1)
            .Max();
        if (latestParticipantIndex < 0 || selectedIndex > latestParticipantIndex)
        {
            return null;
        }

        var repairedOrder = orderedModIds.ToList();
        repairedOrder.RemoveAt(selectedIndex);
        var insertIndex = repairedOrder
            .Select((modId, index) => new { modId, index })
            .Where(entry => participantIds.Contains(entry.modId))
            .Select(entry => entry.index + 1)
            .DefaultIfEmpty(repairedOrder.Count)
            .Max();
        repairedOrder.Insert(Math.Clamp(insertIndex, 0, repairedOrder.Count), selectedModId);
        return repairedOrder;
    }

    private static int FindModIndex(IReadOnlyList<string> orderedModIds, string modId)
    {
        for (var index = 0; index < orderedModIds.Count; index++)
        {
            if (string.Equals(orderedModIds[index], modId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    [RelayCommand]
    private async Task EnableAllModsAsync()
    {
        await SetAllModsEnabledAsync(isEnabled: true);
    }

    [RelayCommand]
    private async Task DisableAllModsAsync()
    {
        await SetAllModsEnabledAsync(isEnabled: false);
    }

    [RelayCommand]
    private async Task EnableModGroupAsync(InstalledModGroupViewModel? group)
    {
        await SetModGroupEnabledAsync(group, isEnabled: true);
    }

    [RelayCommand]
    private async Task DisableModGroupAsync(InstalledModGroupViewModel? group)
    {
        await SetModGroupEnabledAsync(group, isEnabled: false);
    }

    [RelayCommand]
    private void OpenSelectedModFolder()
    {
        if (SelectedMod is null)
        {
            StatusText = T("SelectModBeforeOpeningFolder");
            LastActionText = StatusText;
            return;
        }

        StatusText = TryOpenShellTarget(SelectedMod.RootPath)
            ? string.Format(T("OpenedSelectedModFolder"), SelectedMod.DisplayName)
            : $"Mod folder: {SelectedMod.RootPath}";
        LastActionText = StatusText;
    }

    [RelayCommand]
    private async Task ViewModDetailsAsync(ModListItemViewModel? mod)
    {
        if (mod is null)
        {
            StatusText = T("SelectModBeforeViewingDetails");
            LastActionText = StatusText;
            return;
        }

        SelectedMod = mod;
        NexusSearchQuery = GetModDetailSearchQuery(mod);
        SelectedModDownloadCategory = GetModDownloadCategoryDisplay("All");

        NavigateCore("Mods", silent: false, updateStatus: false);
        SelectModManagerPage("Download");

        var nexusSource = ModDownloadSources.FirstOrDefault(source => source.IsNexus);
        if (nexusSource is not null && !ReferenceEquals(SelectedModDownloadSource, nexusSource))
        {
            SelectedModDownloadSource = nexusSource;
        }

        await SubmitModDownloadSearchCoreAsync(force: true);
        StatusText = string.Format(T("ViewingModDetails"), mod.DisplayName);
        LastActionText = StatusText;
    }

    internal static string GetModDetailSearchQuery(ModListItemViewModel mod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        return mod.NexusModId?.ToString(CultureInfo.InvariantCulture) ?? mod.DisplayName;
    }

    [RelayCommand]
    private async Task DeleteModAsync(ModListItemViewModel? mod)
    {
        if (mod is null)
        {
            StatusText = T("SelectModBeforeDeleting");
            LastActionText = StatusText;
            return;
        }

        SelectedMod = mod;
        await DeleteSelectedModAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedModAsync()
    {
        if (SelectedMod is null)
        {
            StatusText = T("SelectModBeforeDeleting");
            LastActionText = StatusText;
            return;
        }

        var paths = applicationPathService.GetPaths();
        var modsRoot = Path.GetFullPath(paths.ModsDirectory);
        var selectedRoot = Path.GetFullPath(SelectedMod.RootPath);

        if (!SelectedMod.CanDelete ||
            ModListItemViewModel.IsBuiltInTracker(SelectedMod.Id, SelectedMod.DisplayName, selectedRoot))
        {
            StatusText = string.Format(T("BuiltInModCannotDelete"), SelectedMod.DisplayName);
            LastActionText = StatusText;
            return;
        }

        if (!IsPathInsideDirectory(modsRoot, selectedRoot))
        {
            StatusText = $"Refused to delete a path outside the local mods folder: {SelectedMod.RootPath}";
            LastActionText = StatusText;
            return;
        }

        var deletedModName = SelectedMod.DisplayName;

        try
        {
            Directory.Delete(selectedRoot, recursive: true);
            await RefreshModsAsync();
            StatusText = string.Format(T("DeletedModFromLocalFolder"), deletedModName);
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = string.Format(T("UnableToDeleteMod"), deletedModName, ex.Message);
            LastActionText = StatusText;
        }
    }

    [RelayCommand]
    private async Task SearchNexusModsAsync()
    {
        if (IsModPackSourceSelected)
        {
            await SearchModPacksAsync();
            return;
        }

        if (IsSteamWorkshopSourceSelected())
        {
            await SearchSteamWorkshopModsAsync();
            return;
        }

        if (IsSearchingNexusMods || IsLoadingMoreModSearchResults)
        {
            return;
        }

        try
        {
            IsSearchingNexusMods = true;
            var operation = BeginModDownloadSearchOperation();
            if (!await EnsureNexusAccountBoundAsync())
            {
                return;
            }

            operation.CancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentModDownloadSearch(operation))
            {
                return;
            }

            nexusApiKeyStore.SetApiKey(NexusApiKey);
            activeModSearchSourceKey = ModDownloadSourceOptionViewModel.NexusKey;
            activeModSearchCategoryKey = GetModDownloadCategoryKey(SelectedModDownloadCategory);
            activeModSearchQuery = BuildModDownloadSearchQuery();
            lastSubmittedModSearchSignature = BuildModDownloadSearchSignature();
            ResetModSearchPagination();
            NexusStatusText = string.Format(T("SearchingNexusMods"), BuildModDownloadStatusQueryText(activeModSearchQuery, activeModSearchCategoryKey));
            StatusText = NexusStatusText;

            await LoadNexusModSearchPageAsync(append: false, operation);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (OperationCanceledException)
        {
        }
        catch (NexusModsException ex)
        {
            NexusStatusText = FormatNexusError(ex);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            NexusStatusText = string.Format(T("NexusSearchFailed"), ex.Message);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            IsSearchingNexusMods = false;
        }
    }

    private async Task SearchSteamWorkshopModsAsync()
    {
        if (IsSearchingNexusMods || IsLoadingMoreModSearchResults)
        {
            return;
        }

        try
        {
            IsSearchingNexusMods = true;
            var operation = BeginModDownloadSearchOperation();
            activeModSearchSourceKey = ModDownloadSourceOptionViewModel.SteamWorkshopKey;
            activeModSearchCategoryKey = GetModDownloadCategoryKey(SelectedModDownloadCategory);
            activeModSearchQuery = BuildModDownloadSearchQuery();
            activeWorkshopSortOrder = BuildWorkshopSortOrder();
            lastSubmittedModSearchSignature = BuildModDownloadSearchSignature();
            ResetModSearchPagination();
            NexusStatusText = string.Format(
                T("SearchingSteamWorkshopMods"),
                BuildModDownloadStatusQueryText(activeModSearchQuery, activeModSearchCategoryKey));
            StatusText = NexusStatusText;

            await LoadSteamWorkshopSearchPageAsync(append: false, operation);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (OperationCanceledException)
        {
        }
        catch (WorkshopException ex)
        {
            NexusStatusText = FormatWorkshopError(ex);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            NexusStatusText = string.Format(T("SteamWorkshopSearchFailed"), ex.Message);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            IsSearchingNexusMods = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreModSearchResultsAsync()
    {
        if (!HasMoreModSearchResults || IsSearchingNexusMods || IsLoadingMoreModSearchResults)
        {
            return;
        }

        if (IsModPackSourceSelected)
        {
            try
            {
                IsLoadingMoreModSearchResults = true;
                LoadMoreModSearchResultsText = T("LoadingMoreModPacks");
                NexusStatusText = T("LoadingMoreModPacks");
                StatusText = NexusStatusText;
                await Task.Yield();

                LoadNextModPackBatch(append: true);
                NexusStatusText = string.Format(
                    T("ModPackBatchStatus"),
                    ModPackRows.Count,
                    currentFilteredModPackCatalog.Count);
                StatusText = NexusStatusText;
                LastActionText = StatusText;
            }
            finally
            {
                IsLoadingMoreModSearchResults = false;
                RefreshLoadMoreModSearchResultsText();
            }

            return;
        }

        try
        {
            IsLoadingMoreModSearchResults = true;
            var operation = BeginModDownloadSearchOperation();
            LoadMoreModSearchResultsText = T("LoadingMoreModSearchResults");
            NexusStatusText = T("LoadingMoreModSearchResults");
            StatusText = NexusStatusText;

            if (string.Equals(activeModSearchSourceKey, ModDownloadSourceOptionViewModel.SteamWorkshopKey, StringComparison.Ordinal))
            {
                await LoadSteamWorkshopSearchPageAsync(append: true, operation);
            }
            else
            {
                nexusApiKeyStore.SetApiKey(NexusApiKey);
                await LoadNexusModSearchPageAsync(append: true, operation);
            }

            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (OperationCanceledException)
        {
        }
        catch (NexusModsException ex)
        {
            NexusStatusText = FormatNexusError(ex);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (WorkshopException ex)
        {
            NexusStatusText = FormatWorkshopError(ex);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            NexusStatusText = string.Equals(activeModSearchSourceKey, ModDownloadSourceOptionViewModel.SteamWorkshopKey, StringComparison.Ordinal)
                ? string.Format(T("SteamWorkshopSearchFailed"), ex.Message)
                : string.Format(T("NexusSearchFailed"), ex.Message);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            IsLoadingMoreModSearchResults = false;
            RefreshLoadMoreModSearchResultsText();
        }
    }

    private async Task LoadNexusModSearchPageAsync(bool append, ModDownloadSearchOperation operation)
    {
        var offset = append ? nextNexusModSearchOffset : 0;
        var result = await nexusModService.SearchModsAsync(new NexusModSearchRequest(
            "kingdomcomedeliverance2",
            string.IsNullOrWhiteSpace(activeModSearchQuery) ? null : activeModSearchQuery,
            offset,
            ModSearchPageSize),
            operation.CancellationToken);

        operation.CancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentModDownloadSearch(operation))
        {
            return;
        }

        var addedRows = AppendModSearchRows(
            SortModSearchRowsByCategory(
                result.Mods.Select(mod => new NexusModSearchRowViewModel(mod)),
                activeModSearchCategoryKey),
            append);
        modSearchTotalCount = result.TotalCount;
        nextNexusModSearchOffset = offset + addedRows.Count;
        HasMoreModSearchResults = NexusModSearchRows.Count < modSearchTotalCount && addedRows.Count > 0;
        RefreshLoadMoreModSearchResultsText();
        RefreshNexusResultsState();
        CacheNewModCovers(addedRows);
        SelectFirstModSearchResultIfNeeded(append);
        NexusStatusText = string.Format(T("NexusSearchComplete"), NexusModSearchRows.Count, result.TotalCount);
    }

    private async Task LoadSteamWorkshopSearchPageAsync(bool append, ModDownloadSearchOperation operation)
    {
        var page = append ? nextSteamWorkshopSearchPage : 1;
        var result = await workshopService.SearchModsAsync(new WorkshopSearchRequest(
            string.IsNullOrWhiteSpace(activeModSearchQuery) ? null : activeModSearchQuery,
            Page: page,
            PageSize: ModSearchPageSize,
            SortOrder: activeWorkshopSortOrder),
            operation.CancellationToken);

        operation.CancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentModDownloadSearch(operation))
        {
            return;
        }

        var addedRows = AppendModSearchRows(
            SortModSearchRowsByCategory(
                result.Mods.Select(mod => new NexusModSearchRowViewModel(mod)),
                activeModSearchCategoryKey),
            append);
        modSearchTotalCount = result.TotalCount;
        nextSteamWorkshopSearchPage = page + 1;
        HasMoreModSearchResults = NexusModSearchRows.Count < modSearchTotalCount && addedRows.Count > 0;
        RefreshLoadMoreModSearchResultsText();
        RefreshNexusResultsState();
        CacheNewModCovers(addedRows);
        SelectFirstModSearchResultIfNeeded(append);
        NexusStatusText = string.Format(T("SteamWorkshopSearchComplete"), NexusModSearchRows.Count, result.TotalCount);
    }

    private List<NexusModSearchRowViewModel> AppendModSearchRows(
        IEnumerable<NexusModSearchRowViewModel> rows,
        bool append)
    {
        if (!append)
        {
            ClearModDownloadSearchResults();
        }

        var addedRows = new List<NexusModSearchRowViewModel>();
        foreach (var row in rows)
        {
            ApplyModSearchRowLocalization(row);
            row.ApplyDownloadState(IsModSearchRowDownloaded(row), T);
            row.ApplySearchKeyword(activeModSearchQuery);
            NexusModSearchRows.Add(row);
            addedRows.Add(row);
        }

        return addedRows;
    }

    private void CacheNewModCovers(IReadOnlyCollection<NexusModSearchRowViewModel> rows)
    {
        if (rows.Count > 0)
        {
            modCoverCacheCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(downloadCoverCancellation.Token);
            _ = CacheModCoversAsync(rows.ToArray(), modCoverCacheCancellation.Token, modCoverCacheGeneration);
        }
    }

    private void SelectFirstModSearchResultIfNeeded(bool append)
    {
        if (!append)
        {
            SelectedNexusMod = NexusModSearchRows.FirstOrDefault();
            IsModDownloadDetailVisible = SelectedNexusMod is not null;
        }
    }

    private void ResetModSearchPagination()
    {
        CancelModCoverCaching();
        modCoverCacheCancellation = new CancellationTokenSource();
        modSearchTotalCount = 0;
        nextNexusModSearchOffset = 0;
        nextSteamWorkshopSearchPage = 1;
        nextModPackBatchOffset = 0;
        HasMoreModSearchResults = false;
        RefreshLoadMoreModSearchResultsText();
    }

    private void RefreshLoadMoreModSearchResultsText()
    {
        if (IsModPackSourceSelected)
        {
            LoadMoreModSearchResultsText = HasMoreModSearchResults
                ? T("LoadMoreModPacks")
                : T("AllModSearchResultsLoaded");
            return;
        }

        LoadMoreModSearchResultsText = NexusModSearchRows.Count > 0 && !HasMoreModSearchResults
            ? T("AllModSearchResultsLoaded")
            : T("LoadMoreModSearchResults");
    }

    private string? BuildModDownloadSearchQuery()
    {
        var searchQuery = NexusSearchQuery.Trim();
        return string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery;
    }

    private string BuildModDownloadStatusQueryText(string? query, string categoryKey)
    {
        var categoryText = GetModDownloadCategoryDisplay(categoryKey);
        if (string.IsNullOrWhiteSpace(query))
        {
            return categoryText;
        }

        return string.Equals(categoryKey, "All", StringComparison.Ordinal)
            ? query
            : $"{query} / {categoryText}";
    }

    private bool IsSteamWorkshopSourceSelected()
    {
        return SelectedModDownloadSource?.IsSteamWorkshop == true;
    }

    private WorkshopSortOrder BuildWorkshopSortOrder()
    {
        return string.Equals(GetModDownloadCategoryKey(SelectedModDownloadCategory), "Updated", StringComparison.Ordinal)
            ? WorkshopSortOrder.Updated
            : WorkshopSortOrder.Hot;
    }

    private string BuildModDownloadSearchSignature()
    {
        return string.Join(
            "|",
            GetModDownloadSourceKey(SelectedModDownloadSource),
            GetModDownloadCategoryKey(SelectedModDownloadCategory),
            BuildModDownloadSearchQuery() ?? string.Empty,
            BuildWorkshopSortOrder().ToString());
    }

    partial void OnSelectedModDownloadSourceChanged(ModDownloadSourceOptionViewModel? value)
    {
        OnPropertyChanged(nameof(IsModPackSourceSelected));
        OnPropertyChanged(nameof(IsStandardModSourceSelected));
        OnPropertyChanged(nameof(IsStandardModSearchEmpty));
        OnPropertyChanged(nameof(IsVisibleModPackSearchEmpty));
        OnPropertyChanged(nameof(HasVisibleModDownloadSearchResults));
        RefreshLoadMoreModSearchResultsText();
        if (suppressSelectedModDownloadSourceChanged)
        {
            return;
        }

        SetModDownloadSourceTransitionLoading(value?.IsModPack == true);
        _ = HandleSelectedModDownloadSourceChangedAsync(value);
    }

    partial void OnSelectedModDownloadCategoryChanged(string value)
    {
        RefreshSelectedModDownloadCategoryFilter();
        InvalidateModDownloadSearch(clearResults: false);
        ResetModSearchPagination();
        ScheduleModSearchAutoSearch();
    }

    partial void OnNexusSearchQueryChanged(string value)
    {
        HasModSearchQuery = !string.IsNullOrWhiteSpace(value);
        InvalidateModDownloadSearch(clearResults: false);
        ResetModSearchPagination();
        foreach (var row in NexusModSearchRows)
        {
            row.ApplySearchKeyword(value);
        }

        foreach (var row in ModPackRows)
        {
            row.ApplySearchKeyword(value);
        }

        ScheduleModSearchAutoSearch();
    }

    partial void OnRecommendationQualifierInputChanged(string value)
    {
        AddModRecommendationQualifierCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSearchingNexusModsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsModDownloadLoading));
        RefreshCanSearchModDownloads();
    }

    private void SetModDownloadSourceTransitionLoading(bool value)
    {
        if (isModDownloadSourceTransitionLoading == value)
        {
            return;
        }

        isModDownloadSourceTransitionLoading = value;
        OnPropertyChanged(nameof(IsModDownloadLoading));
    }

    partial void OnIsLoadingMoreModSearchResultsChanged(bool value)
    {
        RefreshCanSearchModDownloads();
    }

    partial void OnIsNexusModSearchEmptyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsStandardModSearchEmpty));
    }

    partial void OnIsModPackSearchEmptyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVisibleModPackSearchEmpty));
    }

    partial void OnHasNexusModSearchResultsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasVisibleModDownloadSearchResults));
    }

    partial void OnHasModPackResultsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasVisibleModDownloadSearchResults));
    }

    private void RefreshCanSearchModDownloads()
    {
        CanSearchModDownloads =
            !IsSearchingNexusMods
            && !IsLoadingMoreModSearchResults
            && !IsSteamAccountBindingDialogOpen
            && !IsSteamAccountBindingBusy
            && !IsNexusAccountBindingDialogOpen
            && !IsNexusAccountBindingDialogBusy;

        if (CanSearchModDownloads && hasPendingModSearchAutoSearch)
        {
            ScheduleModSearchAutoSearch();
        }
    }

    private async Task SearchModPacksAsync()
    {
        if (IsSearchingNexusMods || IsLoadingMoreModSearchResults)
        {
            return;
        }

        var loadingStateStopwatch = Stopwatch.StartNew();
        var searchCancellationToken = CancellationToken.None;
        try
        {
            IsSearchingNexusMods = true;
            var operation = BeginModDownloadSearchOperation();
            searchCancellationToken = operation.CancellationToken;
            activeModSearchSourceKey = ModDownloadSourceOptionViewModel.ModPackKey;
            activeModSearchCategoryKey = GetModDownloadCategoryKey(SelectedModDownloadCategory);
            activeModSearchQuery = BuildModDownloadSearchQuery();
            lastSubmittedModSearchSignature = BuildModDownloadSearchSignature();
            HasMoreModSearchResults = false;
            RefreshLoadMoreModSearchResultsText();
            NexusStatusText = T("LoadingModPackCatalog");
            StatusText = NexusStatusText;

            var result = await modPackCatalogService
                .GetCatalogAsync(refreshRemote: !hasLoadedModPackCatalog, operation.CancellationToken)
                .ConfigureAwait(true);
            operation.CancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentModDownloadSearch(operation))
            {
                return;
            }

            currentModPackCatalog = result.Entries;
            hasLoadedModPackCatalog = true;
            currentFilteredModPackCatalog = FilterModPackCatalog(
                currentModPackCatalog,
                activeModSearchQuery,
                activeModSearchCategoryKey);
            nextModPackBatchOffset = 0;
            LoadNextModPackBatch(append: false);
            NexusStatusText = result.Warning is null
                ? string.Format(T("ModPackBatchStatus"), ModPackRows.Count, currentFilteredModPackCatalog.Count)
                : string.Format(T("ModPackCatalogFallback"), ModPackRows.Count, currentFilteredModPackCatalog.Count);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or HttpRequestException
                                   or InvalidOperationException)
        {
            ClearModPackRows();
            NexusStatusText = string.Format(T("ModPackCatalogFailed"), ex.Message);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            var minimumVisibleDuration = TimeSpan.FromMilliseconds(320);
            var remainingVisibleDuration = minimumVisibleDuration - loadingStateStopwatch.Elapsed;
            if (remainingVisibleDuration > TimeSpan.Zero && !searchCancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(remainingVisibleDuration, searchCancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                }
            }

            IsSearchingNexusMods = false;
            SetModDownloadSourceTransitionLoading(false);
        }
    }

    internal static IReadOnlyList<ModPackCatalogEntry> FilterModPackCatalog(
        IEnumerable<ModPackCatalogEntry> entries,
        string? query,
        string categoryKey)
    {
        var filtered = entries.Where(entry => entry.IsActive
            && entry.IsPublic
            && entry.RightsBasis == ModPackRightsBasis.OfficialPlatform);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(entry => tokens.All(token =>
                entry.Title.Contains(token, StringComparison.OrdinalIgnoreCase)
                || entry.Curator.Contains(token, StringComparison.OrdinalIgnoreCase)
                || entry.Summary.Contains(token, StringComparison.OrdinalIgnoreCase)
                || entry.Tags.Any(tag => tag.Contains(token, StringComparison.OrdinalIgnoreCase))));
        }

        if (!string.Equals(categoryKey, "All", StringComparison.Ordinal)
            && !string.Equals(categoryKey, "Updated", StringComparison.Ordinal)
            && Enum.TryParse<ModPackCategory>(categoryKey, out var category))
        {
            filtered = filtered.Where(entry => entry.Category == category);
        }

        return filtered
            .OrderByDescending(entry => entry.UpdatedAt)
            .ThenBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static (IReadOnlyList<ModPackCatalogEntry> Entries, int NextOffset) GetModPackBatch(
        IReadOnlyList<ModPackCatalogEntry> entries,
        int offset,
        int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        if (entries.Count == 0)
        {
            return ([], 0);
        }

        if (offset >= entries.Count)
        {
            return ([], entries.Count);
        }

        var batch = entries.Skip(offset).Take(batchSize).ToArray();
        return (batch, offset + batch.Length);
    }

    private void LoadNextModPackBatch(bool append)
    {
        var batch = GetModPackBatch(currentFilteredModPackCatalog, nextModPackBatchOffset, ModSearchPageSize);
        nextModPackBatchOffset = batch.NextOffset;
        if (append)
        {
            AppendModPackRows(batch.Entries);
        }
        else
        {
            ReplaceModPackRows(batch.Entries);
        }

        HasMoreModSearchResults = nextModPackBatchOffset < currentFilteredModPackCatalog.Count;
        RefreshLoadMoreModSearchResultsText();
    }

    private void ReplaceModPackRows(IReadOnlyList<ModPackCatalogEntry> entries)
    {
        ClearModDownloadSearchResults();
        modPackCoverCacheCancellation = CancellationTokenSource.CreateLinkedTokenSource(downloadCoverCancellation.Token);
        AppendModPackRows(entries);
    }

    private void AppendModPackRows(IReadOnlyList<ModPackCatalogEntry> entries)
    {
        modPackCoverCacheCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(downloadCoverCancellation.Token);
        var coverCancellationToken = modPackCoverCacheCancellation.Token;
        foreach (var entry in entries)
        {
            var row = new ModPackRowViewModel(entry);
            row.ApplyLocalization(T);
            row.ApplySearchKeyword(activeModSearchQuery);
            ModPackRows.Add(row);
            _ = CacheModPackCoverAsync(row, coverCancellationToken);
        }

        HasModPackResults = ModPackRows.Count > 0;
        IsModPackSearchEmpty = !HasModPackResults;
        if (SelectedModPack is null)
        {
            SelectedModPack = ModPackRows.FirstOrDefault();
            IsModPackDetailVisible = SelectedModPack is not null;
        }
    }

    private async Task CacheModPackCoverAsync(ModPackRowViewModel row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.RemoteThumbnailUrl))
        {
            return;
        }

        var cachedPath = modCoverCacheService.GetCachedCoverPath(row.ThumbnailCacheId);
        var throttleAcquired = false;
        try
        {
            if (string.IsNullOrWhiteSpace(cachedPath))
            {
                await modPackCoverCacheThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
                throttleAcquired = true;
                cachedPath = await modCoverCacheService.CacheCoverAsync(
                    row.ThumbnailCacheId,
                    row.RemoteThumbnailUrl,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
        }
        finally
        {
            if (throttleAcquired)
            {
                modPackCoverCacheThrottle.Release();
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ModPackRows.Contains(row))
            {
                row.SetThumbnailPath(cachedPath, activateVisualResources: false);
            }
        });
    }

    [RelayCommand]
    private async Task SubmitModDownloadSearchAsync()
    {
        await SubmitModDownloadSearchCoreAsync(force: true);
    }

    [RelayCommand]
    private async Task ClearModDownloadSearchAsync()
    {
        if (!HasModSearchQuery)
        {
            return;
        }

        NexusSearchQuery = string.Empty;
        await SubmitModDownloadSearchCoreAsync(force: true);
    }

    private void ScheduleModSearchAutoSearch()
    {
        if (!IsModDownloadPageVisible)
        {
            return;
        }

        hasPendingModSearchAutoSearch = true;
        modSearchAutoSearchCancellation?.Cancel();
        modSearchAutoSearchCancellation?.Dispose();
        modSearchAutoSearchCancellation = new CancellationTokenSource();
        var token = modSearchAutoSearchCancellation.Token;

        _ = RunModSearchAutoSearchAsync(token);
    }

    private async Task RunModSearchAutoSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ModSearchAutoSearchDelayMilliseconds, token);
            await Dispatcher.UIThread.InvokeAsync(
                async () =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        await SubmitModDownloadSearchCoreAsync(force: false);
                    }
                },
                DispatcherPriority.Background,
                token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SubmitModDownloadSearchCoreAsync(bool force)
    {
        if (!IsModDownloadPageVisible)
        {
            return;
        }

        var signature = BuildModDownloadSearchSignature();
        if (!force && string.Equals(signature, lastSubmittedModSearchSignature, StringComparison.Ordinal))
        {
            hasPendingModSearchAutoSearch = false;
            return;
        }

        if (!CanSearchModDownloads)
        {
            hasPendingModSearchAutoSearch = !force;
            return;
        }

        hasPendingModSearchAutoSearch = false;
        lastSubmittedModSearchSignature = signature;
        await SearchNexusModsAsync();
    }

    private void RefreshModDownloadSourceUiText()
    {
        NexusResultEmptyText = IsModPackSourceSelected
            ? T("ModPackResultEmpty")
            : IsSteamWorkshopSourceSelected()
                ? T("WorkshopResultEmpty")
                : T("NexusResultEmpty");

        NexusDownloadQueueEmptyText = T("ModDownloadQueueEmpty");
    }

    private ModDownloadSearchOperation BeginModDownloadSearchOperation()
    {
        return modDownloadSearchOperationGate.Begin();
    }

    private bool IsCurrentModDownloadSearch(ModDownloadSearchOperation operation)
    {
        return modDownloadSearchOperationGate.IsCurrent(operation);
    }

    private void InvalidateModDownloadSearch(bool clearResults)
    {
        modDownloadSearchOperationGate.Invalidate();
        if (!clearResults)
        {
            return;
        }

        ClearModDownloadSearchResults();
        NexusStatusText = T("NexusReady");
        StatusText = NexusStatusText;
    }

    private void ClearModDownloadSearchResults()
    {
        foreach (var row in NexusModSearchRows
                     .Concat(ModRecommendationRows.Select(item => item.Mod))
                     .Distinct())
        {
            row.Dispose();
        }

        NexusModSearchRows.Clear();
        SelectedNexusMod = null;
        IsModDownloadDetailVisible = false;
        ClearModPackRows();
        RefreshNexusResultsState();
    }

    private void ClearModPackRows()
    {
        modPackCoverCacheCancellation?.Cancel();
        modPackCoverCacheCancellation?.Dispose();
        modPackCoverCacheCancellation = null;
        foreach (var row in ModPackRows)
        {
            row.Dispose();
        }

        ModPackRows.Clear();
        SelectedModPack = null;
        IsModPackDetailVisible = false;
        HasModPackResults = false;
        IsModPackSearchEmpty = true;
    }

    private void RefreshModDownloadPickerOptions()
    {
        var selectedSourceKey = GetModDownloadSourceKey(SelectedModDownloadSource);
        var selectedCategoryKey = GetModDownloadCategoryKey(SelectedModDownloadCategory);

        foreach (var source in ModDownloadSources)
        {
            source.DisplayName = GetModDownloadSourceDisplay(source.Key);
        }

        ReplaceItems(
            ModDownloadCategories,
            GetModDownloadCategoryKeys().Select(GetModDownloadCategoryDisplay).ToArray());

        suppressSelectedModDownloadSourceChanged = true;
        try
        {
            SelectedModDownloadSource = ModDownloadSources.FirstOrDefault(source =>
                string.Equals(source.Key, selectedSourceKey, StringComparison.Ordinal))
                ?? ModDownloadSources.First();
        }
        finally
        {
            suppressSelectedModDownloadSourceChanged = false;
        }

        SelectedModDownloadCategory = GetModDownloadCategoryDisplay(selectedCategoryKey);
        RefreshModDownloadCategoryFilters();
    }

    private void RefreshModDownloadCategoryFilters()
    {
        var selectedCategoryKey = GetModDownloadCategoryKey(SelectedModDownloadCategory);
        var categoryKeys = GetModDownloadCategoryKeys();

        ModDownloadCategoryFilters.Clear();
        foreach (var key in categoryKeys)
        {
            ModDownloadCategoryFilters.Add(new ModDownloadCategoryFilterViewModel(key, GetModDownloadCategoryDisplay(key))
            {
                IsSelected = string.Equals(key, selectedCategoryKey, StringComparison.Ordinal)
            });
        }
    }

    private void RefreshSelectedModDownloadCategoryFilter()
    {
        var selectedCategoryKey = GetModDownloadCategoryKey(SelectedModDownloadCategory);
        foreach (var filter in ModDownloadCategoryFilters)
        {
            filter.DisplayName = GetModDownloadCategoryDisplay(filter.Key);
            filter.IsSelected = string.Equals(filter.Key, selectedCategoryKey, StringComparison.Ordinal);
        }
    }

    private string GetModDownloadSourceKey(ModDownloadSourceOptionViewModel? source) =>
        source?.Key ?? ModDownloadSourceOptionViewModel.NexusKey;

    private string GetModDownloadSourceDisplay(string sourceKey)
    {
        return sourceKey switch
        {
            ModDownloadSourceOptionViewModel.SteamWorkshopKey => T("SteamWorkshopSource"),
            ModDownloadSourceOptionViewModel.ModPackKey => T("ModPackSource"),
            _ => T("NexusModsSource")
        };
    }

    private IReadOnlyList<string> GetModDownloadCategoryKeys() => IsModPackSourceSelected
        ? ["All", "VanillaPlus", "Overhaul", "Visuals", "ImmersionHardcore", "EssentialsTools", "Updated"]
        : ["All", "Gameplay", "Visuals", "Interface", "Items", "Utilities", "Updated"];

    private string GetModDownloadCategoryKey(string? category)
    {
        return category switch
        {
            var value when MatchesModDownloadCategory(value, "Gameplay") => "Gameplay",
            var value when MatchesModDownloadCategory(value, "Visuals") => "Visuals",
            var value when MatchesModDownloadCategory(value, "Interface") => "Interface",
            var value when MatchesModDownloadCategory(value, "Items") => "Items",
            var value when MatchesModDownloadCategory(value, "Utilities") => "Utilities",
            var value when MatchesModDownloadCategory(value, "VanillaPlus") => "VanillaPlus",
            var value when MatchesModDownloadCategory(value, "Overhaul") => "Overhaul",
            var value when MatchesModDownloadCategory(value, "ImmersionHardcore") => "ImmersionHardcore",
            var value when MatchesModDownloadCategory(value, "EssentialsTools") => "EssentialsTools",
            var value when MatchesModDownloadCategory(value, "Updated") => "Updated",
            _ => "All"
        };
    }

    private string GetModDownloadCategoryDisplay(string categoryKey)
    {
        return GetModDownloadCategoryDisplay(categoryKey, T);
    }

    private static string GetModDownloadCategoryDisplay(string categoryKey, Func<string, string> translate)
    {
        return translate(categoryKey is "VanillaPlus" or "Overhaul" or "ImmersionHardcore" or "EssentialsTools"
            ? $"ModPackCategory{categoryKey}"
            : $"ModCategory{categoryKey}");
    }

    private bool MatchesModDownloadCategory(string? category, string categoryKey)
    {
        return string.Equals(category, categoryKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, T($"ModCategory{categoryKey}"), StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyModSearchRowLocalization(NexusModSearchRowViewModel row)
    {
        row.ApplyLocalization(T);
        row.UseSummaryLanguage(GetModSummaryTargetLanguage(), T);
        row.ApplyDownloadState(IsModSearchRowDownloaded(row), T);
    }

    [RelayCommand]
    private async Task SelectModDownloadCategoryAsync(string categoryKey)
    {
        categoryKey = string.IsNullOrWhiteSpace(categoryKey) ? "All" : categoryKey;
        var categoryDisplay = GetModDownloadCategoryDisplay(categoryKey);

        if (string.Equals(SelectedModDownloadCategory, categoryDisplay, StringComparison.Ordinal))
        {
            return;
        }

        SelectedModDownloadCategory = categoryDisplay;

        if (IsModDownloadPageVisible)
        {
            await SearchNexusModsAsync();
        }
    }

    private IEnumerable<NexusModSearchRowViewModel> SortModSearchRowsByCategory(
        IEnumerable<NexusModSearchRowViewModel> rows,
        string categoryKey)
    {
        if (string.Equals(categoryKey, "All", StringComparison.Ordinal))
        {
            return rows;
        }

        if (string.Equals(categoryKey, "Updated", StringComparison.Ordinal))
        {
            return rows.OrderByDescending(row => ParseUpdatedDate(row.UpdatedText));
        }

        return rows
            .OrderByDescending(row => GetModCategoryMatchScore(row, categoryKey))
            .ThenByDescending(row => ParseInvariantInteger(row.DownloadsText));
    }

    private static int GetModCategoryMatchScore(NexusModSearchRowViewModel row, string categoryKey)
    {
        var haystack = $"{row.Name} {row.Summary} {row.MetaText}".ToLowerInvariant();
        var keywords = categoryKey switch
        {
            "Gameplay" => new[] { "gameplay", "balance", "combat", "perk", "skill", "difficulty", "ai" },
            "Visuals" => new[] { "visual", "graphics", "texture", "lighting", "reshade", "model", "appearance", "overhaul" },
            "Interface" => new[] { "ui", "hud", "interface", "menu", "map", "inventory" },
            "Items" => new[] { "item", "weapon", "armor", "equipment", "sword", "bow", "clothing" },
            "Utilities" => new[] { "utility", "tool", "framework", "fix", "patch", "manager", "script" },
            _ => []
        };

        return keywords.Sum(keyword => haystack.Contains(keyword, StringComparison.Ordinal) ? 1 : 0);
    }

    private static int ParseInvariantInteger(string value)
    {
        return int.TryParse(value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static DateTime ParseUpdatedDate(string value)
    {
        return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : DateTime.MinValue;
    }

    private async Task CacheModCoversAsync(
        IReadOnlyCollection<NexusModSearchRowViewModel> rows,
        CancellationToken cancellationToken,
        long generation)
    {
        var tasks = rows.Select(row => CacheModCoverAsync(row, cancellationToken, generation));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CacheModCoverAsync(
        NexusModSearchRowViewModel row,
        CancellationToken cancellationToken,
        long generation)
    {
        if (string.IsNullOrWhiteSpace(row.RemoteThumbnailUrl))
        {
            await SetModCoverAsync(row, null, generation, cancellationToken);
            return;
        }

        await modCoverCacheThrottle.WaitAsync(cancellationToken);
        try
        {
            try
            {
                var cachedPath = await modCoverCacheService.CacheCoverAsync(
                    row.ThumbnailCacheId,
                    row.RemoteThumbnailUrl,
                    cancellationToken);
                await SetModCoverAsync(row, cachedPath, generation, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
            {
                await SetModCoverAsync(row, null, generation, cancellationToken);
            }
        }
        finally
        {
            modCoverCacheThrottle.Release();
        }
    }

    private async Task SetModCoverAsync(
        NexusModSearchRowViewModel row,
        string? cachedPath,
        long generation,
        CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (generation == modCoverCacheGeneration)
                {
                    // A failed refresh must not erase a valid cached image already in use.
                    if (!string.IsNullOrWhiteSpace(cachedPath)
                        || string.IsNullOrWhiteSpace(row.ThumbnailPath))
                    {
                        row.SetThumbnailPath(cachedPath, activateVisualResources: false);
                    }
                }
            },
            DispatcherPriority.Background,
            cancellationToken);
    }

    private void CancelModCoverCaching()
    {
        modCoverCacheGeneration++;
        modCoverCacheCancellation?.Cancel();
        modCoverCacheCancellation?.Dispose();
        modCoverCacheCancellation = null;
    }

    [RelayCommand]
    private void SetModDownloadView(string view)
    {
        IsModDownloadCardView = !string.Equals(view, "List", StringComparison.OrdinalIgnoreCase);
        IsModDownloadListView = !IsModDownloadCardView;
    }

    [RelayCommand]
    private void SetModRecommendationView(string view)
    {
        IsModRecommendationCardView = !string.Equals(view, "List", StringComparison.OrdinalIgnoreCase);
        IsModRecommendationListView = !IsModRecommendationCardView;
    }

    [RelayCommand]
    private void SetModDownloadQueueSection(string section)
    {
        if (!string.Equals(section, DownloadedQueueSection, StringComparison.OrdinalIgnoreCase))
        {
            section = DownloadingQueueSection;
        }

        if (string.Equals(SelectedModDownloadQueueSection, section, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedModDownloadQueueSection = section;
        SyncVisibleDownloadQueueRows();
    }

    [RelayCommand]
    private void OpenDownloadCenter()
    {
        EnsureSelectedDownloadQueueItem();
        NavigateCore("Downloads", silent: true);
    }

    [RelayCommand]
    private void CloseDownloadCenter()
    {
        NavigateCore("Dashboard", silent: true);
    }

    private async Task EnsureNexusResultsLoadedAsync()
    {
        if (NexusModSearchRows.Count > 0 || IsSearchingNexusMods)
        {
            return;
        }

        await SearchNexusModsAsync();
    }





    [RelayCommand]
    private void ViewNexusModDetails(NexusModSearchRowViewModel? mod)
    {
        if (mod is null)
        {
            return;
        }

        SelectedNexusMod = mod;
        OpenNexusModDetailsPage(mod, string.Format(T("OpenedModDetailsPage"), mod.Name));
    }

    [RelayCommand]
    private void SelectNexusModSearchResult(NexusModSearchRowViewModel? mod)
    {
        if (mod is null)
        {
            return;
        }

        SelectedNexusMod = mod;
        IsModDownloadDetailVisible = true;
    }

    [RelayCommand]
    private async Task TranslateModSummaryAsync(NexusModSearchRowViewModel? mod)
    {
        if (mod is null || !mod.CanTranslateSummary)
        {
            return;
        }

        if (mod.HasTranslationForCurrentLanguage)
        {
            mod.ToggleSummaryTranslation(T);
            return;
        }

        var targetLanguage = GetModSummaryTargetLanguage();
        var sourceLanguage = DetectModSummaryLanguage(mod.Summary);
        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            mod.ShowSummaryTranslationNotice(T("ModSummaryAlreadyCurrentLanguage"), T);
            return;
        }

        mod.BeginSummaryTranslation(T);
        try
        {
            var translatedText = await modTranslationService.TranslateAsync(
                mod.Summary,
                sourceLanguage,
                targetLanguage);
            mod.ApplySummaryTranslation(translatedText, targetLanguage, T);
        }
        catch (ModTranslationException ex)
        {
            mod.FailSummaryTranslation(T(ex.FailureKind switch
            {
                ModTranslationFailureKind.Network => "ModSummaryTranslationNetworkFailed",
                ModTranslationFailureKind.QuotaExceeded => "ModSummaryTranslationQuotaExceeded",
                ModTranslationFailureKind.InvalidResponse => "ModSummaryTranslationInvalidResponse",
                _ => "ModSummaryTranslationServiceFailed"
            }), T);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            mod.FailSummaryTranslation(T("ModSummaryTranslationServiceFailed"), T);
        }
    }

    private string GetModSummaryTargetLanguage() =>
        string.Equals(SelectedLanguage, EnglishLanguage, StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh-CN";

    internal static string DetectModSummaryLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "en";
        }

        return text.Any(character => character is >= '\u3400' and <= '\u9fff')
            ? "zh-CN"
            : "en";
    }

    private static string BuildModDetailsUri(NexusModSearchRowViewModel mod)
    {
        return mod.IsSteamWorkshop
            ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={mod.PublishedFileId.ToString(CultureInfo.InvariantCulture)}"
            : $"https://www.nexusmods.com/kingdomcomedeliverance2/mods/{mod.ModId.ToString(CultureInfo.InvariantCulture)}";
    }

    private void OpenNexusModDetailsPage(NexusModSearchRowViewModel mod, string successMessage)
    {
        SelectedNexusMod = mod;
        var target = BuildModDetailsUri(mod);
        NexusStatusText = TryOpenShellTarget(target)
            ? successMessage
            : string.Format(T("OpenModDetailsPageFailed"), target);
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void CloseNexusModDetails()
    {
        IsModDownloadDetailVisible = false;
    }

    [RelayCommand]
    private void SelectModPackSearchResult(ModPackRowViewModel? modPack)
    {
        if (modPack is null)
        {
            return;
        }

        SelectedModPack = modPack;
        IsModPackDetailVisible = true;
    }

    [RelayCommand]
    private void CloseModPackDetails()
    {
        IsModPackDetailVisible = false;
    }

    [RelayCommand]
    private void ViewModPackOriginalPage(ModPackRowViewModel? modPack)
    {
        if (modPack is null)
        {
            return;
        }

        NexusStatusText = TryOpenShellTarget(modPack.OfficialPageUrl)
            ? string.Format(T("OpenedModPackOriginalPage"), modPack.Name)
            : string.Format(T("OpenModPackOriginalPageFailed"), modPack.OfficialPageUrl);
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    [RelayCommand]
    private async Task InstallModPackAsync(ModPackRowViewModel? modPack)
    {
        if (modPack is null || modPack.IsInstalling || IsShuttingDown)
        {
            return;
        }

        SelectedModPack = modPack;
        IsModPackDetailVisible = true;
        if (!modPack.IsSteamWorkshop)
        {
            await PrepareNexusModPackInstallAsync(modPack);
            return;
        }

        pendingSteamModPackInstall = modPack;
        await ShowSteamAccountBindingDialogAsync();
    }

    private async Task PrepareNexusModPackInstallAsync(ModPackRowViewModel modPack)
    {
        try
        {
            modPack.IsInstalling = true;
            pendingNexusModPackInstall = modPack;
            NexusStatusText = T("PreparingNexusModPack");
            StatusText = NexusStatusText;

            var plan = await modPackInstallService.PrepareAsync(
                modPack.Entry,
                allowAdultContent: modPack.ContainsAdultContent,
                cancellationToken: shutdownCancellation.Token);
            pendingModPackInstallPlan = plan;
            ModPackInstallOptions.Clear();
            foreach (var item in plan.Items)
            {
                ModPackInstallOptions.Add(new ModPackInstallOptionViewModel(item, OnModPackInstallSelectionChanged));
            }

            ModPackPreflightTitleText = string.Format(T("ModPackPreflightTitle"), plan.ModPackName, plan.RevisionNumber);
            ModPackPreflightSummaryText = modPack.Summary;
            ModPackPreflightCompatibilityText = plan.CompatibilityText;
            ModPackPreflightContainsAdultContent = plan.ContainsAdultContent;
            IsModPackAdultContentConfirmed = false;
            ModPackPreflightSizeText = plan.TotalSizeInBytes is > 0
                ? string.Format(T("ModPackPreflightSize"), FormatSize(plan.TotalSizeInBytes.Value))
                : T("ModPackPreflightSizeUnknown");
            ModPackPreflightStatusText = plan.UnsupportedCount > 0
                ? string.Format(T("ModPackPreflightUnsupported"), plan.UnsupportedCount)
                : T("ModPackPreflightReady");
            ModPackInstallProgressPercent = 0;
            ModPackInstallProgressText = string.Empty;
            UpdateModPackPreflightCounts();
            IsModPackPreflightOpen = true;
            OnPropertyChanged(nameof(CanConfirmModPackInstall));
            NexusStatusText = T("ModPackPreflightReady");
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (OperationCanceledException)
        {
            NexusStatusText = T("NexusModPackPreparationCanceled");
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (NexusModsException ex) when (ex.IsAuthenticationFailure)
        {
            pendingNexusModPackInstall = modPack;
            IsNexusApiKeyRequiredForPendingAction = true;
            NexusStatusText = "准备 Nexus 整合包需要有效的 Nexus 登录会话。";
            StatusText = NexusStatusText;
            LastActionText = StatusText;
            OpenNexusAccountBindingDialog(
                "请登录并绑定 Nexus Mods；确认登录后会自动继续准备整合包。整合包内容会逐项下载；如果某个文件必须在官网确认，软件会自动打开对应页面。个人密钥只在浏览器登录不可用时使用。");
        }
        catch (Exception ex)
        {
            NexusStatusText = string.Format(T("NexusModPackPreparationFailed"), ex.Message);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            modPack.IsInstalling = false;
        }
    }

    [RelayCommand]
    private void CloseModPackPreflight()
    {
        IsModPackPreflightOpen = false;

        if (IsModPackPreflightBusy)
        {
            NexusStatusText = T("ModPackInstallContinuesInBackground");
            StatusText = NexusStatusText;
            LastActionText = StatusText;
            return;
        }

        ClearModPackPreflightState();
    }

    private void ClearModPackPreflightState()
    {
        pendingModPackInstallPlan = null;
        pendingNexusModPackInstall = null;
        ModPackInstallOptions.Clear();
        OnPropertyChanged(nameof(CanConfirmModPackInstall));
    }

    [RelayCommand]
    private async Task ConfirmModPackInstallAsync()
    {
        if (!CanConfirmModPackInstall || pendingModPackInstallPlan is null || IsShuttingDown)
        {
            return;
        }

        var plan = pendingModPackInstallPlan;
        var row = pendingNexusModPackInstall;
        try
        {
            IsModPackPreflightBusy = true;
            if (row is not null)
            {
                row.IsInstalling = true;
            }

            var selectedOptionalIds = ModPackInstallOptions
                .Where(option => !option.IsRequired && option.IsSelected)
                .Select(option => option.Id)
                .ToArray();
            var progress = new Progress<ModPackInstallProgress>(value =>
            {
                if (value.DownloadProgress is not null)
                {
                    ApplyDownloadProgress(value.DownloadProgress);
                }

                ModPackPreflightStatusText = value.Message;
                ModPackInstallProgressText = value.CurrentItemName is null
                    ? value.Message
                    : $"{value.Message} ({value.CompletedCount}/{value.TotalCount})";
                ModPackInstallProgressPercent = value.TotalCount > 0
                    ? Math.Clamp(value.CompletedCount * 100d / value.TotalCount, 0, 100)
                    : 0;
                NexusStatusText = value.Message;
                StatusText = value.Message;
                LastActionText = value.Message;
                SyncDownloadQueueRows(modDownloader.Queue);
            });
            var result = await modPackInstallService.InstallAsync(
                plan.SessionId,
                selectedOptionalIds,
                progress,
                shutdownCancellation.Token);
            ModPackInstallProgressPercent = 100;
            ModPackPreflightStatusText = result.Message;
            ModPackInstallProgressText = string.Format(
                T("NexusModPackInstallResult"),
                result.InstalledCount,
                result.ReusedCount,
                result.FailedCount,
                result.SkippedCount);
            NexusStatusText = ModPackInstallProgressText;
            StatusText = NexusStatusText;
            LastActionText = StatusText;
            await RefreshModsAsync();
            RefreshModSearchDownloadStates();
            SyncDownloadQueueRows(modDownloader.Queue);
        }
        catch (OperationCanceledException)
        {
            ModPackPreflightStatusText = T("NexusModPackInstallPaused");
            NexusStatusText = ModPackPreflightStatusText;
            StatusText = NexusStatusText;
        }
        catch (Exception ex) when (ex is NexusModsException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ModPackPreflightStatusText = string.Format(T("NexusModPackInstallFailed"), ex.Message);
            NexusStatusText = ModPackPreflightStatusText;
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            IsModPackPreflightBusy = false;
            if (row is not null)
            {
                row.IsInstalling = false;
            }

            if (!IsModPackPreflightOpen)
            {
                ClearModPackPreflightState();
            }
        }
    }

    [RelayCommand]
    private async Task PauseModPackInstallAsync()
    {
        if (pendingModPackInstallPlan is null || !IsModPackPreflightBusy)
        {
            return;
        }

        await modPackInstallService.PauseAsync(pendingModPackInstallPlan.SessionId);
        ModPackPreflightStatusText = T("NexusModPackInstallPaused");
    }

    [RelayCommand]
    private void ViewModPackManualItems()
    {
        if (pendingModPackInstallPlan is null)
        {
            return;
        }

        var manualUrl = pendingModPackInstallPlan.Items
            .Where(item => item.Support != ModPackInstallItemSupport.Supported)
            .Select(item => item.ManualUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        if (string.IsNullOrWhiteSpace(manualUrl) || !TryOpenShellTarget(manualUrl))
        {
            _ = TryOpenShellTarget(pendingModPackInstallPlan.OfficialPageUrl);
        }
    }

    private void OnModPackInstallSelectionChanged()
    {
        UpdateModPackPreflightCounts();
        OnPropertyChanged(nameof(CanConfirmModPackInstall));
    }

    private void UpdateModPackPreflightCounts()
    {
        var required = ModPackInstallOptions.Count(option => option.IsRequired);
        var selectedOptional = ModPackInstallOptions.Count(option => !option.IsRequired && option.IsSelected);
        var unsupported = ModPackInstallOptions.Count(option => !option.IsSupported);
        ModPackPreflightCountsText = string.Format(
            T("ModPackPreflightCounts"),
            required,
            selectedOptional,
            unsupported);
    }

    private async Task InstallSteamModPackCoreAsync(ModPackRowViewModel modPack)
    {
        if (IsShuttingDown)
        {
            return;
        }

        if (!ulong.TryParse(modPack.PlatformIdentifier, NumberStyles.None, CultureInfo.InvariantCulture, out var collectionId)
            || collectionId == 0)
        {
            NexusStatusText = T("InvalidSteamModPackId");
            StatusText = NexusStatusText;
            LastActionText = StatusText;
            return;
        }

        try
        {
            IsDownloadingMods = true;
            modPack.IsInstalling = true;
            var modsDirectory = applicationPathService.GetPaths().ModsDirectory;
            ModsDirectory = modsDirectory;
            var progress = new Progress<WorkshopCollectionInstallProgress>(value =>
            {
                NexusStatusText = string.Format(
                    T("InstallingSteamModPackProgress"),
                    value.CompletedCount,
                    value.TotalCount);
                StatusText = NexusStatusText;
                LastActionText = StatusText;
            });

            NexusStatusText = string.Format(T("InstallingSteamModPack"), modPack.Name);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
            var result = await workshopService.InstallCollectionAsync(
                new WorkshopCollectionInstallRequest(collectionId, modsDirectory),
                progress,
                shutdownCancellation.Token);

            NexusStatusText = result.FailedCount == 0
                ? string.Format(T("SteamModPackInstallComplete"), result.InstalledCount)
                : string.Format(T("SteamModPackInstallPartial"), result.InstalledCount, result.FailedCount);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
            if (result.InstalledCount > 0)
            {
                await RefreshModsAsync();
                RefreshModSearchDownloadStates();
            }
        }
        catch (WorkshopException ex)
        {
            NexusStatusText = FormatWorkshopError(ex);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            NexusStatusText = string.Format(T("SteamModPackInstallFailed"), ex.Message);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            modPack.IsInstalling = false;
            IsDownloadingMods = false;
        }
    }

    [RelayCommand]
    private async Task DownloadNexusModAsync(NexusModSearchRowViewModel? mod)
    {
        if (mod is null)
        {
            NexusStatusText = T("SelectNexusModBeforeDownload");
            StatusText = NexusStatusText;
            LastActionText = StatusText;
            return;
        }

        if (mod.IsDownloaded)
        {
            NexusStatusText = T("ModDownloadStatusDownloaded");
            StatusText = NexusStatusText;
            LastActionText = StatusText;
            return;
        }

        if (mod.IsSteamWorkshop)
        {
            await InstallSteamWorkshopModAsync(mod);
            return;
        }

        var pickerRequestId = 0L;
        try
        {
            if (!await EnsureNexusAccountBoundForDownloadAsync(mod))
            {
                return;
            }

            nexusApiKeyStore.SetApiKey(NexusApiKey);
            pickerRequestId = ++nexusModFilePickerRequestId;
            NexusModFilePickerMod = mod;
            NexusModFileOptions.Clear();
            NexusModDownloadPresets.Clear();
            NexusModRequirementOptions.Clear();
            OnPropertyChanged(nameof(HasNexusModRequirements));
            SelectedNexusModDownloadPreset = null;
            IsNexusModFileAdvancedSelectionVisible = false;
            AutoDownloadNexusModRequirements = true;
            NexusModRequirementsSummaryText = string.Empty;
            NexusModFilePickerStatusText = string.Empty;
            IsNexusModFilePickerVisible = true;
            IsLoadingNexusModFileOptions = true;
            NexusStatusText = string.Format(T("LoadingNexusModFiles"), mod.Name);
            StatusText = NexusStatusText;

            var details = await nexusModService.GetModDetailsAsync("kingdomcomedeliverance2", mod.ModId);
            if (pickerRequestId != nexusModFilePickerRequestId)
            {
                return;
            }

            foreach (var file in details.Files
                         .OrderByDescending(file => file.IsPrimary)
                         .ThenByDescending(file => file.IsManagerDownload)
                         .ThenByDescending(file => file.UploadedTimestamp))
            {
                var option = new NexusModFileOptionViewModel(
                    file,
                    string.Equals(SelectedLanguage, SimplifiedChineseLanguage, StringComparison.OrdinalIgnoreCase));
                option.SelectionChanged = OnNexusModFileOptionSelectionChanged;
                NexusModFileOptions.Add(option);
            }

            PopulateNexusModRequirements(details, mod.ModId);

            BuildNexusModDownloadPresets();
            NexusModFilePickerStatusText = NexusModFileOptions.Count == 0
                ? T("NexusModFilePickerEmpty")
                : string.Empty;
            NexusStatusText = NexusModFilePickerStatusText;
            StatusText = NexusStatusText;
        }
        catch (NexusModsException ex)
        {
            if (pickerRequestId != nexusModFilePickerRequestId)
            {
                return;
            }

            NexusModFilePickerStatusText = FormatNexusError(ex);
            NexusStatusText = NexusModFilePickerStatusText;
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            if (pickerRequestId != nexusModFilePickerRequestId)
            {
                return;
            }

            NexusModFilePickerStatusText = string.Format(T("QueueNexusDownloadFailed"), ex.Message);
            NexusStatusText = NexusModFilePickerStatusText;
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            if (pickerRequestId == nexusModFilePickerRequestId)
            {
                IsLoadingNexusModFileOptions = false;
            }
        }
    }

    [RelayCommand]
    private void CloseNexusModFilePicker()
    {
        nexusModFilePickerRequestId++;
        IsNexusModFilePickerVisible = false;
        IsLoadingNexusModFileOptions = false;
        NexusModFilePickerMod = null;
        SelectedNexusModDownloadPreset = null;
        NexusModFileOptions.Clear();
        NexusModDownloadPresets.Clear();
        NexusModRequirementOptions.Clear();
        OnPropertyChanged(nameof(HasNexusModRequirements));
        IsNexusModFileAdvancedSelectionVisible = false;
        NexusModRequirementsSummaryText = string.Empty;
        NexusModFilePickerStatusText = string.Empty;
    }

    private bool CanConfirmNexusModFileDownload() =>
        NexusModFilePickerMod is not null
        && NexusModFileOptions.Any(file => file.IsSelected)
        && !IsLoadingNexusModFileOptions;

    private void PopulateNexusModRequirements(NexusModDetails details, int selectedModId)
    {
        NexusModRequirementOptions.Clear();

        foreach (var requirement in details.Requirements)
        {
            if (requirement.ModId == selectedModId)
            {
                continue;
            }

            if (!requirement.IsExternal
                && requirement.ModId is { } requiredModId
                && IsNexusModDownloaded(requiredModId))
            {
                continue;
            }

            NexusModRequirementOptions.Add(new NexusModRequirementViewModel(
                requirement,
                T("NexusModRequirementSourceNexus"),
                T("NexusModRequirementSourceManual")));
        }

        var automaticCount = NexusModRequirementOptions.Count(requirement => requirement.IsAutoDownloadAvailable);
        var manualCount = NexusModRequirementOptions.Count - automaticCount;
        NexusModRequirementsSummaryText = manualCount switch
        {
            0 => string.Format(T("NexusModRequirementsAutoSummary"), automaticCount),
            _ => string.Format(T("NexusModRequirementsMixedSummary"), automaticCount, manualCount)
        };
        OnPropertyChanged(nameof(HasNexusModRequirements));
    }

    [RelayCommand]
    private void OpenNexusModRequirementDetails(NexusModRequirementViewModel? requirement)
    {
        if (requirement is null || string.IsNullOrWhiteSpace(requirement.DetailsUrl))
        {
            return;
        }

        NexusStatusText = TryOpenShellTarget(requirement.DetailsUrl)
            ? string.Format(T("OpenedModDetailsPage"), requirement.Name)
            : string.Format(T("OpenModDetailsPageFailed"), requirement.DetailsUrl);
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void ApplyNexusModDownloadPreset(NexusModDownloadPresetViewModel? preset)
    {
        if (preset is null)
        {
            return;
        }

        isApplyingNexusModDownloadPreset = true;
        try
        {
            foreach (var option in NexusModFileOptions)
            {
                option.IsSelected = preset.FileIds.Contains(option.File.FileId);
            }

            foreach (var candidate in NexusModDownloadPresets)
            {
                candidate.IsSelected = ReferenceEquals(candidate, preset);
            }

            SelectedNexusModDownloadPreset = preset;
        }
        finally
        {
            isApplyingNexusModDownloadPreset = false;
        }

        RefreshNexusModFilePickerSelection();
    }

    [RelayCommand]
    private void ToggleNexusModFileAdvancedSelection()
    {
        IsNexusModFileAdvancedSelectionVisible = !IsNexusModFileAdvancedSelectionVisible;
    }

    private void BuildNexusModDownloadPresets()
    {
        NexusModDownloadPresets.Clear();
        var primary = NexusModFileOptions.FirstOrDefault(file => file.File.IsPrimary && file.File.IsManagerDownload)
            ?? NexusModFileOptions.FirstOrDefault(file => file.File.IsPrimary)
            ?? NexusModFileOptions.FirstOrDefault(file => file.File.IsManagerDownload)
            ?? NexusModFileOptions.FirstOrDefault();
        if (primary is null)
        {
            RefreshNexusModFilePickerSelection();
            return;
        }

        var baseFiles = new[] { primary.File.FileId };
        NexusModDownloadPresets.Add(CreateNexusModDownloadPreset(
            "Recommended",
            T("NexusModDownloadPresetRecommendedTitle"),
            T("NexusModDownloadPresetRecommendedDescription"),
            baseFiles,
            isRecommended: true));

        var updates = NexusModFileOptions
            .Where(file => IsNexusFileCategory(file, "UPDATE"))
            .Select(file => file.File.FileId)
            .ToArray();
        if (updates.Length > 0)
        {
            NexusModDownloadPresets.Add(CreateNexusModDownloadPreset(
                "MainAndUpdates",
                T("NexusModDownloadPresetUpdatesTitle"),
                T("NexusModDownloadPresetUpdatesDescription"),
                baseFiles.Concat(updates)));
        }

        var currentExtras = NexusModFileOptions
            .Where(file => IsNexusFileCategory(file, "UPDATE") || IsNexusFileCategory(file, "OPTIONAL"))
            .Select(file => file.File.FileId)
            .ToArray();
        if (currentExtras.Length > updates.Length)
        {
            NexusModDownloadPresets.Add(CreateNexusModDownloadPreset(
                "Complete",
                T("NexusModDownloadPresetCompleteTitle"),
                T("NexusModDownloadPresetCompleteDescription"),
                baseFiles.Concat(currentExtras)));
        }

        ApplyNexusModDownloadPreset(NexusModDownloadPresets[0]);
    }

    private NexusModDownloadPresetViewModel CreateNexusModDownloadPreset(
        string key,
        string title,
        string description,
        IEnumerable<int> fileIds,
        bool isRecommended = false)
    {
        var selectedFileIds = fileIds.Distinct().ToArray();
        return new NexusModDownloadPresetViewModel(
            key,
            title,
            description,
            selectedFileIds,
            string.Format(T("NexusModFilePickerFileCount"), selectedFileIds.Length),
            isRecommended);
    }

    private void OnNexusModFileOptionSelectionChanged()
    {
        if (!isApplyingNexusModDownloadPreset)
        {
            foreach (var preset in NexusModDownloadPresets)
            {
                preset.IsSelected = false;
            }

            SelectedNexusModDownloadPreset = null;
        }

        RefreshNexusModFilePickerSelection();
    }

    private void RefreshNexusModFilePickerSelection()
    {
        var selectedCount = NexusModFileOptions.Count(file => file.IsSelected);
        NexusModFilePickerSelectionText = selectedCount == 0
            ? T("NexusModFilePickerNoSelection")
            : string.Format(T("NexusModFilePickerSelectedCount"), selectedCount);

        var hasCurrentMainFile = NexusModFileOptions.Any(file =>
            file.IsSelected && file.IsPrimary && !file.IsLegacyVersion);
        var hasLegacyVersion = NexusModFileOptions.Any(file =>
            file.IsSelected && file.IsLegacyVersion);
        if (hasCurrentMainFile && hasLegacyVersion)
        {
            NexusModFilePickerStatusText = T("NexusModFilePickerLegacyConflict");
        }
        else if (NexusModFilePickerStatusText == T("NexusModFilePickerLegacyConflict"))
        {
            NexusModFilePickerStatusText = string.Empty;
        }

        ConfirmNexusModFileDownloadCommand.NotifyCanExecuteChanged();
    }

    private static bool IsNexusFileCategory(NexusModFileOptionViewModel option, string category) =>
        option.File.Category.Contains(category, StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanConfirmNexusModFileDownload))]
    private async Task ConfirmNexusModFileDownloadAsync()
    {
        var mod = NexusModFilePickerMod;
        var selectedFiles = NexusModFileOptions
            .Where(option => option.IsSelected)
            .Select(option => option.File)
            .ToArray();
        if (mod is null || selectedFiles.Length == 0)
        {
            return;
        }

        try
        {
            var destinationDirectory = GetModDownloadDirectory();
            Directory.CreateDirectory(destinationDirectory);

            IsLoadingNexusModFileOptions = true;
            NexusModFilePickerStatusText = string.Format(T("ResolvingNexusModFiles"), selectedFiles.Length);
            NexusStatusText = NexusModFilePickerStatusText;
            StatusText = NexusStatusText;

            var items = await (AutoDownloadNexusModRequirements
                ? modDownloader.EnqueueFilesWithDependenciesAsync(
                    "kingdomcomedeliverance2",
                    mod.ModId,
                    selectedFiles,
                    destinationDirectory)
                : modDownloader.EnqueueFilesAsync(
                    "kingdomcomedeliverance2",
                    mod.ModId,
                    selectedFiles,
                    destinationDirectory));

            foreach (var item in items)
            {
                dismissedCanceledDownloadKeys.Remove(item.QueueKey);
            }

            SyncDownloadQueueRows(modDownloader.Queue);
            _ = EnrichDownloadQueuePresentationAsync(items, mod, downloadCoverCancellation.Token);
            NexusStatusText = string.Format(T("QueuedNexusModDownloads"), items.Count);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
            CloseNexusModFilePicker();

            if (!IsDownloadingMods)
            {
                await StartModDownloadQueueAsync();
            }
        }
        catch (NexusModsException ex)
        {
            NexusModFilePickerStatusText = FormatNexusError(ex);
            NexusStatusText = NexusModFilePickerStatusText;
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            NexusModFilePickerStatusText = string.Format(T("QueueNexusDownloadFailed"), ex.Message);
            NexusStatusText = NexusModFilePickerStatusText;
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            IsLoadingNexusModFileOptions = false;
        }
    }


    [RelayCommand]
    private void OpenStore()
    {
        Navigate("Store");
        StatusText = TryOpenShellTarget("https://store.steampowered.com/app/1771300/")
            ? T("OpenedStore")
            : T("StoreOpenFailed");
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        Navigate("Settings");
        var paths = applicationPathService.GetPaths();
        DataDirectory = paths.DataDirectory;
        DatabasePath = paths.DatabasePath;
        ModsDirectory = paths.ModsDirectory;
        TrackerBridgePath = paths.TrackerBridgeEventsPath;
        SelectSettingsCategory("Navigation");
        StatusText = T("SettingsOpened");
        LastActionText = StatusText;
    }

    // Lab workspace: home of KCD2 gameplay simulations (alchemy and forging).
    // The top navigation entry navigates here; the left
    // sub-navigation switches between simulation modules without re-triggering
    // the entrance animation.
    [RelayCommand]
    private async Task OpenLabAsync()
    {
        EnsureLabNavigationInitialized();
        await EnsureLabModuleReadyAsync(currentLabModuleKey);
        Navigate("Lab");
        ApplySelectedLabModule(currentLabModuleKey);
        StatusText = T("LabOpened");
        LastActionText = StatusText;
    }

    [RelayCommand]
    private async Task SelectLabModuleAsync(string module)
    {
        var normalized = module switch
        {
            "Forging" => "Forging",
            _ => "Alchemy"
        };

        await EnsureLabModuleReadyAsync(normalized);
        ApplySelectedLabModule(normalized);
    }

    private void ApplySelectedLabModule(string normalized)
    {
        currentLabModuleKey = normalized;
        IsLabAlchemyActive = normalized == "Alchemy";
        IsLabForgingActive = normalized == "Forging";

        LabModuleDescription = normalized switch
        {
            "Forging" => T("LabForgingLongDescription"),
            _ => T("LabAlchemyLongDescription")
        };

        foreach (var card in LabModuleCards)
        {
            card.IsActive = string.Equals(card.ModuleKey, normalized, StringComparison.Ordinal);
            card.HighlightThickness = card.IsActive ? new Thickness(2) : new Thickness(0);
        }

        StatusText = string.Format(T("LabModuleSelected"), normalized);
        LastActionText = StatusText;
    }

    private async Task EnsureLabModuleReadyAsync(string module)
    {
        await labModuleInitializationGate.WaitAsync();
        try
        {
            var playerStateVersion = Volatile.Read(ref labPlayerStateVersion);
            var alchemy = alchemyWorkshop.Value;
            alchemy.UseLanguage(SelectedLanguage == EnglishLanguage);
            if (alchemyPlayerStateVersion != playerStateVersion)
            {
                alchemy.ResetFromGesture();
                alchemyPlayerStateVersion = playerStateVersion;
            }

            if (module == "Forging" && forgePlayerStateVersion != playerStateVersion)
            {
                var forge = forgeWorkshop.Value;
                forge.UseLanguage(SelectedLanguage == EnglishLanguage);
                await forge.ReloadPlayerProfileAsync();
                forgePlayerStateVersion = playerStateVersion;
            }
            else if (module == "Forging")
            {
                forgeWorkshop.Value.UseLanguage(SelectedLanguage == EnglishLanguage);
            }
        }
        finally
        {
            labModuleInitializationGate.Release();
        }
    }

    private async Task RefreshCreatedLabModulesAsync(long playerStateVersion)
    {
        await labModuleInitializationGate.WaitAsync();
        try
        {
            if (alchemyWorkshop.IsValueCreated)
            {
                alchemyWorkshop.Value.ResetFromGesture();
                alchemyPlayerStateVersion = playerStateVersion;
            }

            if (forgeWorkshop.IsValueCreated)
            {
                await forgeWorkshop.Value.ReloadPlayerProfileAsync();
                forgePlayerStateVersion = playerStateVersion;
            }
        }
        finally
        {
            labModuleInitializationGate.Release();
        }
    }

    private string currentLabModuleKey = "Alchemy";

    private void EnsureLabNavigationInitialized()
    {
        if (labNavigationInitialized)
        {
            return;
        }

        InitializeLabModules();
        labNavigationInitialized = true;
    }

    private void InitializeLabModules()
    {
        var flaskIcon = StreamGeometry.Parse("M10,3 L14,3 L14,9 L19,19 A1.6,1.6 0 0,1 17.6,21 L6.4,21 A1.6,1.6 0 0,1 5,19 L10,9 Z M8.5,3 L8.5,2.4 A1.4,1.4 0 0,1 9.9,1 L14.1,1 A1.4,1.4 0 0,1 15.5,2.4 L15.5,3 M9,14 L15,14");
        var hammerIcon = StreamGeometry.Parse("M14,4 L20,10 L18,12 L12,6 Z M12,6 L5,13 L4,16 L7,15 L14,8 M11,13 L18,20 M16,22 L20,18");
        LabModuleCards.Clear();
        LabModuleCards.Add(new LabModuleCardViewModel("Alchemy", T("LabAlchemyText"), T("LabStatusPrototypeText"), flaskIcon, isAvailable: true)
        {
            SelectCommand = SelectLabModuleCommand,
            IsActive = true,
            HighlightThickness = new Thickness(2)
        });
        LabModuleCards.Add(new LabModuleCardViewModel("Forging", T("LabForgingText"), T("LabStatusPrototypeText"), hammerIcon, isAvailable: true)
        {
            SelectCommand = SelectLabModuleCommand
        });
        ReplaceItems(LabAlchemyFeatures, T("LabAlchemyFeature1"), T("LabAlchemyFeature2"), T("LabAlchemyFeature3"));
        ReplaceItems(LabForgingFeatures, T("LabForgingFeature1"), T("LabForgingFeature2"), T("LabForgingFeature3"));
    }

    private void ApplyLabLocalization()
    {
        LabModuleDescription = currentLabModuleKey switch
        {
            "Forging" => T("LabForgingLongDescription"),
            _ => T("LabAlchemyLongDescription")
        };

        foreach (var card in LabModuleCards)
        {
            card.Title = card.ModuleKey switch
            {
                "Forging" => T("LabForgingText"),
                _ => T("LabAlchemyText")
            };
            // Status strings depend on availability rather than module key.
            card.Status = card.IsAvailable ? T("LabStatusPrototypeText") : T("LabStatusPlannedText");
        }

        ReplaceItems(LabAlchemyFeatures, T("LabAlchemyFeature1"), T("LabAlchemyFeature2"), T("LabAlchemyFeature3"));
        ReplaceItems(LabForgingFeatures, T("LabForgingFeature1"), T("LabForgingFeature2"), T("LabForgingFeature3"));
    }

    private async Task OpenSavesCore(bool silent)
    {
        var shouldAnimateSaveWorkspace = silent && !IsSavesActive;
        NavigateCore("Saves", silent);
        if (shouldAnimateSaveWorkspace)
        {
            SaveWorkspaceTransitionRequested?.Invoke();
        }

        await SaveManager.InitializeAsync();
    }

    [RelayCommand]
    private Task OpenSavesAsync()
    {
        return OpenSavesCore(silent: false);
    }

    // Invoked by the LEFT sub-navigation bar; suppresses the entrance animation.
    // Method name is chosen so the generated command property is "OpenSavesSilentCommand".
    [RelayCommand]
    private Task OpenSavesSilent()
    {
        return OpenSavesCore(silent: true);
    }

    private async Task LoadCachedGameNewsAsync()
    {
        try
        {
            var cached = await gameNewsService.LoadCachedAsync();
            if (cached is not null)
            {
                ApplyGameNewsFeed(cached);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DashboardNewsStatusText = T("DashboardNewsUnavailable");
        }
    }

    [RelayCommand]
    private async Task RefreshGameNewsAsync()
    {
        IsGameNewsLoading = true;
        DashboardNewsStatusText = T("DashboardNewsLoading");
        try
        {
            var feed = await gameNewsService.RefreshAsync();
            ApplyGameNewsFeed(feed);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or TaskCanceledException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            HasGameNewsError = !HasGameNews;
            DashboardNewsStatusText = HasGameNews
                ? T("DashboardNewsCached")
                : T("DashboardNewsUnavailable");
        }
        finally
        {
            IsGameNewsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenGameNews(GameNewsItemViewModel? item)
    {
        if (item is null || !IsAllowedDashboardNewsUrl(item.Url))
        {
            DashboardNewsStatusText = T("DashboardNewsLinkBlocked");
            return;
        }

        DashboardNewsStatusText = TryOpenShellTarget(item.Url)
            ? T("DashboardNewsOpened")
            : T("DashboardNewsOpenFailed");
    }

    [RelayCommand]
    private async Task OpenDashboardModConflictsAsync()
    {
        NavigateCore("Mods", silent: false);
        SelectModManagerPage("List");
        await RefreshModsAsync();

        var firstPendingConflict = currentModConflicts.FirstOrDefault(conflict =>
            !IsConflictReviewed(conflict, currentModConflictReviews));
        if (firstPendingConflict is not null)
        {
            var preferredModId = firstPendingConflict.ModIds.FirstOrDefault(modId =>
                                     !string.Equals(modId, firstPendingConflict.WinningModId, StringComparison.OrdinalIgnoreCase))
                                 ?? firstPendingConflict.WinningModId;
            SelectedMod = ModRows.FirstOrDefault(row =>
                              string.Equals(row.Id, preferredModId, StringComparison.OrdinalIgnoreCase))
                          ?? ModRows.FirstOrDefault(row => row.IsConflicted);
        }
    }

    [RelayCommand]
    private async Task SetDashboardModEnabledAsync(ModListItemViewModel? mod)
    {
        if (mod is null)
        {
            return;
        }

        if (mod.IsBuiltIn)
        {
            mod.IsEnabled = true;
            return;
        }

        var requestedState = mod.IsEnabled;
        try
        {
            await modCatalogService.SaveModEnabledStateAsync(mod.Id, requestedState);
        }
        catch (Exception ex)
        {
            mod.IsEnabled = !requestedState;
            StatusText = string.Format(T("DashboardModToggleFailed"), mod.DisplayName, ex.Message);
            LastActionText = StatusText;
            return;
        }

        await RefreshModsAsync();
        StatusText = string.Format(
            T(requestedState ? "DashboardModEnabled" : "DashboardModDisabled"),
            mod.DisplayName);
        LastActionText = StatusText;
        ModCatalogStatusText = StatusText;
    }

    private void ApplyGameNewsFeed(GameNewsFeed feed)
    {
        var rows = feed.Items
            .Take(5)
            .Select(item => new GameNewsItemViewModel(item))
            .ToArray();

        if (launcherDetailsVisualResourcesActive)
        {
            foreach (var row in rows)
            {
                row.ActivateVisualResources();
            }
        }

        var previousRows = SecondaryGameNewsItems
            .Prepend(FeaturedGameNews)
            .Where(row => row is not null)
            .Cast<GameNewsItemViewModel>()
            .Distinct()
            .ToArray();
        FeaturedGameNews = rows.FirstOrDefault();
        SecondaryGameNewsItems.Clear();
        foreach (var row in rows.Skip(1))
        {
            SecondaryGameNewsItems.Add(row);
        }

        foreach (var row in previousRows)
        {
            row.Dispose();
        }

        HasGameNews = FeaturedGameNews is not null;
        HasGameNewsError = false;
        IsGameNewsUsingCache = feed.IsFromCache;
        DashboardUpdatedText = string.Format(
            T("DashboardUpdatedAt"),
            feed.UpdatedAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture));
        DashboardNewsStatusText = feed.IsStale
            ? T("DashboardNewsCached")
            : string.Format(T("DashboardNewsItems"), rows.Length);
    }

    private static bool IsAllowedDashboardNewsUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return uri.Host.Equals("steamstore-a.akamaihd.net", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDashboardTimestamp()
    {
        DashboardUpdatedText = string.Format(
            T("DashboardUpdatedAt"),
            DateTimeOffset.Now.ToString("HH:mm", CultureInfo.CurrentCulture));
    }

    private void OnSaveManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SaveManagerViewModel.ProfileCount)
            or nameof(SaveManagerViewModel.SelectedProfile))
        {
            SyncAdventureSaveOptions();
            RefreshAdventureProfileRuntimeData();
            _ = RefreshAdventureProfileDataAsync();
        }
    }

    private void SyncAdventureSaveOptions()
    {
        var options = SaveManager.AllProfiles
            .OrderByDescending(profile => profile.Profile.LastSavedAtUtc
                                          ?? profile.Profile.LastActivatedAtUtc
                                          ?? profile.Profile.UpdatedAtUtc)
            .Select(profile => new AdventureSaveOption(profile.Id, profile.DisplayName));

        AdventureProfile.UpdateSaveOptions(
            options,
            SaveManager.SelectedProfile?.Id,
            OpenAdventureSaveAsync);
    }

    private async Task OpenAdventureSaveAsync(Guid saveId)
    {
        var profile = SaveManager.AllProfiles.FirstOrDefault(item => item.Id == saveId);
        if (profile is not null)
        {
            SaveManager.SelectedProfile = profile;
        }

        await OpenSavesCore(silent: false);
    }

    private void RefreshAdventureProfileRuntimeData()
    {
        var selectedHours = SaveManager.SelectedProfile?.Profile.PlayTime?.TotalHours ?? 0;

        AdventureProfile.UpdateRuntimeData(
            FormatAdventurePlatform(SourceText),
            (int)Math.Floor(Math.Max(0, selectedHours)),
            SaveManager.ProfileCount,
            EnabledModCount);
    }

    private void ApplyAdventureProfileAccountIdentity()
    {
        var account = PlayerProfiles.CurrentPlayer;
        AdventureProfile.SetAccountIdentity(account?.DisplayName, account?.AvatarImage);
    }

    private async Task RefreshAdventureProfileDataAsync()
    {
        var refreshVersion = Interlocked.Increment(ref adventureProfileRefreshVersion);
        var saveSlotId = SaveManager.SelectedProfile?.Id;
        try
        {
            var data = await adventureProfileDataService.LoadAsync(saveSlotId);
            if (refreshVersion == Volatile.Read(ref adventureProfileRefreshVersion))
            {
                AdventureProfile.ApplyGameData(data);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Warning(ex, "Unable to load Adventure Profile game data");
        }
    }

    private static string? FormatAdventurePlatform(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)
            || source.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || source.Equals("未知", StringComparison.Ordinal)
            || source.Equals("Not scanned", StringComparison.OrdinalIgnoreCase)
            || source.Equals("未扫描", StringComparison.Ordinal))
        {
            return null;
        }

        if (source.Contains("Steam", StringComparison.OrdinalIgnoreCase))
        {
            return "Steam Edition";
        }

        if (source.Contains("GOG", StringComparison.OrdinalIgnoreCase))
        {
            return "GOG Edition";
        }

        if (source.Contains("Epic", StringComparison.OrdinalIgnoreCase))
        {
            return "Epic Games";
        }

        return source;
    }

    [RelayCommand]
    private async Task ResetKcd2GameDirectoryAsync()
    {
        await gameInstallationStore.SaveAsync(Array.Empty<DiscoveredGame>());
        selectedGame = null;
        ManualGamePath = string.Empty;
        GameCount = 0;
        VerifiedGameCount = 0;
        RefreshLaunchStateText();
        Sections[1].Detail = T("SectionInstallationDetail");
        StatusText = "StatusText";
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        var paths = applicationPathService.GetPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        DataDirectory = paths.DataDirectory;
        StatusText = TryOpenShellTarget(paths.DataDirectory)
            ? T("OpenedDataFolder")
            : string.Format(T("DataFolderLabel"), paths.DataDirectory);
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        var paths = applicationPathService.GetPaths();
        Directory.CreateDirectory(paths.ModsDirectory);
        ModsDirectory = paths.ModsDirectory;
        StatusText = TryOpenShellTarget(paths.ModsDirectory)
            ? T("OpenedModsFolder")
            : string.Format(T("ModsFolderLabel"), paths.ModsDirectory);
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void AddMod()
    {
        Navigate("Mods");
        OpenModsFolder();
        StatusText = T("OpenedModsFolderAddExtracted");
        LastActionText = StatusText;
    }

    private string GetModDownloadDirectory()
    {
        var paths = applicationPathService.GetPaths();
        var downloadDirectory = Path.Combine(paths.ModsDirectory, "downloads");
        ModsDirectory = paths.ModsDirectory;
        return downloadDirectory;
    }

    private void SyncDownloadQueueRows(IEnumerable<ModDownloadQueueItem> items)
    {
        var queueItems = items.ToArray();
        var currentQueueKeys = queueItems
            .Select(item => item.QueueKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        dismissedCanceledDownloadKeys.RemoveWhere(key => !currentQueueKeys.Contains(key));
        var currentModPackModIds = queueItems
            .Where(item => item.Request.ModPackSessionId is not null)
            .Select(item => item.Request.ModId)
            .ToHashSet();
        requestedModPackDownloadPresentationModIds.RemoveWhere(modId => !currentModPackModIds.Contains(modId));
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in queueItems)
        {
            if (dismissedCanceledDownloadKeys.Contains(item.QueueKey)
                || item.Status == ModDownloadStatus.Canceled)
            {
                continue;
            }

            seenKeys.Add(item.QueueKey);
            var row = ModDownloadQueueRows.FirstOrDefault(row => string.Equals(row.QueueKey, item.QueueKey, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new ModDownloadQueueRowViewModel(item);
                ApplyCachedDownloadPresentation(row);
                ModDownloadQueueRows.Add(row);
                continue;
            }

            row.ApplyQueueItem(item);
        }

        for (var index = ModDownloadQueueRows.Count - 1; index >= 0; index--)
        {
            if (!seenKeys.Contains(ModDownloadQueueRows[index].QueueKey))
            {
                ModDownloadQueueRows[index].Dispose();
                ModDownloadQueueRows.RemoveAt(index);
            }
        }

        EnsureModPackDownloadPresentation(queueItems);
        RefreshDownloadQueueState();
        RefreshModSearchDownloadStates();
    }

    private void ApplyDownloadProgress(ModDownloadProgress progress)
    {
        var queueItem = modDownloader.Queue.FirstOrDefault(item =>
            string.Equals(item.QueueKey, progress.QueueKey, StringComparison.OrdinalIgnoreCase));
        if (queueItem is null)
        {
            return;
        }

        if (dismissedCanceledDownloadKeys.Contains(progress.QueueKey))
        {
            return;
        }

        var row = ModDownloadQueueRows.FirstOrDefault(row => string.Equals(row.QueueKey, progress.QueueKey, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new ModDownloadQueueRowViewModel(queueItem);
            ApplyCachedDownloadPresentation(row);
            ModDownloadQueueRows.Add(row);
            RefreshDownloadQueueState();
        }

        row.ApplyProgress(progress);
        if (progress.Status == ModDownloadStatus.Downloading
            && (SelectedDownloadQueueItem is null
                || SelectedDownloadQueueItem.Status is not (ModDownloadStatus.Downloading or ModDownloadStatus.Paused)))
        {
            SelectedDownloadQueueItem = row;
        }

        NexusStatusText = progress.Status == ModDownloadStatus.Downloading
            ? string.Format(T("DownloadProgressWithSpeed"), progress.FileName, row.ProgressText, row.SpeedText)
            : $"{progress.FileName}: {row.ProgressText}";
        StatusText = NexusStatusText;
        RefreshDownloadQueueState();
        RefreshModSearchDownloadStates();
    }

    private void ApplyCachedDownloadPresentation(ModDownloadQueueRowViewModel row)
    {
        var source = FindModSearchRow(row.ModId);
        var coverPath = source?.ThumbnailPath ?? modCoverCacheService.GetCachedCoverPath(row.ModId);
        ModPackRowViewModel? modPack = null;
        if (row.IsModPackDownload)
        {
            modPack = ModPackRows.FirstOrDefault(item =>
                string.Equals(item.Entry.Id, row.ModPackId, StringComparison.OrdinalIgnoreCase));
            coverPath ??= modPack?.ThumbnailPath;
            if (coverPath is null && modPack is not null)
            {
                coverPath = modCoverCacheService.GetCachedCoverPath(modPack.ThumbnailCacheId);
            }
        }

        row.SetModPresentation(
            source?.Name ?? modPack?.Name ?? row.ModPackName,
            coverPath,
            activateVisualResources: false);
    }

    private void EnsureModPackDownloadPresentation(IReadOnlyCollection<ModDownloadQueueItem> items)
    {
        var pendingItems = items
            .Where(item => item.Request.ModPackSessionId is not null)
            .Where(item => requestedModPackDownloadPresentationModIds.Add(item.Request.ModId))
            .ToArray();
        if (pendingItems.Length == 0)
        {
            return;
        }

        _ = EnrichDownloadQueuePresentationAsync(pendingItems, null, downloadCoverCancellation.Token);
    }

    private NexusModSearchRowViewModel? FindModSearchRow(int modId)
    {
        return NexusModSearchRows
                   .Concat(ModRecommendationRows.Select(item => item.Mod))
                   .FirstOrDefault(item => !item.IsSteamWorkshop && item.ModId == modId);
    }

    private async Task EnrichDownloadQueuePresentationAsync(
        IReadOnlyCollection<ModDownloadQueueItem> items,
        NexusModSearchRowViewModel? primaryMod,
        CancellationToken cancellationToken)
    {
        var tasks = items
            .GroupBy(item => item.Request.ModId)
            .Select(group => EnrichDownloadPresentationAsync(
                group.Key,
                group.Key == primaryMod?.ModId ? primaryMod : FindModSearchRow(group.Key),
                cancellationToken));

        await Task.WhenAll(tasks);
    }

    private async Task EnrichDownloadPresentationAsync(
        int modId,
        NexusModSearchRowViewModel? source,
        CancellationToken cancellationToken)
    {
        try
        {
            var name = source?.Name;
            var coverPath = source?.ThumbnailPath ?? modCoverCacheService.GetCachedCoverPath(modId);
            var remoteThumbnailUrl = source?.RemoteThumbnailUrl;
            if (string.IsNullOrWhiteSpace(remoteThumbnailUrl))
            {
                var search = await nexusModService.SearchModsAsync(
                    new NexusModSearchRequest("kingdomcomedeliverance2", modId.ToString(CultureInfo.InvariantCulture), 0, 10),
                    cancellationToken);
                var summary = search.Mods.FirstOrDefault(item => item.ModId == modId);
                name = summary?.Name ?? name;
                remoteThumbnailUrl = summary?.ThumbnailUrl;
            }

            if (!string.IsNullOrWhiteSpace(remoteThumbnailUrl))
            {
                await modCoverCacheThrottle.WaitAsync(cancellationToken);
                try
                {
                    var refreshedCoverPath = await modCoverCacheService.CacheCoverAsync(
                        modId,
                        remoteThumbnailUrl,
                        cancellationToken);
                    if (!string.IsNullOrWhiteSpace(refreshedCoverPath))
                    {
                        coverPath = refreshedCoverPath;
                    }
                }
                finally
                {
                    modCoverCacheThrottle.Release();
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var row in ModDownloadQueueRows.Where(item => item.ModId == modId))
                {
                    row.SetModPresentation(name, coverPath, activateVisualResources: false);
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or NexusModsException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var row in ModDownloadQueueRows.Where(item => item.ModId == modId))
                {
                    ApplyCachedDownloadPresentation(row);
                }
            });
        }
    }

    private void RefreshModSearchDownloadStates()
    {
        foreach (var row in NexusModSearchRows)
        {
            row.ApplyDownloadState(IsModSearchRowDownloaded(row), T);
        }

        RefreshModRecommendationDownloadStates();
    }

    private void RefreshModRecommendationDownloadStates()
    {
        foreach (var row in ModRecommendationRows)
        {
            row.ApplyDownloadState(IsModSearchRowDownloaded(row.Mod), T);
        }
    }

    private bool IsModSearchRowDownloaded(NexusModSearchRowViewModel row)
    {
        return row.IsSteamWorkshop
            ? IsSteamWorkshopModDownloaded(row.PublishedFileId)
            : IsNexusModDownloaded(row.ModId);
    }

    private bool IsNexusModDownloaded(int modId)
    {
        if (modId <= 0)
        {
            return false;
        }

        return modDownloader.Queue.Any(item => item.Status == ModDownloadStatus.Completed && item.Request.ModId == modId)
            || ModDownloadQueueRows.Any(row => row.Status == ModDownloadStatus.Completed && row.ModId == modId)
            || currentModManifests.Any(mod => IsNexusManifestMatch(mod, modId));
    }

    private bool IsSteamWorkshopModDownloaded(ulong publishedFileId)
    {
        if (publishedFileId == 0)
        {
            return false;
        }

        var idText = publishedFileId.ToString(CultureInfo.InvariantCulture);
        return currentModManifests.Any(mod => IsSteamWorkshopManifestMatch(mod, idText));
    }

    private static bool IsNexusManifestMatch(ModManifest mod, int modId)
    {
        var idText = modId.ToString(CultureInfo.InvariantCulture);
        return IsDownloadedModKeyMatch(mod.Id, idText, "nexus", "nexusmods")
            || IsDownloadedModKeyMatch(mod.DisplayName, idText, "nexus", "nexusmods")
            || IsDownloadedModKeyMatch(Path.GetFileName(mod.RootPath), idText, "nexus", "nexusmods");
    }

    private static bool IsSteamWorkshopManifestMatch(ModManifest mod, string idText)
    {
        return IsDownloadedModKeyMatch(mod.Id, idText, "steam", "workshop")
            || IsDownloadedModKeyMatch(mod.DisplayName, idText, "steam", "workshop")
            || IsDownloadedModKeyMatch(Path.GetFileName(mod.RootPath), idText, "steam", "workshop");
    }

    private static bool IsDownloadedModKeyMatch(string? value, string idText, params string[] prefixes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (string.Equals(normalized, idText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return prefixes.Any(prefix =>
            normalized.Equals($"{prefix}-{idText}", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith($"{prefix}-{idText}-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith($"{prefix}_{idText}_", StringComparison.OrdinalIgnoreCase));
    }

    private string FormatNexusError(NexusModsException ex)
    {
        if (ex.IsAuthenticationFailure)
        {
            return T("NexusAuthFailed");
        }

        if (ex.IsRateLimited)
        {
            return ex.RetryAfter is null
                ? T("NexusRateLimited")
                : string.Format(T("NexusRateLimitedRetry"), Math.Ceiling(ex.RetryAfter.Value.TotalSeconds));
        }

        return string.Format(T("NexusRequestFailed"), ex.Message);
    }

    private string FormatWorkshopError(WorkshopException ex)
    {
        return ex.FailureKind switch
        {
            WorkshopFailureKind.SteamUnavailable => string.Format(T("SteamWorkshopUnavailable"), ex.Message),
            WorkshopFailureKind.NotLoggedIn => string.Format(T("SteamWorkshopLoginRequired"), ex.Message),
            WorkshopFailureKind.GameNotOwned => string.Format(T("SteamWorkshopOwnershipRequired"), ex.Message),
            WorkshopFailureKind.DownloadTimeout => string.Format(T("SteamWorkshopDownloadTimedOut"), ex.Message),
            WorkshopFailureKind.SubscriptionFailed => string.Format(T("SteamWorkshopSubscriptionFailed"), ex.Message),
            WorkshopFailureKind.DeploymentFailed => string.Format(T("SteamWorkshopDeploymentFailed"), ex.Message),
            _ => string.Format(T("SteamWorkshopRequestFailed"), ex.Message)
        };
    }

    private string FormatWorkshopInstallProgress(NexusModSearchRowViewModel mod, WorkshopInstallProgress progress)
    {
        var progressText = progress.Stage switch
        {
            WorkshopInstallStage.ResolvingDependencies => T("WorkshopProgressResolvingDependencies"),
            WorkshopInstallStage.Subscribing => T("WorkshopProgressSubscribing"),
            WorkshopInstallStage.WaitingForSteamDownload => T("WorkshopProgressWaitingForSteamDownload"),
            WorkshopInstallStage.Deploying => T("WorkshopProgressDeploying"),
            WorkshopInstallStage.Completed => T("WorkshopProgressCompleted"),
            _ => progress.Message
        };

        return $"{mod.Name}: {progressText}";
    }

    private void RefreshNexusResultsState()
    {
        HasNexusModSearchResults = NexusModSearchRows.Count > 0;
        IsNexusModSearchEmpty = !HasNexusModSearchResults;
    }

    private void RefreshDownloadQueueState()
    {
        HasModDownloadQueueItems = ModDownloadQueueRows.Count > 0;
        IsModDownloadQueueEmpty = !HasModDownloadQueueItems;
        EnsureSelectedDownloadQueueItem();
        RefreshDownloadLauncherVisibility();
        SyncVisibleDownloadQueueRows();
        SyncDownloadQueuePartitions();
        SyncDownloadQueueGroups();
    }

    private void EnsureSelectedDownloadQueueItem()
    {
        if (SelectedDownloadQueueItem is not null && ModDownloadQueueRows.Contains(SelectedDownloadQueueItem))
        {
            return;
        }

        SelectedDownloadQueueItem = ModDownloadQueueRows.FirstOrDefault(row => row.Status == ModDownloadStatus.Downloading)
            ?? ModDownloadQueueRows.FirstOrDefault(row => row.Status == ModDownloadStatus.Resolving)
            ?? ModDownloadQueueRows.FirstOrDefault(row => row.Status == ModDownloadStatus.Pending)
            ?? ModDownloadQueueRows.FirstOrDefault(row => row.Status == ModDownloadStatus.Paused)
            ?? ModDownloadQueueRows.FirstOrDefault(row => row.Status is ModDownloadStatus.Failed or ModDownloadStatus.ChecksumFailed)
            ?? ModDownloadQueueRows.LastOrDefault();
    }

    private void RefreshDownloadLauncherVisibility()
    {
        var hasActionableDownload = ModDownloadQueueRows.Any(row => ModDownloadQueuePolicy.IsActionable(row.Status));

        IsDownloadLauncherVisible = hasActionableDownload && !IsDownloadCenterVisible;
    }

    private void SyncVisibleDownloadQueueRows()
    {
        var activeCount = ModDownloadQueueRows.Count(row => ModDownloadQueuePolicy.IsActionable(row.Status));
        var downloadedCount = ModDownloadQueueRows.Count(row => row.Status == ModDownloadStatus.Completed);
        ActiveModDownloadQueueCountText = activeCount.ToString(CultureInfo.InvariantCulture);
        DownloadedModDownloadQueueCountText = downloadedCount.ToString(CultureInfo.InvariantCulture);
        TotalModDownloadQueueCountText = ModDownloadQueueRows.Count.ToString(CultureInfo.InvariantCulture);
        RefreshDownloadQueueSummary(activeCount, downloadedCount);

        var isDownloadedSection = string.Equals(
            SelectedModDownloadQueueSection,
            DownloadedQueueSection,
            StringComparison.OrdinalIgnoreCase);

        IsDownloadedQueueSectionActive = isDownloadedSection;
        IsDownloadingQueueSectionActive = !isDownloadedSection;

        var rows = isDownloadedSection
            ? ModDownloadQueueRows.Where(row => row.Status == ModDownloadStatus.Completed)
            : ModDownloadQueueRows.Where(row => row.Status != ModDownloadStatus.Completed);

        ReplaceRows(VisibleModDownloadQueueRows, rows);
        HasVisibleModDownloadQueueItems = VisibleModDownloadQueueRows.Count > 0;
        IsVisibleModDownloadQueueEmpty = !HasVisibleModDownloadQueueItems;
        VisibleDownloadQueueEmptyText = isDownloadedSection
            ? CompletedDownloadQueueEmptyText
            : ActiveDownloadQueueEmptyText;
        VisibleDownloadQueueSectionTitleText = isDownloadedSection
            ? DownloadedQueueText
            : DownloadingQueueText;
        VisibleDownloadQueueSectionDescriptionText = isDownloadedSection
            ? CompletedDownloadQueueDescriptionText
            : ActiveDownloadQueueDescriptionText;
        SyncDownloadQueuePartitionCollection(VisibleModDownloadQueueRows, VisibleModDownloadQueueGroups);
    }

    private void SyncDownloadQueueGroups()
    {
        CanStartModDownloadQueue = ModDownloadQueueRows.Any(row => ModDownloadQueuePolicy.CanStart(row.Status));
        CanPauseModDownloadQueue = ModDownloadQueueRows.Any(row => ModDownloadQueuePolicy.CanPause(row.Status));
        CanResumeModDownloadQueue = ModDownloadQueueRows.Any(row => row.Status == ModDownloadStatus.Paused);
        CanCancelModDownloadQueue = ModDownloadQueueRows.Any(row => ModDownloadQueuePolicy.CanCancel(row.Status));
        CanClearModDownloadQueue = ModDownloadQueueRows.Any(row => ModDownloadQueuePolicy.CanClear(row.Status));
        CanOpenModDownloadDirectory = true;
    }

    private void SyncDownloadQueuePartitions()
    {
        SyncDownloadQueuePartitionCollection(ModDownloadQueueRows, ModDownloadQueueGroups);
    }

    private void SyncDownloadQueuePartitionCollection(
        IReadOnlyList<ModDownloadQueueRowViewModel> rows,
        ObservableCollection<ModDownloadQueueGroupViewModel> targetGroups)
    {
        var partitions = rows
            .Select((row, index) => new { Row = row, Index = index })
            .GroupBy(value => ModDownloadQueueGroupViewModel.GetGroupKey(value.Row), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Rows = (IReadOnlyList<ModDownloadQueueRowViewModel>)group
                    .OrderBy(value => value.Index)
                    .Select(value => value.Row)
                    .ToArray(),
                FirstIndex = group.Min(value => value.Index),
                HasActiveItem = group.Any(value => ModDownloadQueuePolicy.IsActive(value.Row.Status)),
                IsStandalone = group.Key.Equals(
                    ModDownloadQueueGroupViewModel.StandaloneGroupKey,
                    StringComparison.OrdinalIgnoreCase)
            })
            .OrderBy(partition => partition.HasActiveItem ? 0 : 1)
            .ThenBy(partition => partition.IsStandalone ? 1 : 0)
            .ThenBy(partition => partition.FirstIndex)
            .ToArray();

        var activeKeys = partitions
            .Select(partition => partition.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = targetGroups.Count - 1; index >= 0; index--)
        {
            if (!activeKeys.Contains(targetGroups[index].GroupKey))
            {
                targetGroups.RemoveAt(index);
            }
        }

        for (var index = 0; index < partitions.Length; index++)
        {
            var partition = partitions[index];
            var group = targetGroups.FirstOrDefault(value =>
                value.GroupKey.Equals(partition.Key, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                group = new ModDownloadQueueGroupViewModel(
                    partition.Key,
                    row => SelectedDownloadQueueItem = row);
                targetGroups.Insert(index, group);
            }
            else
            {
                var currentIndex = targetGroups.IndexOf(group);
                if (currentIndex != index)
                {
                    targetGroups.Move(currentIndex, index);
                }
            }

            group.Update(partition.Rows, T);
            group.SyncSelectedItem(SelectedDownloadQueueItem);
        }
    }

    private void RefreshDownloadQueueSummary(int activeCount, int downloadedCount)
    {
        var totalCount = ModDownloadQueueRows.Count;
        if (totalCount == 0)
        {
            ModDownloadQueueSummaryText = T("ModDownloadQueueSummaryEmpty");
            ModDownloadQueueThroughputText = "-";
            ModDownloadQueueSizeText = "-";
            return;
        }

        var knownTotalBytes = ModDownloadQueueRows
            .Where(row => row.TotalBytes is > 0)
            .Sum(row => row.TotalBytes ?? 0);
        var downloadedBytes = ModDownloadQueueRows.Sum(row => Math.Max(0, row.BytesDownloaded));
        var totalSpeed = ModDownloadQueueRows
            .Where(row => row.Status == ModDownloadStatus.Downloading)
            .Sum(row => Math.Max(0, row.BytesPerSecond));
        var percent = knownTotalBytes > 0
            ? Math.Clamp(downloadedBytes * 100d / knownTotalBytes, 0, 100)
            : 0;

        ModDownloadQueueSummaryText = string.Format(
            T("ModDownloadQueueSummary"),
            activeCount,
            downloadedCount,
            percent.ToString("0", CultureInfo.InvariantCulture));
        ModDownloadQueueThroughputText = totalSpeed > 0
            ? string.Format(T("ModDownloadQueueSpeed"), FormatSize((long)totalSpeed))
            : T("ModDownloadQueueSpeedIdle");
        ModDownloadQueueSizeText = knownTotalBytes > 0
            ? string.Format(T("ModDownloadQueueSize"), FormatSize(downloadedBytes), FormatSize(knownTotalBytes))
            : T("ModDownloadQueueSizeUnknown");
    }

    private static string FormatSize(long sizeInBytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)Math.Max(0, sizeInBytes);
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size.ToString("0", CultureInfo.InvariantCulture)} {units[unitIndex]}"
            : $"{size.ToString("0.0", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private async Task RefreshModsAsync()
    {
        await EnsureBuiltInTrackerModAsync();

        var snapshot = await modManagementSnapshotService.LoadSnapshotAsync();
        snapshot = await RepairLegacyNexusModMetadataAsync(snapshot);
        snapshot = await EnrichModPackSourceNamesAsync(snapshot);
        ApplyModSnapshot(snapshot);
        RefreshInstalledModCovers();
        VfsStateText = LocalizeVfsState(vfsSessionService.CurrentState);

        Sections[2].Detail = snapshot.Mods.Count == 0
            ? string.Format(T("NoModsRegistered"), VfsStateText)
            : string.Format(T("LoadedModsSummary"), snapshot.Mods.Count, snapshot.ConflictCount, VfsStateText);

        ModCatalogStatusText = Sections[2].Detail;
        LastActionText = Sections[2].Detail;
    }

    private void RefreshInstalledModCovers()
    {
        installedModCoverGeneration++;
        installedModCoverCancellation?.Cancel();
        installedModCoverCancellation?.Dispose();
        installedModCoverCancellation = null;

        var rows = ModRows
            .Where(row => row.NexusModId is > 0 && string.IsNullOrWhiteSpace(row.CoverImagePath))
            .ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        installedModCoverCancellation = CancellationTokenSource.CreateLinkedTokenSource(downloadCoverCancellation.Token);
        _ = CacheInstalledModCoversAsync(rows, installedModCoverGeneration, installedModCoverCancellation.Token);
    }

    private async Task CacheInstalledModCoversAsync(
        IReadOnlyList<ModListItemViewModel> rows,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(rows.Select(row => CacheInstalledModCoverAsync(row, generation, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CacheInstalledModCoverAsync(
        ModListItemViewModel row,
        long generation,
        CancellationToken cancellationToken)
    {
        if (row.NexusModId is not { } modId)
        {
            return;
        }

        try
        {
            var cachedPath = modCoverCacheService.GetCachedCoverPath(modId);
            if (cachedPath is null)
            {
                await modCoverCacheThrottle.WaitAsync(cancellationToken);
                try
                {
                    var search = await nexusModService.SearchModsAsync(
                        new NexusModSearchRequest("kingdomcomedeliverance2", modId.ToString(CultureInfo.InvariantCulture), 0, 10),
                        cancellationToken);
                    var thumbnailUrl = search.Mods.FirstOrDefault(mod => mod.ModId == modId)?.ThumbnailUrl;
                    if (string.IsNullOrWhiteSpace(thumbnailUrl))
                    {
                        var details = await nexusModService.GetModDetailsAsync(
                            "kingdomcomedeliverance2",
                            modId,
                            cancellationToken);
                        thumbnailUrl = details.ThumbnailUrl;
                    }

                    if (string.IsNullOrWhiteSpace(thumbnailUrl))
                    {
                        return;
                    }

                    cachedPath = await modCoverCacheService.CacheCoverAsync(modId, thumbnailUrl, cancellationToken);
                }
                finally
                {
                    modCoverCacheThrottle.Release();
                }
            }

            if (string.IsNullOrWhiteSpace(cachedPath))
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (generation != installedModCoverGeneration
                        || cancellationToken.IsCancellationRequested
                        || !ModRows.Contains(row))
                    {
                        return;
                    }

                    row.SetCoverImagePath(cachedPath);
                },
                DispatcherPriority.Background,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is NexusModsException
                                   or HttpRequestException
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
        }
    }

    private async Task<ModManagementSnapshot> EnrichModPackSourceNamesAsync(ModManagementSnapshot snapshot)
    {
        var missingNames = snapshot.Mods.Any(mod =>
            !string.IsNullOrWhiteSpace(mod.Source?.ModPackId)
            && string.IsNullOrWhiteSpace(mod.Source.ModPackName));
        if (!missingNames)
        {
            return snapshot;
        }

        try
        {
            if (!hasLoadedModPackCatalog)
            {
                var catalog = await modPackCatalogService.GetCatalogAsync(refreshRemote: false);
                currentModPackCatalog = catalog.Entries;
                hasLoadedModPackCatalog = true;
            }

            var namesById = currentModPackCatalog
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.OrdinalIgnoreCase);
            var enrichedMods = snapshot.Mods
                .Select(mod => mod.Source is { ModPackId: { Length: > 0 } modPackId } source
                    && string.IsNullOrWhiteSpace(source.ModPackName)
                    && namesById.TryGetValue(modPackId, out var title)
                    ? mod with { Source = source with { ModPackName = title } }
                    : mod)
                .ToArray();

            return snapshot with { Mods = enrichedMods };
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or HttpRequestException
                                   or InvalidOperationException)
        {
            return snapshot;
        }
    }

    private async Task<ModManagementSnapshot> RepairLegacyNexusModMetadataAsync(ModManagementSnapshot snapshot)
    {
        var changed = false;
        foreach (var mod in snapshot.Mods.Where(mod =>
                     string.Equals(mod.Version, "nexus", StringComparison.OrdinalIgnoreCase)
                     && TryGetNexusModId(mod.Id, out _)))
        {
            if (!TryGetNexusModId(mod.Id, out var modId))
            {
                continue;
            }

            try
            {
                var details = await nexusModService.GetModDetailsAsync("kingdomcomedeliverance2", modId);
                if (string.IsNullOrWhiteSpace(details.Name))
                {
                    continue;
                }

                await modCatalogService.UpdateModMetadataAsync(
                    mod,
                    details.Name,
                    string.IsNullOrWhiteSpace(details.Version) ? mod.Version : details.Version);
                changed = true;
            }
            catch (Exception ex) when (ex is NexusModsException or HttpRequestException or IOException or InvalidOperationException)
            {
                // Leave the local metadata unchanged when Nexus cannot be reached.
            }
        }

        return changed
            ? await modManagementSnapshotService.LoadSnapshotAsync()
            : snapshot;
    }

    private static bool TryGetNexusModId(string modId, out int nexusModId)
    {
        const string nexusPrefix = "nexus-";
        nexusModId = 0;
        return modId.StartsWith(nexusPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(modId[nexusPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out nexusModId);
    }

    private void ApplyModSnapshot(ModManagementSnapshot snapshot)
    {
        ApplyModCatalog(snapshot.Mods, snapshot.Conflicts, snapshot.ConflictReviews);
    }

    private void ApplyModCatalog(
        IReadOnlyList<ModManifest> mods,
        IReadOnlyList<ModConflict> conflicts,
        IReadOnlyDictionary<string, ModConflictReview>? conflictReviews = null)
    {
        currentModManifests = mods.ToArray();
        currentModConflicts = conflicts.ToArray();
        currentModConflictReviews = conflictReviews;
        var previousSelectionId = SelectedMod?.Id;
        var pendingConflicts = conflicts
            .Where(conflict => !IsConflictReviewed(conflict, conflictReviews))
            .ToArray();
        var handledConflicts = conflicts
            .Where(conflict => IsConflictReviewed(conflict, conflictReviews))
            .ToArray();
        var winningPathCounts = pendingConflicts
            .GroupBy(conflict => conflict.WinningModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var overriddenPathCounts = pendingConflicts
            .SelectMany(conflict => conflict.ModIds
                .Where(modId => !string.Equals(modId, conflict.WinningModId, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(modId => modId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var conflictsByMod = pendingConflicts
            .SelectMany(conflict => conflict.ModIds.Select(modId => new
            {
                ModId = modId,
                conflict.NormalizedVirtualPath
            }))
            .GroupBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.NormalizedVirtualPath).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var handledConflictsByMod = handledConflicts
            .SelectMany(conflict => conflict.ModIds.Select(modId => new
            {
                ModId = modId,
                conflict.NormalizedVirtualPath
            }))
            .GroupBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.NormalizedVirtualPath).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var dataDirectory = applicationPathService.GetPaths().DataDirectory;
        DashboardFeaturedMod = null;
        foreach (var existingRow in ModRows)
        {
            existingRow.Dispose();
        }
        ModRows.Clear();
        InstalledModGroups.Clear();
        foreach (var mod in mods.OrderBy(mod => mod.LoadOrder).ThenBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ModListItemViewModel(mod, dataDirectory);
            // Conflict / override lines are hidden by default (IsConflicted/HasOverrides stay false),
            // so healthy rows stay clean. They only surface when there is something to report.
            if (!row.IsEnabled)
            {
                row.ConflictSummary = T("DisabledExcludedFromVfsChecks");
                row.IsConflicted = true;
            }
            else if (conflictsByMod.TryGetValue(mod.Id, out var modConflicts))
            {
                winningPathCounts.TryGetValue(mod.Id, out var wins);
                overriddenPathCounts.TryGetValue(mod.Id, out var overridden);
                row.HasOverrides = true;
                row.IsConflicted = overridden > 0;
                if (overridden > 0)
                {
                    row.ConflictSummary = string.Format(T("ConflictPathSummary"), modConflicts.Length, string.Join(", ", modConflicts.Take(3)));
                    row.ConflictOutcomeSummary = string.Format(T("ConflictOutcomeSummary"), wins, overridden);
                }
                else
                {
                    row.ConflictSummary = string.Format(T("ConflictResolvedPathSummary"), modConflicts.Length);
                    row.ConflictOutcomeSummary = string.Format(T("ConflictResolvedOutcomeSummary"), wins);
                }
            }
            else if (handledConflictsByMod.TryGetValue(mod.Id, out var handledModConflicts))
            {
                row.HasOverrides = true;
                row.IsConflicted = false;
                row.ConflictSummary = string.Format(T("ConflictResolvedPathSummary"), handledModConflicts.Length);
                row.ConflictOutcomeSummary = string.Empty;
            }

            ModRows.Add(row);
        }

        SyncInstalledModGroups();

        DashboardFeaturedMod = ModRows.FirstOrDefault(row =>
            !row.IsBuiltIn && !string.IsNullOrWhiteSpace(row.CoverImagePath));

        ModConflictRows.Clear();
        foreach (var conflict in conflicts)
        {
            ModConflictReview? review = null;
            conflictReviews?.TryGetValue(conflict.Fingerprint, out review);
            ModConflictRows.Add(new ModConflictRowViewModel(conflict, review));
        }

        DashboardConflictRows.Clear();
        foreach (var row in SelectDashboardConflictRows(ModConflictRows))
        {
            DashboardConflictRows.Add(row);
        }

        ModCount = ModRows.Count;
        EnabledModCount = ModRows.Count(row => row.IsEnabled);
        DisabledModCount = ModRows.Count - EnabledModCount;
        ConflictCount = ModConflictRows.Count(row => !row.IsReviewed);
        HasMods = ModRows.Count > 0;
        HasModConflicts = ConflictCount > 0;
        DashboardModSummaryText = string.Format(
            T("DashboardModSummary"),
            EnabledModCount,
            ConflictCount);
        SelectedMod = ModRows.FirstOrDefault(row => string.Equals(row.Id, previousSelectionId, StringComparison.OrdinalIgnoreCase))
            ?? ModRows.FirstOrDefault();
        foreach (var row in ModRows)
        {
            row.IsSelected = ReferenceEquals(row, SelectedMod);
        }

        RefreshModSearchDownloadStates();
        StartInstalledModRequirementsRefresh();
    }

    private void StartInstalledModRequirementsRefresh()
    {
        installedModRequirementsCancellation?.Cancel();
        installedModRequirementsCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        installedModRequirementsCancellation = cancellation;
        var rows = ModRows.Where(row => row.NexusModId is > 0).ToArray();
        var installedMods = currentModManifests.ToArray();
        _ = RefreshInstalledModRequirementsAsync(rows, installedMods, cancellation);
    }

    private async Task RefreshInstalledModRequirementsAsync(
        IReadOnlyList<ModListItemViewModel> rows,
        IReadOnlyList<ModManifest> installedMods,
        CancellationTokenSource cancellation)
    {
        try
        {
            foreach (var row in rows)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var nexusModId = row.NexusModId!.Value;
                IReadOnlyList<NexusModRequirement> requirements;

                if (!installedModRequirementCache.TryGetValue(nexusModId, out requirements!))
                {
                    try
                    {
                        var details = await nexusModService.GetModDetailsAsync(
                            "kingdomcomedeliverance2",
                            nexusModId,
                            cancellation.Token);
                        requirements = details.Requirements;
                        installedModRequirementCache[nexusModId] = requirements;
                    }
                    catch (Exception ex) when (ex is NexusModsException or HttpRequestException or IOException or InvalidOperationException)
                    {
                        // A connectivity or authorization failure must not be shown as a missing dependency.
                        continue;
                    }
                }

                var missingRequirements = requirements
                    .Where(requirement => requirement.ModId != nexusModId)
                    .Where(requirement => !IsInstalledModRequirementSatisfied(requirement, installedMods))
                    .Select(requirement => new NexusModRequirementViewModel(
                        requirement,
                        T("NexusModRequirementSourceNexus"),
                        T("NexusModRequirementSourceManual")))
                    .ToArray();
                var summary = string.Format(
                    T("InstalledModRequirementsMissingSummary"),
                    missingRequirements.Length);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(installedModRequirementsCancellation, cancellation)
                        && ModRows.Contains(row))
                    {
                        row.ApplyMissingRequirements(missingRequirements, summary);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer catalog snapshot superseded this dependency check.
        }
    }

    private static bool IsInstalledModRequirementSatisfied(
        NexusModRequirement requirement,
        IReadOnlyList<ModManifest> installedMods)
    {
        if (!requirement.IsExternal && requirement.ModId is { } nexusModId && nexusModId > 0)
        {
            return installedMods.Any(mod =>
                mod.Source?.NexusModId == nexusModId || IsNexusManifestMatch(mod, nexusModId));
        }

        if (string.IsNullOrWhiteSpace(requirement.ModName))
        {
            return false;
        }

        return installedMods.Any(mod =>
            string.Equals(mod.DisplayName, requirement.ModName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mod.Id, requirement.ModName, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void OpenModGroupNameEditor(InstalledModGroupViewModel? group)
    {
        if (group is null || IsModGroupNameSaving)
        {
            return;
        }

        CloseModGroupNameEditor();
        editingInstalledModGroupKey = group.GroupKey;
        editingInstalledModGroup = group;
        group.IsNameEditing = true;
        ModGroupNameDraft = group.TitleText;
        SaveModGroupNameCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CancelModGroupNameEditor()
    {
        if (IsModGroupNameSaving)
        {
            return;
        }

        CloseModGroupNameEditor();
    }

    private bool CanSaveModGroupName() =>
        editingInstalledModGroupKey is not null && !IsModGroupNameSaving;

    [RelayCommand(CanExecute = nameof(CanSaveModGroupName))]
    private async Task SaveModGroupNameAsync()
    {
        var customName = InstalledModGroupViewModel.NormalizeCustomName(ModGroupNameDraft);
        if (editingInstalledModGroupKey is null)
        {
            return;
        }

        if (InstalledModGroupViewModel.IsCustomGroupKey(editingInstalledModGroupKey)
            && customName is null)
        {
            StatusText = T("ModGroupNameRequired");
            LastActionText = StatusText;
            return;
        }

        if (customName is not null
            && installedModGroupNames.Any(pair =>
                !string.Equals(pair.Key, editingInstalledModGroupKey, StringComparison.OrdinalIgnoreCase)
                && InstalledModGroupViewModel.IsCustomGroupKey(pair.Key)
                && string.Equals(pair.Value, customName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = string.Format(T("ModGroupNameExists"), customName);
            LastActionText = StatusText;
            return;
        }

        await PersistModGroupNameAsync(
            editingInstalledModGroupKey,
            customName,
            customName is null
                ? T("ModGroupNameReset")
                : string.Format(T("ModGroupNameSaved"), customName));
    }

    [RelayCommand]
    private async Task ResetModGroupNameAsync()
    {
        if (editingInstalledModGroupKey is null
            || IsModGroupNameSaving
            || InstalledModGroupViewModel.IsCustomGroupKey(editingInstalledModGroupKey))
        {
            return;
        }

        await PersistModGroupNameAsync(
            editingInstalledModGroupKey,
            customName: null,
            T("ModGroupNameReset"));
    }

    private async Task PersistModGroupNameAsync(string groupKey, string? customName, string successText)
    {
        var hadPreviousName = installedModGroupNames.TryGetValue(groupKey, out var previousName);
        if (customName is null)
        {
            installedModGroupNames.Remove(groupKey);
        }
        else
        {
            installedModGroupNames[groupKey] = customName;
        }

        IsModGroupNameSaving = true;
        try
        {
            await SaveCurrentSettingsAsync();
            SyncInstalledModGroups();
            StatusText = successText;
            LastActionText = successText;
            CloseModGroupNameEditor();
        }
        catch (Exception ex)
        {
            if (hadPreviousName)
            {
                installedModGroupNames[groupKey] = previousName!;
            }
            else
            {
                installedModGroupNames.Remove(groupKey);
            }

            StatusText = string.Format(T("ModGroupNameSaveFailed"), ex.Message);
            LastActionText = StatusText;
        }
        finally
        {
            IsModGroupNameSaving = false;
        }
    }

    private void CloseModGroupNameEditor()
    {
        if (editingInstalledModGroup is not null)
        {
            editingInstalledModGroup.IsNameEditing = false;
            editingInstalledModGroup = null;
        }

        editingInstalledModGroupKey = null;
        ModGroupNameDraft = string.Empty;
        SaveModGroupNameCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task CreateModGroupAsync()
    {
        if (IsModGroupNameSaving)
        {
            return;
        }

        var groupKey = InstalledModGroupViewModel.CreateCustomGroupKey();
        var groupName = CreateUniqueModGroupName();
        installedModGroupNames[groupKey] = groupName;
        IsModGroupNameSaving = true;
        try
        {
            await SaveCurrentSettingsAsync();
            IsModGroupNameSaving = false;
            SyncInstalledModGroups();
            var group = InstalledModGroups.First(value =>
                string.Equals(value.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase));
            OpenModGroupNameEditor(group);
            StatusText = string.Format(T("ModGroupCreated"), groupName);
            LastActionText = StatusText;
        }
        catch (Exception ex)
        {
            installedModGroupNames.Remove(groupKey);
            SyncInstalledModGroups();
            StatusText = string.Format(T("ModGroupCreateFailed"), ex.Message);
            LastActionText = StatusText;
        }
        finally
        {
            IsModGroupNameSaving = false;
        }
    }

    [RelayCommand]
    private async Task DeleteModGroupAsync(InstalledModGroupViewModel? group)
    {
        if (group?.IsCustom != true
            || IsModGroupNameSaving
            || !installedModGroupNames.TryGetValue(group.GroupKey, out var previousName))
        {
            return;
        }

        var previousAssignments = installedModGroupAssignments
            .Where(pair => string.Equals(pair.Value, group.GroupKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        installedModGroupNames.Remove(group.GroupKey);
        foreach (var assignment in previousAssignments)
        {
            installedModGroupAssignments.Remove(assignment.Key);
        }

        IsModGroupNameSaving = true;
        try
        {
            await SaveCurrentSettingsAsync();
            SyncInstalledModGroups();
            StatusText = string.Format(T("ModGroupDeleted"), previousName);
            LastActionText = StatusText;
        }
        catch (Exception ex)
        {
            installedModGroupNames[group.GroupKey] = previousName;
            foreach (var assignment in previousAssignments)
            {
                installedModGroupAssignments[assignment.Key] = assignment.Value;
            }

            SyncInstalledModGroups();
            StatusText = string.Format(T("ModGroupDeleteFailed"), ex.Message);
            LastActionText = StatusText;
        }
        finally
        {
            IsModGroupNameSaving = false;
        }
    }

    private bool CanMoveSelectedModToGroup()
    {
        if (SelectedMod is null || SelectedModGroupOption is null || IsModGroupNameSaving)
        {
            return false;
        }

        installedModGroupAssignments.TryGetValue(SelectedMod.Id, out var currentGroupKey);
        return !string.Equals(
            currentGroupKey,
            SelectedModGroupOption.GroupKey,
            StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedModToGroup))]
    private async Task MoveSelectedModToGroupAsync()
    {
        if (SelectedMod is null || SelectedModGroupOption is null)
        {
            return;
        }

        var mod = SelectedMod;
        var target = SelectedModGroupOption;
        await AssignModToGroupAsync(
            mod,
            target.IsAutomatic ? null : target.GroupKey,
            target.TitleText);
    }

    [RelayCommand]
    private async Task AddModToGroupAsync(ModGroupAssignmentRequest? request)
    {
        if (request?.Mod is null
            || request.Group?.IsCustom != true
            || IsModGroupNameSaving)
        {
            return;
        }

        await AssignModToGroupAsync(request.Mod, request.Group.GroupKey, request.Group.TitleText);
    }

    [RelayCommand]
    private async Task MoveModToGroupAsync(ModGroupAssignmentRequest? request)
    {
        if (request?.Mod is null
            || request.Group is null
            || IsModGroupNameSaving
            || !request.Group.CanAcceptDrop(request.Mod))
        {
            return;
        }

        await AssignModToGroupAsync(
            request.Mod,
            request.Group.IsCustom ? request.Group.GroupKey : null,
            request.Group.IsCustom ? request.Group.TitleText : AutomaticModGroupText);
    }

    [RelayCommand]
    private async Task ReturnModToAutomaticGroupAsync(ModListItemViewModel? mod)
    {
        if (mod is null
            || IsModGroupNameSaving
            || !installedModGroupAssignments.TryGetValue(mod.Id, out var currentGroupKey)
            || !InstalledModGroupViewModel.IsCustomGroupKey(currentGroupKey)
            || !installedModGroupNames.ContainsKey(currentGroupKey))
        {
            return;
        }

        await AssignModToGroupAsync(mod, null, AutomaticModGroupText);
    }

    private async Task AssignModToGroupAsync(
        ModListItemViewModel mod,
        string? targetGroupKey,
        string targetTitleText)
    {
        var hadPreviousAssignment = installedModGroupAssignments.TryGetValue(mod.Id, out var previousGroupKey);
        if (targetGroupKey is null)
        {
            installedModGroupAssignments.Remove(mod.Id);
        }
        else
        {
            installedModGroupAssignments[mod.Id] = targetGroupKey;
        }

        IsModGroupNameSaving = true;
        SyncInstalledModGroups();
        try
        {
            await SaveCurrentSettingsAsync();
            StatusText = string.Format(T("ModMovedToGroup"), mod.DisplayName, targetTitleText);
            LastActionText = StatusText;
        }
        catch (Exception ex)
        {
            if (hadPreviousAssignment)
            {
                installedModGroupAssignments[mod.Id] = previousGroupKey!;
            }
            else
            {
                installedModGroupAssignments.Remove(mod.Id);
            }

            SyncInstalledModGroups();
            StatusText = string.Format(T("ModGroupMoveFailed"), ex.Message);
            LastActionText = StatusText;
        }
        finally
        {
            IsModGroupNameSaving = false;
        }
    }

    private string CreateUniqueModGroupName()
    {
        var baseName = T("NewModGroupDefaultName");
        var existingNames = installedModGroupNames
            .Where(pair => InstalledModGroupViewModel.IsCustomGroupKey(pair.Key))
            .Select(pair => pair.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} {index.ToString(CultureInfo.CurrentCulture)}";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void RefreshSelectedModGroupOption(ModListItemViewModel? mod)
    {
        string? assignedGroupKey = null;
        if (mod is not null)
        {
            installedModGroupAssignments.TryGetValue(mod.Id, out assignedGroupKey);
        }

        SelectedModGroupOption = InstalledModGroupOptions.FirstOrDefault(option =>
                string.Equals(option.GroupKey, assignedGroupKey, StringComparison.OrdinalIgnoreCase))
            ?? InstalledModGroupOptions.FirstOrDefault();
    }

    private void SyncInstalledModGroups()
    {
        foreach (var row in ModRows)
        {
            row.IsAssignedToCustomGroup = InstalledModGroupViewModel.IsCustomGroupKey(
                InstalledModGroupViewModel.GetEffectiveGroupKey(
                    row,
                    installedModGroupAssignments,
                    installedModGroupNames));
        }

        var indexedRows = ModRows
            .Select((row, index) => new { Row = row, Index = index })
            .ToArray();
        var groups = indexedRows
            .GroupBy(
                value => InstalledModGroupViewModel.GetEffectiveGroupKey(
                    value.Row,
                    installedModGroupAssignments,
                    installedModGroupNames),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new InstalledModGroupBuildState(
                group.Key,
                InstalledModGroupViewModel.IsCustomGroupKey(group.Key),
                group.Min(value => value.Index),
                (IReadOnlyList<ModListItemViewModel>)group
                    .OrderBy(value => value.Index)
                    .Select(value => value.Row)
                    .ToArray(),
                group.First().Row))
            .ToList();

        foreach (var automaticGroup in indexedRows.GroupBy(
                     value => InstalledModGroupViewModel.GetGroupKey(value.Row),
                     StringComparer.OrdinalIgnoreCase))
        {
            if (groups.All(group => !string.Equals(
                    group.Key,
                    automaticGroup.Key,
                    StringComparison.OrdinalIgnoreCase)))
            {
                groups.Add(new InstalledModGroupBuildState(
                    automaticGroup.Key,
                    false,
                    automaticGroup.Min(value => value.Index),
                    Array.Empty<ModListItemViewModel>(),
                    automaticGroup.First().Row));
            }
        }

        foreach (var groupKey in installedModGroupNames.Keys
                     .Where(InstalledModGroupViewModel.IsCustomGroupKey))
        {
            if (groups.All(group => !string.Equals(group.Key, groupKey, StringComparison.OrdinalIgnoreCase)))
            {
                groups.Add(new InstalledModGroupBuildState(
                    groupKey,
                    true,
                    int.MaxValue,
                    Array.Empty<ModListItemViewModel>(),
                    null));
            }
        }

        var groupViewModels = groups
            .OrderBy(group => group.IsCustom ? 0 : string.Equals(
                group.Key,
                InstalledModGroupViewModel.StandaloneGroupKey,
                StringComparison.OrdinalIgnoreCase) ? 2 : 1)
            .ThenBy(
                group => group.IsCustom ? installedModGroupNames.GetValueOrDefault(group.Key) : null,
                StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.FirstIndex)
            .Select(group => new InstalledModGroupViewModel(
                group.Key,
                group.Rows,
                T,
                installedModGroupNames.GetValueOrDefault(group.Key),
                group.SourceRow))
            .ToArray();

        InstalledModGroups.Clear();
        foreach (var group in groupViewModels)
        {
            InstalledModGroups.Add(group);
        }

        InstalledModGroupOptions.Clear();
        InstalledModGroupOptions.Add(new InstalledModGroupOptionViewModel(null, AutomaticModGroupText));
        foreach (var group in groupViewModels.Where(value => value.IsCustom))
        {
            InstalledModGroupOptions.Add(new InstalledModGroupOptionViewModel(group.GroupKey, group.TitleText));
        }

        RefreshSelectedModGroupOption(SelectedMod);
    }

    internal static IReadOnlyList<ModConflictRowViewModel> SelectDashboardConflictRows(
        IEnumerable<ModConflictRowViewModel> rows)
    {
        return rows
            .Where(row => !row.IsReviewed)
            .Take(3)
            .ToArray();
    }

    private static bool IsConflictReviewed(
        ModConflict conflict,
        IReadOnlyDictionary<string, ModConflictReview>? conflictReviews) =>
        conflictReviews is not null &&
        conflictReviews.TryGetValue(conflict.Fingerprint, out var review) &&
        review.IsReviewed;

    private async Task SaveVisibleModConflictReviewsAsync(bool isReviewed)
    {
        if (ModConflictRows.Count == 0)
        {
            StatusText = T("NoActiveModConflictsToReview");
            LastActionText = StatusText;
            return;
        }

        await modConflictReviewService.SaveReviewsAsync(
            ModConflictRows.ToDictionary(row => row.Fingerprint, _ => isReviewed, StringComparer.OrdinalIgnoreCase));
        await RefreshModsAsync();

        StatusText = isReviewed
            ? $"Marked {ModConflictRows.Count} mod conflict(s) as reviewed."
            : $"Cleared review state for {ModConflictRows.Count} mod conflict(s).";
        LastActionText = StatusText;
        ModCatalogStatusText = StatusText;
    }

    private IReadOnlyList<ModManifest> BuildCurrentModPreviewManifests()
    {
        var manifestsById = currentModManifests
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return ModRows
            .Select((row, index) => manifestsById.TryGetValue(row.Id, out var manifest)
                ? manifest with
                {
                    LoadOrder = index,
                    IsEnabled = row.IsEnabled
                }
                : null)
            .Where(mod => mod is not null)
            .Cast<ModManifest>()
            .ToArray();
    }

    private void RecalculateModConflictPreviewFromRows()
    {
        var previewMods = BuildCurrentModPreviewManifests();
        var conflicts = modConflictAnalyzer.AnalyzeConflicts(previewMods.Where(mod => mod.IsEnabled));

        ApplyModCatalog(previewMods, conflicts);
        VfsStateText = LocalizeVfsState(vfsSessionService.CurrentState);
        ModCatalogStatusText = previewMods.Count == 0
            ? string.Format(T("NoModsRegistered"), VfsStateText)
            : string.Format(T("ModPreviewUpdated"), previewMods.Count, previewMods.Count(mod => mod.IsEnabled), conflicts.Count);
        Sections[2].Detail = ModCatalogStatusText;
    }

    private async Task MoveSelectedModAsync(int offset)
    {
        if (SelectedMod is null)
        {
            StatusText = T("SelectModBeforeChangingLoadOrder");
            LastActionText = StatusText;
            return;
        }

        var currentIndex = ModRows.IndexOf(SelectedMod);
        var nextIndex = currentIndex + offset;
        await MoveSelectedModToIndexAsync(nextIndex);
    }

    private async Task MoveSelectedModToIndexAsync(int targetIndex)
    {
        if (SelectedMod is null)
        {
            StatusText = T("SelectModBeforeChangingLoadOrder");
            LastActionText = StatusText;
            return;
        }

        await MoveModToIndexAsync(SelectedMod, targetIndex);
    }

    internal async Task MoveModFromDropAsync(
        ModListItemViewModel mod,
        int targetIndex,
        InstalledModGroupViewModel targetGroup)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(targetGroup);

        if (ModRows.IndexOf(mod) < 0 || IsModGroupNameSaving)
        {
            return;
        }

        if (targetGroup.Items.All(item => !ReferenceEquals(item, mod)))
        {
            if (!targetGroup.CanAcceptDrop(mod))
            {
                return;
            }

            await AssignModToGroupAsync(
                mod,
                targetGroup.IsCustom ? targetGroup.GroupKey : null,
                targetGroup.IsCustom ? targetGroup.TitleText : AutomaticModGroupText);
        }

        SelectedMod = mod;
        await MoveModToIndexAsync(mod, targetIndex);
    }

    private async Task MoveModToIndexAsync(ModListItemViewModel mod, int targetIndex)
    {
        var plan = modLoadOrderPlanner.MoveToIndex(BuildCurrentModPreviewManifests(), mod.Id, targetIndex);
        if (!plan.Changed)
        {
            return;
        }

        ApplyModRowOrder(plan.OrderedModIds);
        UpdateVisibleLoadOrder();
        await SaveCurrentModLoadOrderAsync();
    }

    private void ApplyModRowOrder(IReadOnlyList<string> orderedModIds)
    {
        var rowsById = ModRows.ToDictionary(row => row.Id, row => row, StringComparer.OrdinalIgnoreCase);
        ModRows.Clear();
        foreach (var modId in orderedModIds)
        {
            if (rowsById.TryGetValue(modId, out var row))
            {
                ModRows.Add(row);
            }
        }

        SyncInstalledModGroups();
    }

    private async Task SetAllModsEnabledAsync(bool isEnabled)
    {
        if (ModRows.Count == 0)
        {
            StatusText = T("NoLocalModsToUpdate");
            LastActionText = StatusText;
            return;
        }

        foreach (var row in ModRows)
        {
            row.IsEnabled = row.IsBuiltIn || isEnabled;
        }

        await SaveCurrentModLoadOrderAsync();
    }

    private async Task SetModGroupEnabledAsync(InstalledModGroupViewModel? group, bool isEnabled)
    {
        if (group is null || group.IsEmpty)
        {
            return;
        }

        group.SetEnabledState(isEnabled);
        await SaveCurrentModLoadOrderAsync();
    }

    private async Task SaveCurrentModLoadOrderAsync()
    {
        if (ModRows.Count == 0)
        {
            StatusText = T("NoLocalModsToOrder");
            LastActionText = StatusText;
            return;
        }

        UpdateVisibleLoadOrder();
        RecalculateModConflictPreviewFromRows();
        await modCatalogService.SaveLoadOrderAsync(ModRows.Select(row => row.Id).ToArray());
        await modCatalogService.SaveModEnabledStatesAsync(
            ModRows.ToDictionary(row => row.Id, row => row.IsBuiltIn || row.IsEnabled, StringComparer.OrdinalIgnoreCase));

        var enabledCount = ModRows.Count(row => row.IsEnabled);
        StatusText = string.Format(T("SavedLoadOrderEnabledState"), ModRows.Count, enabledCount);
        LastActionText = StatusText;
        ModCatalogStatusText = StatusText;
        Sections[2].Detail = $"{StatusText} {string.Format(T("CurrentConflictsDetected"), ConflictCount)}";
    }

    private void UpdateVisibleLoadOrder()
    {
        for (var index = 0; index < ModRows.Count; index++)
        {
            ModRows[index].LoadOrder = index;
        }
    }

    private void ApplyDiscoveredGames(System.Collections.Generic.IReadOnlyList<DiscoveredGame> games)
    {
        var kcd2Games = games
            .Where(game => IsKcd2(game.Name) || IsKcd2(game.InstallPath) || IsKcd2(game.ExecutablePath))
            .ToArray();

        selectedGame = kcd2Games
            .OrderByDescending(game => game.IsVerified)
            .ThenBy(game => game.Source)
            .FirstOrDefault();

        GameCount = kcd2Games.Length;
        VerifiedGameCount = kcd2Games.Count(game => game.IsVerified);

        if (selectedGame is null)
        {
            RefreshLaunchStateText();
            VerificationText = T("Missing");
            Sections[1].Detail = T("Kcd2NotFound");
            return;
        }

        RefreshLaunchStateText();
        Sections[1].Detail = selectedGame.IsVerified
            ? string.Format(T("Kcd2VerifiedAt"), GameTitle, selectedGame.InstallPath)
            : string.Format(T("Kcd2FoundExecutableUnverified"), GameTitle);
    }

    private static bool IsKcd2(string value)
    {
        return value.Contains("Kingdom Come", StringComparison.OrdinalIgnoreCase)
            || value.Contains("KCD2", StringComparison.OrdinalIgnoreCase)
            || value.Contains("KingdomCome", StringComparison.OrdinalIgnoreCase);
    }

    private string GetWorkspaceTitle(string destination)
    {
        return destination switch
        {
            "Home" or "Dashboard" => T("WorkspaceLauncherTitle"),
            "Mods" or "Community" => T("WorkspaceModsTitle"),
            "Saves" => T("WorkspaceSavesTitle"),
            "Downloads" => DownloadQueueText,
            "Store" => T("WorkspaceStoreTitle"),
            "Lab" => T("WorkspaceLabTitle"),
            "Settings" => T("LauncherSettings"),
            _ => T("WorkspaceLauncherTitle")
        };
    }

    private string GetWorkspaceDescription(string destination)
    {
        return destination switch
        {
            "Home" or "Dashboard" => T("WorkspaceLauncherDescription"),
            "Mods" or "Community" => T("WorkspaceModsDescription"),
            "Saves" => T("WorkspaceSavesDescription"),
            "Downloads" => ModDownloadQueueSummaryText,
            "Store" => T("WorkspaceStoreDescription"),
            "Lab" => T("WorkspaceLabDescription"),
            "Settings" => T("WorkspaceSettingsDescription"),
            _ => T("WorkspaceLauncherDescription")
        };
    }

    private void RefreshLaunchStateText()
    {
        IsLaunchReady = selectedGame is { IsVerified: true };
        PrimaryActionLabel = IsLaunchReady ? T("LaunchGame") : T("VerifyInstall");

        if (selectedGame is null)
        {
            Kcd2InstallPath = T("NotScanned");
            Kcd2ExecutablePath = T("NotVerified");
            SourceText = T("Unknown");
            VerificationText = T("ScanRequired");
            return;
        }

        Kcd2InstallPath = string.IsNullOrWhiteSpace(selectedGame.InstallPath)
            ? T("NotScanned")
            : selectedGame.InstallPath;
        Kcd2ExecutablePath = string.IsNullOrWhiteSpace(selectedGame.ExecutablePath)
            ? T("NotVerified")
            : selectedGame.ExecutablePath;
        SourceText = selectedGame.Source.ToString();
        VerificationText = selectedGame.IsVerified ? T("Verified") : T("ExecutableMissing");
    }

    private string LocalizeVfsState(VfsSessionState state)
    {
        return state switch
        {
            VfsSessionState.Mounted => T("VfsMounted"),
            VfsSessionState.Unmounting => T("VfsUnmounting"),
            VfsSessionState.Faulted => T("VfsFaulted"),
            _ => T("VfsIdle")
        };
    }

    private void RefreshSectionDetails()
    {
        Sections[0].Detail = T("SectionLauncherDetail");
        Sections[1].Detail = selectedGame is null
            ? T("SectionInstallationDetail")
            : selectedGame.IsVerified
                ? string.Format(T("Kcd2VerifiedAt"), GameTitle, selectedGame.InstallPath)
                : string.Format(T("Kcd2FoundExecutableUnverified"), GameTitle);
        Sections[2].Detail = ModCount == 0
            ? string.Format(T("NoModsRegistered"), VfsStateText)
            : string.Format(T("LoadedModsSummary"), ModCount, ConflictCount, VfsStateText);
        Sections[3].Detail = T("SectionSavesDetail");
        Sections[4].Detail = T("SectionSettingsDetail");
        Sections[5].Detail = T("SectionLabDetail");
    }

    private void ApplyLanguage()
    {
        AdventureProfile.UseLanguage(SelectedLanguage == EnglishLanguage);
        CreatedAlchemyWorkshop?.UseLanguage(SelectedLanguage == EnglishLanguage);
        CreatedForgeWorkshop?.UseLanguage(SelectedLanguage == EnglishLanguage);
        NavigationHeaderText = T("NavHeader");
        SettingsHeaderText = T("SettingsHeader");
        SettingsPlayerProfilesText = T("PlayerProfiles");
        DashboardNavText = T("Dashboard");
        ModsNavText = T("Mods");
        SavesNavText = T("Saves");
        SettingsNavigationText = T("Navigation");
        SettingsLaunchText = T("Launch");
        SettingsAppearanceText = T("Appearance");
        SettingsGraphicsText = T("Graphics");
        SettingsStorageText = T("Storage");
        SettingsAdvancedText = T("Advanced");
        SettingsMoreText = T("More");
        MoreSettings.ApplyLanguage(SelectedLanguage == EnglishLanguage);
        StoreNavText = T("Store");
        LabNavText = T("Lab");
        LabPlannedFeaturesText = T("LabPlannedFeatures");
        LabPlaceholderText = T("LabPlaceholder");
        LabStatusPrototypeText = T("LabStatusPrototype");
        LabStatusPlannedText = T("LabStatusPlanned");
        LabAlchemyText = T("LabAlchemy");
        LabForgingText = T("LabForging");
        LabAlchemyLongDescription = T("LabAlchemyLongDescription");
        LabForgingLongDescription = T("LabForgingLongDescription");
        ApplyLabLocalization();
        LauncherSettingsTitleText = T("LauncherSettings");
        ResetButtonText = T("Reset");
        ApplyButtonText = T("Apply");
        LanguageSettingText = T("Language");
        LanguageSettingDescriptionText = T("LanguageDescription");
        DefaultPageSettingText = T("DefaultPage");
        DefaultPageSettingDescriptionText = T("DefaultPageDescription");
        TopNavigationTooltipsText = T("TopNavigationTooltips");
        TopNavigationTooltipsDescriptionText = T("TopNavigationTooltipsDescription");
        KeepTopNavigationVisibleText = T("KeepTopNav");
        KeepTopNavigationVisibleDescriptionText = T("KeepTopNavDescription");
        SidebarRuntimeCardText = T("SidebarRuntimeCard");
        SidebarRuntimeCardDescriptionText = T("SidebarRuntimeCardDescription");
        SidebarCollapseText = T("SidebarCollapse");
        SidebarExpandText = T("SidebarExpand");
        SidebarToggleText = T("SidebarToggle");
        ScanOnStartupText = T("ScanOnStartup");
        ScanOnStartupDescriptionText = T("ScanOnStartupDescription");
        PrepareVfsText = T("PrepareVfs");
        PrepareVfsDescriptionText = T("PrepareVfsDescription");
        LaunchArgumentsText = T("LaunchArguments");
        LaunchArgumentsDescriptionText = T("LaunchArgumentsDescription");
        UseSteamProtocolText = T("UseSteamProtocol");
        WindowBehaviorText = T("WindowBehavior");
        WindowBehaviorDescriptionText = T("WindowBehaviorDescription");
        MinimizeText = T("Minimize");
        CloseText = T("Close");
        ScaleText = T("Scale");
        GameGenreText = T("GameGenre");
        DashboardOverviewTitleText = T("DashboardOverviewTitle");
        DashboardRecentSavesText = T("DashboardRecentSaves");
        DashboardNoRecentSavesText = T("DashboardNoRecentSaves");
        DashboardManageSavesText = T("DashboardManageSaves");
        DashboardModStatusText = T("DashboardModStatus");
        DashboardNoConflictsText = T("DashboardNoConflicts");
        DashboardReviewConflictsText = T("DashboardReviewConflicts");
        DashboardManageModsText = T("DashboardManageMods");
        DashboardNoModsText = T("DashboardNoMods");
        DashboardOfficialNewsText = T("DashboardOfficialNews");
        DashboardOpenArticleText = T("DashboardOpenArticle");
        DailyModRecommendationTitleText = T("DailyModRecommendationTitle");
        DailyModRecommendationOpenText = T("DailyModRecommendationOpen");
        DailyModRecommendationBrowseText = T("DailyModRecommendationBrowse");
        DailyModRecommendationDateText = FormatDailyModRecommendationDate(DateTimeOffset.Now);
        if (!HasDailyModRecommendation)
        {
            DailyModRecommendationStatusText = T("DailyModRecommendationConnect");
        }
        DashboardModSummaryText = string.Format(T("DashboardModSummary"), EnabledModCount, ConflictCount);
        LauncherHeroDescriptionText = T("LauncherHeroDescription");
        VerifyInstallText = T("VerifyInstall");
        LauncherActivityTitleText = T("LauncherActivity");
        StatusLabelText = T("Status");
        CurrentLauncherStateText = T("CurrentLauncherState");
        InstallLabelText = T("InstallLabel");
        LastLabelText = T("Last");
        Kcd2InstallTitleText = T("Kcd2Install");
        ExecutableLabelText = T("Executable");
        Kcd2InstallsLabelText = T("Kcd2Installs");
        ConflictsLabelText = T("Conflicts");
        SelectedModConflictHeadingText = SelectedMod is null
            ? T("ConflictStatusHeading")
            : !SelectedMod.IsEnabled
                ? T("ModCheckStatusHeading")
                : SelectedMod.IsConflicted
                    ? T("PendingConflictHeading")
                    : SelectedMod.HasOverrides
                        ? T("HandledOverlapHeading")
                        : T("NoConflictHeading");
        IsSelectedModConflictStatusVisible = SelectedMod is null || !SelectedMod.HasOverrides || SelectedMod.IsConflicted;
        Kcd2RuntimeTitleText = T("Kcd2Runtime");
        InstallSourceLabelText = T("InstallSource");
        VfsStateLabelText = T("VfsState");
        AutoFixSelectedModConflictsText = T("AutoFixSelectedModConflicts");
        AutoFixSelectedModConflictsDescriptionText = T("AutoFixSelectedModConflictsDescription");
        AutoFixSelectedModConflictsHintText = T("AutoFixSelectedModConflictsHint");
        AutoFixAllModConflictsText = T("AutoFixAllModConflicts");
        AutoFixAllModConflictsHintText = T("AutoFixAllModConflictsHint");
        DatabaseLabelText = T("Database");
        QuickActionsTitleText = T("QuickActions");
        QuickLaunchSubtitleText = T("QuickLaunchSubtitle");
        ManageModsText = T("ManageMods");
        SaveMonitorText = T("SaveMonitor");
        OpenSaveToolsText = T("OpenSaveTools");
        RuntimePathsText = T("RuntimePaths");
        VerifyShortText = T("VerifyShort");
        PlayNowText = T("PlayNow");
        Kcd2OnlyText = T("Kcd2Only");
        RuntimeLabelText = T("Runtime");
        ScrollForDetailsText = T("ScrollForDetails");
        SettingsCategorySelectedPrefixText = T("CategorySelected");
        AcrylicGlassEffectsText = T("AcrylicGlassEffects");
        AcrylicGlassEffectsDescriptionText = T("AcrylicGlassEffectsDescription");
        ReduceMotionText = T("ReduceMotion");
        ReduceMotionDescriptionText = T("ReduceMotionDescription");
        BackdropDimStrengthText = T("BackdropDimStrength");
        BackdropDimStrengthDescriptionText = T("BackdropDimStrengthDescription");
        ForgeRenderQualityText = T("ForgeRenderQuality");
        ForgeRenderQualityDescriptionText = T("ForgeRenderQualityDescription");
        ForgeTextureQualityText = T("ForgeTextureQuality");
        ForgeTextureQualityDescriptionText = T("ForgeTextureQualityDescription");
        ForgeSettingsApplyNextEntryText = T("ForgeSettingsApplyNextEntry");
        ReplaceItems(ForgeQualityOptions, T("QualityLow"), T("QualityMedium"), T("QualityHigh"));
        CompactLeftNavigationText = T("CompactLeftNavigation");
        CompactLeftNavigationDescriptionText = T("CompactLeftNavigationDescription");
        DataDirectoryText = T("DataDirectory");
        ModsDirectoryText = T("ModsDirectory");
        OpenText = T("Open");
        RuntimeStateText = T("RuntimeState");
        Kcd2ExecutableText = T("Kcd2Executable");
        LastActionLabelText = T("LastAction");
        ResetKcd2GameDirectoryText = T("ResetKcd2GameDirectory");
        VerificationDialogTitleText = T("VerificationDialogTitle");
        VerificationDialogQuestionText = T("VerificationDialogQuestion");
        VerificationDialogIntroText = T("VerificationDialogIntro");
        AutomaticSearchTitleText = T("AutomaticSearchTitle");
        ManualSelectionTitleText = T("ManualSelectionTitle");
        VerificationDialogExitText = T("VerificationDialogExit");
        VerificationDialogPurchaseText = T("VerificationDialogPurchase");
        VerificationDialogOwnedText = T("VerificationDialogOwned");
        ManualGamePathWatermarkText = T("ManualGamePathWatermark");
        GameDirectoryText = T("Directory");
        AddPathText = T("AddPath");
        AutoSearchButtonText = T("AutoSearch");
        DeepSearchButtonText = T("DeepSearch");
        ModManagerExitText = T("ModManagerExit");
        ModManagerHeaderText = T("ModManagerHeader");
        ModManagerTitleText = T("ModManagerTitle");
        ModManagerLoadingText = T("ModManagerLoading");
        ModListPageText = T("ModListPage");
        ImportModPackText = T("ImportModPack");
        LocalModPackPasswordText = T("LocalModPackPassword");
        LocalModPackRetryText = T("LocalModPackRetry");
        LocalModPackUseAuthorOrderText = T("LocalModPackUseAuthorOrder");
        LocalModPackReplaceText = T("LocalModPackReplace");
        LocalModPackConfirmText = T("LocalModPackConfirm");
        LocalModPackCancelText = T("LocalModPackCancel");
        ModDownloadPageText = T("ModDownloadPage");
        ModRecommendationPageText = T("ModRecommendationPage");
        AddLocalModTooltipText = T("AddLocalModTooltip");
        DeleteSelectedModTooltipText = T("DeleteSelectedModTooltip");
        RenameModGroupTooltipText = T("RenameModGroup");
        CreateModGroupText = T("CreateModGroup");
        DeleteModGroupTooltipText = T("DeleteModGroup");
        AddModToGroupText = T("AddModToGroup");
        DragModToGroupTooltipText = T("DragModToGroup");
        CustomGroupBadgeText = T("CustomInstalledSection");
        ReturnToAutomaticGroupTooltipText = T("ReturnToAutomaticModGroup");
        EmptyModGroupDropText = T("EmptyModGroupDrop");
        NoModsAvailableForGroupText = T("NoModsAvailableForGroup");
        ModGroupAssignmentLabelText = T("ModGroupAssignmentLabel");
        AutomaticModGroupText = T("AutomaticModGroup");
        MoveToModGroupText = T("MoveToModGroup");
        ModGroupNameEditorTitleText = T("RenameModGroup");
        ModGroupNameLabelText = T("ModGroupNameLabel");
        ModGroupNameWatermarkText = T("ModGroupNameWatermark");
        RestoreDefaultModGroupNameText = T("RestoreDefaultModGroupName");
        SaveModGroupNameText = T("SaveModGroupName");
        AddText = T("Add");
        DirectoryText = T("Directory");
        RefreshText = T("Refresh");
        DeleteText = T("Delete");
        EnableAllText = T("EnableAll");
        DisableAllText = T("DisableAll");
        InstalledText = T("Installed");
        EnabledWithSeparatorText = T("EnabledWithSeparator");
        DisabledText = T("Disabled");
        OrderText = T("Order");
        StateText = T("State");
        NameText = T("Name");
        VersionText = T("Version");
        IdText = T("Id");
        ActionsText = T("Actions");
        ProgressColumnText = T("Progress");
        SizeColumnText = T("Size");
        SpeedColumnText = T("Speed");
        EnableText = T("Enable");
        DetailsText = T("Details");
        BuiltInText = T("BuiltIn");
        OpenDirectoryText = T("OpenDirectory");
        ModSearchWatermarkText = T("ModSearchWatermark");
        NexusApiKeyWatermarkText = T("NexusApiKeyWatermark");
        NexusCookieConsentTitleText = T("NexusCookieConsentTitle");
        NexusCookieConsentIntroText = T("NexusCookieConsentIntro");
        NexusCookieConsentReadText = T("NexusCookieConsentRead");
        NexusCookieConsentProtectText = T("NexusCookieConsentProtect");
        NexusCookieConsentRevokeText = T("NexusCookieConsentRevoke");
        NexusCookieConsentLegalReviewText = T("NexusCookieConsentLegalReview");
        NexusCookieConsentAgreeText = T("NexusCookieConsentAgree");
        NexusCookieConsentDeclineText = T("NexusCookieConsentDecline");
        NexusCookieConsentRememberText = T("NexusCookieConsentRemember");
        RefreshSteamAccountBindingDialogText();
        NexusModFilePickerTitleText = T("NexusModFilePickerTitle");
        NexusModFilePickerDescriptionText = T("NexusModFilePickerDescription");
        NexusModFilePickerLoadingText = T("NexusModFilePickerLoading");
        NexusModFilePickerEmptyText = T("NexusModFilePickerEmpty");
        NexusModFilePickerPrimaryText = T("NexusModFilePickerPrimary");
        NexusModFilePickerFileLabelText = T("NexusModFilePickerFile");
        NexusModFilePickerUploadedText = T("NexusModFilePickerUploaded");
        NexusModFilePickerConfirmText = T("NexusModFilePickerConfirm");
        NexusModFilePickerCancelText = T("NexusModFilePickerCancel");
        NexusModFilePickerPresetsText = T("NexusModFilePickerPresets");
        NexusModFilePickerAdvancedText = T("NexusModFilePickerAdvanced");
        NexusModRequirementsTitleText = T("NexusModRequirementsTitle");
        NexusModRequirementsAutoDownloadText = T("NexusModRequirementsAutoDownload");
        NexusModRequirementDetailsText = T("NexusModRequirementDetails");
        RefreshNexusModFilePickerSelection();
        SearchResultsText = T("SearchResults");
        DownloadText = T("Download");
        StartQueueText = T("StartQueue");
        PauseText = T("Pause");
        ResumeText = T("Resume");
        CancelText = T("Cancel");
        ClearText = T("Clear");
        RetryText = T("Retry");
        OpenFolderText = T("OpenFolder");
        OpenDownloadDirectoryText = T("OpenDownloadDirectory");
        RefreshLoadMoreModSearchResultsText();
        CardViewText = T("CardView");
        ListViewText = T("ListView");
        ModCategoryText = T("ModCategory");
        DownloadProviderText = T("DownloadProvider");
        ModDetailText = T("ModDetail");
        ModSummaryText = T("ModSummary");
        ViewOriginalPageText = T("ViewOriginalPage");
        LocalPackageText = T("LocalPackage");
        SearchText = T("Search");
        DownloadSourceText = T("DownloadSource");
        OnlineSearchImportText = T("OnlineSearchImport");
        ConnectText = T("Connect");
        LocalArchiveText = T("LocalArchive");
        ChooseFileImportText = T("ChooseFileImport");
        BrowseText = T("Browse");
        DownloadQueueText = T("DownloadQueue");
        CurrentDownloadText = T("CurrentDownload");
        DownloadCenterEmptyText = T("DownloadCenterEmpty");
        DownloadCenterLauncherTooltipText = T("DownloadCenterLauncherTooltip");
        DownloadingQueueText = T("DownloadingQueue");
        DownloadedQueueText = T("DownloadedQueue");
        ActiveDownloadQueueEmptyText = T("ActiveDownloadQueueEmpty");
        CompletedDownloadQueueEmptyText = T("CompletedDownloadQueueEmpty");
        ActiveDownloadQueueDescriptionText = T("ActiveDownloadQueueDescription");
        CompletedDownloadQueueDescriptionText = T("CompletedDownloadQueueDescription");
        NoTasksText = T("NoTasks");
        SyncVisibleDownloadQueueRows();
        NexusStatusText = T("NexusReady");
        RefreshModDownloadSourceUiText();
        PopularText = T("Popular");
        StableText = T("Stable");
        VisualsText = T("Visuals");
        StarterPackText = T("StarterPack");
        FirstInstallRecommendationText = T("FirstInstallRecommendation");
        VisualOptimizationText = T("VisualOptimization");
        TextureLightingUiEnhancementText = T("TextureLightingUiEnhancement");
        CompatibilityFirstText = T("CompatibilityFirst");
        LowConflictCombinationText = T("LowConflictCombination");
        RecommendationListText = T("RecommendationList");
        InventoryInfoOptimizationText = T("InventoryInfoOptimization");
        InterfaceText = T("Interface");
        StablePerformancePresetText = T("StablePerformancePreset");
        PerformanceText = T("Performance");
        RecommendationBasisText = T("RecommendationBasis");
        RecommendationBasisDescriptionText = T("RecommendationBasisDescription");
        ModRecommendationStatusText = T("ModRecommendationStatusReady");
        ModRecommendationAlgorithmText = T("ModRecommendationAlgorithm");
        ModRecommendationAlgorithmDescriptionText = T("ModRecommendationAlgorithmDescription");
        ContentRecommendationText = T("ContentRecommendation");
        PopularityQualityRecommendationText = T("PopularityQualityRecommendation");
        StarterRecommendationText = T("StarterRecommendation");
        RefreshRecommendationsText = T("RefreshRecommendations");
        RecommendationQualifierText = T("RecommendationQualifier");
        RecommendationQualifierWatermarkText = T("RecommendationQualifierWatermark");
        RecommendationQualifierHintText = T("RecommendationQualifierHint");
        RecommendationQualifierEmptyText = T("RecommendationQualifierEmpty");
        RefreshAvailableModRecommendationQualifiers();
        RefreshModRecommendationQualifierState();
        RecommendationScoreText = T("RecommendationScore");
        RecommendationMatchText = T("RecommendationMatch");
        RecommendationQualityText = T("RecommendationQuality");
        RecommendationReasonText = T("RecommendationReason");
        RefreshModRecommendationStrategyFilters();
        foreach (var row in ModRecommendationRows)
        {
            row.ApplyLocalization(T);
            row.UseSummaryLanguage(GetModSummaryTargetLanguage(), T);
        }
        CheckConflictsBeforeLaunchText = T("CheckConflictsBeforeLaunch");
        CheckConflictsBeforeLaunchDescriptionText = T("CheckConflictsBeforeLaunchDescription");
        ConflictDetailsText = T("ConflictDetails");
        ConflictDetailsDescriptionText = T("ConflictDetailsDescription");
        NexusBrowserCookieAuthText = T("NexusBrowserCookieAuth");
        NexusBrowserCookieAuthDescriptionText = T("NexusBrowserCookieAuthDescription");
        RevokeNexusCookieAuthText = T("RevokeNexusCookieAuth");
        NexusSignInText = T("NexusSignIn");
        TestNexusCookieAuthText = T("TestNexusCookieAuth");
        InstalledModsText = T("InstalledMods");
        RegisteredModsText = T("RegisteredMods");
        DetectedConflictsText = T("DetectedConflicts");
        SaveImportCurrentText = T("SaveImportCurrent");
        SaveVaultText = T("SaveVault");
        SaveVaultRootText = T("SaveVaultRoot");
        SaveImportDisplayNameText = T("SaveImportDisplayName");
        SaveDisplayNameWatermarkText = T("SaveDisplayNameWatermark");
        SaveSafetyLockLabelText = T("SaveSafetyLockLabel");
        SaveVaultSlotsText = T("SaveVaultSlots");
        SaveMountedJunctionTargetText = T("SaveMountedJunctionTarget");
        SaveMountSelectedText = T("SaveMountSelected");
        SaveUnmountJunctionText = T("SaveUnmountJunction");
        SaveOpenVaultText = T("SaveOpenVault");
        SaveSafetyNoticeText = T("SaveSafetyNotice");
        SaveMountedText = T("SaveMounted");
        TrackerEventsLabelText = T("TrackerEvents");
        TrackerVisibleLabelText = T("TrackerVisible");
        TrackerActiveLabelText = T("TrackerActive");
        TrackerDoneLabelText = T("TrackerDone");
        TrackerUnconfirmedLabelText = T("TrackerUnconfirmed");
        TrackerPositionLabelText = T("TrackerPosition");
        TrackerBridgeProjectionText = T("TrackerBridgeProjection");
        PrepareText = T("Prepare");
        InstallText = T("Install");
        SelfTestText = T("SelfTest");
        HealthText = T("Health");
        TrackerAchievementsText = T("TrackerAchievements");
        TrackerVisibleEntitiesText = T("TrackerVisibleEntities");
        TrackerRecentEventsText = T("TrackerRecentEvents");
        if (TrackerModStatusText is "Tracker mod package not prepared")
        {
            TrackerModStatusText = T("TrackerModPackageNotPrepared");
        }
        if (TrackerModInstallStatusText is "Verify KCD2 install before installing Tracker mod")
        {
            TrackerModInstallStatusText = T("VerifyKcd2BeforeTrackerInstall");
        }
        if (TrackerDiagnosticsStatusText is "Bridge self-test not run")
        {
            TrackerDiagnosticsStatusText = T("BridgeSelfTestNotRun");
        }
        if (TrackerModHealthStatusText is "Tracker mod health not checked")
        {
            TrackerModHealthStatusText = T("TrackerModHealthNotChecked");
        }
        SaveRouterText = T("SaveRouter");
        SavePathText = T("SavePath");
        OfficialSaveEntryText = T("OfficialSaveEntry");
        SavePhysicalModeText = T("SavePhysicalMode");
        SavePhysicalModeDescriptionText = T("SavePhysicalModeDescription");
        SaveVaultSlotsDescriptionText = T("SaveVaultSlotsDescription");
        DisplayNameText = T("DisplayName");
        PlayTimeText = T("PlayTime");
        LastSavedText = T("LastSaved");
        PhysicalFolderText = T("PhysicalFolder");
        SelectedSlotText = T("SelectedSlot");
        SaveSwitchingBlockedNoticeText = T("SaveSwitchingBlockedNotice");
        JunctionRoutingNoticeText = T("JunctionRoutingNotice");
        if (TrackerLastEventText is "No tracker events processed")
        {
            TrackerLastEventText = T("NoTrackerEventsProcessed");
        }
        RefreshModDownloadPickerOptions();
        foreach (var row in NexusModSearchRows)
        {
            ApplyModSearchRowLocalization(row);
        }
        foreach (var row in ModPackRows)
        {
            row.ApplyLocalization(T);
        }
        foreach (var row in ModDownloadQueueRows)
        {
            row.ApplyLocalization(T);
        }
        SyncDownloadQueuePartitions();

        TrackerBridgeLastWriteText = T("BridgeLastWriteNever");
        RefreshModManagerPageText();
        WorkspaceTitle = GetWorkspaceTitle(SelectedTopNav);
        WorkspaceDescription = GetWorkspaceDescription(SelectedTopNav);
        RefreshLaunchStateText();
        VfsStateText = LocalizeVfsState(vfsSessionService.CurrentState);
        RefreshSectionDetails();
        if (currentModManifests.Count > 0 || ModRows.Count > 0)
        {
            ApplyModCatalog(currentModManifests, currentModConflicts, currentModConflictReviews);
        }
        else
        {
            SyncInstalledModGroups();
        }

        RefreshSelectedModText(SelectedMod);
        RefreshSelectedModConflictDetails(SelectedMod);

        var currentDefaultNavigationKey = CanonicalizeNavigationTarget(DefaultNavigationTarget);
        ReplaceItems(
            DefaultNavigationTargets,
            DashboardNavText,
            ModsNavText,
            SavesNavText,
            SettingsHeaderText);

        DefaultNavigationTarget = LocalizeNavigationTarget(currentDefaultNavigationKey);
        NotifyNavigationTooltipPropertiesChanged();
        if (PlayerProfiles.HasNexusBinding)
        {
            _ = PlayerProfiles.RefreshNexusAccountAsync();
        }
    }

    private string CanonicalizeNavigationTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NavigationTargetDashboard;
        }

        if (value.Equals(NavigationTargetDashboard, StringComparison.OrdinalIgnoreCase)
            || value.Equals(DashboardNavText, StringComparison.OrdinalIgnoreCase)
            || value.Equals(T("Dashboard"), StringComparison.OrdinalIgnoreCase))
        {
            return NavigationTargetDashboard;
        }

        if (value.Equals("Installation", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Install", StringComparison.OrdinalIgnoreCase)
            || value.Equals(T("Installation"), StringComparison.OrdinalIgnoreCase))
        {
            return NavigationTargetDashboard;
        }

        if (value.Equals(NavigationTargetMods, StringComparison.OrdinalIgnoreCase)
            || value.Equals(ModsNavText, StringComparison.OrdinalIgnoreCase)
            || value.Equals(T("Mods"), StringComparison.OrdinalIgnoreCase))
        {
            return NavigationTargetMods;
        }

        if (value.Equals(NavigationTargetSaves, StringComparison.OrdinalIgnoreCase)
            || value.Equals(SavesNavText, StringComparison.OrdinalIgnoreCase)
            || value.Equals(T("Saves"), StringComparison.OrdinalIgnoreCase))
        {
            return NavigationTargetSaves;
        }

        if (value.Equals(NavigationTargetSettings, StringComparison.OrdinalIgnoreCase)
            || value.Equals(SettingsHeaderText, StringComparison.OrdinalIgnoreCase)
            || value.Equals(T("SettingsHeader"), StringComparison.OrdinalIgnoreCase))
        {
            return NavigationTargetSettings;
        }

        return NavigationTargetDashboard;
    }

    private string LocalizeNavigationTarget(string target)
    {
        return target switch
        {
            NavigationTargetMods => ModsNavText,
            NavigationTargetSaves => SavesNavText,
            NavigationTargetSettings => SettingsHeaderText,
            _ => DashboardNavText
        };
    }

    private string NavigationTargetToDestination(string? target)
    {
        return CanonicalizeNavigationTarget(target) switch
        {
            NavigationTargetMods => "Mods",
            NavigationTargetSaves => "Saves",
            NavigationTargetSettings => "Settings",
            _ => "Dashboard"
        };
    }

    private static void ReplaceItems(ObservableCollection<string> target, params string[] items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void NotifyNavigationTooltipPropertiesChanged()
    {
        OnPropertyChanged(nameof(DashboardNavTooltipText));
        OnPropertyChanged(nameof(ModsNavTooltipText));
        OnPropertyChanged(nameof(SavesNavTooltipText));
        OnPropertyChanged(nameof(StoreNavTooltipText));
        OnPropertyChanged(nameof(LabNavTooltipText));
        OnPropertyChanged(nameof(SettingsNavTooltipText));
        OnPropertyChanged(nameof(SettingsPlayerProfilesTooltipText));
        OnPropertyChanged(nameof(SettingsNavigationTooltipText));
        OnPropertyChanged(nameof(SettingsLaunchTooltipText));
        OnPropertyChanged(nameof(SettingsAppearanceTooltipText));
        OnPropertyChanged(nameof(SettingsGraphicsTooltipText));
        OnPropertyChanged(nameof(SettingsStorageTooltipText));
        OnPropertyChanged(nameof(SettingsAdvancedTooltipText));
        OnPropertyChanged(nameof(SettingsMoreTooltipText));
    }

    private static void ReplaceRows(
        ObservableCollection<ModDownloadQueueRowViewModel> target,
        IEnumerable<ModDownloadQueueRowViewModel> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    private string T(string key) =>
        textCatalog.Translate(SelectedLanguage, key);

    private void SyncModVisualResources(bool activate)
    {
        if (!activate)
        {
            CancelModCoverCaching();
            installedModCoverGeneration++;
            installedModCoverCancellation?.Cancel();
            installedModCoverCancellation?.Dispose();
            installedModCoverCancellation = null;
            modPackCoverCacheCancellation?.Cancel();
            modPackCoverCacheCancellation?.Dispose();
            modPackCoverCacheCancellation = null;
        }

        if (!activate)
        {
            foreach (var row in ModRows)
            {
                row.DeactivateVisualResources();
            }

            var searchRows = NexusModSearchRows
                .Concat(ModRecommendationRows.Select(item => item.Mod))
                .Distinct();
            foreach (var row in searchRows)
            {
                row.DeactivateVisualResources();
            }

            foreach (var row in ModPackRows)
            {
                row.DeactivateVisualResources();
            }

            foreach (var row in ModDownloadQueueRows)
            {
                row.DeactivateVisualResources();
            }
        }

        if (!activate && launcherDetailsVisualResourcesActive)
        {
            DailyModRecommendation?.Mod.ActivateVisualResources();
        }

        if (activate)
        {
            RefreshInstalledModCovers();
            CacheNewModCovers(NexusModSearchRows
                .Concat(ModRecommendationRows.Select(item => item.Mod))
                .Distinct()
                .ToArray());

            if (ModPackRows.Count > 0)
            {
                modPackCoverCacheCancellation = CancellationTokenSource.CreateLinkedTokenSource(downloadCoverCancellation.Token);
                foreach (var row in ModPackRows)
                {
                    _ = CacheModPackCoverAsync(row, modPackCoverCacheCancellation.Token);
                }
            }
        }
    }

    internal void ActivateLauncherDetailsVisualResources()
    {
        launcherDetailsVisualResourcesActive = true;
        SaveManager.ActivateDashboardVisualResources();
        FeaturedGameNews?.ActivateVisualResources();
        foreach (var row in SecondaryGameNewsItems)
        {
            row.ActivateVisualResources();
        }

        DailyModRecommendation?.Mod.ActivateVisualResources();
    }

    internal void DeactivateLauncherDetailsVisualResources()
    {
        launcherDetailsVisualResourcesActive = false;
        SaveManager.DeactivateDashboardVisualResources();
        FeaturedGameNews?.DeactivateVisualResources();
        foreach (var row in SecondaryGameNewsItems)
        {
            row.DeactivateVisualResources();
        }

        DailyModRecommendation?.Mod.DeactivateVisualResources();
    }

    private static bool TryOpenShellTarget(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsPathInsideDirectory(string rootDirectory, string candidatePath)
    {
        var normalizedRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static bool SamePath(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

    private bool IsShuttingDown => Volatile.Read(ref isShuttingDown) != 0;

    internal Task PrepareForShutdownAsync()
    {
        lock (shutdownPreparationGate)
        {
            if (shutdownPreparationTask is null || shutdownPreparationTask.IsFaulted || shutdownPreparationTask.IsCanceled)
            {
                shutdownPreparationTask = PrepareForShutdownCoreAsync();
            }

            return shutdownPreparationTask;
        }
    }

    private async Task PrepareForShutdownCoreAsync()
    {
        Interlocked.Exchange(ref isShuttingDown, 1);
        shutdownCancellation.Cancel();
        gameDiscoveryCancellation?.Cancel();
        steamAccountBindingDetectionCancellation?.Cancel();
        modSearchAutoSearchCancellation?.Cancel();
        modCoverCacheCancellation?.Cancel();
        installedModCoverCancellation?.Cancel();
        installedModRequirementsCancellation?.Cancel();
        modPackCoverCacheCancellation?.Cancel();
        dailyModCoverCancellation?.Cancel();
        downloadCoverCancellation.Cancel();
        localModPackImportCancellation?.Cancel();
        SaveManager.CancelPendingOperationsForShutdown();
        modDownloader.CancelQueue();

        if (pendingModPackInstallPlan is not null)
        {
            try
            {
                await modPackInstallService.CancelAsync(pendingModPackInstallPlan.SessionId);
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Warning(ex, "Unable to cancel the active mod-pack installation during shutdown");
            }
        }

        await AwaitRunningCommandsAsync(this, SaveManager, PlayerProfiles, MoreSettings);
        var backgroundDownloadQueueTask = backgroundModDownloadQueueTask;
        if (backgroundDownloadQueueTask is not null)
        {
            try
            {
                await backgroundDownloadQueueTask;
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Warning(ex, "Unable to finish the background mod download queue during shutdown");
            }
        }

        if (localModPackImportPlan is not null)
        {
            try
            {
                await modPackImportService.DiscardAsync(localModPackImportPlan.SessionId);
                localModPackImportPlan = null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Warning(ex, "Unable to discard the active local mod-pack import session during shutdown");
            }
        }

        await playerRuntimeGate.WaitAsync();
        try
        {
            await StopPlayerScopedBackgroundOperationsAsync();
        }
        finally
        {
            playerRuntimeGate.Release();
        }

        try
        {
            await vfsSessionService.UnmountAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Warning(ex, "Unable to unmount the virtual file-system session during shutdown");
        }
    }

    private static async Task AwaitRunningCommandsAsync(params object[] owners)
    {
        while (true)
        {
            var tasks = owners
                .SelectMany(owner => owner.GetType().GetProperties().Select(property => (Owner: owner, Property: property)))
                .Where(item => item.Property.GetIndexParameters().Length == 0)
                .Where(item => typeof(IAsyncRelayCommand).IsAssignableFrom(item.Property.PropertyType))
                .Select(item => item.Property.GetValue(item.Owner))
                .OfType<IAsyncRelayCommand>()
                .Where(command => command.IsRunning && command.ExecutionTask is not null)
                .Select(command => command.ExecutionTask!)
                .Distinct()
                .ToArray();
            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // Command handlers own user-facing error reporting; shutdown only waits for completion.
            }

            await Task.Yield();
        }
    }

    public void Dispose()
    {
        shutdownCancellation.Cancel();
        PlayerProfiles.PlayerChanged -= OnPlayerChanged;
        PlayerProfiles.SettingsRequested -= OnPlayerSettingsRequested;
        SaveManager.PropertyChanged -= OnSaveManagerPropertyChanged;
        DeactivateLauncherDetailsVisualResources();
        MoreSettings.DeactivateVisualResources();
        SyncModVisualResources(false);
        FeaturedGameNews?.Dispose();
        FeaturedGameNews = null;
        foreach (var row in SecondaryGameNewsItems)
        {
            row.Dispose();
        }
        SecondaryGameNewsItems.Clear();
        foreach (var row in ModRows)
        {
            row.Dispose();
        }
        foreach (var row in NexusModSearchRows.Distinct())
        {
            row.Dispose();
        }
        SaveManager.Dispose();
        CreatedAlchemyWorkshop?.Deactivate();
        CreatedForgeWorkshop?.Dispose();
        modDownloadSearchOperationGate.Dispose();
        localModPackImportCancellation?.Cancel();
        localModPackImportCancellation?.Dispose();
        localModPackImportCancellation = null;
        if (localModPackImportPlan is not null)
        {
            try
            {
                modPackImportService.DiscardAsync(localModPackImportPlan.SessionId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Warning(ex, "Unable to discard the active local mod-pack import session during shutdown");
            }

            localModPackImportPlan = null;
        }
        gameDiscoveryCancellation?.Cancel();
        gameDiscoveryCancellation?.Dispose();
        steamAccountBindingDetectionCancellation?.Cancel();
        steamAccountBindingDetectionCancellation?.Dispose();
        modSearchAutoSearchCancellation?.Cancel();
        modSearchAutoSearchCancellation?.Dispose();
        modCoverCacheCancellation?.Cancel();
        modCoverCacheCancellation?.Dispose();
        installedModCoverCancellation?.Cancel();
        installedModCoverCancellation?.Dispose();
        installedModRequirementsCancellation?.Cancel();
        installedModRequirementsCancellation?.Dispose();
        modPackCoverCacheCancellation?.Cancel();
        modPackCoverCacheCancellation?.Dispose();
        dailyModCoverCancellation?.Cancel();
        dailyModCoverCancellation?.Dispose();
        downloadCoverCancellation.Cancel();
        downloadCoverCancellation.Dispose();
        shutdownCancellation.Dispose();
        foreach (var row in ModDownloadQueueRows)
        {
            row.Dispose();
        }
        if (isTrackerRuntimeSubscribed)
        {
            trackerRuntimeService.Updated -= OnTrackerRuntimeUpdated;
            isTrackerRuntimeSubscribed = false;
        }
        trackerRuntimeService.StopAsync().GetAwaiter().GetResult();
        gameRuntimeMonitorService.Exited -= OnGameRuntimeExited;
        gameRuntimeMonitorService.StopAsync().GetAwaiter().GetResult();
        modCoverCacheThrottle.Dispose();
        modPackCoverCacheThrottle.Dispose();
        labModuleInitializationGate.Dispose();
        playerRuntimeGate.Dispose();
    }

    private sealed record RecommendationScoreBreakdown(
        double ContentScore,
        double PopularityScore,
        double QualityScore,
        double FreshnessScore,
        double StabilityScore,
        double InstallConvenienceScore,
        double RiskPenalty,
        IReadOnlyList<string> ReasonSignals);

    private sealed record InstalledModGroupBuildState(
        string Key,
        bool IsCustom,
        int FirstIndex,
        IReadOnlyList<ModListItemViewModel> Rows,
        ModListItemViewModel? SourceRow);
}

public sealed record ModGroupAssignmentRequest(
    ModListItemViewModel Mod,
    InstalledModGroupViewModel Group);
