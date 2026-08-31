using UnityEngine;

namespace Ramblers;

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
    private const float VisualMemorySettleSeconds = 0.35f;
    private const float VisualMemoryAimToleranceDegrees = 6f;

    private enum GazeIntent
    {
        Attend,
        Glance
    }

    private readonly CompanionAttention _attention;

    private readonly System.Random _random = new System.Random();

    private CompanionBody _body;
    private PlayerCharacter _humanAtSpawn;
    private GazeIntent _intent;
    private float _intentUntil;
    private int _consecutiveGlances;
    private Vector3 _glanceTarget;
    private bool _glanceAnchored;
    private float _glanceStartedAt;
    private bool _glanceObservationOffered;
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
        _glanceStartedAt = 0f;
        _glanceObservationOffered = false;
        _conversationActive = false;
    }

    internal void SetConversationActive(bool active)
    {
        _conversationActive = active;
    }

    internal void Tick(float now, Vector3 movementIntent)
    {
        if (_body == null || !_body.IsAlive)
            return;

        _peakHeadYaw = Mathf.Max(_peakHeadYaw, Mathf.Abs(_attention.HeadState.x));

        var human = GetHumanPlayer();
        if (human == null)
        {

            _attention.ClearTarget(GazeChannel.Ambient);
            return;
        }

        var humanHead = CompanionBody.HeadPositionOf(human);

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

    internal bool TryTakeSettledGlance(
        float now,
        out CompanionAmbientObservationCandidate candidate)
    {
        candidate = null;
        if (_intent != GazeIntent.Glance || _conversationActive ||
            _glanceObservationOffered ||
            now - _glanceStartedAt < VisualMemorySettleSeconds ||
            _attention.IsOverridden(GazeChannel.Ambient) ||
            !_attention.IsAimWithin(
                GazeChannel.Ambient,
                VisualMemoryAimToleranceDegrees,
                VisualMemoryAimToleranceDegrees))
        {
            return false;
        }

        var direction = _attention.AimDirectionFor(GazeChannel.Ambient);
        if (direction.sqrMagnitude < 0.0001f && _body != null)
            direction = _glanceTarget - _body.HeadPosition;
        if (direction.sqrMagnitude < 0.0001f)
            return false;

        _glanceObservationOffered = true;
        candidate = new CompanionAmbientObservationCandidate
        {
            TargetPoint = _glanceTarget,
            ViewDirection = direction.normalized,
            Anchored = _glanceAnchored
        };
        return true;
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
        _glanceStartedAt = 0f;
        _glanceObservationOffered = false;
        _conversationActive = false;
        _peakHeadYaw = 0f;
        _attention.ClearTarget(GazeChannel.Ambient);
    }

    private void HoldAttention(float until, Vector3 humanHead)
    {
        _intent = GazeIntent.Attend;
        _intentUntil = until;
        _consecutiveGlances = 0;
        _glanceStartedAt = 0f;
        _glanceObservationOffered = false;

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
            _glanceStartedAt = now;
            _glanceObservationOffered = false;
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
        _glanceStartedAt = 0f;
        _glanceObservationOffered = false;
        var attendSeconds = NextRange(MinAttendSeconds, MaxAttendSeconds);
        _intentUntil = now + attendSeconds;
        Plugin.Logger.LogInfo(
            $"[GAZE] ATTEND seconds={attendSeconds:F2}, moving={moving}, " +
            $"peakHeadYaw={_peakHeadYaw:F1}.");
        _peakHeadYaw = 0f;
    }

    private bool TryPickGlanceTarget(
        bool moving,
        Vector3 movementIntent,
        PlayerCharacter human,
        Vector3 humanHead)
    {
        var origin = _body.HeadPosition;

        var baseDirection = new Vector3(movementIntent.x, 0f, movementIntent.z);
        var baseYaw = moving && baseDirection.sqrMagnitude >= 0.0001f
            ? Mathf.Atan2(baseDirection.x, baseDirection.z) * Mathf.Rad2Deg
            : _attention.LastBodyYaw;

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
