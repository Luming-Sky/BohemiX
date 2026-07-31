using System.Numerics;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Rendering;

public enum ForgeWorkpieceStation
{
    Hearth,
    Anvil,
    WaterVat,
    OilVat,
    Grinder,
    Inspection
}

public readonly record struct ForgeObjectPose(Vector3 Scale, Quaternion Rotation, Vector3 Position)
{
    public Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Position);

    public static ForgeObjectPose Interpolate(ForgeObjectPose from, ForgeObjectPose to, float amount) => new(
        Vector3.Lerp(from.Scale, to.Scale, amount),
        Quaternion.Slerp(from.Rotation, to.Rotation, amount),
        Vector3.Lerp(from.Position, to.Position, amount));
}

/// <summary>
/// Converts forge state changes into visual-only workpiece motion. It never advances or mutates gameplay state.
/// </summary>
public sealed class ForgeWorkpieceMotion
{
    // Local billet axes become: length -> world -Z (tip perpendicular into the hearth),
    // thickness -> world +Y, width -> world +X (screen horizontal).
    // The front camera therefore sees the billet entering the furnace opening end-on,
    // with the tong grip remaining on the camera side of the mouth.
    private static readonly Quaternion HearthRotation = Quaternion.Normalize(
        Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            0, 0, -1, 0,
            0, 1, 0, 0,
            1, 0, 0, 0,
            0, 0, 0, 1)));
    internal const float HearthApproachFraction = .58f;
    internal static readonly Vector3 HearthApproachOffset = new(0, .09f, 1.42f);
    // KCD2 and real treadle-wheel sharpening place the bevel on the crown of the
    // cylindrical working surface, never on the circular side cap or the rim shoulder.
    // The blade runs along the axle so a longitudinal stroke moves each section of
    // edge through the dressed top contact line.
    internal static readonly Vector3 GrindingWheelAxis = Vector3.Normalize(new(-.179f, 0, .984f));
    internal static readonly Vector3 GrindingContactNormal = Vector3.UnitY;
    internal static readonly Vector3 GrindingTangent = Vector3.Normalize(Vector3.Cross(
        GrindingWheelAxis,
        GrindingContactNormal));
    private const float GrindingBevelAngle = 12f * MathF.PI / 180f;
    // Swords point down the wheel tangent, away from the player. Their face tilts
    // across the wheel width to establish the bevel while keeping length and face
    // axes orthogonal.
    internal static readonly Vector3 GrindingLengthAxis = GrindingTangent;
    internal static readonly Vector3 GrindingFaceNormal = Vector3.Normalize(
        GrindingContactNormal * MathF.Cos(GrindingBevelAngle) +
        GrindingWheelAxis * MathF.Sin(GrindingBevelAngle));
    internal static readonly Vector3 GrindingEdgeDirection = Vector3.Normalize(Vector3.Cross(
        GrindingLengthAxis,
        GrindingFaceNormal));
    internal static readonly Vector3 GrindingWheelCenter = Vector3.Transform(
        ForgeWorkbenchLayout.GrinderWheelPivotLocal,
        ForgeWorkbenchLayout.Grinder.Transform);
    internal const float GrindingWheelRadius = ForgeWorkbenchLayout.GrinderWheelRadiusLocal * .88f;
    internal static readonly Vector3 GrindingContactPoint =
        GrindingWheelCenter + GrindingContactNormal * GrindingWheelRadius;
    private ForgeGrindingAlignment grindingAlignment = ForgeGrindingAlignment.Fallback("duelling-longsword");
    private ForgeObjectPose from;
    private ForgeObjectPose approach;
    private ForgeObjectPose target;
    private ForgeObjectPose current;
    private ForgeMotionKey key;
    private bool initialized;
    private float transition = 1;
    private float duration = .58f;
    private float arcHeight;
    private bool hearthInsertion;
    private float quenchDepth;
    private float quenchDepthTarget;
    private float quenchSpeed;
    private float grindPosition = .5f;
    private float grindPositionTarget = .5f;
    private float grindPulse;
    private float impactPulse;
    private float inspectionYaw;
    private float inspectionPitch;
    private float inspectionYawVelocity;
    private float inspectionPitchVelocity;
    private float inspectionZoom = 1;
    private bool inspectionDragging;

    public ForgeObjectPose CurrentPose => current;
    public Matrix4x4 World { get; private set; } = Matrix4x4.Identity;
    public bool IsTransferring => transition < 1 && arcHeight > .2f;
    public float TransferProgress => transition;

    public void SetTarget(ForgeStateId state, bool flipped, QuenchMedium medium, bool reducedMotion)
    {
        var nextKey = new ForgeMotionKey(StationFor(state, medium), flipped);
        var nextTarget = PoseFor(state, flipped, medium, grindingAlignment);
        if (!initialized)
        {
            initialized = true;
            key = nextKey;
            from = approach = target = current = nextTarget;
            World = current.ToMatrix();
            return;
        }

        if (nextKey == key)
        {
            target = nextTarget;
            return;
        }

        var changesStation = nextKey.Station != key.Station;
        if (changesStation && nextKey.Station == ForgeWorkpieceStation.Inspection)
        {
            inspectionYaw = inspectionPitch = 0;
            inspectionYawVelocity = inspectionPitchVelocity = 0;
            inspectionZoom = 1;
            inspectionDragging = false;
        }
        else if (nextKey.Station != ForgeWorkpieceStation.Inspection)
        {
            inspectionDragging = false;
            inspectionYawVelocity = inspectionPitchVelocity = 0;
        }
        from = current;
        target = nextTarget;
        hearthInsertion = changesStation && nextKey.Station == ForgeWorkpieceStation.Hearth;
        approach = hearthInsertion
            ? nextTarget with { Position = nextTarget.Position + HearthApproachOffset }
            : nextTarget;
        key = nextKey;
        transition = 0;
        duration = reducedMotion
            ? .12f
            : hearthInsertion
                ? .86f
                : nextKey.Station == ForgeWorkpieceStation.Inspection
                    ? .46f
                    : changesStation ? .62f : .34f;
        arcHeight = reducedMotion
            ? .08f
            : hearthInsertion
                ? .44f
                : nextKey.Station == ForgeWorkpieceStation.Inspection
                    ? .20f
                    : changesStation ? .68f : .16f;
        if (nextKey.Station is not ForgeWorkpieceStation.WaterVat and not ForgeWorkpieceStation.OilVat)
        {
            quenchDepth = quenchDepthTarget = quenchSpeed = 0;
        }
        if (nextKey.Station != ForgeWorkpieceStation.Grinder)
        {
            grindPosition = grindPositionTarget = .5f;
            grindPulse = 0;
        }
    }

    public Matrix4x4 Update(float elapsedSeconds, float timeSeconds)
    {
        if (!initialized)
        {
            SetTarget(ForgeStateId.RecipeSelect, false, QuenchMedium.Water, false);
        }

        transition = Math.Min(1, transition + elapsedSeconds / Math.Max(.01f, duration));
        var pose = hearthInsertion && transition < 1
            ? HearthInsertionPose(transition)
            : TransferPose(transition);

        quenchDepth = ExpApproach(quenchDepth, quenchDepthTarget, elapsedSeconds, 9f);
        grindPosition = ExpApproach(grindPosition, grindPositionTarget, elapsedSeconds, 12f);
        grindPulse = Math.Max(0, grindPulse - elapsedSeconds * 3.8f);
        impactPulse = Math.Max(0, impactPulse - elapsedSeconds * 8.5f);

        if (key.Station is ForgeWorkpieceStation.WaterVat or ForgeWorkpieceStation.OilVat)
        {
            var bathResistance = MathF.Sin(timeSeconds * 8.5f) * .012f * quenchSpeed;
            pose = pose with { Position = pose.Position + new Vector3(0, -.92f * quenchDepth + bathResistance, 0) };
        }
        else if (key.Station == ForgeWorkpieceStation.Grinder)
        {
            var lengthAxis = GrindingLengthAxisFor(grindingAlignment);
            var strokeOffset = GrindingStrokeOffset(grindPosition, grindingAlignment);
            pose = pose with
            {
                // Any micro-motion stays on the blade/wheel axis. Vertical wobble or
                // stroke-driven rotation would break the fixed bevel contact.
                Position = pose.Position + strokeOffset +
                           lengthAxis * (MathF.Sin(timeSeconds * 42f) * .003f * grindPulse)
            };
        }
        else if (key.Station == ForgeWorkpieceStation.Inspection)
        {
            if (!inspectionDragging)
            {
                inspectionYaw = WrapAngle(inspectionYaw + inspectionYawVelocity * elapsedSeconds);
                inspectionPitch = Math.Clamp(
                    inspectionPitch + inspectionPitchVelocity * elapsedSeconds,
                    Degrees(-78),
                    Degrees(78));
                var damping = MathF.Exp(-elapsedSeconds * 4.8f);
                inspectionYawVelocity *= damping;
                inspectionPitchVelocity *= damping;
            }
            var inspectionRotation = Quaternion.CreateFromYawPitchRoll(
                inspectionYaw,
                inspectionPitch,
                0);
            pose = pose with
            {
                Scale = pose.Scale * inspectionZoom,
                Rotation = Quaternion.Normalize(pose.Rotation * inspectionRotation)
            };
        }

        if (impactPulse > 0)
        {
            pose = pose with { Position = pose.Position - Vector3.UnitY * (.025f * impactPulse) };
        }

        current = pose;
        World = current.ToMatrix();
        return World;
    }

    private ForgeObjectPose HearthInsertionPose(float amount)
    {
        if (amount <= HearthApproachFraction)
        {
            var approachAmount = SmoothStep(amount / HearthApproachFraction);
            var pose = ForgeObjectPose.Interpolate(from, approach, approachAmount);
            return pose with
            {
                Position = pose.Position + Vector3.UnitY * (MathF.Sin(approachAmount * MathF.PI) * arcHeight)
            };
        }

        var insertionAmount = SmoothStep((amount - HearthApproachFraction) / (1 - HearthApproachFraction));
        var inserted = ForgeObjectPose.Interpolate(approach, target, insertionAmount);
        var handSettle = MathF.Sin(insertionAmount * MathF.PI) * .025f;
        return inserted with { Position = inserted.Position + Vector3.UnitY * handSettle };
    }

    private ForgeObjectPose TransferPose(float amount)
    {
        var eased = SmoothStep(amount);
        var pose = ForgeObjectPose.Interpolate(from, target, eased);
        if (amount >= 1)
        {
            return pose;
        }

        return pose with
        {
            Position = pose.Position + Vector3.UnitY * (MathF.Sin(eased * MathF.PI) * arcHeight)
        };
    }

    public void SetQuench(double depth, double speed)
    {
        quenchDepthTarget = (float)Math.Clamp(depth, 0, 1);
        quenchSpeed = (float)Math.Clamp(speed, 0, 1.5);
    }

    public void SetGrindingStroke(double position, double intensity)
    {
        grindPositionTarget = (float)Math.Clamp(position, 0, 1);
        grindPulse = Math.Max(grindPulse, (float)Math.Clamp(intensity, .15, 1));
    }

    internal static Vector3 GrindingStrokeOffset(float position) =>
        GrindingLengthAxis * ((.5f - Math.Clamp(position, 0, 1)) * .72f);

    internal static Vector3 GrindingStrokeOffset(float position, ForgeGrindingAlignment alignment) =>
        GrindingLengthAxisFor(alignment) * ((.5f - Math.Clamp(position, 0, 1)) * .72f);

    internal static Vector3 GrindingLengthAxisFor(ForgeGrindingAlignment alignment) =>
        alignment.Orientation == ForgeGrindingOrientation.EdgeAcrossWheel
            ? GrindingWheelAxis
            : GrindingTangent;

    internal static Vector3 GrindingFaceNormalFor(ForgeGrindingAlignment alignment) =>
        Vector3.Normalize(
            GrindingContactNormal * MathF.Cos(GrindingBevelAngle) +
            (alignment.Orientation == ForgeGrindingOrientation.EdgeAcrossWheel
                ? GrindingTangent
                : GrindingWheelAxis) * MathF.Sin(GrindingBevelAngle));

    internal static Vector3 GrindingInteriorDirectionFor(ForgeGrindingAlignment alignment) =>
        Vector3.Normalize(Vector3.Cross(
            GrindingFaceNormalFor(alignment),
            GrindingLengthAxisFor(alignment)));

    internal void SetGrindingAlignment(ForgeGrindingAlignment alignment) =>
        grindingAlignment = alignment;

    public void Impact(double intensity) =>
        impactPulse = Math.Max(impactPulse, (float)Math.Clamp(intensity, .15, 1));

    public void BeginInspectionDrag()
    {
        if (key.Station != ForgeWorkpieceStation.Inspection) return;
        inspectionDragging = true;
        inspectionYawVelocity = inspectionPitchVelocity = 0;
    }

    public void RotateInspection(float yawDelta, float pitchDelta, float seconds)
    {
        if (key.Station != ForgeWorkpieceStation.Inspection) return;
        inspectionDragging = true;
        inspectionYaw = WrapAngle(inspectionYaw + yawDelta);
        inspectionPitch = Math.Clamp(inspectionPitch + pitchDelta, Degrees(-78), Degrees(78));
        var sampleSeconds = Math.Max(.001f, seconds);
        inspectionYawVelocity = Math.Clamp(yawDelta / sampleSeconds, -8f, 8f);
        inspectionPitchVelocity = Math.Clamp(pitchDelta / sampleSeconds, -6f, 6f);
    }

    public void EndInspectionDrag() => inspectionDragging = false;

    public void AddInspectionZoom(float delta) =>
        inspectionZoom = Math.Clamp(inspectionZoom + delta, .76f, 1.24f);

    public static ForgeWorkpieceStation StationFor(ForgeStateId state, QuenchMedium medium) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => ForgeWorkpieceStation.Hearth,
        ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece => ForgeWorkpieceStation.Anvil,
        ForgeStateId.Quenching when medium == QuenchMedium.Oil => ForgeWorkpieceStation.OilVat,
        ForgeStateId.Quenching => ForgeWorkpieceStation.WaterVat,
        ForgeStateId.Grinding => ForgeWorkpieceStation.Grinder,
        ForgeStateId.Inspection or ForgeStateId.Result => ForgeWorkpieceStation.Inspection,
        _ => ForgeWorkpieceStation.Anvil
    };

    public static ForgeObjectPose PoseFor(ForgeStateId state, bool flipped, QuenchMedium medium) =>
        PoseFor(state, flipped, medium, ForgeGrindingAlignment.Fallback("duelling-longsword"));

    internal static ForgeObjectPose PoseFor(
        ForgeStateId state,
        bool flipped,
        QuenchMedium medium,
        ForgeGrindingAlignment grindingAlignment)
    {
        var station = StationFor(state, medium);
        var flip = flipped ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI) : Quaternion.Identity;
        return station switch
        {
            ForgeWorkpieceStation.Hearth => Pose(
                new Vector3(.42f), HearthRotation, new Vector3(-3.05f, -.52f, .10f), flip),
            ForgeWorkpieceStation.Anvil => Pose(
                new Vector3(.76f, .76f, 1.12f), new Vector3(-.10f, .53f, .18f), flip),
            ForgeWorkpieceStation.WaterVat => Pose(
                new Vector3(.58f), roll: -1.22f, position: new Vector3(2.12f, .75f, .26f), flip: flip),
            ForgeWorkpieceStation.OilVat => Pose(
                new Vector3(.58f), roll: -1.22f, position: new Vector3(3.35f, .75f, .20f), flip: flip),
            ForgeWorkpieceStation.Grinder => GrindingPose(
                grindingAlignment,
                flipped),
            ForgeWorkpieceStation.Inspection => Pose(
                // Present the forged face instead of looking almost along its width.
                // This keeps the tang, shoulder collar and guard in the same readable
                // depth hierarchy while retaining enough angle to show blade thickness.
                new Vector3(.92f), yaw: .10f, pitch: .90f, roll: -.025f, position: new Vector3(.08f, 1.02f, .56f), flip: flip),
            _ => Pose(new Vector3(.76f), Vector3.Zero, flip)
        };
    }

    private static ForgeObjectPose GrindingPose(ForgeGrindingAlignment alignment, bool flipped)
    {
        var rotation = CreateGrindingRotation(alignment);
        if (flipped)
        {
            rotation = Quaternion.Normalize(rotation * Quaternion.CreateFromAxisAngle(
                alignment.LocalLengthAxis,
                MathF.PI));
        }

        var scale = new Vector3(alignment.Scale);
        var localContactOffset = Vector3.Transform(alignment.LocalContactPoint * scale, rotation);
        return new ForgeObjectPose(scale, rotation, GrindingContactPoint - localContactOffset);
    }

    internal static Vector3 GrindingContactForPose(
        ForgeObjectPose pose,
        ForgeGrindingAlignment alignment) =>
        Vector3.Transform(alignment.LocalContactPoint, pose.ToMatrix());

    private static ForgeObjectPose Pose(
        Vector3 scale,
        Vector3 position,
        Quaternion flip) => new(scale, flip, position);

    private static ForgeObjectPose Pose(
        Vector3 scale,
        Quaternion rotation,
        Vector3 position,
        Quaternion flip) =>
        // Apply the turn in the workpiece's local frame. A world-space flip made a
        // tilted quench pose invert its length axis, sending the tip upward.
        new(scale, Quaternion.Normalize(rotation * flip), position);

    private static ForgeObjectPose Pose(
        Vector3 scale,
        float yaw = 0,
        float pitch = 0,
        float roll = 0,
        Vector3 position = default,
        Quaternion flip = default)
    {
        if (flip == default) flip = Quaternion.Identity;
        var rotation = Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
        return new ForgeObjectPose(scale, Quaternion.Normalize(rotation * flip), position);
    }

    private static Quaternion CreateGrindingRotation(ForgeGrindingAlignment alignment)
    {
        var localLength = Vector3.Normalize(alignment.LocalLengthAxis);
        var localFace = Vector3.Normalize(alignment.LocalFaceNormal);
        var localSide = Vector3.Normalize(Vector3.Cross(localFace, localLength));
        var worldLength = GrindingLengthAxisFor(alignment);
        var worldFace = GrindingFaceNormalFor(alignment);
        var worldSide = Vector3.Normalize(Vector3.Cross(worldFace, worldLength));
        var x = worldLength * localLength.X + worldSide * localSide.X + worldFace * localFace.X;
        var y = worldLength * localLength.Y + worldSide * localSide.Y + worldFace * localFace.Y;
        var z = worldLength * localLength.Z + worldSide * localSide.Z + worldFace * localFace.Z;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1)));
    }

    private static float SmoothStep(float value) => value * value * (3 - 2 * value);

    private static float ExpApproach(float currentValue, float targetValue, float seconds, float response) =>
        currentValue + (targetValue - currentValue) * (1 - MathF.Exp(-Math.Max(0, seconds) * response));

    private static float WrapAngle(float value) =>
        MathF.IEEERemainder(value, MathF.Tau);

    private static float Degrees(float value) => value * MathF.PI / 180f;

    private readonly record struct ForgeMotionKey(ForgeWorkpieceStation Station, bool Flipped);
}

/// <summary>Visual-only motion for tools that react to forge events.</summary>
public sealed class ForgeToolMotion
{
    internal const float HammerRaiseDuration = .07f;
    internal const float HammerImpactTime = .18f;
    internal const float HammerReboundEnd = .31f;
    internal const float HammerStrikeDuration = .48f;
    internal static readonly Vector3 HammerHeadCenterLocal = new(1.49f, 0, 0);
    internal static readonly Vector3 HammerFlatFaceCenterLocal = new(1.49f, -.64f, 0);
    internal static readonly Vector3 HammerContactFaceOffset = new(0, .018f, 0);
    private float bellowsPulse;
    private float hammerFaceAngle;
    private bool hammerGestureActive;
    private bool hammerTargetVisible;
    private float hammerGesturePull;
    private float hammerGestureBlend;
    private Vector3 hammerGesturePoint = new(-.10f, .52f, .12f);
    private float grinderAngle;
    private float grinderSpeed;
    private Vector3 hammerImpactPoint = new(-.10f, .52f, .12f);
    private float hammerStrikeTime = HammerStrikeDuration;
    private float hammerStrikeLift = .70f;
    private float hammerStrikeIntensity;
    private bool hammerImpactReady;
    public float HammerGesturePull => hammerGesturePull;

    public float GrinderAngle => grinderAngle;
    public float GrinderSpeed => grinderSpeed;
    public bool IsHammerStrikeActive => hammerStrikeTime < HammerStrikeDuration;

    public void Update(
        float elapsedSeconds,
        ForgeStateId state,
        HammerFace face,
        bool grindingEngaged = true,
        double grinderSpeedScale = 1)
    {
        bellowsPulse = Math.Max(0, bellowsPulse - elapsedSeconds * 2.6f);
        if (IsHammerStrikeActive)
        {
            var previous = hammerStrikeTime;
            hammerStrikeTime = Math.Min(HammerStrikeDuration, hammerStrikeTime + Math.Max(0, elapsedSeconds));
            if (previous < HammerImpactTime && hammerStrikeTime >= HammerImpactTime)
            {
                hammerImpactReady = true;
            }
        }
        if (hammerGestureActive)
        {
            hammerGesturePull = Math.Clamp(hammerGesturePull + Math.Max(0, elapsedSeconds) / 1.15f, 0, 1);
        }
        hammerGestureBlend = ExpApproach(
            hammerGestureBlend,
            hammerGestureActive || hammerTargetVisible ? 1 : 0,
            elapsedSeconds,
            hammerGestureActive ? 15f : 9f);
        hammerFaceAngle = ExpApproach(hammerFaceAngle, 0, elapsedSeconds, 11f);
        var grinderTarget = state == ForgeStateId.Grinding && grindingEngaged && grinderSpeedScale > .001
            ? 5.2f * (float)Math.Clamp(grinderSpeedScale, 0, 1.35)
            : 0;
        grinderSpeed = ExpApproach(
            grinderSpeed,
            grinderTarget,
            elapsedSeconds,
            grinderTarget > 0 ? 6f : 4.2f);
        grinderAngle = MathF.IEEERemainder(grinderAngle + grinderSpeed * elapsedSeconds, MathF.Tau);
    }

    public void PumpBellows(double intensity) =>
        bellowsPulse = Math.Max(bellowsPulse, (float)Math.Clamp(intensity, .25, 1));

    public void BeginHammerGesture(Vector3 point)
    {
        hammerStrikeTime = HammerStrikeDuration;
        hammerImpactReady = false;
        hammerGesturePoint = point;
        hammerGesturePull = 0;
        hammerGestureActive = true;
        hammerTargetVisible = true;
    }

    public void UpdateHammerGesture(float pull) =>
        hammerGesturePull = Math.Clamp(pull, 0, 1);

    public void SetHammerTarget(Vector3 point, bool visible)
    {
        hammerGesturePoint = point;
        hammerTargetVisible = visible;
    }

    public void EndHammerGesture() => hammerGestureActive = false;
    public void ClearHammerTarget() => hammerTargetVisible = false;

    public void Strike(Vector3 point, double intensity)
    {
        var preparedPull = hammerGesturePull;
        var wasPrepared = preparedPull > .08f;
        hammerGestureActive = false;
        hammerGestureBlend = 0;
        hammerImpactPoint = point;
        hammerStrikeIntensity = (float)Math.Clamp(intensity, .15, 1.4);
        hammerStrikeLift = Math.Clamp(Math.Max(.62f, preparedPull) + hammerStrikeIntensity * .12f, .62f, 1f);
        hammerGesturePull = 0;
        // A held pull has already shown the lift, so it can enter the downswing immediately.
        // A fast click starts at rest and still plays the complete, readable swing.
        hammerStrikeTime = wasPrepared ? HammerRaiseDuration : 0;
        hammerImpactReady = false;
    }

    public bool TryConsumeHammerImpact(out Vector3 point, out float intensity)
    {
        point = hammerImpactPoint;
        intensity = hammerStrikeIntensity;
        if (!hammerImpactReady)
        {
            return false;
        }

        hammerImpactReady = false;
        return true;
    }

    public Matrix4x4 BellowsTransform
    {
        get
        {
            var compression = bellowsPulse <= 0 ? 0 : MathF.Sin((1 - bellowsPulse) * MathF.PI) * .34f;
            return Matrix4x4.CreateScale(1, 1 - compression, 1) *
                   Matrix4x4.CreateTranslation(0, compression * .16f, 0) *
                   ForgeWorkbenchLayout.Bellows.Transform;
        }
    }

    public Matrix4x4 HammerTransform(ForgeStateId state)
    {
        // A strike owns the hammer until its rebound finishes. The accepted blow can
        // transition the forge state immediately (for example to Reheat), but that must
        // not snap the visible hammer back to the tool rack before impact is shown.
        if (IsHammerStrikeActive)
        {
            return StrikeTransform();
        }

        if (state is not ForgeStateId.Hammering and not ForgeStateId.RotateWorkpiece)
        {
            return ForgeWorkbenchLayout.Hammer.Transform;
        }

        if (hammerGestureActive || hammerTargetVisible || hammerGestureBlend > .015f)
        {
            var raised = SmoothStep(hammerGesturePull);
            var defaultHead = new Vector3(-.02f, 1.03f, .25f);
            var targetHead = hammerGesturePoint + new Vector3(
                0,
                .50f + raised * .48f,
                Lerp(-.02f, -.22f, raised));
            var head = Vector3.Lerp(defaultHead, targetHead, hammerGestureBlend);
            var angle = Lerp(.45f, 1.10f, raised);
            return HammerAtFace(head, angle);
        }

        return HammerAtFace(new Vector3(-.02f, 1.03f, .25f), .45f);
    }

    private Matrix4x4 StrikeTransform()
    {
        var hoverFace = hammerImpactPoint + new Vector3(0, .50f, -.02f);
        var raisedFace = hammerImpactPoint + new Vector3(-.08f, .68f + hammerStrikeLift * .24f, -.22f);
        var contactFace = hammerImpactPoint + HammerContactFaceOffset;
        var reboundFace = hammerImpactPoint + new Vector3(.035f, .34f, -.055f);
        Vector3 face;
        float angle;
        if (hammerStrikeTime <= HammerRaiseDuration)
        {
            var amount = SmoothStep(hammerStrikeTime / HammerRaiseDuration);
            face = Vector3.Lerp(hoverFace, raisedFace, amount);
            angle = Lerp(.45f, 1.10f, amount);
        }
        else if (hammerStrikeTime <= HammerImpactTime)
        {
            var normalized = (hammerStrikeTime - HammerRaiseDuration) / (HammerImpactTime - HammerRaiseDuration);
            var amount = normalized * normalized;
            var control = hammerImpactPoint + new Vector3(.12f, .42f, -.10f);
            face = QuadraticBezier(raisedFace, control, contactFace, amount);
            angle = Lerp(1.10f, 0, amount);
        }
        else if (hammerStrikeTime <= HammerReboundEnd)
        {
            var amount = SmoothStep((hammerStrikeTime - HammerImpactTime) / (HammerReboundEnd - HammerImpactTime));
            face = Vector3.Lerp(contactFace, reboundFace, amount);
            angle = Lerp(0, .24f, amount);
        }
        else
        {
            var amount = SmoothStep((hammerStrikeTime - HammerReboundEnd) / (HammerStrikeDuration - HammerReboundEnd));
            face = Vector3.Lerp(reboundFace, hoverFace, amount);
            angle = Lerp(.24f, .45f, amount);
        }

        return HammerAtFace(face, angle);
    }

    private Matrix4x4 HammerAtFace(Vector3 facePosition, float angle)
    {
        // Runtime GLB axes after Blender export: +X runs from grip to head,
        // +Y runs from the flat face toward the peen, and +Z is head thickness.
        // At impact flat face (-Y) points down. +X points away from the camera,
        // so the grip at the local origin extends toward the smith.
        var heldBasis = new Matrix4x4(
            0, 0, -1, 0,
            0, 1, 0, 0,
            1, 0, 0, 0,
            0, 0, 0, 1);
        var orientation = Matrix4x4.CreateScale(.42f) *
                          Matrix4x4.CreateRotationX(hammerFaceAngle) *
                          heldBasis *
                          Matrix4x4.CreateRotationX(angle);
        var faceOffset = Vector3.Transform(HammerFlatFaceCenterLocal, orientation);
        return orientation * Matrix4x4.CreateTranslation(facePosition - faceOffset);
    }

    private static Vector3 QuadraticBezier(Vector3 from, Vector3 control, Vector3 to, float amount)
    {
        var inverse = 1 - amount;
        return inverse * inverse * from + 2 * inverse * amount * control + amount * amount * to;
    }

    public Matrix4x4 GrinderWheelTransform =>
        Matrix4x4.CreateTranslation(-ForgeWorkbenchLayout.GrinderWheelPivotLocal) *
        Matrix4x4.CreateRotationZ(grinderAngle) *
        Matrix4x4.CreateTranslation(ForgeWorkbenchLayout.GrinderWheelPivotLocal) *
        ForgeWorkbenchLayout.Grinder.Transform;

    private static float ExpApproach(float currentValue, float targetValue, float seconds, float response) =>
        currentValue + (targetValue - currentValue) * (1 - MathF.Exp(-Math.Max(0, seconds) * response));

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;
    private static float SmoothStep(float value) => value * value * (3 - 2 * value);
}
