using UnityEngine;

namespace Ramblers;

/// <summary>
/// The companion's idle attention. Someone walking with a friend does not stare
/// at them: they look at what is around and come back. This publishes that habit
/// on its own gaze channel, above follow so a glance interrupts watching the
/// human, below the action channels so a deliberate aim still wins.
///
/// It is not a job. There is no tool behind it, no completion to report and
/// nothing to cancel, so it lives beside follow intent and posture as long-lived
/// state rather than inside the job layer.
///
/// Every horizontal glance is a whole-body turn, because Big Walk gives a player
/// no sustained head yaw: PlayerHead accumulates look input into headState.x and
/// PlayerMover drains it into the body, so the residual always decays to zero.
/// Turning costs nothing here. PlayerMover.GetForwardSpeed has no direction term
/// and PlayerTunings has no side or backward speed, so the companion holds the
/// same path at the same pace while facing away from it — which is what a player
/// pressing W and D to look aside is doing.
/// </summary>
internal sealed class CompanionAmbientGaze
{
    private const float MinAttendSeconds = 1.1f;
    private const float MaxAttendSeconds = 2.9f;
    private const float MinGlanceSeconds = 0.9f;
    private const float MaxGlanceSeconds = 2.4f;

    private const float MovingGlanceChance = 0.65f;
    private const float HoldingGlanceChance = 0.5f;
    private const float PitchOnlyGlanceChance = 0.3f;
    private const int MaximumConsecutiveGlances = 2;

    // Walking glances stay inside the arc a player strafes through without
    // losing sight of where they are going. Standing still, the whole body is
    // free, so the arc opens up.
    private const float MovingMinYawDegrees = 20f;
    private const float MovingMaxYawDegrees = 70f;
    private const float HoldingMinYawDegrees = 25f;
    private const float HoldingMaxYawDegrees = 120f;
    private const float PitchOnlyMaxYawDegrees = 12f;
    private const float MaxPitchUpDegrees = 16f;
    private const float MaxPitchDownDegrees = 30f;

    private const float HumanRejectConeDegrees = 18f;
    private const float RayStartOffset = 0.35f;
    private const float MinimumGlanceDistance = 1.2f;
    private const float MaximumGlanceDistance = 30f;
    private const float FallbackGlanceDistance = 18f;
    private const int GlanceCandidates = 4;

    private const float OverrideSettleSeconds = 0.7f;
    private const float ConversationSettleSeconds = 0.9f;
    private const float MovingIntentThreshold = 0.01f;

    private enum GazeIntent
    {
        Attend,
        Glance
    }

    private readonly CompanionAttention _attention;

    // System.Random deliberately, not UnityEngine.Random: this needs no Unity
    // API surface at all, so it cannot be one of the stripped ones.
    private readonly System.Random _random = new System.Random();

    private CompanionBody _body;
    private PlayerCharacter _humanAtSpawn;
    private GazeIntent _intent;
    private float _intentUntil;
    private int _consecutiveGlances;
    private Vector3 _glanceTarget;
    private bool _glanceAnchored;
    private bool _conversationActive;
    private float _peakHeadYaw;

    internal CompanionAmbientGaze(CompanionAttention attention)
    {
        _attention = attention;
    }

    internal void Bind(CompanionBody body, PlayerCharacter human, float now)
    {
        _body = body;
        _humanAtSpawn = human;
        _intent = GazeIntent.Attend;
        _intentUntil = now + NextRange(MinAttendSeconds, MaxAttendSeconds);
        _consecutiveGlances = 0;
        _glanceTarget = Vector3.zero;
        _glanceAnchored = false;
        _conversationActive = false;
    }

    /// <summary>
    /// Whether the companion is currently in conversation. Looking away
    /// mid-sentence is what makes attention read as absent, so speech on either
    /// side pins the gaze to the human.
    /// </summary>
    internal void SetConversationActive(bool active)
    {
        _conversationActive = active;
    }

    internal void Tick(float now, Vector3 movementIntent)
    {
        if (_body == null || !_body.IsAlive)
            return;

        // Head yaw is a lag buffer the body is always draining, so its peak over
        // one intent says how hard the look was thrown. A value near the side
        // limit means the aim was snapped rather than moved, which is the pose
        // no player reaches outside a fast flick.
        _peakHeadYaw = Mathf.Max(_peakHeadYaw, Mathf.Abs(_attention.HeadState.x));

        var human = GetHumanPlayer();
        if (human == null)
        {
            // Follow's own claim is the fallback when there is nobody to attend to.
            _attention.ClearTarget(GazeChannel.Ambient);
            return;
        }

        var humanHead = CompanionBody.HeadPositionOf(human);

        // A deliberate action owns the gaze. Hold the social default underneath
        // it and re-arm the dwell every frame, so the habit does not run its
        // timers down unseen and then whip the head away the moment an
        // inspection releases.
        if (_attention.IsOverridden(GazeChannel.Ambient))
        {
            HoldAttention(now + OverrideSettleSeconds, humanHead);
            return;
        }

        if (_conversationActive)
        {
            HoldAttention(now + ConversationSettleSeconds, humanHead);
            return;
        }

        if (now >= _intentUntil)
            ChooseNextIntent(now, movementIntent, human, humanHead);

        _attention.SetTarget(
            GazeChannel.Ambient,
            _intent == GazeIntent.Glance ? _glanceTarget : humanHead);
    }

    internal void Release()
    {
        _body = null;
        _humanAtSpawn = null;
        _intent = GazeIntent.Attend;
        _intentUntil = 0f;
        _consecutiveGlances = 0;
        _glanceTarget = Vector3.zero;
        _glanceAnchored = false;
        _conversationActive = false;
        _peakHeadYaw = 0f;
        _attention.ClearTarget(GazeChannel.Ambient);
    }

    private void HoldAttention(float until, Vector3 humanHead)
    {
        _intent = GazeIntent.Attend;
        _intentUntil = until;
        _consecutiveGlances = 0;
        // An inspection's own turn is not this habit's residual to report.
        _peakHeadYaw = 0f;
        _attention.SetTarget(GazeChannel.Ambient, humanHead);
    }

    private void ChooseNextIntent(
        float now,
        Vector3 movementIntent,
        PlayerCharacter human,
        Vector3 humanHead)
    {
        var moving = movementIntent.sqrMagnitude > MovingIntentThreshold;
        var glanceChance = moving ? MovingGlanceChance : HoldingGlanceChance;
        if (_consecutiveGlances < MaximumConsecutiveGlances &&
            NextUnit() < glanceChance &&
            TryPickGlanceTarget(moving, movementIntent, human, humanHead))
        {
            _intent = GazeIntent.Glance;
            _consecutiveGlances++;
            var glanceSeconds = NextRange(MinGlanceSeconds, MaxGlanceSeconds);
            _intentUntil = now + glanceSeconds;
            Plugin.Logger.LogInfo(
                "[GAZE] GLANCE " +
                $"target={_glanceTarget}, anchored={_glanceAnchored}, " +
                $"seconds={glanceSeconds:F2}, moving={moving}, " +
                $"peakHeadYaw={_peakHeadYaw:F1}.");
            _peakHeadYaw = 0f;
            return;
        }

        _intent = GazeIntent.Attend;
        _consecutiveGlances = 0;
        var attendSeconds = NextRange(MinAttendSeconds, MaxAttendSeconds);
        _intentUntil = now + attendSeconds;
        Plugin.Logger.LogInfo(
            $"[GAZE] ATTEND seconds={attendSeconds:F2}, moving={moving}, " +
            $"peakHeadYaw={_peakHeadYaw:F1}.");
        _peakHeadYaw = 0f;
    }

    /// <summary>
    /// Samples a few directions off the current heading and prefers one whose
    /// ray lands on something, so the companion looks at the world rather than
    /// into empty sky. Falls back to a far point along the first usable
    /// direction so an open horizon still gets looked at.
    /// </summary>
    private bool TryPickGlanceTarget(
        bool moving,
        Vector3 movementIntent,
        PlayerCharacter human,
        Vector3 humanHead)
    {
        var origin = _body.HeadPosition;

        // Glances are offsets from where the companion is headed, or from where
        // it is already facing when stopped. Standing still it reads the yaw
        // facing last committed rather than the transform, so the two cannot
        // disagree while the networked rotation is still catching up.
        var baseDirection = new Vector3(movementIntent.x, 0f, movementIntent.z);
        var baseYaw = moving && baseDirection.sqrMagnitude >= 0.0001f
            ? Mathf.Atan2(baseDirection.x, baseDirection.z) * Mathf.Rad2Deg
            : _attention.LastBodyYaw;

        // The human's bearing in the same yaw/pitch terms the offsets are
        // sampled in. Kept as angles rather than a vector comparison so this
        // needs no Unity API the facing path has not already proven at runtime.
        var toHuman = humanHead - origin;
        var humanHorizontal = new Vector3(toHuman.x, 0f, toHuman.z).magnitude;
        var humanYaw = Mathf.Atan2(toHuman.x, toHuman.z) * Mathf.Rad2Deg;
        var humanPitch = -Mathf.Atan2(toHuman.y, humanHorizontal) * Mathf.Rad2Deg;

        var layerMask = ResolveLayerMask(human);
        var fallback = Vector3.zero;
        var hasFallback = false;

        for (var attempt = 0; attempt < GlanceCandidates; attempt++)
        {
            float yawOffset;
            float pitchOffset;
            SampleOffsets(moving, out yawOffset, out pitchOffset);
            var candidateYaw = baseYaw + yawOffset;

            // A glance that lands back on the human is not a look away.
            if (Mathf.Abs(Mathf.DeltaAngle(candidateYaw, humanYaw)) < HumanRejectConeDegrees &&
                Mathf.Abs(pitchOffset - humanPitch) < HumanRejectConeDegrees)
            {
                continue;
            }

            var direction =
                Quaternion.Euler(pitchOffset, candidateYaw, 0f) * Vector3.forward;

            RaycastHit hit;
            if (Physics.Raycast(
                    origin + direction * RayStartOffset,
                    direction,
                    out hit,
                    MaximumGlanceDistance,
                    layerMask,
                    QueryTriggerInteraction.Ignore) &&
                hit.distance >= MinimumGlanceDistance &&
                !_body.Contains(hit.collider == null ? null : hit.collider.transform))
            {
                _glanceTarget = hit.point;
                _glanceAnchored = true;
                return true;
            }

            if (!hasFallback)
            {
                fallback = origin + direction * FallbackGlanceDistance;
                hasFallback = true;
            }
        }

        if (!hasFallback)
            return false;

        _glanceTarget = fallback;
        _glanceAnchored = false;
        return true;
    }

    private void SampleOffsets(bool moving, out float yawOffset, out float pitchOffset)
    {
        // Pitch is the one axis a player holds without turning at all, because
        // nothing ever drains headState.y. Those glances are free while walking,
        // so they carry a share of the habit no body turn has to pay for.
        if (NextUnit() < PitchOnlyGlanceChance)
        {
            yawOffset = NextRange(-PitchOnlyMaxYawDegrees, PitchOnlyMaxYawDegrees);
        }
        else
        {
            yawOffset = moving
                ? NextRange(MovingMinYawDegrees, MovingMaxYawDegrees)
                : NextRange(HoldingMinYawDegrees, HoldingMaxYawDegrees);
            if (NextUnit() < 0.5f)
                yawOffset = -yawOffset;
        }

        pitchOffset = NextRange(-MaxPitchUpDegrees, MaxPitchDownDegrees);
    }

    private static int ResolveLayerMask(PlayerCharacter human)
    {
        return human != null && human.caster != null && human.caster.layerMask.value != 0
            ? human.caster.layerMask.value
            : Physics.DefaultRaycastLayers;
    }

    private PlayerCharacter GetHumanPlayer()
    {
        var human = WorldManager.localPlayerCharacter;
        if (human == null)
            human = _humanAtSpawn;
        if (human == null || (_body != null && human.gameObject == _body.GameObject))
            return null;
        return human;
    }

    private float NextUnit()
    {
        return (float)_random.NextDouble();
    }

    private float NextRange(float minimum, float maximum)
    {
        return minimum + (maximum - minimum) * NextUnit();
    }
}
