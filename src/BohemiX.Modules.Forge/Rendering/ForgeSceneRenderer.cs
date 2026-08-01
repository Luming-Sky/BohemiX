using System.Numerics;
using System.Diagnostics;
using System.IO;
using Avalonia.OpenGL;
using BohemiX.Modules.Forge.Models;
using SkiaSharp;
using Silk.NET.OpenGL;

namespace BohemiX.Modules.Forge.Rendering;

internal sealed class PreparedMeshAsset(
    MeshAsset asset,
    IReadOnlyDictionary<string, ForgeCompressedTexture> compressedTextures) : IDisposable
{
    public MeshAsset Asset { get; } = asset;
    public IReadOnlyDictionary<string, ForgeCompressedTexture> CompressedTextures { get; } = compressedTextures;

    public void Dispose() => Asset.Dispose();
}

public sealed class ForgeSceneRenderer : IDisposable
{
    private ForgeRenderSettings settings;
    private readonly IForgeAssetLoader? suppliedAssetLoader;
    private IForgeAssetLoader assetLoader;
    private ForgeTextureQuality textureQuality;
    private readonly MaterialLibrary materials = new();
    private readonly ForgeCameraRig camera = new();
    private GlResourceRegistry? resources;
    private GL? gl;
    private ShaderProgram? sceneShader;
    private ShaderProgram? shadowShader;
    private ShaderProgram? postShader;
    private ShaderProgram? particleShader;
    private GpuMeshAsset? anvilMesh;
    private GpuMeshAsset? workbenchMesh;
    private GpuMeshAsset? hearthMesh;
    private GpuMeshAsset? bellowsMesh;
    private GpuMeshAsset? waterVatMesh;
    private GpuMeshAsset? oilVatMesh;
    private GpuMeshAsset? grinderStandMesh;
    private GpuMeshAsset? grinderWheelMesh;
    private GpuMeshAsset? hammerMesh;
    private GpuMeshAsset? tongsMesh;
    private int staticModelLoadIndex;
    private int staticModelLoadDelayFrames;
    private Task<PreparedMeshAsset>? staticModelLoadTask;
    private CancellationTokenSource? staticModelLoadCancellation;
    private int proceduralTextureUploadStage;
    private static readonly string[] StaticModelLoadOrder =
    [
        "workbench", "hearth", "anvil", "hammer", "bellows",
        "quench-water", "quench-oil", "grinder-stand", "grinder-wheel", "tongs"
    ];
    private MeshBuffer? workpieceMesh;
    private GlResourceRegistry? weaponResources;
    private GpuMeshAsset? activeWeaponMesh;
    private string? activeWeaponRecipeId;
    private Texture2D? backgroundTexture;
    private Texture2D? detailAtlas;
    private Texture2D? normalAtlas;
    private Texture2D? environmentMap;
    private Texture2D? brdfLut;
    private Texture2D? whiteTexture;
    private Texture2D? flatNormalTexture;
    private Texture2D? defaultOrmTexture;
    private Texture2D? blackTexture;
    private SharedGpuTextureCache? textureCache;
    private ForgeTextureCompressionCapabilities compressionCapabilities;
    private RenderTarget? hdrTarget;
    private RenderTarget? bloomTarget;
    private ShadowMap? shadowMap;
    private FullscreenQuad? fullscreen;
    private ParticlePool? particles;
    private ForgeSnapshot? snapshot;
    private readonly ForgeWorkpieceMotion workpieceMotion = new();
    private readonly ForgeToolMotion toolMotion = new();
    private readonly ForgeShapeTransition shapeTransition = new();
    private long meshRevision = -1;
    private long eventSequence;
    private ForgeStateId lastState;
    private Matrix4x4 lastView;
    private Matrix4x4 lastProjection;
    private Matrix4x4 workpieceWorld = WorkpieceMeshBuilder.WorldTransform;
    private bool disposed;
    private readonly Stopwatch frameTimer = new();
    private double slowFrameSeconds;
    private bool qualityDegraded;
    private ForgeRenderQuality activeQuality;
    private ForgeRenderQuality requestedQuality;
    private bool isGles;
    private QuenchMedium activeQuenchMedium = QuenchMedium.Water;
    private float impactFlash;
    private float overheatSparkAccumulator;
    private float grindingSparkAccumulator;
    private float grindingContactImpulse;
    private float hammerSparkAfterglow;
    private float hammerSparkAccumulator;
    private Vector3 hammerSparkOrigin;
    private float hammerSparkIntensity;
    private ForgeHeatBand hammerSparkHeat;
    private Vector3 impactPoint;
    private Vector3 hammerTargetPoint;
    private bool hammerTargetVisible;
    private uint outputFramebuffer;
    private ForgeStationId? hoveredStation;
    private bool workpieceHovered;

    public ForgeSceneRenderer(
        ForgeRenderSettings? settings = null,
        IForgeAssetLoader? assetLoader = null,
        ForgeTextureQuality textureQuality = ForgeTextureQuality.Medium)
    {
        this.settings = settings ?? ForgeRenderSettings.Medium;
        suppliedAssetLoader = assetLoader;
        this.textureQuality = textureQuality;
        this.assetLoader = assetLoader ?? new ForgeGlbAssetLoader(textureQuality);
        activeQuality = requestedQuality = this.settings.Quality;
    }
    public ForgeCameraRig Camera => camera;

    public void SetTransferPreview(bool active) =>
        camera.SetTransferOverview(active, settings.ReducedMotion);
    public bool IsInitialized => gl is not null && resources is not null;
    internal static bool ShouldEnableProgramPointSize(bool glesContext) => !glesContext;
    public string? InitializationError { get; private set; }
    public string? ShadowWarning { get; private set; }
    public string? LastRenderError { get; private set; }
    public bool QualityDegraded => qualityDegraded;
    public ForgeRenderQuality ActiveQuality => activeQuality;
    public void RequestQuality(ForgeRenderQuality quality) => requestedQuality = quality;
    public void RequestTextureQuality(ForgeTextureQuality quality)
    {
        if (suppliedAssetLoader is null)
        {
            assetLoader = new ForgeGlbAssetLoader(quality);
            textureQuality = quality;
        }
    }

    public void ConfigureBeforeInitialization(ForgeRenderQuality renderQuality, ForgeTextureQuality requestedTextureQuality)
    {
        if (IsInitialized) return;
        settings = renderQuality switch
        {
            ForgeRenderQuality.Low => ForgeRenderSettings.Low,
            ForgeRenderQuality.High => ForgeRenderSettings.High,
            _ => ForgeRenderSettings.Medium
        };
        activeQuality = requestedQuality = renderQuality;
        RequestTextureQuality(requestedTextureQuality);
    }
    public float HammerCharge => toolMotion.HammerGesturePull;
    public void SetHoveredStation(ForgeStationId? station) => hoveredStation = station;
    public void SetWorkpieceHovered(bool hovered) => workpieceHovered = hovered;
    public void BeginInspectionDrag() => workpieceMotion.BeginInspectionDrag();
    public void RotateInspection(float yawDelta, float pitchDelta, float seconds) =>
        workpieceMotion.RotateInspection(yawDelta, pitchDelta, seconds);
    public void EndInspectionDrag() => workpieceMotion.EndInspectionDrag();
    public void AddInspectionZoom(float delta) => workpieceMotion.AddInspectionZoom(delta);

    public void SetHammerTarget(Vector2 lattice, bool visible = true)
    {
        if (!visible || snapshot?.State != ForgeStateId.Hammering)
        {
            hammerTargetVisible = false;
            toolMotion.ClearHammerTarget();
            return;
        }

        var localY = snapshot.IsFlipped ? 1 - lattice.Y : lattice.Y;
        var localPoint = WorkpieceMeshBuilder.LocalPoint(
            lattice.X,
            localY,
            WorkpieceSurfaceHeight(lattice.X, localY) + .018f,
            snapshot.RecipeId,
            snapshot.RecipeShape);
        hammerTargetPoint = Vector3.Transform(localPoint, workpieceWorld);
        hammerTargetVisible = true;
        toolMotion.SetHammerTarget(hammerTargetPoint, true);
    }

    public void BeginHammerGesture(Vector2 lattice)
    {
        if (snapshot?.State != ForgeStateId.Hammering) return;
        var localY = snapshot.IsFlipped ? 1 - lattice.Y : lattice.Y;
        var localPoint = WorkpieceMeshBuilder.LocalPoint(
            lattice.X,
            localY,
            WorkpieceSurfaceHeight(lattice.X, localY) + .018f,
            snapshot.RecipeId,
            snapshot.RecipeShape);
        toolMotion.BeginHammerGesture(Vector3.Transform(localPoint, workpieceWorld));
        hammerTargetPoint = Vector3.Transform(localPoint, workpieceWorld);
        hammerTargetVisible = true;
    }

    public void UpdateHammerGesture(float pull) => toolMotion.UpdateHammerGesture(pull);
    public void EndHammerGesture() => toolMotion.EndHammerGesture();
    public void CancelHammerGesture()
    {
        toolMotion.EndHammerGesture();
        toolMotion.ClearHammerTarget();
        hammerTargetVisible = false;
    }

    public void PlayHammerStrike(Vector2 lattice, double intensity, ForgeSnapshot currentSnapshot)
    {
        if (!IsInitialized)
        {
            return;
        }

        snapshot = currentSnapshot;
        var localY = snapshot.IsFlipped ? 1 - lattice.Y : lattice.Y;
        var localPoint = WorkpieceMeshBuilder.LocalPoint(
            lattice.X,
            localY,
            WorkpieceSurfaceHeight(lattice.X, localY) + .018f,
            snapshot.RecipeId,
            snapshot.RecipeShape);
        toolMotion.Strike(Vector3.Transform(localPoint, workpieceWorld), intensity);
    }

    public bool SubmitVisualEvent(ForgeVisualEvent visual, ForgeSnapshot currentSnapshot)
    {
        if (!IsInitialized || particles is null || visual.Sequence == eventSequence)
        {
            return false;
        }

        snapshot = currentSnapshot;
        eventSequence = visual.Sequence;
        HandleVisualEvent(visual);
        return true;
    }

    public void Initialize(GlInterface glInterface)
    {
        CancelStaticModelLoading();
        AbandonResources();
        disposed = false;
        try
        {
            gl = GL.GetApi(name => glInterface.GetProcAddress(name));
            var version = gl.GetStringS(StringName.Version) ?? string.Empty;
            isGles = version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
            compressionCapabilities = ForgeTextureCompressionCapabilities.Detect(gl, isGles);
            resources = new GlResourceRegistry();
            textureCache = resources.Register(new SharedGpuTextureCache());
            sceneShader = resources.Register(new ShaderProgram(gl, SceneVertexShader, SceneFragmentShader, isGles, "Forge scene"));
            shadowShader = resources.Register(new ShaderProgram(gl, ShadowVertexShader, ShadowFragmentShader, isGles, "Forge shadow"));
            postShader = resources.Register(new ShaderProgram(gl, PostVertexShader, PostFragmentShader, isGles, "Forge post"));
            particleShader = resources.Register(new ShaderProgram(gl, ParticleVertexShader, ParticleFragmentShader, isGles, "Forge particles"));
            SetMaterials(sceneShader);
            whiteTexture = resources.Register(new Texture2D(gl, 1, 1, [255, 255, 255, 255], srgb: true, repeat: false));
            flatNormalTexture = resources.Register(new Texture2D(gl, 1, 1, [128, 128, 255, 255], srgb: false, repeat: false));
            defaultOrmTexture = resources.Register(new Texture2D(gl, 1, 1, [255, 255, 255, 255], srgb: false, repeat: false));
            blackTexture = resources.Register(new Texture2D(gl, 1, 1, [0, 0, 0, 255], srgb: true, repeat: false));
            // Static GLB files are uploaded incrementally from Render.  Recipe
            // selection can therefore paint its UI immediately instead of
            // blocking on the entire workshop asset set.
            staticModelLoadIndex = 0;
            staticModelLoadDelayFrames = 3;
            ForgeRenderWarmup.Begin();
            workpieceMesh = resources.Register(new MeshBuffer(gl, new MeshData([], []), dynamic: true));
            backgroundTexture = resources.Register(LoadWorkshopBackground(gl));
            detailAtlas = resources.Register(new Texture2D(gl, 1, 1, [128, 166, 224, 255], srgb: false));
            normalAtlas = resources.Register(new Texture2D(gl, 1, 1, [128, 128, 255, 255], srgb: false));
            proceduralTextureUploadStage = 0;
            environmentMap = resources.Register(new Texture2D(gl, ForgeEnvironmentTextures.EnvironmentWidth, ForgeEnvironmentTextures.EnvironmentHeight, ForgeEnvironmentTextures.CreateEnvironment(), srgb: false));
            brdfLut = resources.Register(new Texture2D(gl, ForgeEnvironmentTextures.BrdfSize, ForgeEnvironmentTextures.BrdfSize, ForgeEnvironmentTextures.CreateBrdfLut(), srgb: false, repeat: false));
            try
            {
                shadowMap = resources.Register(new ShadowMap(gl, Math.Clamp(settings.ShadowResolution, 512, 2048), isGles));
                ShadowWarning = null;
            }
            catch (Exception shadowError)
            {
                while (gl.GetError() != GLEnum.NoError) { }
                shadowMap = null;
                ShadowWarning = $"实时阴影不可用，已切换到接触阴影：{shadowError.Message}";
            }
            hdrTarget = resources.Register(new RenderTarget(gl, 1, 1, withDepth: true, preferHdr: true, withEmission: true));
            bloomTarget = resources.Register(new RenderTarget(gl, 1, 1, withDepth: false));
            fullscreen = resources.Register(new FullscreenQuad(gl));
            particles = resources.Register(new ParticlePool(gl, (int)Math.Clamp(768 * settings.ParticleDensity, 160, 768)));
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);
            // OpenGL ES always takes point size from gl_PointSize and does not expose
            // GL_PROGRAM_POINT_SIZE. Enabling the desktop-only enum poisons the context
            // with InvalidEnum and previously caused the whole Forge viewport to fail.
            if (ShouldEnableProgramPointSize(isGles))
            {
                gl.Enable(EnableCap.ProgramPointSize);
            }
            gl.Disable(EnableCap.Dither);
            gl.CullFace(TriangleFace.Back);
            gl.DepthFunc(DepthFunction.Less);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            StartNextStaticModelLoad();
            var initializationGlError = gl.GetError();
            if (initializationGlError != GLEnum.NoError)
            {
                throw new InvalidOperationException($"OpenGL resource initialization error: {initializationGlError}");
            }
            InitializationError = null;
        }
        catch (Exception ex)
        {
            InitializationError = $"Forge Simulator 无法创建 3D 资源。\n{ex.Message}";
            DisposeResources(finish: false);
        }
    }

    public void Render(ForgeSnapshot nextSnapshot, uint framebuffer, int width, int height, float elapsedSeconds, float timeSeconds)
    {
        frameTimer.Restart();
        snapshot = nextSnapshot;
        if (ShouldRenderFinishedWeapon(nextSnapshot.State, nextSnapshot.GrindingEngaged))
        {
            EnsureWeaponResources(nextSnapshot.RecipeId);
        }
        else if (weaponResources is not null)
        {
            DisposeWeaponResources();
        }
        if (!IsInitialized || gl is null || sceneShader is null || shadowShader is null || postShader is null || particleShader is null || workpieceMesh is null || backgroundTexture is null || detailAtlas is null || normalAtlas is null || environmentMap is null || brdfLut is null || hdrTarget is null || bloomTarget is null || fullscreen is null || particles is null) return;
        UploadNextPreparedProceduralTexture();
        LoadNextStaticModel();
        outputFramebuffer = framebuffer;
        while (gl.GetError() != GLEnum.NoError) { }
        LastRenderError = null;
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.Dither);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.StencilTest);
        gl.Disable(EnableCap.SampleAlphaToCoverage);
        gl.Disable(EnableCap.SampleCoverage);
        gl.Disable(EnableCap.PolygonOffsetFill);
        gl.Enable(EnableCap.DepthTest);
        gl.Enable(EnableCap.CullFace);
        if (ShouldEnableProgramPointSize(isGles))
        {
            gl.Enable(EnableCap.ProgramPointSize);
        }
        gl.DepthMask(true);
        gl.ColorMask(true, true, true, true);
        gl.DepthFunc(DepthFunction.Less);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (nextSnapshot.State != lastState)
        {
            ApplyRequestedQuality();
        }
        var renderScale = RenderScaleFor(activeQuality, qualityDegraded);
        var sceneWidth = Math.Max(1, (int)MathF.Ceiling(width * renderScale));
        var sceneHeight = Math.Max(1, (int)MathF.Ceiling(height * renderScale));
        hdrTarget.Resize(sceneWidth, sceneHeight);
        bloomTarget.Resize(Math.Max(1, sceneWidth / 2), Math.Max(1, sceneHeight / 2));
        if (snapshot.ActiveQuenchMedium is { } snapshotMedium)
        {
            activeQuenchMedium = snapshotMedium;
            camera.SetQuenchMedium(snapshotMedium, settings.ReducedMotion);
        }
        camera.SetGrindingEngaged(snapshot.GrindingEngaged, settings.ReducedMotion);
        if (snapshot.State != lastState)
        {
            camera.SetState(snapshot.State, settings.ReducedMotion);
            lastState = snapshot.State;
        }
        var pose = camera.Update(elapsedSeconds, settings.ReducedMotion, timeSeconds);
        lastView = Matrix4x4.CreateLookAt(pose.Position, pose.Target, Vector3.UnitY);
        lastProjection = Matrix4x4.CreatePerspectiveFieldOfView(pose.FieldOfView, width / (float)height, .08f, 40f);

        ForgeVisualEvent? pendingVisual = null;
        if (snapshot.VisualEvent is { Sequence: var sequence } visual && sequence != eventSequence)
        {
            eventSequence = sequence;
            pendingVisual = visual;
            if (visual.Kind == ForgeVisualEventKind.QuenchStarted)
            {
                activeQuenchMedium = visual.Medium ?? QuenchMedium.Water;
                camera.SetQuenchMedium(activeQuenchMedium, settings.ReducedMotion);
            }
        }

        var workpieceState = snapshot.State == ForgeStateId.Grinding && !snapshot.GrindingEngaged
            ? ForgeStateId.Inspection
            : snapshot.State;
        workpieceMotion.SetTarget(workpieceState, snapshot.IsFlipped, activeQuenchMedium, settings.ReducedMotion);
        toolMotion.Update(
            elapsedSeconds,
            snapshot.State,
            snapshot.HammerFace,
            snapshot.GrindingEngaged,
            snapshot.GrinderSpeed);
        workpieceWorld = workpieceMotion.Update(elapsedSeconds, timeSeconds);
        if (snapshot.State == ForgeStateId.Hammering && !hammerTargetVisible && !toolMotion.IsHammerStrikeActive)
        {
            SetHammerTarget(new Vector2(.5f, .5f));
        }
        else if (snapshot.State != ForgeStateId.Hammering && hammerTargetVisible)
        {
            hammerTargetVisible = false;
            toolMotion.ClearHammerTarget();
        }
        if (toolMotion.TryConsumeHammerImpact(out var hammerPoint, out var hammerIntensity))
        {
            ApplyHammerImpact(hammerPoint, hammerIntensity);
        }

        var shapeMeshChanged = false;
        if (meshRevision != snapshot.ShapeRevision)
        {
            if (meshRevision < 0 || pendingVisual?.Kind != ForgeVisualEventKind.HammerStrike)
            {
                shapeTransition.SetImmediate(snapshot.ShapeCells);
                shapeMeshChanged = true;
            }
            else
            {
                shapeTransition.Queue(
                    snapshot.ShapeCells,
                    pendingVisual.X,
                    snapshot.IsFlipped ? 1 - pendingVisual.Y : pendingVisual.Y,
                    pendingVisual.Intensity);
            }
            meshRevision = snapshot.ShapeRevision;
        }
        shapeMeshChanged |= shapeTransition.Update(elapsedSeconds);
        if (shapeMeshChanged)
        {
            workpieceMesh.Upload(WorkpieceMeshBuilder.Build(
                shapeTransition.Cells,
                flipped: false,
                snapshot.RecipeId,
                snapshot.StrikeCount,
                snapshot.RecipeShape));
        }
        if (pendingVisual is not null)
        {
            HandleVisualEvent(pendingVisual);
        }
        UpdateHammerSparkAfterglow(elapsedSeconds);
        UpdateGrindingContact(elapsedSeconds);
        particles.Update(elapsedSeconds);
        impactFlash = Math.Max(0, impactFlash - elapsedSeconds * 5.5f);
        if (!qualityDegraded)
        {
            if (snapshot.State is ForgeStateId.Heating or ForgeStateId.Reheat)
            {
                particles.EmitFire(new Vector3(-3.05f, -.52f, .18f), snapshot.HeatBand is ForgeHeatBand.Cold ? .22f : 1f);
            }
        }
        var overheatIntensity = OverheatVisualIntensity(snapshot.State, snapshot.HeatBand, snapshot.Heat);
        if (overheatIntensity > 0)
        {
            var density = qualityDegraded ? .55f : 1f;
            overheatSparkAccumulator += elapsedSeconds * ThermalSparkRate(
                snapshot.State,
                snapshot.HeatBand,
                snapshot.Heat) * density;
            var sparkCount = Math.Min(12, (int)overheatSparkAccumulator);
            if (sparkCount > 0)
            {
                overheatSparkAccumulator -= sparkCount;
                particles.EmitOverheatScale(HeatingWorkpieceSparkOrigin(), overheatIntensity, sparkCount);
            }
        }
        else
        {
            overheatSparkAccumulator = 0;
        }

        var lightMvp = ShadowViewProjectionFor(snapshot.State, activeQuenchMedium);

        if (shadowMap is not null)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, shadowMap.Framebuffer);
            gl.Viewport(0, 0, (uint)shadowMap.Resolution, (uint)shadowMap.Resolution);
            gl.Clear(ClearBufferMask.DepthBufferBit);
            gl.CullFace(TriangleFace.Front);
            gl.Enable(EnableCap.PolygonOffsetFill);
            gl.PolygonOffset(1.35f, 2.1f);
            shadowShader.Use();
            shadowShader.Set("uLightMvp", lightMvp);
            DrawStageGeometry(shadowShader, snapshot.State, includeWorkpiece: ShouldDrawWorkpiece(snapshot.State));
            gl.Disable(EnableCap.PolygonOffsetFill);
            gl.CullFace(TriangleFace.Back);
            var shadowFrameError = gl.GetError();
            if (shadowFrameError != GLEnum.NoError)
            {
                ShadowWarning = $"实时阴影已在当前显卡上禁用：{shadowFrameError}";
                shadowMap = null;
                while (gl.GetError() != GLEnum.NoError) { }
            }
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, hdrTarget.Framebuffer);
        gl.Viewport(0, 0, (uint)sceneWidth, (uint)sceneHeight);
        gl.ClearColor(0, 0, 0, 0);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        sceneShader.Use();
        sceneShader.Set("uView", lastView);
        sceneShader.Set("uProjection", lastProjection);
        sceneShader.Set("uLightMvp", lightMvp);
        sceneShader.Set("uCameraPosition", pose.Position);
        sceneShader.Set("uLightPosition", ForgeLightPosition(snapshot.State));
        sceneShader.Set("uTaskLightPosition", TaskLightPositionFor(snapshot.State, activeQuenchMedium));
        sceneShader.Set("uTaskLightDirection", TaskLightDirectionFor(snapshot.State, activeQuenchMedium));
        sceneShader.Set("uTaskLightColor", TaskLightColorFor(snapshot.State));
        sceneShader.Set("uTaskLightIntensity", TaskLightIntensityFor(snapshot.State));
        sceneShader.Set("uRimLightDirection", RimLightDirectionFor(snapshot.State));
        sceneShader.Set("uRimLightIntensity", RimLightIntensityFor(snapshot.State));
        sceneShader.Set("uDirectionalIntensity", DirectionalIntensityFor(snapshot.State));
        sceneShader.Set("uEnvironmentStrength", EnvironmentStrengthFor(snapshot.State));
        sceneShader.Set("uShadowSoftness", activeQuality == ForgeRenderQuality.High && !qualityDegraded ? 1.45f : 1.0f);
        var heatColor = MaterialLibrary.HeatColor(nextSnapshot.Heat);
        var workpieceGlow = WorkpieceGlowSegment(
            workpieceWorld,
            nextSnapshot.RecipeId,
            nextSnapshot.RecipeShape);
        sceneShader.Set("uHeatColor", heatColor);
        sceneShader.Set("uIncandescenceColor", IncandescenceColorFor(nextSnapshot.Heat));
        sceneShader.Set("uWorkpieceLightStart", workpieceGlow.Start);
        sceneShader.Set("uWorkpieceLightEnd", workpieceGlow.End);
        sceneShader.Set("uWorkpieceLightColor", WorkpieceLightColorFor(nextSnapshot.Heat));
        sceneShader.Set("uWorkpieceLightIntensity", WorkpieceLightIntensityFor(nextSnapshot.Heat));
        sceneShader.Set("uHeatAmount", (float)Math.Clamp(nextSnapshot.Heat, 0, 1.1));
        sceneShader.Set("uForgeProgress", ForgeProgress(
            nextSnapshot.RecipeId,
            nextSnapshot.StrikeCount,
            nextSnapshot.RecipeVisual?.RecommendedDevelopmentStrikes ?? 0));
        sceneShader.Set("uGrindCoverage", (float)Math.Clamp(nextSnapshot.GrindCoverage, 0, 1));
        sceneShader.Set("uGrindQuality", (float)Math.Clamp(nextSnapshot.GrindingQuality, 0, 1));
        sceneShader.Set("uForgeLightIntensity", ForgeLightIntensity(snapshot.State, nextSnapshot.Heat, timeSeconds));
        sceneShader.Set("uAmbientStrength", AmbientStrengthFor(snapshot.State));
        sceneShader.Set("uImpactFlash", impactFlash);
        sceneShader.Set("uImpactPoint", impactPoint);
        sceneShader.Set("uTargetPoint", hammerTargetPoint);
        sceneShader.Set("uTargetVisible", hammerTargetVisible && snapshot.State == ForgeStateId.Hammering ? 1f : 0f);
        sceneShader.Set("uTime", timeSeconds);
        detailAtlas.Bind(TextureUnit.Texture0);
        normalAtlas.Bind(TextureUnit.Texture1);
        environmentMap.Bind(TextureUnit.Texture3);
        brdfLut.Bind(TextureUnit.Texture4);
        sceneShader.Set("uDetailAtlas", 0);
        sceneShader.Set("uNormalAtlas", 1);
        sceneShader.Set("uEnvironmentMap", 3);
        sceneShader.Set("uBrdfLut", 4);
        shadowMap?.BindTexture(TextureUnit.Texture2);
        sceneShader.Set("uShadowMap", 2);
        sceneShader.Set("uShadowsEnabled", shadowMap is null ? 0 : 1);
        sceneShader.Set("uStageWarmth", snapshot.State is ForgeStateId.Heating or ForgeStateId.Reheat ? 1f : .25f);
        DrawStageGeometry(sceneShader, snapshot.State, includeWorkpiece: ShouldDrawWorkpiece(snapshot.State));
        if (SetFrameError("HDR 场景")) return;

        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        gl.DepthMask(false);
        gl.DepthFunc(DepthFunction.Lequal);
        particleShader.Use();
        particleShader.Set("uViewProjection", lastView * lastProjection);
        particleShader.Set("uTime", timeSeconds);
        particles.Draw();
        gl.DepthMask(true);
        gl.DepthFunc(DepthFunction.Less);
        gl.Disable(EnableCap.Blend);
        if (SetFrameError("粒子")) return;

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, bloomTarget.Framebuffer);
        gl.Viewport(0, 0, (uint)bloomTarget.Width, (uint)bloomTarget.Height);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        postShader.Use();
        hdrTarget.BindEmissionTexture(TextureUnit.Texture0);
        postShader.Set("uHdr", 0);
        postShader.Set("uBloom", 0);
        postShader.Set("uMode", 1);
        postShader.Set("uResolution", new Vector2(sceneWidth, sceneHeight));
        fullscreen.Draw();
        if (SetFrameError("热发光 Bloom")) return;

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        postShader.Use();
        hdrTarget.BindTexture(TextureUnit.Texture0);
        bloomTarget.BindTexture(TextureUnit.Texture1);
        backgroundTexture.Bind(TextureUnit.Texture2);
        hdrTarget.BindDepthTexture(TextureUnit.Texture3);
        postShader.Set("uHdr", 0);
        postShader.Set("uBloom", 1);
        postShader.Set("uBackground", 2);
        postShader.Set("uDepth", 3);
        postShader.Set("uMode", 0);
        postShader.Set("uResolution", new Vector2(sceneWidth, sceneHeight));
        postShader.Set("uViewportAspect", width / (float)height);
        postShader.Set("uBackgroundAspect", backgroundTexture.Width / (float)backgroundTexture.Height);
        postShader.Set("uVignette", activeQuality == ForgeRenderQuality.High ? .09f : .07f);
        postShader.Set("uBloomStrength", BloomStrengthFor(settings.BloomEnabled, qualityDegraded || activeQuality != ForgeRenderQuality.High, nextSnapshot.Heat));
        postShader.Set("uDofStrength", settings.DepthOfFieldEnabled && !qualityDegraded ? .65f : 0f);
        postShader.Set("uExposure", ExposureFor(snapshot.State));
        postShader.Set("uHeatAmount", (float)Math.Clamp(nextSnapshot.Heat, 0, 1));
        postShader.Set("uTime", timeSeconds);
        postShader.Set("uHeatCenter", HeatScreenCenter(snapshot.State));
        postShader.Set("uContactShadow", ContactShadow(snapshot.State));
        fullscreen.Draw();
        gl.Enable(EnableCap.DepthTest);
        if (SetFrameError("最终合成")) return;
        frameTimer.Stop();
        if (frameTimer.Elapsed.TotalMilliseconds > 20)
        {
            slowFrameSeconds += elapsedSeconds;
            if (slowFrameSeconds >= 3 && activeQuality == ForgeRenderQuality.High)
            {
                qualityDegraded = true;
            }
        }
        else
        {
            slowFrameSeconds = Math.Max(0, slowFrameSeconds - elapsedSeconds * .5);
        }
    }

    private bool SetFrameError(string stage)
    {
        if (gl is null) return false;
        var error = gl.GetError();
        if (error == GLEnum.NoError) return false;
        LastRenderError = $"OpenGL {stage}阶段失败：{error}";
        return true;
    }

    private void ApplyRequestedQuality()
    {
        if (requestedQuality == activeQuality || gl is null || resources is null)
        {
            return;
        }

        var resolution = requestedQuality switch
        {
            ForgeRenderQuality.High => 1536,
            ForgeRenderQuality.Medium => 1024,
            _ => 768
        };
        try
        {
            var replacement = resources.Register(new ShadowMap(gl, resolution, isGles));
            shadowMap?.Dispose();
            shadowMap = replacement;
            activeQuality = requestedQuality;
            qualityDegraded = false;
            slowFrameSeconds = 0;
            ShadowWarning = null;
        }
        catch (Exception ex)
        {
            while (gl.GetError() != GLEnum.NoError) { }
            ShadowWarning = $"Unable to change Forge shadow quality: {ex.Message}";
        }
    }

    /// <summary>Debug-only framebuffer capture written as an uncompressed 24-bit BMP.</summary>
    public unsafe bool CaptureFrame(string path, int width, int height)
    {
        if (gl is null || !IsInitialized || width <= 0 || height <= 0) return false;
        var pixels = new byte[width * height * 3];
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, outputFramebuffer);
        gl.ReadBuffer(outputFramebuffer == 0 ? ReadBufferMode.Back : ReadBufferMode.ColorAttachment0);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        gl.Finish();
        fixed (byte* pointer = pixels)
        {
            gl.ReadPixels(0, 0, (uint)width, (uint)height, (GLEnum)PixelFormat.Rgb, (GLEnum)PixelType.UnsignedByte, pointer);
        }
        if (gl.GetError() != GLEnum.NoError) return false;
        var rowSize = (width * 3 + 3) & ~3;
        var imageSize = rowSize * height;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4D42);
        writer.Write(54 + imageSize);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        var padding = rowSize - width * 3;
        for (var y = 0; y < height; y++)
        {
            var offset = y * width * 3;
            for (var x = 0; x < width; x++)
            {
                writer.Write(pixels[offset + x * 3 + 2]);
                writer.Write(pixels[offset + x * 3 + 1]);
                writer.Write(pixels[offset + x * 3]);
            }
            for (var p = 0; p < padding; p++) writer.Write((byte)0);
        }
        return true;
    }

    public bool TryMapPointer(Vector2 pixel, Vector2 viewport, out Vector2 lattice)
    {
        lattice = default;
        if (snapshot is null) return false;
        var ray = ForgeRaycaster.CreateRay(pixel, viewport, lastView, lastProjection);
        if (ForgeRaycaster.TryHitOccupiedWorkpiece(
            ray,
            workpieceWorld,
            shapeTransition.Cells,
            snapshot.IsFlipped,
            out lattice,
            out _,
            snapshot.RecipeId,
            snapshot.RecipeShape))
        {
            return true;
        }

        // A cursor can visibly sit on a changing bevel while narrowly missing the
        // occupied-cell ray test. Magnetize that plane hit to a nearby live cell.
        if (!ForgeRaycaster.TryHitWorkpiece(ray, workpieceWorld, out var planeLattice, out _, snapshot.RecipeId) ||
            !WorkpieceMeshBuilder.TrySnapToOccupied(
                shapeTransition.Cells,
                planeLattice,
                out var snapped,
                searchRadiusX: 10,
                searchRadiusY: 8))
        {
            return false;
        }

        lattice = snapshot.IsFlipped
            ? new Vector2(snapped.X, 1 - snapped.Y)
            : snapped;
        return true;
    }

    public bool TryHitStation(
        Vector2 pixel,
        Vector2 viewport,
        ForgeStateId state,
        bool canProceed,
        out ForgeStationId station)
    {
        var ray = ForgeRaycaster.CreateRay(pixel, viewport, lastView, lastProjection);
        return ForgeWorkbenchLayout.TryHitInteractive(ray, state, canProceed, out station);
    }

    private void HandleVisualEvent(ForgeVisualEvent visual)
    {
        if (particles is null) return;
        var visualY = snapshot?.IsFlipped == true ? 1 - visual.Y : visual.Y;
        var point = Vector3.Transform(
            WorkpieceMeshBuilder.LocalPoint(
                (float)visual.X,
                (float)visualY,
                WorkpieceSurfaceHeight(visual.X, visualY) + .018f,
                snapshot?.RecipeId,
                snapshot?.RecipeShape),
            workpieceWorld);
        switch (visual.Kind)
        {
            case ForgeVisualEventKind.HammerStrike:
                toolMotion.Strike(point, visual.Intensity);
                break;
            case ForgeVisualEventKind.QuenchStarted:
                workpieceMotion.SetQuench(0, 0);
                particles.EmitSteam(QuenchSurfacePoint(), (float)Math.Max(.65, visual.Intensity));
                break;
            case ForgeVisualEventKind.QuenchUpdated:
                workpieceMotion.SetQuench(visual.Y, visual.Intensity);
                particles.EmitSteam(QuenchSurfacePoint(), (float)Math.Clamp(.25 + visual.Intensity, .35, 1.35));
                break;
            case ForgeVisualEventKind.QuenchCompleted:
                particles.EmitSteam(QuenchSurfacePoint(), (float)Math.Max(.85, visual.Intensity));
                break;
            case ForgeVisualEventKind.GrindingStroke:
                workpieceMotion.SetGrindingStroke(visual.X, visual.Intensity);
                grindingContactImpulse = Math.Max(
                    grindingContactImpulse,
                    GrindingContactIntensity(
                        snapshot?.GrindingEngaged ?? false,
                        snapshot?.GrinderSpeed ?? 0,
                        visual.Intensity,
                        snapshot?.GrindingQuality ?? 0));
                break;
            case ForgeVisualEventKind.BellowsPumped:
                toolMotion.PumpBellows(visual.Intensity);
                particles.EmitFire(new Vector3(-3.05f, -.52f, .18f), (float)visual.Intensity);
                var overheat = OverheatVisualIntensity(
                    snapshot?.State ?? ForgeStateId.Heating,
                    snapshot?.HeatBand ?? ForgeHeatBand.Cold,
                    snapshot?.Heat ?? 0);
                if (overheat > 0)
                {
                    particles.EmitOverheatScale(
                        HeatingWorkpieceSparkOrigin(),
                        overheat,
                        Math.Clamp((int)(8 + overheat * 18 + visual.Intensity * 10), 8, 42));
                }
                break;
        }
    }

    private void ApplyHammerImpact(Vector3 point, float intensity)
    {
        shapeTransition.Commit(settings.ReducedMotion);
        hammerSparkOrigin = point + Vector3.UnitY * .035f;
        hammerSparkIntensity = intensity;
        hammerSparkHeat = snapshot?.HeatBand ?? ForgeHeatBand.Cold;
        particles?.EmitHammer(hammerSparkOrigin, intensity, hammerSparkHeat);
        hammerSparkAfterglow = HammerSparkAfterglowDuration(intensity);
        hammerSparkAccumulator = 0;
        camera.Impact(intensity, settings.ReducedMotion);
        impactPoint = point;
        impactFlash = Math.Clamp(.35f + intensity * .65f, 0, 1);
        workpieceMotion.Impact(intensity);
    }

    private void UpdateHammerSparkAfterglow(float elapsedSeconds)
    {
        if (particles is null || hammerSparkAfterglow <= 0)
        {
            return;
        }

        hammerSparkAfterglow = Math.Max(0, hammerSparkAfterglow - elapsedSeconds);
        hammerSparkAccumulator += elapsedSeconds * (72f + hammerSparkIntensity * 58f);
        var count = Math.Min(8, (int)hammerSparkAccumulator);
        if (count <= 0)
        {
            return;
        }

        hammerSparkAccumulator -= count;
        particles.EmitHammerAfterglow(hammerSparkOrigin, hammerSparkIntensity, hammerSparkHeat, count);
    }

    private void UpdateGrindingContact(float elapsedSeconds)
    {
        if (particles is null || snapshot is null ||
            snapshot.State != ForgeStateId.Grinding || !snapshot.GrindingEngaged ||
            snapshot.GrinderSpeed <= .01)
        {
            grindingContactImpulse = 0;
            grindingSparkAccumulator = 0;
            return;
        }

        grindingContactImpulse = Math.Max(0, grindingContactImpulse - elapsedSeconds * 2.8f);
        var restingContact = GrindingContactIntensity(
            engaged: true,
            snapshot.GrinderSpeed,
            strokeSpeed: .08,
            snapshot.GrindingQuality);
        var intensity = Math.Max(restingContact, grindingContactImpulse);
        var quality = (float)Math.Clamp(snapshot.GrindingQuality, 0, 1);
        var rate = GrindingSparkRate(qualityDegraded, intensity, quality);
        grindingSparkAccumulator += elapsedSeconds * rate;
        var count = Math.Min(14, (int)grindingSparkAccumulator);
        if (count <= 0)
        {
            return;
        }

        grindingSparkAccumulator -= count;
        particles.EmitGrind(
            workpieceMotion.CurrentGrindingContactPoint,
            intensity,
            quality,
            count);
    }

    internal static float GrindingContactIntensity(
        bool engaged,
        double wheelSpeed,
        double strokeSpeed,
        double quality)
    {
        if (!engaged || wheelSpeed <= .01)
        {
            return 0;
        }

        var wheel = (float)Math.Clamp(wheelSpeed / 1.15, .08, 1.18);
        var movement = (float)Math.Clamp(strokeSpeed / 1.05, .06, 1.25);
        var consistency = (float)Math.Clamp(.72 + quality * .28, .72, 1);
        return Math.Clamp((.12f + movement * .88f) * (.42f + wheel * .58f) * consistency, .08f, 1.35f);
    }

    internal static float GrindingSparkRate(bool degraded, float intensity, float quality)
    {
        intensity = Math.Clamp(intensity, 0, 1.35f);
        quality = Math.Clamp(quality, 0, 1);
        var baseRate = degraded ? 20f : 30f;
        var contactRate = degraded ? 38f : 68f;
        var finishFactor = .85f + quality * .35f;
        return (baseRate + intensity * contactRate) * finishFactor;
    }

    internal static float HammerSparkAfterglowDuration(float intensity) =>
        .10f + Math.Clamp(intensity, 0, 1.4f) * .07f;

    private float WorkpieceSurfaceHeight(double x, double y)
    {
        if (snapshot is null || shapeTransition.Cells.Count == 0)
        {
            return .08f;
        }
        return WorkpieceMeshBuilder.SurfaceHeightAt(
            shapeTransition.Cells,
            new Vector2((float)x, (float)y),
            snapshot.RecipeId,
            snapshot.RecipeShape);
    }

    private Vector3 QuenchSurfacePoint() => activeQuenchMedium == QuenchMedium.Oil
        ? new Vector3(3.35f, -.26f, .18f)
        : new Vector3(2.12f, -.20f, .30f);

    private Vector3 HeatingWorkpieceSparkOrigin()
    {
        const double sampleX = .56;
        const double sampleY = .50;
        var local = WorkpieceMeshBuilder.LocalPoint(
            (float)sampleX,
            (float)sampleY,
            WorkpieceSurfaceHeight(sampleX, sampleY) + .018f,
            snapshot?.RecipeId,
            snapshot?.RecipeShape);
        return Vector3.Transform(local, workpieceWorld);
    }

    private Texture2D LoadWorkshopBackground(GL gl)
    {
        var backgroundPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Forge",
            "workshop_background.png");
        using var stream = File.OpenRead(backgroundPath);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidOperationException("The Forge workshop background could not be decoded.");
        var maximumWidth = ForgeGraphicsSettings.MaximumBackgroundWidth(textureQuality);
        var scale = Math.Min(1f, maximumWidth / (float)codec.Info.Width);
        var width = Math.Max(1, (int)MathF.Round(codec.Info.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(codec.Info.Height * scale));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var sampledBitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, sampledBitmap.GetPixels());
        byte[] pixels;
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            pixels = sampledBitmap.Bytes.ToArray();
        }
        else
        {
            using var fallbackStream = File.OpenRead(backgroundPath);
            using var native = SKBitmap.Decode(fallbackStream) ??
                throw new InvalidOperationException($"The Forge workshop background could not be decoded: {result}");
            using var resized = native.Resize(info, SKFilterQuality.Medium) ??
                throw new InvalidOperationException("The Forge workshop background could not be resized.");
            pixels = resized.Bytes.ToArray();
        }

        using var texture = new PbrTextureAsset(
            "ForgeWorkshopBackground",
            width,
            height,
            pixels,
            srgb: true,
            repeat: false,
            $"forge-background:{textureQuality}:{width}x{height}",
            ForgeTextureRole.Background);
        return CreateGpuTexture(texture);
    }

    private GpuMeshAsset LoadRenderModel(string name) => LoadRenderModel(name, resources);

    private void UploadNextPreparedProceduralTexture()
    {
        if (proceduralTextureUploadStage >= 2 || gl is null || resources is null ||
            !ForgeRenderWarmup.TryGetProceduralTextures(out var prepared) || prepared is null)
        {
            return;
        }

        if (proceduralTextureUploadStage == 0)
        {
            detailAtlas = resources.Register(new Texture2D(
                gl,
                ProceduralTextureAtlas.Size,
                ProceduralTextureAtlas.Size,
                prepared.Color,
                srgb: false));
        }
        else
        {
            normalAtlas = resources.Register(new Texture2D(
                gl,
                ProceduralTextureAtlas.Size,
                ProceduralTextureAtlas.Size,
                prepared.Normal,
                srgb: false));
        }

        proceduralTextureUploadStage++;
    }

    private void StartNextStaticModelLoad()
    {
        if (staticModelLoadTask is not null || staticModelLoadIndex >= StaticModelLoadOrder.Length)
        {
            return;
        }

        staticModelLoadCancellation ??= new CancellationTokenSource();
        var cancellationToken = staticModelLoadCancellation.Token;
        var name = StaticModelLoadOrder[staticModelLoadIndex];
        var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Forge", "Models", $"{name}.glb");
        var backgroundLoader = suppliedAssetLoader ?? new ForgeGlbAssetLoader(textureQuality);
        staticModelLoadTask = Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = backgroundLoader.LoadMesh(new Uri(assetPath));
            var prepared = PrepareMeshAsset(asset, compressionCapabilities);
            if (!cancellationToken.IsCancellationRequested)
            {
                return prepared;
            }

            prepared.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            return prepared;
        }, cancellationToken);
    }

    private void LoadNextStaticModel()
    {
        if (staticModelLoadDelayFrames > 0)
        {
            staticModelLoadDelayFrames--;
            return;
        }

        if (staticModelLoadIndex >= StaticModelLoadOrder.Length)
        {
            return;
        }

        StartNextStaticModelLoad();
        if (staticModelLoadTask is not { IsCompleted: true } completedLoad)
        {
            return;
        }

        staticModelLoadTask = null;
        var name = StaticModelLoadOrder[staticModelLoadIndex++];
        using var prepared = completedLoad.GetAwaiter().GetResult();
        var model = UploadRenderModel(prepared.Asset, resources, compressedTextures: prepared.CompressedTextures);
        switch (name)
        {
            case "workbench": workbenchMesh = model; break;
            case "hearth": hearthMesh = model; break;
            case "anvil": anvilMesh = model; break;
            case "hammer": hammerMesh = model; break;
            case "bellows": bellowsMesh = model; break;
            case "quench-water": waterVatMesh = model; break;
            case "quench-oil": oilVatMesh = model; break;
            case "grinder-stand": grinderStandMesh = model; break;
            case "grinder-wheel": grinderWheelMesh = model; break;
            case "tongs": tongsMesh = model; break;
        }

        StartNextStaticModelLoad();
    }

    private GpuMeshAsset LoadRenderModel(
        string name,
        GlResourceRegistry? resourceScope,
        Action<MeshAsset>? inspectAsset = null)
    {
        if (gl is null || resourceScope is null || textureCache is null)
        {
            throw new InvalidOperationException("OpenGL resources are not initialized.");
        }

        var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Forge", "Models", $"{name}.glb");
        using var asset = assetLoader.LoadMesh(new Uri(assetPath));
        return UploadRenderModel(asset, resourceScope, inspectAsset);
    }

    private GpuMeshAsset UploadRenderModel(
        MeshAsset asset,
        GlResourceRegistry? resourceScope,
        Action<MeshAsset>? inspectAsset = null,
        IReadOnlyDictionary<string, ForgeCompressedTexture>? compressedTextures = null)
    {
        if (gl is null || resourceScope is null || textureCache is null)
        {
            throw new InvalidOperationException("OpenGL resources are not initialized.");
        }

        inspectAsset?.Invoke(asset);
        var buffer = new MeshBuffer(gl, asset.Mesh);
        var leases = new List<SharedGpuTextureCache.TextureLease>();
        try
        {
            Texture2D? Acquire(PbrTextureAsset? texture)
            {
                if (texture is null) return null;
                var lease = UploadAndReleaseCpuTexture(texture, source =>
                {
                    return textureCache.Acquire(
                        source.ContentKey,
                        () => CreateGpuTexture(source, compressedTextures));
                });
                leases.Add(lease);
                return lease.Texture;
            }

            var gpuMaterials = asset.Materials.Select(material => new GpuPbrMaterial(
                material.Name,
                material.BaseColor,
                material.Metallic,
                material.Roughness,
                material.HasNormalMap,
                material.HasAmbientOcclusion,
                material.MetallicRoughnessTexture is not null,
                material.EmissiveFactor ?? Vector3.Zero,
                Acquire(material.BaseColorTexture),
                Acquire(material.NormalTexture),
                Acquire(material.MetallicRoughnessTexture),
                Acquire(material.OcclusionTexture),
                Acquire(material.EmissiveTexture))).ToArray();
            var primitives = asset.Primitives is { Count: > 0 }
                ? asset.Primitives
                : [new MeshPrimitiveAsset(0, asset.Mesh.Indices.Length, 0, asset.Materials.FirstOrDefault()?.Name ?? ForgeMaterialId.Iron.ToString())];
            return resourceScope.Register(new GpuMeshAsset(buffer, primitives, gpuMaterials, leases));
        }
        catch
        {
            buffer.Dispose();
            foreach (var lease in leases) lease.Dispose();
            throw;
        }
    }

    internal static T UploadAndReleaseCpuTexture<T>(PbrTextureAsset texture, Func<PbrTextureAsset, T> upload)
    {
        try
        {
            return upload(texture);
        }
        finally
        {
            texture.Dispose();
        }
    }

    private Texture2D CreateGpuTexture(
        PbrTextureAsset texture,
        IReadOnlyDictionary<string, ForgeCompressedTexture>? compressedTextures = null)
    {
        if (gl is null) throw new InvalidOperationException("OpenGL resources are not initialized.");
        var compressed = compressedTextures?.GetValueOrDefault(texture.ContentKey);
        if (compressed is not null ||
            ForgeTextureCompressor.TryCompress(texture, compressionCapabilities, out compressed))
        {
            try
            {
                return new Texture2D(gl, compressed!, texture.Repeat);
            }
            catch
            {
                ClearGlErrors(gl);
            }
        }

        return new Texture2D(gl, texture.Width, texture.Height, texture.Rgba, texture.Srgb, texture.Repeat);
    }

    private static PreparedMeshAsset PrepareMeshAsset(
        MeshAsset asset,
        ForgeTextureCompressionCapabilities capabilities)
    {
        try
        {
            var compressedTextures = new Dictionary<string, ForgeCompressedTexture>(StringComparer.Ordinal);
            foreach (var texture in asset.Materials
                         .SelectMany(material => new[]
                         {
                             material.BaseColorTexture,
                             material.NormalTexture,
                             material.MetallicRoughnessTexture,
                             material.OcclusionTexture,
                             material.EmissiveTexture
                         })
                         .Where(texture => texture is not null)
                         .Cast<PbrTextureAsset>()
                         .DistinctBy(texture => texture.ContentKey))
            {
                if (ForgeTextureCompressor.TryCompress(texture, capabilities, out var compressed) && compressed is not null)
                {
                    compressedTextures.Add(texture.ContentKey, compressed);
                }
            }

            return new PreparedMeshAsset(asset, compressedTextures);
        }
        catch
        {
            asset.Dispose();
            throw;
        }
    }

    private static void ClearGlErrors(GL gl)
    {
        for (var index = 0; index < 16 && gl.GetError() != GLEnum.NoError; index++) { }
    }

    private void EnsureWeaponResources(string? recipeId)
    {
        var normalizedRecipeId = recipeId?.ToLowerInvariant();
        if (string.Equals(activeWeaponRecipeId, normalizedRecipeId, StringComparison.Ordinal))
        {
            return;
        }

        DisposeWeaponResources();
        var assetName = normalizedRecipeId switch
        {
            "duelling-longsword" => "weapon-duelling-longsword",
            "basilard" => "weapon-basilard",
            "bearded-axe" => "weapon-bearded-axe",
            _ => null
        };
        if (assetName is null || gl is null)
        {
            return;
        }

        weaponResources = new GlResourceRegistry();
        try
        {
            activeWeaponMesh = LoadRenderModel(
                assetName,
                weaponResources,
                asset => workpieceMotion.SetGrindingAlignment(
                    ForgeGrindingAlignment.FromMesh(normalizedRecipeId, asset)));
            activeWeaponRecipeId = normalizedRecipeId;
        }
        catch
        {
            DisposeWeaponResources();
            throw;
        }
    }

    private void DrawStageGeometry(ShaderProgram shader, ForgeStateId state, bool includeWorkpiece)
    {
        DrawMesh(shader, workbenchMesh, ForgeWorkbenchLayout.WorkbenchTransform);
        DrawMesh(shader, hearthMesh, ForgeWorkbenchLayout.Hearth.Transform, ForgeStationId.Hearth);
        DrawMesh(shader, bellowsMesh, toolMotion.BellowsTransform, ForgeStationId.Bellows);
        DrawMesh(shader, anvilMesh, ForgeWorkbenchLayout.Anvil.Transform, ForgeStationId.Anvil);
        DrawMesh(shader, waterVatMesh, ForgeWorkbenchLayout.WaterVat.Transform, ForgeStationId.WaterVat);
        DrawMesh(shader, oilVatMesh, ForgeWorkbenchLayout.OilVat.Transform, ForgeStationId.OilVat);
        DrawMesh(shader, grinderStandMesh, ForgeWorkbenchLayout.Grinder.Transform, ForgeStationId.Grinder);
        DrawMesh(shader, grinderWheelMesh, toolMotion.GrinderWheelTransform, ForgeStationId.Grinder);
        DrawMesh(shader, hammerMesh, toolMotion.HammerTransform(state), ForgeStationId.Hammer);
        if (includeWorkpiece && workpieceMesh is not null)
        {
            if (ShouldRenderFinishedWeapon(state, snapshot?.GrindingEngaged ?? false) &&
                FinishedWeaponFor(snapshot?.RecipeId) is { } finishedWeapon)
            {
                // The presentation asset contains the blade, shoulder collar, guard and grip
                // as one closed assembly. Drawing it as a single mesh avoids the overlapping
                // dynamic blade/fittings surfaces that made the hilt appear to pass through steel.
                DrawMesh(shader, finishedWeapon, workpieceWorld);
            }
            else
            {
                shader.Set("uUseAssetMaterial", 0);
                shader.Set("uToolHighlight", workpieceHovered ? .72f : 0f);
                shader.Set("uModel", workpieceWorld);
                workpieceMesh.Draw();
            }
            DrawMesh(shader, tongsMesh, TongsTransform(state));
        }
        else
        {
            DrawMesh(shader, tongsMesh, TongsRestTransform());
        }
    }

    private void DrawMesh(ShaderProgram shader, GpuMeshAsset? mesh, Matrix4x4 transform, ForgeStationId? station = null)
    {
        if (mesh is null) return;
        shader.Set("uToolHighlight", station is not null && hoveredStation == station ? 1f : 0f);
        shader.Set("uModel", transform);
        if (!ReferenceEquals(shader, sceneShader))
        {
            mesh.Buffer.Draw();
            return;
        }

        foreach (var primitive in mesh.Primitives)
        {
            var material = primitive.MaterialIndex >= 0 && primitive.MaterialIndex < mesh.Materials.Count
                ? mesh.Materials[primitive.MaterialIndex]
                : null;
            BindAssetMaterial(shader, material);
            mesh.Buffer.DrawRange((uint)primitive.FirstIndex, (uint)primitive.IndexCount);
        }
        shader.Set("uUseAssetMaterial", 0);
    }

    private void BindAssetMaterial(ShaderProgram shader, GpuPbrMaterial? material)
    {
        if (material is null || whiteTexture is null || flatNormalTexture is null || defaultOrmTexture is null || blackTexture is null)
        {
            shader.Set("uUseAssetMaterial", 0);
            return;
        }

        // A malformed or partially exported GLB must never turn a submesh into
        // a white placeholder. Keep the same material id on the vertex stream
        // and let the procedural PBR atlas render the affected primitive.
        // Normal/ORM/emission maps can safely fall back independently below.
        if (material.BaseColor is null)
        {
            shader.Set("uUseAssetMaterial", 0);
            return;
        }

        (material.BaseColor ?? whiteTexture).Bind(TextureUnit.Texture5);
        (material.Normal ?? flatNormalTexture).Bind(TextureUnit.Texture6);
        (material.MetallicRoughness ?? defaultOrmTexture).Bind(TextureUnit.Texture7);
        (material.Occlusion ?? material.MetallicRoughness ?? defaultOrmTexture).Bind(TextureUnit.Texture8);
        (material.Emissive ?? blackTexture).Bind(TextureUnit.Texture9);
        shader.Set("uAssetBaseColor", 5);
        shader.Set("uAssetNormal", 6);
        shader.Set("uAssetOrm", 7);
        shader.Set("uAssetOcclusion", 8);
        shader.Set("uAssetEmissive", 9);
        shader.Set("uAssetBaseFactor", material.BaseColorFactor);
        var normalScale = !material.HasNormalMap ? 0f : material.Name switch
        {
            nameof(ForgeMaterialId.Workpiece) or nameof(ForgeMaterialId.WorkpieceEdge) => .24f,
            nameof(ForgeMaterialId.Iron) or nameof(ForgeMaterialId.IronDark) or nameof(ForgeMaterialId.Brass) => .30f,
            nameof(ForgeMaterialId.Wood) or nameof(ForgeMaterialId.WoodDark) => .16f,
            nameof(ForgeMaterialId.Leather) => .20f,
            nameof(ForgeMaterialId.Stone) => .42f,
            nameof(ForgeMaterialId.Brick) => .28f,
            _ => .18f
        };
        shader.Set("uAssetFactors", new Vector4(
            material.Metallic,
            material.Roughness,
            material.HasAmbientOcclusion || material.HasMetallicRoughnessMap ? 1f : 0f,
            normalScale));
        shader.Set("uAssetEmissiveFactor", material.EmissiveFactor);
        shader.Set("uUseAssetMaterial", 1);
    }

    private GpuMeshAsset? FinishedWeaponFor(string? recipeId) =>
        string.Equals(activeWeaponRecipeId, recipeId, StringComparison.OrdinalIgnoreCase)
            ? activeWeaponMesh
            : null;

    internal static bool ShouldRenderFinishedWeapon(ForgeStateId state, bool grindingEngaged) =>
        state is ForgeStateId.Grinding or ForgeStateId.Inspection or ForgeStateId.Result;

    private static bool ShouldDrawWorkpiece(ForgeStateId state) => state is not ForgeStateId.RecipeSelect and not ForgeStateId.MaterialSelect;

    private Matrix4x4 TongsTransform(ForgeStateId state)
    {
        if (state is ForgeStateId.Grinding or ForgeStateId.Inspection or ForgeStateId.Result)
        {
            return TongsRestTransform();
        }

        return TongsAttachmentTransform(workpieceWorld);
    }

    internal static Matrix4x4 TongsAttachmentTransform(Matrix4x4 attachedWorkpieceWorld) =>
        Matrix4x4.CreateScale(.62f) *
        // The jaw center is x=1.22 in the authored mesh. Centering it on the
        // billet mid-plane makes both jaws bite around the metal instead of
        // floating above and behind it.
        Matrix4x4.CreateTranslation(-2.05f, .035f, .015f) *
        attachedWorkpieceWorld;

    private static Matrix4x4 TongsRestTransform() =>
        Matrix4x4.CreateScale(.48f) *
        Matrix4x4.CreateRotationY(-.16f) *
        Matrix4x4.CreateRotationZ(.05f) *
        Matrix4x4.CreateTranslation(-1.52f, -.80f, 1.02f);

    private static Matrix4x4 StageTransform(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => Matrix4x4.CreateScale(.82f) * Matrix4x4.CreateTranslation(0, -.58f, 0),
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => Matrix4x4.CreateScale(.78f) * Matrix4x4.CreateTranslation(0, -.35f, 0),
        ForgeStateId.Quenching => Matrix4x4.CreateScale(1.15f) * Matrix4x4.CreateTranslation(0, -.62f, 0),
        ForgeStateId.Grinding => Matrix4x4.CreateScale(1.12f) * Matrix4x4.CreateTranslation(0, -.12f, 0),
        _ => Matrix4x4.Identity
    };

    private void SetMaterials(ShaderProgram shader)
    {
        shader.Use();
        for (var i = 0; i < materials.Count; i++)
        {
            var material = materials[(ForgeMaterialId)i];
            shader.Set($"uMaterials[{i}]", new Vector4(material.Albedo, material.Metallic));
            shader.Set($"uMaterialExtra[{i}]", new Vector4(material.Roughness, material.AmbientOcclusion, material.TextureScale, 0));
            shader.Set($"uMaterialEmissive[{i}]", material.Emissive);
        }
    }

    private static Vector3 ForgeLightPosition(ForgeStateId state) => new(-3.05f, -.18f, .28f);

    internal static Vector3 TaskLightPositionFor(ForgeStateId state, QuenchMedium medium = QuenchMedium.Water) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => new(-2.72f, 1.42f, 1.38f),
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => new(.42f, 2.85f, 1.72f),
        ForgeStateId.Quenching when medium == QuenchMedium.Oil => new(3.20f, 1.74f, 1.42f),
        ForgeStateId.Quenching => new(2.02f, 1.74f, 1.48f),
        ForgeStateId.Grinding => new(2.72f, 2.08f, -.12f),
        ForgeStateId.Inspection or ForgeStateId.Result => new(.28f, 2.72f, 2.82f),
        _ => new(.15f, 3.20f, 2.60f)
    };

    internal static Vector3 TaskLightTargetFor(ForgeStateId state, QuenchMedium medium = QuenchMedium.Water) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => new(-3.05f, -.26f, .05f),
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => new(-.10f, .36f, .12f),
        ForgeStateId.Quenching when medium == QuenchMedium.Oil => new(3.35f, -.14f, .18f),
        ForgeStateId.Quenching => new(2.12f, -.14f, .30f),
        ForgeStateId.Grinding => new(2.72f, .18f, -1.25f),
        ForgeStateId.Inspection or ForgeStateId.Result => new(.05f, .92f, .42f),
        _ => new(0, -.18f, 0)
    };

    internal static Vector3 TaskLightDirectionFor(ForgeStateId state, QuenchMedium medium = QuenchMedium.Water) =>
        Vector3.Normalize(TaskLightTargetFor(state, medium) - TaskLightPositionFor(state, medium));

    internal static Vector3 RimLightDirectionFor(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => Vector3.Normalize(new Vector3(.68f, .38f, -.62f)),
        ForgeStateId.Grinding => Vector3.Normalize(new Vector3(-.74f, .42f, .52f)),
        _ => Vector3.Normalize(new Vector3(-.62f, .46f, -.64f))
    };

    internal static float RimLightIntensityFor(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => .42f,
        ForgeStateId.Inspection or ForgeStateId.Result => .88f,
        ForgeStateId.Grinding => .72f,
        _ => .62f
    };

    internal static Vector3 TaskLightColorFor(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => new(1.00f, .63f, .31f),
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => new(1.00f, .78f, .54f),
        ForgeStateId.Quenching => new(.78f, .86f, 1.00f),
        ForgeStateId.Grinding => new(.92f, .90f, .78f),
        ForgeStateId.Inspection or ForgeStateId.Result => new(.88f, .92f, 1.00f),
        _ => new(.92f, .82f, .68f)
    };

    internal static float TaskLightIntensityFor(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => 4.35f,
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => 5.25f,
        ForgeStateId.Quenching => 4.45f,
        ForgeStateId.Grinding => 5.10f,
        ForgeStateId.Inspection or ForgeStateId.Result => 5.65f,
        _ => 3.65f
    };

    internal static float DirectionalIntensityFor(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => .72f,
        ForgeStateId.Inspection or ForgeStateId.Result => 1.12f,
        _ => .94f
    };

    internal static float EnvironmentStrengthFor(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => .92f,
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => 1.14f,
        ForgeStateId.Quenching => 1.10f,
        ForgeStateId.Grinding => 1.16f,
        ForgeStateId.Inspection or ForgeStateId.Result => 1.24f,
        _ => 1.04f
    };

    internal static float WorkpieceLightIntensityFor(double heat)
    {
        var amount = (float)Math.Clamp((heat - .54) / .50, 0, 1);
        amount = amount * amount * (3 - 2 * amount);
        return amount * 7.2f;
    }

    internal static Vector3 WorkpieceLightColorFor(double heat)
    {
        var incandescent = IncandescenceColorFor(heat);
        var peak = Math.Max(.001f, Math.Max(incandescent.X, Math.Max(incandescent.Y, incandescent.Z)));
        return incandescent / peak;
    }

    internal static Vector3 IncandescenceColorFor(double heat)
    {
        var value = (float)Math.Clamp(heat, 0, 1.1);
        if (value < .18f) return Vector3.Zero;
        if (value < .38f)
        {
            return Vector3.Lerp(new Vector3(.10f, .001f, 0), new Vector3(.62f, .018f, .001f), (value - .18f) / .20f);
        }
        if (value < .58f)
        {
            return Vector3.Lerp(new Vector3(.62f, .018f, .001f), new Vector3(1.00f, .075f, .004f), (value - .38f) / .20f);
        }
        if (value < .82f)
        {
            return Vector3.Lerp(new Vector3(1.00f, .075f, .004f), new Vector3(1.16f, .28f, .020f), (value - .58f) / .24f);
        }
        return Vector3.Lerp(new Vector3(1.16f, .28f, .020f), new Vector3(1.28f, .61f, .105f), (value - .82f) / .28f);
    }

    internal static (Vector3 Start, Vector3 End) WorkpieceGlowSegment(
        Matrix4x4 world,
        string? recipeId,
        ShapeTemplateDefinition? shape)
    {
        var profile = WorkpieceMeshBuilder.ProfileFor(recipeId, shape);
        var startX = profile.Kind == ForgeWorkpieceKind.Axe ? .30f : .16f;
        var endX = profile.Kind == ForgeWorkpieceKind.Axe ? .70f : .84f;
        var start = WorkpieceMeshBuilder.LocalPoint(startX, .5f, .055f, recipeId, shape);
        var end = WorkpieceMeshBuilder.LocalPoint(endX, .5f, .055f, recipeId, shape);
        return (Vector3.Transform(start, world), Vector3.Transform(end, world));
    }

    internal static Matrix4x4 ShadowViewProjectionFor(ForgeStateId state, QuenchMedium medium = QuenchMedium.Water)
    {
        var center = TaskLightTargetFor(state, medium);
        center.Y = -.18f;
        var span = state switch
        {
            ForgeStateId.Heating or ForgeStateId.Reheat => 3.5f,
            ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => 3.15f,
            ForgeStateId.Quenching => 3.05f,
            ForgeStateId.Grinding => 3.0f,
            ForgeStateId.Inspection or ForgeStateId.Result => 2.8f,
            _ => 6.6f
        };
        var lightOffset = Vector3.Normalize(new Vector3(-4.5f, 7.5f, 4.8f)) * 10.5f;
        var view = Matrix4x4.CreateLookAt(center + lightOffset, center, Vector3.UnitY);
        var projection = Matrix4x4.CreateOrthographicOffCenter(-span, span, -span, span, .2f, 18f);
        return view * projection;
    }

    private static float ForgeLightIntensity(ForgeStateId state, double heat, float time)
    {
        var flicker = 1 + MathF.Sin(time * 9.7f) * .045f + MathF.Sin(time * 17.3f) * .025f;
        var baseIntensity = state is ForgeStateId.Heating or ForgeStateId.Reheat ? 7.15f : 3.35f;
        return (baseIntensity + (float)Math.Clamp(heat, 0, 1) * 3.15f) * flicker;
    }

    internal static float ForgeProgress(string? recipeId, int strikes, int recommendedDevelopmentStrikes = 0)
    {
        var target = recommendedDevelopmentStrikes > 0
            ? recommendedDevelopmentStrikes
            : recipeId?.ToLowerInvariant() switch
        {
            "longsword" or "duelling-longsword" => 52f,
            "shortsword" or "basilard" => 42f,
            "axe" or "bearded-axe" => 46f,
            _ => 48f
        };
        var amount = Math.Clamp(strikes / target, 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    internal static float AmbientStrengthFor(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => 1.00f,
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => 1.14f,
        ForgeStateId.Quenching => 1.10f,
        ForgeStateId.Grinding => 1.13f,
        ForgeStateId.Inspection or ForgeStateId.Result => 1.16f,
        _ => 1.06f
    };

    internal static float ExposureFor(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => 1.04f,
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => 1.12f,
        ForgeStateId.Quenching => 1.10f,
        ForgeStateId.Grinding => 1.12f,
        ForgeStateId.Inspection or ForgeStateId.Result => 1.15f,
        _ => 1.08f
    };

    internal static float RenderScaleFor(ForgeRenderQuality quality, bool degraded) => quality switch
    {
        ForgeRenderQuality.High when !degraded => 1f,
        ForgeRenderQuality.Low => .75f,
        _ when degraded => .75f,
        _ => .85f
    };

    internal static float OverheatVisualIntensity(ForgeStateId state, ForgeHeatBand band, double heat)
    {
        if (state is not ForgeStateId.Heating and not ForgeStateId.Reheat ||
            band is ForgeHeatBand.Cold or ForgeHeatBand.DarkRed || heat <= .52)
        {
            return 0;
        }

        var amount = (float)Math.Clamp((heat - .52) / .56, 0, 1);
        amount = amount * amount * (3 - 2 * amount);
        amount *= 1.15f;
        return band == ForgeHeatBand.Burnt ? Math.Max(1.10f, amount) : amount;
    }

    internal static float ThermalSparkRate(ForgeStateId state, ForgeHeatBand band, double heat)
    {
        var intensity = OverheatVisualIntensity(state, band, heat);
        return intensity <= 0 ? 0 : 3f + MathF.Pow(intensity, 1.35f) * 180f;
    }

    internal static float BloomStrengthFor(bool enabled, bool degraded, double heat)
    {
        if (!enabled) return 0;
        var amount = (float)Math.Clamp((heat - .78) / .29, 0, 1);
        amount = amount * amount * (3 - 2 * amount);
        var strength = .028f + amount * .205f;
        return degraded ? strength * .62f : strength;
    }

    private static Vector2 HeatScreenCenter(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => new Vector2(.50f, .57f),
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => new Vector2(.49f, .51f),
        ForgeStateId.Quenching => new Vector2(.70f, .52f),
        ForgeStateId.Grinding => new Vector2(.68f, .47f),
        _ => new Vector2(.50f, .52f)
    };

    internal static Vector4 ContactShadow(ForgeStateId state) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => new(.50f, .67f, .18f, .075f),
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => new(.50f, .72f, .22f, .085f),
        ForgeStateId.Quenching => new(.67f, .70f, .15f, .065f),
        ForgeStateId.Grinding => new(.69f, .70f, .17f, .070f),
        _ => new(.50f, .69f, .16f, .060f)
    };

    private void CancelStaticModelLoading()
    {
        staticModelLoadCancellation?.Cancel();
        staticModelLoadCancellation?.Dispose();
        staticModelLoadCancellation = null;

        if (staticModelLoadTask is not { } pending)
        {
            return;
        }

        staticModelLoadTask = null;
        _ = pending.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    completed.Result.Dispose();
                }
                else
                {
                    _ = completed.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposeResources(bool finish)
    {
        CancelStaticModelLoading();
        var currentGl = gl;
        DisposeWeaponResources();
        resources?.Dispose();
        resources = null;
        if (finish && currentGl is not null)
        {
            try
            {
                currentGl.Finish();
            }
            catch
            {
                // The context may already be tearing down. Resource deletion has
                // still been submitted from the only callback where it is valid.
            }
        }
        currentGl?.Dispose();
        gl = null;
        ClearResourceReferences();
    }

    private void AbandonResources()
    {
        CancelStaticModelLoading();
        weaponResources?.Abandon();
        weaponResources = null;
        resources?.Abandon();
        resources = null;
        gl = null;
        ClearResourceReferences();
    }

    private void ClearResourceReferences()
    {
        sceneShader = null; shadowShader = null; postShader = null; particleShader = null;
        anvilMesh = null; workbenchMesh = null; hearthMesh = null; bellowsMesh = null; waterVatMesh = null; oilVatMesh = null;
        grinderStandMesh = null; grinderWheelMesh = null; hammerMesh = null; tongsMesh = null;
        activeWeaponMesh = null; activeWeaponRecipeId = null;
        workpieceMesh = null; backgroundTexture = null; detailAtlas = null; normalAtlas = null; environmentMap = null; brdfLut = null;
        whiteTexture = null; flatNormalTexture = null; defaultOrmTexture = null; blackTexture = null; textureCache = null;
        hdrTarget = null; bloomTarget = null; shadowMap = null; fullscreen = null; particles = null;
        meshRevision = -1;
        staticModelLoadIndex = 0;
        staticModelLoadDelayFrames = 0;
        proceduralTextureUploadStage = 0;
    }

    private void DisposeWeaponResources()
    {
        weaponResources?.Dispose();
        weaponResources = null;
        activeWeaponMesh = null;
        activeWeaponRecipeId = null;
    }

    public void Deinitialize()
    {
        if (disposed) return;
        disposed = true;
        DisposeResources(finish: true);
    }

    public void AbandonContext()
    {
        if (disposed) return;
        disposed = true;
        AbandonResources();
    }

    public void Dispose()
    {
        AbandonContext();
    }

    private const string ShadowVertexShader = """
        layout(location=0) in vec3 aPosition;
        uniform mat4 uModel;
        uniform mat4 uLightMvp;
        void main(){ gl_Position = uLightMvp * uModel * vec4(aPosition,1.0); }
        """;
    private const string ShadowFragmentShader = """
        void main(){}
        """;
    private const string SceneVertexShader = """
        layout(location=0) in vec3 aPosition;
        layout(location=1) in vec3 aNormal;
        layout(location=2) in vec4 aTangent;
        layout(location=3) in vec2 aTexCoord;
        layout(location=4) in float aMaterial;
        uniform mat4 uModel; uniform mat4 uView; uniform mat4 uProjection; uniform mat4 uLightMvp;
        out vec3 vPosition; out vec3 vNormal; flat out vec3 vFaceNormal; out vec4 vTangent; out vec2 vTexCoord; flat out float vMaterial; out vec4 vShadow;
        void main(){ vec4 world=uModel*vec4(aPosition,1.0); vPosition=world.xyz; vNormal=normalize(mat3(uModel)*aNormal); vFaceNormal=vNormal; vTangent=vec4(normalize(mat3(uModel)*aTangent.xyz),aTangent.w); vTexCoord=aTexCoord; vMaterial=aMaterial; vShadow=uLightMvp*world; gl_Position=uProjection*uView*world; }
        """;
    private const string SceneFragmentShader = """
        in vec3 vPosition; in vec3 vNormal; flat in vec3 vFaceNormal; in vec4 vTangent; in vec2 vTexCoord; flat in float vMaterial; in vec4 vShadow;
        layout(location=0) out vec4 FragColor;
        layout(location=1) out vec4 EmissionColor;
        uniform sampler2D uDetailAtlas; uniform sampler2D uNormalAtlas; uniform sampler2D uShadowMap; uniform sampler2D uEnvironmentMap; uniform sampler2D uBrdfLut; uniform int uShadowsEnabled;
        uniform sampler2D uAssetBaseColor; uniform sampler2D uAssetNormal; uniform sampler2D uAssetOrm; uniform sampler2D uAssetOcclusion; uniform sampler2D uAssetEmissive;
        uniform int uUseAssetMaterial; uniform vec4 uAssetBaseFactor; uniform vec4 uAssetFactors; uniform vec3 uAssetEmissiveFactor;
        uniform vec4 uMaterials[14]; uniform vec4 uMaterialExtra[14]; uniform vec3 uMaterialEmissive[14];
        uniform vec3 uCameraPosition; uniform vec3 uLightPosition; uniform vec3 uTaskLightPosition; uniform vec3 uTaskLightDirection; uniform vec3 uTaskLightColor; uniform vec3 uRimLightDirection; uniform vec3 uHeatColor; uniform vec3 uIncandescenceColor; uniform vec3 uWorkpieceLightStart; uniform vec3 uWorkpieceLightEnd; uniform vec3 uWorkpieceLightColor; uniform vec3 uImpactPoint; uniform vec3 uTargetPoint;
        uniform float uHeatAmount; uniform float uForgeProgress; uniform float uGrindCoverage; uniform float uGrindQuality; uniform float uForgeLightIntensity; uniform float uWorkpieceLightIntensity; uniform float uTaskLightIntensity; uniform float uRimLightIntensity; uniform float uDirectionalIntensity; uniform float uEnvironmentStrength; uniform float uShadowSoftness; uniform float uAmbientStrength; uniform float uImpactFlash; uniform float uTime; uniform float uStageWarmth; uniform float uToolHighlight; uniform float uTargetVisible;
        int materialTile(int m){ if(m<=3||m==10||m==13) return 0; if(m<=6) return 1; if(m<=8) return 2; return 3; }
        vec2 atlasUv(float material, vec2 uv){ int m=int(material); int tile=materialTile(m); vec2 tileOffset=vec2(float(tile%2),float(tile/2))*.5; return fract(uv*max(.25,uMaterialExtra[m].z))*.484+tileOffset+vec2(.008); }
        float shadowFactor(vec3 n){
            if(uShadowsEnabled==0) return 1.0;
            vec3 p=vShadow.xyz/vShadow.w; p=p*.5+.5;
            if(p.z<=0.||p.z>=1.||any(lessThan(p.xy,vec2(0.)))||any(greaterThan(p.xy,vec2(1.)))) return 1.0;
            vec3 lightDirection=normalize(vec3(-.42,.80,.38));
            float bias=max(.00045,.0018*(1.-max(dot(n,lightDirection),0.)));
            vec2 texel=uShadowSoftness/vec2(textureSize(uShadowMap,0));
            vec2 taps[12]=vec2[12](vec2(-.326,-.406),vec2(-.840,-.074),vec2(-.696,.457),vec2(-.203,.621),vec2(.962,-.195),vec2(.473,-.480),vec2(.519,.767),vec2(.185,-.893),vec2(.507,.064),vec2(.896,.412),vec2(-.322,-.933),vec2(-.792,-.598));
            float result=0.;
            for(int i=0;i<12;i++){
                float depth=texture(uShadowMap,p.xy+taps[i]*texel*1.65).r;
                result+=(p.z-bias<=depth)?1.:0.;
            }
            return result/12.;
        }
        float distributionGGX(float ndh,float roughness){ float a=roughness*roughness; float a2=a*a; float d=ndh*ndh*(a2-1.)+1.; return a2/max(.0001,3.14159265*d*d); }
        float geometrySchlick(float ndv,float roughness){ float r=roughness+1.; float k=r*r*.125; return ndv/max(.0001,ndv*(1.-k)+k); }
        vec3 fresnelSchlick(float cosine,vec3 f0){ return f0+(1.-f0)*pow(1.-cosine,5.); }
        vec3 evaluateLight(vec3 n,vec3 v,vec3 l,vec3 radiance,vec3 baseColor,float metallic,float roughness,vec3 f0){
            vec3 h=normalize(v+l); float ndl=max(dot(n,l),0.); float ndv=max(dot(n,v),.001); float ndh=max(dot(n,h),0.); float vdh=max(dot(v,h),0.);
            float d=distributionGGX(ndh,roughness); float g=geometrySchlick(ndv,roughness)*geometrySchlick(ndl,roughness); vec3 f=fresnelSchlick(vdh,f0);
            vec3 specular=d*g*f/max(.001,4.*ndv*max(ndl,.001)); vec3 diffuse=(1.-f)*(1.-metallic)*baseColor/3.14159265;
            return (diffuse+specular)*radiance*ndl;
        }
        void main(){
            int m=int(vMaterial);
            bool isWorkpiece=(m==10||m==13);
            float grindFinish=clamp(uGrindCoverage*(.32+.68*uGrindQuality),0.,1.);
            vec2 detailUv=atlasUv(vMaterial,vTexCoord);
            vec3 materialSample=texture(uDetailAtlas,detailUv).rgb;
            float detail=materialSample.r;
            float albedoVariation=(m<=3||m==10||m==13)?(.78+.30*detail):(.72+.38*detail);
            vec3 baseColor=uMaterials[m].rgb*albedoVariation;
            float metallic=uMaterials[m].a;
            float roughness=clamp(uMaterialExtra[m].x*mix(.82,1.16,materialSample.g),.08,1.);
            float ao=clamp(uMaterialExtra[m].y*mix(.80,1.,materialSample.b),.45,1.);
            vec3 geometric=normalize(vNormal);
            vec3 tangent=normalize(vTangent.xyz-geometric*dot(vTangent.xyz,geometric));
            vec3 bitangent=normalize(cross(geometric,tangent))*vTangent.w;
            vec3 mapped=texture(uNormalAtlas,detailUv).xyz*2.-1.;
            float normalStrength=(m==10 ? .13 : (m==13 ? .055 : (m<=3 ? .10 : (m==7||m==8 ? .24 : .17))));
            vec3 assetEmission=vec3(0.);
            if(uUseAssetMaterial==1){
                vec4 assetBase=texture(uAssetBaseColor,vTexCoord)*uAssetBaseFactor;
                vec3 orm=texture(uAssetOrm,vTexCoord).rgb;
                baseColor=max(assetBase.rgb,vec3(.002));
                metallic=clamp(uAssetFactors.x*orm.b,0.,1.);
                roughness=clamp(uAssetFactors.y*orm.g,.055,1.);
                float sampledAo=mix(1.,texture(uAssetOcclusion,vTexCoord).r,uAssetFactors.z);
                ao=clamp(sampledAo,.28,1.);
                mapped=texture(uAssetNormal,vTexCoord).xyz*2.-1.;
                normalStrength=uAssetFactors.w;
                assetEmission=texture(uAssetEmissive,vTexCoord).rgb*max(uAssetEmissiveFactor,vec3(1.));
            }
            if(isWorkpiece) normalStrength*=mix(1.,m==13?.28:.72,grindFinish);
            mapped.xy*=normalStrength; mapped.z=sqrt(max(.08,1.-dot(mapped.xy,mapped.xy)));
            vec3 n=normalize(mat3(tangent,bitangent,geometric)*mapped);
            if(m==0){ float polished=smoothstep(.72,.96,n.y); roughness=mix(roughness,max(.18,roughness*.52),polished); float rust=(1.-smoothstep(.08,.26,detail))*(1.-polished)*(uUseAssetMaterial==1?.055:.16); baseColor=mix(baseColor,vec3(.115,.025,.008),rust); }
            if(m==1||m==2){ float rust=(1.-smoothstep(.06,.23,detail))*(uUseAssetMaterial==1?.065:.19); baseColor=mix(baseColor,vec3(.095,.018,.006),rust); roughness=mix(roughness,.84,rust); }
            if(uUseAssetMaterial==0 && (m==4||m==5)){ baseColor*=mix(vec3(.78,.70,.62),vec3(1.18,.98,.78),detail); roughness=clamp(roughness+(.5-detail)*.12,.40,.96); }
            float thermal=clamp(uHeatAmount,0.,1.1);
            float heat=clamp(thermal,0.,1.);
            float overheat=smoothstep(.80,1.06,thermal);
            vec3 emission=uMaterialEmissive[m]+assetEmission;
            float hotSurface=0.;
            if(isWorkpiece){
                float scalePattern=smoothstep(.18,.76,detail);
                float scaleCover=(1.-uForgeProgress)*(.50+.24*(1.-scalePattern));
                baseColor=mix(vec3(.022,.025,.030)+baseColor*.24,baseColor,1.-scaleCover);
                roughness=mix(.88,roughness,uForgeProgress*.82+.18);
                metallic=mix(.52,metallic,uForgeProgress*.78+.22);
                float edgeRetention=m==13?mix(.88,.96,uStageWarmth):1.;
                float localThermal=clamp(thermal*edgeRetention*mix(.91,1.035,scalePattern),0.,1.1);
                float localHeat=clamp(localThermal,0.,1.);
                float localOverheat=smoothstep(.80,1.06,localThermal);
                float oxide=smoothstep(.18,.52,localHeat)*(1.-smoothstep(.72,.94,localHeat));
                vec3 oxideColor=mix(baseColor*.48,vec3(.11,.028,.085),smoothstep(.28,.58,heat));
                baseColor=mix(baseColor,oxideColor,oxide*.72);
                hotSurface=smoothstep(.34,.92,localHeat);
                baseColor=mix(baseColor,baseColor*.48+uIncandescenceColor*.18,hotSurface*.58);
                roughness=mix(roughness,.57,hotSurface*.36); metallic=mix(metallic,.62,hotSurface*.24);
                float thermalPulse=.975+.025*sin(uTime*8.7+detail*5.0);
                float scaleTransmission=mix(.57,1.04,scalePattern);
                float glow=pow(smoothstep(.25,.96,localHeat),2.05)*mix(.28,1.42,localOverheat)*thermalPulse*scaleTransmission;
                emission+=uIncandescenceColor*glow;
                emission+=vec3(1.18,.34,.035)*localOverheat*localOverheat*.24*scaleTransmission;
                float edgeMask=m==13?1.:.20;
                float ground=edgeMask*clamp(uGrindCoverage*(.42+.58*uGrindQuality),0.,1.);
                vec3 groundMetal=mix(vec3(.32,.36,.42),vec3(.56,.62,.70),uGrindQuality);
                baseColor=mix(baseColor,groundMetal,ground*.58);
                roughness=mix(roughness,mix(.38,.105,uGrindQuality),ground);
                metallic=mix(metallic,mix(.88,1.,uGrindQuality),ground);
            }
            vec3 v=normalize(uCameraPosition-vPosition);
            vec3 directional=normalize(vec3(-.42,.80,.38));
            vec3 toForge=uLightPosition-vPosition; float forgeDistance=length(toForge); vec3 forgeDirection=toForge/max(.001,forgeDistance);
            vec3 f0=mix(vec3(.035),baseColor,metallic); if(isWorkpiece) f0=mix(f0,max(f0,uHeatColor*.52),heat*.68);
            float visibility=mix(.43,1.,shadowFactor(n));
            vec3 color=evaluateLight(n,v,directional,vec3(1.17,1.24,1.38)*uDirectionalIntensity,baseColor,metallic,roughness,f0)*visibility;
            vec3 fillDirection=normalize(vec3(.58,.44,-.62));
            color+=evaluateLight(n,v,fillDirection,vec3(.22,.29,.43)*uAmbientStrength,baseColor,metallic,roughness,f0);
            float attenuation=uForgeLightIntensity/(1.+.38*forgeDistance+.22*forgeDistance*forgeDistance);
            if(isWorkpiece) attenuation*=mix(1.,.46,hotSurface);
            color+=evaluateLight(n,v,forgeDirection,vec3(1.00,.48,.17)*attenuation,baseColor,metallic,roughness,f0);
            vec3 toTask=uTaskLightPosition-vPosition; float taskDistance=length(toTask); vec3 taskDirection=toTask/max(.001,taskDistance);
            float taskAttenuation=uTaskLightIntensity/(1.+.24*taskDistance+.16*taskDistance*taskDistance);
            float taskCone=smoothstep(.66,.88,dot(normalize(vPosition-uTaskLightPosition),normalize(uTaskLightDirection)));
            color+=evaluateLight(n,v,taskDirection,uTaskLightColor*taskAttenuation*taskCone,baseColor,metallic,roughness,f0);
            float rimFacing=pow(1.-max(dot(n,v),0.),2.15);
            vec3 rimLighting=evaluateLight(n,v,normalize(uRimLightDirection),vec3(.34,.48,.76)*uRimLightIntensity,baseColor,metallic,roughness,f0);
            color+=rimLighting*mix(.28,1.,metallic)*(.42+.58*rimFacing);
            vec3 glowSegment=uWorkpieceLightEnd-uWorkpieceLightStart;
            float glowSegmentLength=max(dot(glowSegment,glowSegment),.0001);
            float glowAlong=clamp(dot(vPosition-uWorkpieceLightStart,glowSegment)/glowSegmentLength,0.,1.);
            vec3 glowPosition=mix(uWorkpieceLightStart,uWorkpieceLightEnd,glowAlong);
            vec3 toWorkpieceGlow=glowPosition-vPosition; float glowDistance=length(toWorkpieceGlow);
            vec3 glowDirection=toWorkpieceGlow/max(.001,glowDistance);
            float glowPulse=.96+.04*sin(uTime*7.1);
            float glowAttenuation=uWorkpieceLightIntensity*glowPulse/(1.+.42*glowDistance+1.85*glowDistance*glowDistance);
            glowAttenuation*=isWorkpiece?.08:1.;
            color+=evaluateLight(n,v,glowDirection,uWorkpieceLightColor*glowAttenuation,baseColor,metallic,roughness,f0);
            float hemisphere=clamp(n.y*.5+.5,0.,1.);
            vec3 reflected=reflect(-v,n);
            vec2 environmentUv=vec2(atan(reflected.z,reflected.x)/6.2831853+.5,asin(clamp(reflected.y,-1.,1.))/3.14159265+.5);
            vec3 environment=textureLod(uEnvironmentMap,environmentUv,roughness*6.).rgb;
            vec3 envFresnel=fresnelSchlick(max(dot(n,v),0.),f0);
            vec2 brdf=texture(uBrdfLut,vec2(max(dot(n,v),0.),roughness)).rg;
            vec3 ambientDiffuse=baseColor*(1.-metallic)*mix(vec3(.086,.058,.043),vec3(.23,.28,.37),hemisphere)*.82;
            float specularOcclusion=clamp(pow(max(dot(n,v),0.)+ao,exp2(-16.*roughness-1.))-1.+ao,0.,1.);
            vec3 ambientSpecular=environment*(envFresnel*brdf.x+brdf.y*.34)*1.18*uEnvironmentStrength*specularOcclusion;
            color+=(ambientDiffuse+ambientSpecular)*ao*uAmbientStrength;
            float groundBounce=(1.-hemisphere)*(1.-metallic*.55); color+=baseColor*vec3(.12,.055,.025)*groundBounce*.20;
            color+=emission;
            float rim=pow(1.-max(dot(n,v),0.),2.4); color+=vec3(.78,.40,.11)*uToolHighlight*(.055+.11*rim);
            if(isWorkpiece){ float flash=uImpactFlash*exp(-length(vPosition-uImpactPoint)*8.5); color+=vec3(1.8,1.25,.62)*flash; }
            if(isWorkpiece && uTargetVisible>.5){
                float ringDistance=abs(length((vPosition-uTargetPoint).xz)-.105);
                float ring=smoothstep(.042,.008,ringDistance);
                float pulse=.68+.32*sin(uTime*7.5);
                color+=vec3(1.0,.36,.035)*ring*pulse*1.65;
                emission+=vec3(1.0,.18,.01)*ring*pulse*.45;
            }
            if(m==3) color*=vec3(1.08,.96,.76);
            if(m==9) color+=vec3(.32,.055,.006)*(.30+.28*uStageWarmth);
            FragColor=vec4(max(color,vec3(0.)),1.);
            EmissionColor=vec4(isWorkpiece?max(emission,vec3(0.)):vec3(0.),1.);
        }
        """;
    private const string PostVertexShader = """
        out vec2 vUv; void main(){ vec2 p=vec2(float((gl_VertexID<<1)&2),float(gl_VertexID&2)); vUv=p; gl_Position=vec4(p*2.-1.,0.,1.); }
        """;
    private const string PostFragmentShader = """
        in vec2 vUv; out vec4 FragColor; uniform sampler2D uHdr; uniform sampler2D uBloom; uniform sampler2D uBackground; uniform sampler2D uDepth; uniform int uMode; uniform vec2 uResolution; uniform vec2 uHeatCenter; uniform vec4 uContactShadow; uniform float uVignette; uniform float uViewportAspect; uniform float uBackgroundAspect; uniform float uBloomStrength; uniform float uDofStrength; uniform float uExposure; uniform float uHeatAmount; uniform float uTime;
        vec3 aces(vec3 x){ const float a=2.51; const float b=.03; const float c=2.43; const float d=.59; const float e=.14; return clamp((x*(a*x+b))/(x*(c*x+d)+e),0.,1.); }
        float depthOcclusion(vec2 uv,float center,vec2 pixelOffset){ float neighbor=texture(uDepth,uv+pixelOffset/uResolution).r; float delta=center-neighbor; float range=1.-smoothstep(.006,.040,delta); return smoothstep(.00018,.0022,delta)*range*(1.-step(.9995,neighbor)); }
        void main(){
            if(uMode==1){
                vec4 source=texture(uHdr,vUv);
                vec3 sum=vec3(0.);
                for(int x=-2;x<=2;x++) for(int y=-2;y<=2;y++) sum+=texture(uHdr,vUv+vec2(float(x),float(y))/uResolution*2.).rgb;
                vec3 wide=vec3(0.);
                wide+=texture(uHdr,vUv+vec2( 8., 0.)/uResolution).rgb; wide+=texture(uHdr,vUv+vec2(-8., 0.)/uResolution).rgb;
                wide+=texture(uHdr,vUv+vec2( 0., 8.)/uResolution).rgb; wide+=texture(uHdr,vUv+vec2( 0.,-8.)/uResolution).rgb;
                wide+=texture(uHdr,vUv+vec2( 6., 6.)/uResolution).rgb; wide+=texture(uHdr,vUv+vec2(-6., 6.)/uResolution).rgb;
                wide+=texture(uHdr,vUv+vec2( 6.,-6.)/uResolution).rgb; wide+=texture(uHdr,vUv+vec2(-6.,-6.)/uResolution).rgb;
                vec3 bright=(sum/25.*.72+wide/8.*.28)*smoothstep(.045,.28,max(max(source.r,source.g),source.b));
                FragColor=vec4(bright,source.a);
                return;
            }
            vec2 sampleUv=vUv;
            vec2 heatDelta=(vUv-uHeatCenter)/vec2(.20,.15);
            float shimmer=smoothstep(.46,.92,uHeatAmount)*exp(-dot(heatDelta,heatDelta)*2.4);
            sampleUv.x+=sin(vUv.y*145.+uTime*5.2)*.00115*shimmer;
            vec2 backgroundUv=sampleUv;
            if(uViewportAspect>uBackgroundAspect) backgroundUv.y=(sampleUv.y-.5)*(uBackgroundAspect/uViewportAspect)+.5;
            else backgroundUv.x=(sampleUv.x-.5)*(uViewportAspect/uBackgroundAspect)+.5;
            vec3 background=texture(uBackground,backgroundUv).rgb;
            vec4 source=texture(uHdr,sampleUv);
            float centerDepth=texture(uDepth,sampleUv).r;
            float dof=uDofStrength*smoothstep(.986,.999,centerDepth);
            if(dof>.001){
                vec2 radius=vec2(2.5)/uResolution;
                vec4 blurred=source+texture(uHdr,sampleUv+vec2(radius.x,0.))+texture(uHdr,sampleUv-vec2(radius.x,0.))+texture(uHdr,sampleUv+vec2(0.,radius.y))+texture(uHdr,sampleUv-vec2(0.,radius.y));
                source=mix(source,blurred*.2,dof);
            }
            float coverage=clamp(source.a,0.,1.);
            vec2 shadowRadius=max(uContactShadow.zw,vec2(.001)); vec2 shadowDelta=(vUv-uContactShadow.xy)/shadowRadius;
            float contact=exp(-dot(shadowDelta,shadowDelta)*2.15)*step(.001,uContactShadow.z);
            background*=1.-contact*.28*(1.-coverage);
            vec3 foreground=source.rgb+texture(uBloom,sampleUv).rgb*uBloomStrength;
            float ssao=0.;
            if(coverage>.01&&centerDepth<.9995){
                ssao+=depthOcclusion(sampleUv,centerDepth,vec2(3.,0.)); ssao+=depthOcclusion(sampleUv,centerDepth,vec2(-3.,0.));
                ssao+=depthOcclusion(sampleUv,centerDepth,vec2(0.,3.)); ssao+=depthOcclusion(sampleUv,centerDepth,vec2(0.,-3.));
                ssao+=depthOcclusion(sampleUv,centerDepth,vec2(5.,5.)); ssao+=depthOcclusion(sampleUv,centerDepth,vec2(-5.,5.));
                ssao+=depthOcclusion(sampleUv,centerDepth,vec2(5.,-5.)); ssao+=depthOcclusion(sampleUv,centerDepth,vec2(-5.,-5.));
                ssao+=depthOcclusion(sampleUv,centerDepth,vec2(10.,0.)); ssao+=depthOcclusion(sampleUv,centerDepth,vec2(-10.,0.));
                ssao+=depthOcclusion(sampleUv,centerDepth,vec2(0.,10.)); ssao+=depthOcclusion(sampleUv,centerDepth,vec2(0.,-10.));
                ssao*=.0833333;
            }
            foreground*=1.-ssao*.23;
            foreground=pow(aces(foreground*uExposure),vec3(1./2.2));
            float luminance=dot(foreground,vec3(.2126,.7152,.0722));
            vec3 shadowTint=vec3(.965,.985,1.025); vec3 highlightTint=vec3(1.025,1.005,.975);
            foreground*=mix(shadowTint,highlightTint,smoothstep(.12,.72,luminance));
            foreground=mix(vec3(luminance),foreground,1.065);
            background=pow(max(background*1.02,vec3(0.)),vec3(1./2.2));
            vec3 color=mix(background,foreground,coverage);
            float vignette=1.-uVignette*dot(vUv-.5,vUv-.5);
            FragColor=vec4(color*vignette,1.);
        }
        """;
    private const string ParticleVertexShader = """
        layout(location=0) in vec3 aPosition; layout(location=1) in vec4 aColor; layout(location=2) in float aSize; layout(location=3) in float aLife; layout(location=4) in float aKind; layout(location=5) in vec3 aVelocity; uniform mat4 uViewProjection; out vec4 vColor; flat out float vKind; flat out vec2 vDirection; void main(){ gl_Position=uViewProjection*vec4(aPosition,1); vec4 next=uViewProjection*vec4(aPosition+normalize(aVelocity+vec3(.00001))*.08,1); vec2 delta=next.xy/max(.001,next.w)-gl_Position.xy/max(.001,gl_Position.w); float deltaLength=length(delta); vDirection=deltaLength>.00001?delta/deltaLength:vec2(1.,0.); gl_PointSize=max(1.,aSize*650./max(.2,gl_Position.w)); vColor=aColor; vKind=aKind; }
        """;
    private const string ParticleFragmentShader = """
        in vec4 vColor; flat in float vKind; flat in vec2 vDirection; out vec4 FragColor; void main(){ vec2 p=gl_PointCoord-.5; if(vKind<.5||vKind>3.5){ vec2 side=vec2(-vDirection.y,vDirection.x); p=vec2(dot(p,vDirection)*.24,dot(p,side)); } float radius=vKind>2.5&&vKind<3.5?max(abs(p.x),abs(p.y)):length(p); float a=smoothstep(.5,.08,radius)*vColor.a; FragColor=vec4(vColor.rgb,a); }
        """;
}
