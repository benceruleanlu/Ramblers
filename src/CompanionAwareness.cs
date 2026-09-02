using System;
using System.Collections.Generic;
using System.Text.Json;
using Mirror;
using UnityEngine;

namespace Ramblers;

internal sealed class CompanionAwarenessTurnContext
{
    internal AgentContinuationItem Message;
    internal int EventCount;
    internal int NearbyPropCount;
    internal int NearbyPlayerCount;
    internal int RememberedPropCount;
    internal int NearbyInteractableCount;
    internal int RememberedInteractableCount;
    internal float VisualAgeSeconds = -1f;
    internal CompanionEntityReferenceSet EntityReferences;
    internal long DeliveredThroughEventSequence;
    internal float PassiveCapturedAt = -1f;
    internal float CapturedAt = -1f;
    internal string HumanStateKey;
    internal string CompanionStateKey;
    internal string[] SentEntityIds;
    internal bool Unsolicited;
    internal CompanionAwarenessInjectionTrigger Trigger;
    internal float SecondsSinceLastPacket = -1f;
    internal int PacketsLastMinute;
    internal bool HumanChanged;
    internal bool CompanionChanged;

    internal bool HasImage =>
        Message?.ImageBytes != null && Message.ImageBytes.Length > 0;
}

internal sealed class CompanionAmbientObservationCandidate
{
    internal Vector3 TargetPoint;
    internal Vector3 ViewDirection;
    internal bool Anchored;
}

internal sealed class CompanionAwareness
{
    private const int MaximumJournalEntries = 8;
    private const float JournalLifetimeSeconds = 120f;
    private const int MaximumNearbyProps = 6;
    private const int MaximumNearbyPlayers = 3;
    private const float NearbyPropRadius = 10f;
    private const float NearbyPlayerRadius = 15f;
    private const int MaximumRememberedProps = 4;
    private const float RememberedPropLifetimeSeconds = 45f;
    private const float RememberedPropMaximumDistance = 30f;

    private const float PassiveCaptureInitialDelaySeconds = 3f;
    private const float PassiveCaptureIntervalSeconds = 30f;
    private const float PassiveCandidateRetrySeconds = 5f;
    private const float PassiveVisualFreshnessSeconds = 45f;
    private const float PassiveVisualMoveThreshold = 4f;
    private const float PassiveVisualDirectionDotThreshold = 0.7071068f;

    private const float SeparationDistance = 12f;
    private const float ReunionDistance = 5f;
    private const float AreaTransitionDistance = 15f;
    private const float AreaTransitionCooldownSeconds = 10f;
    private const float MeaningfulLandingHeight = 1f;
    private const float MeaningfulAirborneSeconds = 1.2f;

    private const float VisibilityRayStartOffset = 0.08f;
    private const float VisibilitySelfAdvance = 0.02f;
    private const int VisibilityRaySteps = 4;

    private sealed class JournalEntry
    {
        internal long Sequence;
        internal float At;
        internal string Description;
    }

    private sealed class EventPayload
    {
        public float age_seconds { get; set; }
        public string description { get; set; }
    }

    private sealed class HeldPayload
    {
        public string id { get; set; }
        public string kind { get; set; }
        public string name { get; set; }
    }

    private sealed class CompanionStatePayload
    {
        public string follow_mode { get; set; }
        public string follow_state { get; set; }
        public string posture { get; set; }
        public bool moving { get; set; }
        public bool grounded { get; set; }
        public bool carried_by_human { get; set; }
        public bool carrying_human { get; set; }
        public string active_action { get; set; }
        public bool jump_queued { get; set; }
        public HeldPayload held_item { get; set; }
    }

    private sealed class HumanStatePayload
    {
        public float distance_from_companion_m { get; set; }
        public float height_from_companion_m { get; set; }
        public string bearing_from_companion { get; set; }
        public bool visible_from_companion { get; set; }
        public bool grounded { get; set; }
        public HeldPayload held_item { get; set; }
    }

    private sealed class NearbyPropPayload
    {
        public string id { get; set; }
        public string name { get; set; }
        public float distance_from_companion_m { get; set; }
        public float distance_from_human_m { get; set; }
        public float height_from_companion_m { get; set; }
        public string bearing_from_companion { get; set; }
        public bool visible_from_companion { get; set; }
        public string held_by { get; set; }
        public bool pickup_safe_now { get; set; }
    }

    private sealed class NearbyPlayerPayload
    {
        public string id { get; set; }
        public float distance_from_companion_m { get; set; }
        public float height_from_companion_m { get; set; }
        public string bearing_from_companion { get; set; }
        public bool visible_from_companion { get; set; }
        public bool grounded { get; set; }
        public HeldPayload held_item { get; set; }
    }

    private sealed class RememberedPropPayload
    {
        public string id { get; set; }
        public string name { get; set; }
        public float last_seen_seconds_ago { get; set; }
        public float current_distance_from_companion_m { get; set; }
        public string current_bearing_from_companion { get; set; }
    }

    private sealed class NearbyInteractablePayload
    {
        public string id { get; set; }
        public string name { get; set; }
        public string kind { get; set; }
        public float distance_from_companion_m { get; set; }
        public float distance_from_human_m { get; set; }
        public float height_from_companion_m { get; set; }
        public string bearing_from_companion { get; set; }
    }

    private sealed class RememberedInteractablePayload
    {
        public string id { get; set; }
        public string name { get; set; }
        public string kind { get; set; }
        public float last_seen_seconds_ago { get; set; }
        public float current_distance_from_companion_m { get; set; }
        public float current_height_from_companion_m { get; set; }
        public string current_bearing_from_companion { get; set; }
    }

    private sealed class RememberedProp
    {
        internal CompanionPropTarget Target;
        internal string Name;
        internal float SeenAt;
    }

    private struct HeldObservation
    {
        internal int Key;
        internal string Id;
        internal string Kind;
        internal string Name;
    }

    private readonly Queue<JournalEntry> _journal = new Queue<JournalEntry>();
    private readonly Dictionary<string, RememberedProp> _rememberedProps =
        new Dictionary<string, RememberedProp>(StringComparer.Ordinal);
    private readonly CompanionInteractableDiscovery _interactableDiscovery =
        new CompanionInteractableDiscovery();
    private readonly LogLatch _passiveFailureLog = new LogLatch();
    private readonly LogLatch _tickFailureLog = new LogLatch();
    private readonly LogLatch _rateCapLog = new LogLatch();
    private readonly Dictionary<string, float> _sentEntityAt =
        new Dictionary<string, float>(StringComparer.Ordinal);
    private readonly Queue<float> _unsolicitedSentAt = new Queue<float>();
    private float _lastPacketAt = -1f;
    private bool _salientEventPending;
    private float _nextUnsolicitedScanAt;
    private string _lastSentHumanStateKey;
    private string _lastSentCompanionStateKey;

    private CompanionBody _body;
    private PlayerCharacter _humanAtSpawn;
    private CompanionActionCoordinator _actions;

    private long _nextEventSequence;
    private long _lastDeliveredEventSequence;
    private HeldObservation _humanHeld;
    private HeldObservation _companionHeld;
    private bool _companionCarried;
    private bool _companionCarryingHuman;
    private bool _followRequested;
    private CompanionPosture _posture;
    private string _activeAction;
    private bool _humanWasGrounded;
    private bool _companionWasGrounded;
    private float _humanTakeoffAt;
    private float _humanTakeoffY;
    private float _companionTakeoffAt;
    private float _companionTakeoffY;
    private bool _separated;
    private Vector3 _areaAnchor;
    private float _lastAreaTransitionAt;

    private byte[] _passiveImageBytes;
    private string _passiveImageMediaType;
    private float _passiveCapturedAt;
    private Vector3 _passiveTargetPoint;
    private bool _passiveAnchored;
    private bool _passiveDelivered;
    private Vector3 _lastVisualPosition;
    private Vector3 _lastVisualDirection;
    private bool _hasVisualCapture;
    private long _visualEventSequence;
    private float _nextPassiveCaptureAt;

    internal void Bind(
        CompanionBody body,
        PlayerCharacter human,
        CompanionActionCoordinator actions,
        float now)
    {
        Release();
        _body = body;
        _humanAtSpawn = human;
        _actions = actions;
        _humanHeld = CaptureHeld(human);
        _companionHeld = CaptureHeld(body?.Character);
        _companionCarried = actions?.IsCarried == true;
        _companionCarryingHuman = actions?.IsCarryingHuman == true;
        _followRequested = actions?.FollowRequested == true;
        _posture = actions == null
            ? CompanionPosture.Standing
            : actions.Posture;
        _activeAction = actions?.ActiveJobName;
        _humanWasGrounded = IsGrounded(human);
        _companionWasGrounded = IsGrounded(body?.Character);
        var humanPosition = human == null ? Vector3.zero : human.transform.position;
        var bodyPosition = body == null ? humanPosition : body.Position;
        _separated = HorizontalDistance(bodyPosition, humanPosition) >=
                     SeparationDistance;
        _areaAnchor = Midpoint(bodyPosition, humanPosition);
        _lastAreaTransitionAt = now;
        _nextPassiveCaptureAt = now + PassiveCaptureInitialDelaySeconds;
        _nextUnsolicitedScanAt =
            now + CompanionAwarenessInjectionPolicy.ScanIntervalSeconds;
    }

    internal void Tick(float now)
    {
        try
        {
            TickCore(now);
            _tickFailureLog.Reset();
        }
        catch (Exception exception)
        {
            if (_tickFailureLog.ShouldLog())
            {
                Plugin.Logger.LogWarning(
                    $"[AWARENESS] STATE_OBSERVATION_FAILED error={exception.Message}");
            }
        }
    }

    private void TickCore(float now)
    {
        if (_body == null || !_body.IsAlive)
            return;

        var human = GetHumanPlayer();
        if (human == null)
            return;

        ObserveHeldItem(now, "human", CaptureHeld(human), ref _humanHeld);
        ObserveHeldItem(
            now,
            "companion",
            CaptureHeld(_body.Character),
            ref _companionHeld);

        var carried = _actions?.IsCarried == true;
        if (carried != _companionCarried)
        {
            RecordEvent(
                now,
                carried
                    ? "the human picked up the companion"
                    : "the human released the companion",
                true);
            _companionCarried = carried;
        }

        var carryingHuman = _actions?.IsCarryingHuman == true;
        if (carryingHuman != _companionCarryingHuman)
        {
            RecordEvent(
                now,
                carryingHuman
                    ? "the companion picked up the human"
                    : "the companion released the human",
                true);
            _companionCarryingHuman = carryingHuman;
        }

        var followRequested = _actions?.FollowRequested == true;
        if (followRequested != _followRequested)
        {
            RecordEvent(
                now,
                followRequested
                    ? "the companion started following the human"
                    : "the companion stopped following and is staying put",
                false);
            _followRequested = followRequested;
        }

        var posture = _actions == null
            ? CompanionPosture.Standing
            : _actions.Posture;
        if (posture != _posture)
        {
            RecordEvent(
                now,
                "the companion changed posture to " + PostureLabel(posture),
                false);
            _posture = posture;
        }

        var activeAction = _actions?.ActiveJobName;
        if (!string.Equals(activeAction, _activeAction, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(activeAction))
            {
                RecordEvent(
                    now,
                    "the companion started action " + activeAction,
                    false);
            }
            else if (!string.IsNullOrEmpty(_activeAction))
            {
                RecordEvent(
                    now,
                    "the companion's " + _activeAction + " action ended",
                    true);
            }
            _activeAction = activeAction;
        }

        ObserveLanding(
            now,
            "human",
            human,
            ref _humanWasGrounded,
            ref _humanTakeoffAt,
            ref _humanTakeoffY);
        ObserveLanding(
            now,
            "companion",
            _body.Character,
            ref _companionWasGrounded,
            ref _companionTakeoffAt,
            ref _companionTakeoffY);

        var humanPosition = human.transform.position;
        var bodyPosition = _body.Position;
        var separation = HorizontalDistance(bodyPosition, humanPosition);
        if (!_separated && separation >= SeparationDistance)
        {
            RecordEvent(
                now,
                $"the human and companion became separated by {Round1(separation):F1}m",
                true);
            _separated = true;
        }
        else if (_separated && separation <= ReunionDistance)
        {
            RecordEvent(
                now,
                "the human and companion came back together",
                true);
            _separated = false;
        }

        var midpoint = Midpoint(bodyPosition, humanPosition);
        var areaDistance = HorizontalDistance(midpoint, _areaAnchor);
        if (areaDistance >= AreaTransitionDistance &&
            now - _lastAreaTransitionAt >= AreaTransitionCooldownSeconds)
        {
            RecordEvent(
                now,
                $"the walk progressed about {Round1(areaDistance):F1}m into a different area",
                true);
            _areaAnchor = midpoint;
            _lastAreaTransitionAt = now;
        }

        RemoveExpiredEvents(now);
    }

    internal void TryRememberPassiveView(
        float now,
        CompanionAmbientObservationCandidate candidate)
    {
        if (candidate == null || _body == null || !_body.IsAlive ||
            now < _nextPassiveCaptureAt)
        {
            return;
        }

        var direction = candidate.ViewDirection;
        if (direction.sqrMagnitude < 0.0001f)
            direction = candidate.TargetPoint - _body.HeadPosition;
        if (direction.sqrMagnitude < 0.0001f)
            return;
        direction.Normalize();

        var moved = !_hasVisualCapture ||
                    HorizontalDistance(_body.Position, _lastVisualPosition) >=
                    PassiveVisualMoveThreshold;
        var directionChanged = !_hasVisualCapture ||
                               Vector3.Dot(direction, _lastVisualDirection) <=
                               PassiveVisualDirectionDotThreshold;
        var worldChanged = _nextEventSequence > _visualEventSequence;
        if (!moved && !directionChanged && !worldChanged)
        {
            _nextPassiveCaptureAt = now + PassiveCandidateRetrySeconds;
            return;
        }

        _nextPassiveCaptureAt = now + PassiveCaptureIntervalSeconds;
        var human = GetHumanPlayer();
        CompanionVisionObservation observation;
        string error;
        if (!CompanionVisionCapture.TryCapture(
                _body,
                human,
                candidate.TargetPoint,
                direction,
                candidate.Anchored,
                false,
                out observation,
                out error))
        {
            _nextPassiveCaptureAt = now + PassiveCandidateRetrySeconds;
            if (_passiveFailureLog.ShouldLog())
            {
                Plugin.Logger.LogWarning(
                    $"[AWARENESS] PASSIVE_VIEW_FAILED error={error ?? "image_capture_failed"}.");
            }
            return;
        }

        _passiveFailureLog.Reset();
        _passiveImageBytes = observation.ImageBytes;
        _passiveImageMediaType = observation.MediaType;
        _passiveCapturedAt = now;
        _passiveTargetPoint = candidate.TargetPoint;
        _passiveAnchored = candidate.Anchored;
        _passiveDelivered = false;
        _lastVisualPosition = _body.Position;
        _lastVisualDirection = direction;
        _hasVisualCapture = true;
        _visualEventSequence = _nextEventSequence;
        Plugin.Logger.LogInfo(
            $"[AWARENESS] PASSIVE_VIEW_CAPTURED imageBytes={observation.ImageBytes.Length}, " +
            $"mediaType={observation.MediaType}, anchored={candidate.Anchored}, " +
            $"target={candidate.TargetPoint}.");
    }

    internal bool TryTakeTurnContext(
        float now,
        CompanionAffordanceCandidates affordanceCandidates,
        out CompanionAwarenessTurnContext context,
        out string error)
    {
        context = null;
        error = null;
        if (_body == null || !_body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }

        var human = GetHumanPlayer();
        if (human == null)
        {
            error = "human_player_unavailable";
            return false;
        }

        Tick(now);
        var entityReferences = new CompanionEntityReferenceSet();
        var nearbyProps = CaptureNearbyProps(human, now, entityReferences);
        var nearbyPropIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < nearbyProps.Length; index++)
            nearbyPropIds.Add(nearbyProps[index].id);
        var rememberedProps = CaptureRememberedProps(
            now,
            nearbyPropIds,
            entityReferences);
        CompanionInteractableObservation[] nearbyInteractableObservations;
        CompanionInteractableObservation[] rememberedInteractableObservations;
        _interactableDiscovery.Capture(
            human,
            _body,
            affordanceCandidates,
            now,
            entityReferences,
            out nearbyInteractableObservations,
            out rememberedInteractableObservations);
        var nearbyInteractables = ToNearbyInteractables(
            nearbyInteractableObservations);
        var rememberedInteractables = ToRememberedInteractables(
            rememberedInteractableObservations,
            now);
        var nearbyPlayers = CaptureNearbyPlayers(human);
        var recentEvents = CaptureUndeliveredEvents(now);
        var visualAge = _passiveImageBytes == null
            ? -1f
            : Mathf.Max(0f, now - _passiveCapturedAt);
        var attachVisual = !_passiveDelivered &&
                           _passiveImageBytes != null &&
                           visualAge <= PassiveVisualFreshnessSeconds;
        if (!attachVisual && !_passiveDelivered && _passiveImageBytes != null &&
            visualAge > PassiveVisualFreshnessSeconds)
        {
            _passiveDelivered = true;
        }

        string companionStateKey;
        string humanStateKey;
        var companionState = CaptureCompanionState(out companionStateKey);
        var humanState = CaptureHumanState(human, out humanStateKey);
        var visualStatus = attachVisual
            ? "attached_recent_ambient_view"
            : "no_new_visual_frame";
        var payload = new
        {
            schema = "ramblers.game_context.v1",
            captured_at = "human_utterance_boundary",
            companion = companionState,
            human = humanState,
            nearby_props = nearbyProps,
            recently_seen_props = rememberedProps,
            nearby_interactables = nearbyInteractables,
            recently_seen_interactables = rememberedInteractables,
            interactable_discovery = new
            {
                bounded = true,
                capability_boundary =
                    CompanionInteractableDiscovery.CapabilityBoundary
            },
            other_nearby_players = nearbyPlayers,
            recent_events = recentEvents,
            visual_memory = new
            {
                status = visualStatus,
                age_seconds = attachVisual ? Round1(visualAge) : -1f,
                source = attachVisual
                    ? (_passiveAnchored
                        ? "settled_ambient_glance_raycast_hit"
                        : "settled_ambient_glance_open_view")
                    : "none",
                target_distance_m = attachVisual
                    ? Round1(Vector3.Distance(
                        _body.HeadPosition,
                        _passiveTargetPoint))
                    : -1f
            }
        };
        var json = JsonSerializer.Serialize(payload);
        var text =
            "[GAME_CONTEXT]\n" +
            "Private nonverbal game perception for the preceding human utterance. " +
            "Use it when relevant; do not answer this packet itself, quote it, or " +
            "narrate every field. Visual details are known only when an attached " +
            "visual_memory frame shows them.\n" +
            json;
        var message = attachVisual
            ? AgentContinuationItem.FromImage(
                text,
                _passiveImageBytes,
                _passiveImageMediaType)
            : AgentContinuationItem.FromText(text);
        context = new CompanionAwarenessTurnContext
        {
            Message = message,
            EventCount = recentEvents.Length,
            NearbyPropCount = nearbyProps.Length,
            NearbyPlayerCount = nearbyPlayers.Length,
            RememberedPropCount = rememberedProps.Length,
            NearbyInteractableCount = nearbyInteractables.Length,
            RememberedInteractableCount = rememberedInteractables.Length,
            EntityReferences = entityReferences,
            DeliveredThroughEventSequence = _nextEventSequence,
            PassiveCapturedAt = attachVisual ? _passiveCapturedAt : -1f,
            VisualAgeSeconds = attachVisual ? visualAge : -1f,
            CapturedAt = now,
            HumanStateKey = humanStateKey,
            CompanionStateKey = companionStateKey,
            SentEntityIds = CollectSentIds(
                nearbyProps,
                rememberedProps,
                nearbyInteractables,
                rememberedInteractables,
                nearbyPlayers)
        };
        return true;
    }

    internal void ConfirmTurnContextDelivered(
        CompanionAwarenessTurnContext context)
    {
        if (context == null)
            return;
        _lastDeliveredEventSequence = Math.Max(
            _lastDeliveredEventSequence,
            context.DeliveredThroughEventSequence);
        if (context.HasImage && context.PassiveCapturedAt >= 0f &&
            Mathf.Approximately(context.PassiveCapturedAt, _passiveCapturedAt))
        {
            _passiveDelivered = true;
        }
        CommitPacket(context);
    }

    internal bool IsUnsolicitedScanDue(float now)
    {
        return _body != null && _body.IsAlive && now >= _nextUnsolicitedScanAt;
    }

    internal bool TryTakeUnsolicitedContext(
        float now,
        CompanionAffordanceCandidates affordanceCandidates,
        out CompanionAwarenessTurnContext context,
        out string error)
    {
        context = null;
        error = null;
        if (_body == null || !_body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }

        var human = GetHumanPlayer();
        if (human == null)
        {
            error = "human_player_unavailable";
            return false;
        }

        _nextUnsolicitedScanAt =
            now + CompanionAwarenessInjectionPolicy.ScanIntervalSeconds;
        var packetsLastMinute = CompanionAwarenessInjectionPolicy.CountInWindow(
            _unsolicitedSentAt,
            now);
        var sinceLastPacket = _lastPacketAt < 0f ? -1f : now - _lastPacketAt;
        string capReason;
        if (CompanionAwarenessInjectionPolicy.IsRateCapped(
                now,
                _lastPacketAt,
                packetsLastMinute,
                out capReason))
        {
            if (string.Equals(capReason, "minimum_interval", StringComparison.Ordinal))
            {
                _nextUnsolicitedScanAt = Mathf.Min(
                    _nextUnsolicitedScanAt,
                    _lastPacketAt +
                    CompanionAwarenessInjectionPolicy.MinimumIntervalSeconds);
            }
            if (_rateCapLog.ShouldLog())
            {
                Plugin.Logger.LogInfo(
                    $"[AWARENESS] EVENT_CONTEXT_DEFERRED reason={capReason}, " +
                    $"salientPending={_salientEventPending}, " +
                    $"packetsLastMinute={packetsLastMinute}, " +
                    $"sinceLastPacketSeconds={sinceLastPacket:F1}.");
            }
            error = "rate_capped_" + capReason;
            return false;
        }
        _rateCapLog.Reset();

        var entityReferences = new CompanionEntityReferenceSet();
        var nearbyProps = CaptureNearbyProps(human, now, entityReferences);
        var nearbyPropIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < nearbyProps.Length; index++)
            nearbyPropIds.Add(nearbyProps[index].id);
        CaptureRememberedProps(now, nearbyPropIds, entityReferences);
        CompanionInteractableObservation[] nearbyInteractableObservations;
        CompanionInteractableObservation[] rememberedInteractableObservations;
        _interactableDiscovery.Capture(
            human,
            _body,
            affordanceCandidates,
            now,
            entityReferences,
            out nearbyInteractableObservations,
            out rememberedInteractableObservations);
        var nearbyInteractables = ToNearbyInteractables(
            nearbyInteractableObservations);
        var nearbyPlayers = CaptureNearbyPlayers(human);
        var recentEvents = CaptureUndeliveredEvents(now);
        string companionStateKey;
        string humanStateKey;
        var companionState = CaptureCompanionState(out companionStateKey);
        var humanState = CaptureHumanState(human, out humanStateKey);

        var newProps = FilterUnsent(nearbyProps, now, prop => prop.id);
        var newInteractables = FilterUnsent(
            nearbyInteractables,
            now,
            interactable => interactable.id);
        var newPlayers = FilterUnsent(nearbyPlayers, now, player => player.id);
        var companionChanged = !string.Equals(
            companionStateKey,
            _lastSentCompanionStateKey,
            StringComparison.Ordinal);
        var humanChanged = !string.Equals(
            humanStateKey,
            _lastSentHumanStateKey,
            StringComparison.Ordinal);
        var entityEntered = newProps.Length + newInteractables.Length +
                            newPlayers.Length > 0;
        var trigger = CompanionAwarenessInjectionPolicy.SelectTrigger(
            _salientEventPending,
            entityEntered,
            companionChanged || humanChanged,
            recentEvents.Length > 0,
            sinceLastPacket < 0f ? float.MaxValue : sinceLastPacket);
        if (trigger == CompanionAwarenessInjectionTrigger.None)
        {
            error = "nothing_salient";
            return false;
        }

        var includeState =
            trigger == CompanionAwarenessInjectionTrigger.SalientEvent;
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schema"] = "ramblers.game_context.v1",
            ["captured_at"] = "unsolicited_" +
                              CompanionAwarenessInjectionPolicy.TriggerLabel(
                                  trigger),
            ["delta_since_last_packet"] = true,
            ["seconds_since_last_packet"] =
                sinceLastPacket < 0f ? -1f : Round1(sinceLastPacket)
        };
        if (includeState || companionChanged)
            payload["companion"] = companionState;
        if (includeState || humanChanged)
            payload["human"] = humanState;
        if (newProps.Length > 0)
            payload["newly_nearby_props"] = newProps;
        if (newInteractables.Length > 0)
            payload["newly_nearby_interactables"] = newInteractables;
        if (newPlayers.Length > 0)
            payload["newly_nearby_players"] = newPlayers;
        if (recentEvents.Length > 0)
            payload["recent_events"] = recentEvents;

        var json = JsonSerializer.Serialize(payload);
        var text =
            "[GAME_CONTEXT]\n" +
            "Private nonverbal game perception that arrived unsolicited while " +
            "nobody was speaking. It lists only what changed since the last " +
            "packet. It is not a message and needs no reply now; keep it in " +
            "mind and use it when it matters.\n" +
            json;
        var sentIds = new List<string>();
        for (var index = 0; index < newProps.Length; index++)
            sentIds.Add(newProps[index].id);
        for (var index = 0; index < newInteractables.Length; index++)
            sentIds.Add(newInteractables[index].id);
        for (var index = 0; index < newPlayers.Length; index++)
            sentIds.Add(newPlayers[index].id);
        context = new CompanionAwarenessTurnContext
        {
            Message = AgentContinuationItem.FromText(text),
            EventCount = recentEvents.Length,
            NearbyPropCount = newProps.Length,
            NearbyPlayerCount = newPlayers.Length,
            NearbyInteractableCount = newInteractables.Length,
            EntityReferences = entityReferences,
            DeliveredThroughEventSequence = _nextEventSequence,
            CapturedAt = now,
            HumanStateKey = humanStateKey,
            CompanionStateKey = companionStateKey,
            SentEntityIds = sentIds.ToArray(),
            Unsolicited = true,
            Trigger = trigger,
            SecondsSinceLastPacket = sinceLastPacket,
            PacketsLastMinute = packetsLastMinute,
            HumanChanged = humanChanged,
            CompanionChanged = companionChanged
        };
        return true;
    }

    internal void ConfirmUnsolicitedContextDelivered(
        CompanionAwarenessTurnContext context)
    {
        if (context == null)
            return;
        _lastDeliveredEventSequence = Math.Max(
            _lastDeliveredEventSequence,
            context.DeliveredThroughEventSequence);
        _unsolicitedSentAt.Enqueue(context.CapturedAt);
        CommitPacket(context);
    }

    private void CommitPacket(CompanionAwarenessTurnContext context)
    {
        if (context.CapturedAt >= 0f)
            _lastPacketAt = context.CapturedAt;
        if (context.HumanStateKey != null)
            _lastSentHumanStateKey = context.HumanStateKey;
        if (context.CompanionStateKey != null)
            _lastSentCompanionStateKey = context.CompanionStateKey;
        if (context.SentEntityIds != null)
        {
            for (var index = 0; index < context.SentEntityIds.Length; index++)
            {
                var id = context.SentEntityIds[index];
                if (!string.IsNullOrEmpty(id))
                    _sentEntityAt[id] = context.CapturedAt;
            }
        }

        var expired = new List<string>();
        foreach (var pair in _sentEntityAt)
        {
            if (context.CapturedAt - pair.Value >=
                CompanionAwarenessInjectionPolicy.EntityResendSeconds)
            {
                expired.Add(pair.Key);
            }
        }
        for (var index = 0; index < expired.Count; index++)
            _sentEntityAt.Remove(expired[index]);
        _salientEventPending = false;
    }

    private T[] FilterUnsent<T>(T[] items, float now, Func<T, string> idOf)
    {
        if (items == null || items.Length == 0)
            return items ?? new T[0];
        var result = new List<T>();
        for (var index = 0; index < items.Length; index++)
        {
            var id = idOf(items[index]);
            var sentAt = 0f;
            var sentBefore = !string.IsNullOrEmpty(id) &&
                             _sentEntityAt.TryGetValue(id, out sentAt);
            if (CompanionAwarenessInjectionPolicy.ShouldResendEntity(
                    sentBefore,
                    sentAt,
                    now))
            {
                result.Add(items[index]);
            }
        }
        return result.ToArray();
    }

    private static string[] CollectSentIds(
        NearbyPropPayload[] nearbyProps,
        RememberedPropPayload[] rememberedProps,
        NearbyInteractablePayload[] nearbyInteractables,
        RememberedInteractablePayload[] rememberedInteractables,
        NearbyPlayerPayload[] nearbyPlayers)
    {
        var ids = new List<string>();
        for (var index = 0; index < nearbyProps.Length; index++)
            ids.Add(nearbyProps[index].id);
        for (var index = 0; index < rememberedProps.Length; index++)
            ids.Add(rememberedProps[index].id);
        for (var index = 0; index < nearbyInteractables.Length; index++)
            ids.Add(nearbyInteractables[index].id);
        for (var index = 0; index < rememberedInteractables.Length; index++)
            ids.Add(rememberedInteractables[index].id);
        for (var index = 0; index < nearbyPlayers.Length; index++)
            ids.Add(nearbyPlayers[index].id);
        return ids.ToArray();
    }

    private CompanionStatePayload CaptureCompanionState(out string key)
    {
        var held = CaptureHeld(_body.Character);
        var followMode = _actions?.FollowRequested == true ? "follow" : "stay";
        var posture = PostureLabel(
            _actions == null ? CompanionPosture.Standing : _actions.Posture);
        var carried = _actions?.IsCarried == true;
        var activeAction = _actions?.ActiveJobName ?? "none";
        key = followMode + "|" + posture + "|" + carried + "|" +
              (_actions?.IsCarryingHuman == true) + "|" + activeAction + "|" +
              (held.Key == 0 ? "none" : held.Id);
        return new CompanionStatePayload
        {
            follow_mode = followMode,
            follow_state = _actions?.FollowStateLabel ?? "unavailable",
            posture = posture,
            moving = _actions?.IsMoving == true,
            grounded = IsGrounded(_body.Character),
            carried_by_human = carried,
            carrying_human = _actions?.IsCarryingHuman == true,
            active_action = activeAction,
            jump_queued = _actions?.JumpQueued == true,
            held_item = ToPayload(held)
        };
    }

    private HumanStatePayload CaptureHumanState(
        PlayerCharacter human,
        out string key)
    {
        var humanOffset = human.transform.position - _body.Position;
        var distance = Round1(HorizontalMagnitude(humanOffset));
        var bearing = BearingLabel(_body.Transform.forward, humanOffset);
        var visible = HasLineOfSight(
            _body.HeadPosition,
            human.transform,
            CompanionBody.HeadPositionOf(human),
            ResolveLayerMask(human));
        var grounded = IsGrounded(human);
        var held = CaptureHeld(human);
        key = CompanionAwarenessInjectionPolicy.HumanDistanceBucket(distance) +
              "|" + bearing + "|" + visible + "|" + grounded + "|" +
              (held.Key == 0 ? "none" : held.Id);
        return new HumanStatePayload
        {
            distance_from_companion_m = distance,
            height_from_companion_m = Round1(humanOffset.y),
            bearing_from_companion = bearing,
            visible_from_companion = visible,
            grounded = grounded,
            held_item = ToPayload(held)
        };
    }

    internal void Release()
    {
        _body = null;
        _humanAtSpawn = null;
        _actions = null;
        _journal.Clear();
        _rememberedProps.Clear();
        _interactableDiscovery.Clear();
        _nextEventSequence = 0;
        _lastDeliveredEventSequence = 0;
        _humanHeld = default;
        _companionHeld = default;
        _companionCarried = false;
        _companionCarryingHuman = false;
        _followRequested = false;
        _posture = CompanionPosture.Standing;
        _activeAction = null;
        _humanWasGrounded = false;
        _companionWasGrounded = false;
        _humanTakeoffAt = 0f;
        _humanTakeoffY = 0f;
        _companionTakeoffAt = 0f;
        _companionTakeoffY = 0f;
        _separated = false;
        _areaAnchor = Vector3.zero;
        _lastAreaTransitionAt = 0f;
        _passiveImageBytes = null;
        _passiveImageMediaType = null;
        _passiveCapturedAt = 0f;
        _passiveTargetPoint = Vector3.zero;
        _passiveAnchored = false;
        _passiveDelivered = false;
        _lastVisualPosition = Vector3.zero;
        _lastVisualDirection = Vector3.zero;
        _hasVisualCapture = false;
        _visualEventSequence = 0;
        _nextPassiveCaptureAt = 0f;
        _sentEntityAt.Clear();
        _unsolicitedSentAt.Clear();
        _lastPacketAt = -1f;
        _salientEventPending = false;
        _nextUnsolicitedScanAt = 0f;
        _lastSentHumanStateKey = null;
        _lastSentCompanionStateKey = null;
        _passiveFailureLog.Reset();
        _tickFailureLog.Reset();
        _rateCapLog.Reset();
    }

    private void ObserveHeldItem(
        float now,
        string actor,
        HeldObservation current,
        ref HeldObservation previous)
    {
        if (current.Key == previous.Key &&
            string.Equals(current.Kind, previous.Kind, StringComparison.Ordinal))
        {
            previous = current;
            return;
        }

        var salient = IsHumanActor(actor);
        if (previous.Key != 0 && current.Key == 0)
        {
            RecordEvent(
                now,
                actor + " released " + DescribeHeld(previous),
                salient);
        }
        else if (current.Key != 0 && previous.Key == 0)
        {
            RecordEvent(
                now,
                actor + " picked up " + DescribeHeld(current),
                salient);
        }
        else if (current.Key != 0)
        {
            RecordEvent(
                now,
                actor + " switched from " + DescribeHeld(previous) +
                " to " + DescribeHeld(current),
                salient);
        }

        previous = current;
    }

    private void ObserveLanding(
        float now,
        string actor,
        PlayerCharacter character,
        ref bool wasGrounded,
        ref float takeoffAt,
        ref float takeoffY)
    {
        if (character?.ground == null)
            return;

        var grounded = character.ground.isGrounded;
        var y = character.transform.position.y;
        if (wasGrounded && !grounded)
        {
            takeoffAt = now;
            takeoffY = y;
        }
        else if (!wasGrounded && grounded)
        {
            var seconds = Mathf.Max(0f, now - takeoffAt);
            var height = y - takeoffY;
            if (Mathf.Abs(height) >= MeaningfulLandingHeight ||
                seconds >= MeaningfulAirborneSeconds)
            {
                var direction = height < -MeaningfulLandingHeight
                    ? "lower"
                    : height > MeaningfulLandingHeight
                        ? "higher"
                        : "near the takeoff height";
                RecordEvent(
                    now,
                    $"{actor} landed {Mathf.Abs(Round1(height)):F1}m {direction} " +
                    $"after {Round1(seconds):F1}s airborne",
                    IsHumanActor(actor));
            }
        }

        wasGrounded = grounded;
    }

    private void RecordEvent(float now, string description, bool salient)
    {
        if (string.IsNullOrWhiteSpace(description))
            return;

        _journal.Enqueue(new JournalEntry
        {
            Sequence = ++_nextEventSequence,
            At = now,
            Description = description
        });
        while (_journal.Count > MaximumJournalEntries)
            _journal.Dequeue();
        if (salient)
        {
            _salientEventPending = true;
            _nextUnsolicitedScanAt =
                CompanionAwarenessInjectionPolicy.NextScanAfterSalientEvent(
                    now,
                    _lastPacketAt,
                    _nextUnsolicitedScanAt);
        }
        Plugin.Logger.LogInfo(
            $"[AWARENESS] EVENT sequence={_nextEventSequence}, " +
            $"salient={salient}, description={description}.");
    }

    private static bool IsHumanActor(string actor)
    {
        return string.Equals(actor, "human", StringComparison.Ordinal);
    }

    private void RemoveExpiredEvents(float now)
    {
        while (_journal.Count > 0 &&
               now - _journal.Peek().At > JournalLifetimeSeconds)
        {
            _journal.Dequeue();
        }
    }

    private EventPayload[] CaptureUndeliveredEvents(float now)
    {
        RemoveExpiredEvents(now);
        var events = new List<EventPayload>();
        foreach (var entry in _journal)
        {
            if (entry.Sequence <= _lastDeliveredEventSequence)
                continue;
            events.Add(new EventPayload
            {
                age_seconds = Round1(Mathf.Max(0f, now - entry.At)),
                description = entry.Description
            });
        }
        return events.ToArray();
    }

    private NearbyInteractablePayload[] ToNearbyInteractables(
        CompanionInteractableObservation[] observations)
    {
        if (observations == null || observations.Length == 0)
            return new NearbyInteractablePayload[0];

        var payloads = new NearbyInteractablePayload[observations.Length];
        for (var index = 0; index < observations.Length; index++)
        {
            var observation = observations[index];
            var offset = observation.Point - _body.Position;
            payloads[index] = new NearbyInteractablePayload
            {
                id = observation.Reference.StableId,
                name = observation.Reference.Name,
                kind = observation.Reference.Kind,
                distance_from_companion_m = Round1(
                    observation.CompanionDistance),
                distance_from_human_m = Round1(observation.HumanDistance),
                height_from_companion_m = Round1(offset.y),
                bearing_from_companion = BearingLabel(
                    _body.Transform.forward,
                    offset)
            };
        }
        return payloads;
    }

    private RememberedInteractablePayload[] ToRememberedInteractables(
        CompanionInteractableObservation[] observations,
        float now)
    {
        if (observations == null || observations.Length == 0)
            return new RememberedInteractablePayload[0];

        var payloads =
            new RememberedInteractablePayload[observations.Length];
        for (var index = 0; index < observations.Length; index++)
        {
            var observation = observations[index];
            var offset = observation.Point - _body.Position;
            payloads[index] = new RememberedInteractablePayload
            {
                id = observation.Reference.StableId,
                name = observation.Reference.Name,
                kind = observation.Reference.Kind,
                last_seen_seconds_ago = Round1(
                    Mathf.Max(0f, now - observation.SeenAt)),
                current_distance_from_companion_m = Round1(
                    observation.CompanionDistance),
                current_height_from_companion_m = Round1(offset.y),
                current_bearing_from_companion = BearingLabel(
                    _body.Transform.forward,
                    offset)
            };
        }
        return payloads;
    }

    private NearbyPropPayload[] CaptureNearbyProps(
        PlayerCharacter human,
        float now,
        CompanionEntityReferenceSet entityReferences)
    {
        var result = new List<NearbyPropPayload>();
        var capturedTargets =
            new Dictionary<string, CompanionPropTarget>(StringComparer.Ordinal);
        try
        {
            var props = Prop.allProps;
            if (props == null)
                return result.ToArray();

            var botPosition = _body.Position;
            var humanPosition = human.transform.position;
            var botHeld = _body.Character?.hands?.heldProp;
            var humanHeld = human.hands?.heldProp;
            var layerMask = ResolveLayerMask(human);
            for (var index = 0; index < props.Count; index++)
            {
                var prop = props[index];
                if (prop == null || prop.gameObject == null ||
                    !prop.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var position = prop.transform.position;
                var botDistance = Vector3.Distance(botPosition, position);
                var humanDistance = Vector3.Distance(humanPosition, position);
                if (botDistance > NearbyPropRadius &&
                    humanDistance > NearbyPropRadius)
                {
                    continue;
                }

                var heldBy = prop == humanHeld
                    ? "human"
                    : prop == botHeld
                        ? "companion"
                        : prop.isInInventory
                            ? "other_or_inventory"
                            : "none";
                CompanionPropTarget entityTarget;
                if (CompanionPropTarget.TryCaptureProp(
                        prop,
                        out entityTarget))
                {
                    capturedTargets[entityTarget.StableId] = entityTarget;
                }
                result.Add(new NearbyPropPayload
                {
                    id = StablePropId(prop),
                    name = PropName(prop),
                    distance_from_companion_m = Round1(botDistance),
                    distance_from_human_m = Round1(humanDistance),
                    height_from_companion_m = Round1(position.y - botPosition.y),
                    bearing_from_companion = BearingLabel(
                        _body.Transform.forward,
                        position - botPosition),
                    visible_from_companion = HasLineOfSight(
                        _body.HeadPosition,
                        prop.transform,
                        position,
                        layerMask),
                    held_by = heldBy,
                    pickup_safe_now = IsPickupSafeNow(prop)
                });
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[AWARENESS] Nearby-prop snapshot degraded: {exception.Message}");
        }

        result.Sort((left, right) =>
            Mathf.Min(
                    left.distance_from_companion_m,
                    left.distance_from_human_m)
                .CompareTo(Mathf.Min(
                    right.distance_from_companion_m,
                    right.distance_from_human_m)));
        if (result.Count > MaximumNearbyProps)
            result.RemoveRange(MaximumNearbyProps, result.Count - MaximumNearbyProps);
        for (var index = 0; index < result.Count; index++)
        {
            CompanionPropTarget target;
            if (!capturedTargets.TryGetValue(result[index].id, out target))
                continue;
            entityReferences.Add(target);
            _rememberedProps[target.StableId] = new RememberedProp
            {
                Target = target,
                Name = result[index].name,
                SeenAt = now
            };
        }
        return result.ToArray();
    }

    private RememberedPropPayload[] CaptureRememberedProps(
        float now,
        HashSet<string> nearbyPropIds,
        CompanionEntityReferenceSet entityReferences)
    {
        var result = new List<RememberedPropPayload>();
        var capturedTargets =
            new Dictionary<string, CompanionPropTarget>(StringComparer.Ordinal);
        var expired = new List<string>();
        foreach (var pair in _rememberedProps)
        {
            var memory = pair.Value;
            Vector3 point;
            if (memory == null || memory.Target == null ||
                now - memory.SeenAt > RememberedPropLifetimeSeconds ||
                !memory.Target.TryGetCurrentPoint(out point))
            {
                expired.Add(pair.Key);
                continue;
            }

            var distance = Vector3.Distance(_body.Position, point);
            if (distance > RememberedPropMaximumDistance)
                continue;

            if (nearbyPropIds.Contains(pair.Key))
                continue;
            capturedTargets[pair.Key] = memory.Target;
            result.Add(new RememberedPropPayload
            {
                id = pair.Key,
                name = memory.Name,
                last_seen_seconds_ago = Round1(Mathf.Max(0f, now - memory.SeenAt)),
                current_distance_from_companion_m = Round1(distance),
                current_bearing_from_companion = BearingLabel(
                    _body.Transform.forward,
                    point - _body.Position)
            });
        }

        for (var index = 0; index < expired.Count; index++)
            _rememberedProps.Remove(expired[index]);
        result.Sort((left, right) =>
            left.last_seen_seconds_ago.CompareTo(right.last_seen_seconds_ago));
        if (result.Count > MaximumRememberedProps)
            result.RemoveRange(MaximumRememberedProps, result.Count - MaximumRememberedProps);
        for (var index = 0; index < result.Count; index++)
        {
            CompanionPropTarget target;
            if (capturedTargets.TryGetValue(result[index].id, out target))
                entityReferences.Add(target);
        }
        return result.ToArray();
    }

    private NearbyPlayerPayload[] CaptureNearbyPlayers(PlayerCharacter human)
    {
        var result = new List<NearbyPlayerPayload>();
        try
        {
            var players = PlayerCharacter.allPlayerCharacters;
            if (players == null)
                return result.ToArray();

            var botPosition = _body.Position;
            var layerMask = ResolveLayerMask(human);
            for (var index = 0; index < players.Count; index++)
            {
                var player = players[index];
                if (player == null || player.gameObject == null ||
                    !player.gameObject.activeInHierarchy ||
                    player.gameObject == _body.GameObject ||
                    player.gameObject == human.gameObject)
                {
                    continue;
                }

                var position = player.transform.position;
                var distance = Vector3.Distance(botPosition, position);
                if (distance > NearbyPlayerRadius)
                    continue;
                result.Add(new NearbyPlayerPayload
                {
                    id = StablePlayerId(player),
                    distance_from_companion_m = Round1(distance),
                    height_from_companion_m = Round1(position.y - botPosition.y),
                    bearing_from_companion = BearingLabel(
                        _body.Transform.forward,
                        position - botPosition),
                    visible_from_companion = HasLineOfSight(
                        _body.HeadPosition,
                        player.transform,
                        CompanionBody.HeadPositionOf(player),
                        layerMask),
                    grounded = IsGrounded(player),
                    held_item = ToPayload(CaptureHeld(player))
                });
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[AWARENESS] Nearby-player snapshot degraded: {exception.Message}");
        }

        result.Sort((left, right) =>
            left.distance_from_companion_m.CompareTo(
                right.distance_from_companion_m));
        if (result.Count > MaximumNearbyPlayers)
            result.RemoveRange(MaximumNearbyPlayers, result.Count - MaximumNearbyPlayers);
        return result.ToArray();
    }

    private bool IsPickupSafeNow(Prop prop)
    {
        var hands = _body.Character?.hands;
        if (hands == null || prop == null || prop.rb == null ||
            prop.isInInventory || hands.heldProp != null ||
            hands.heldCharacter != null)
        {
            return false;
        }

        try
        {
            return hands.IsSafeToPickUp(prop);
        }
        catch
        {
            return false;
        }
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

    private static HeldObservation CaptureHeld(PlayerCharacter character)
    {
        var prop = character?.hands?.heldProp;
        if (prop != null)
        {
            return new HeldObservation
            {
                Key = prop.GetInstanceID(),
                Id = StablePropId(prop),
                Kind = "prop",
                Name = PropName(prop)
            };
        }

        var heldCharacter = character?.hands?.heldCharacter;
        if (heldCharacter != null)
        {
            return new HeldObservation
            {
                Key = heldCharacter.GetInstanceID(),
                Id = StablePlayerId(heldCharacter),
                Kind = "player",
                Name = "player"
            };
        }

        return default;
    }

    private static HeldPayload ToPayload(HeldObservation held)
    {
        if (held.Key == 0)
            return null;
        return new HeldPayload
        {
            id = held.Id,
            kind = held.Kind,
            name = held.Name
        };
    }

    private static string DescribeHeld(HeldObservation held)
    {
        if (held.Key == 0)
            return "nothing";
        return held.Name + " (" + held.Id + ")";
    }

    private static string StablePropId(Prop prop)
    {
        return CompanionPropTarget.StableIdFor(prop);
    }

    private static string StablePlayerId(PlayerCharacter player)
    {
        if (player == null)
            return "player:unavailable";
        var identity = player.GetComponentInParent<NetworkIdentity>();
        return identity != null && identity.netId != 0u
            ? "player:net:" + identity.netId
            : "player:local:" + player.GetInstanceID();
    }

    private static string PropName(Prop prop)
    {
        if (prop == null)
            return "unknown_prop";
        var value = prop.saveablePropName.ToString();
        if ((string.IsNullOrWhiteSpace(value) ||
             string.Equals(value, "notSavable", StringComparison.Ordinal)) &&
            prop.gameObject != null)
        {
            value = prop.gameObject.name;
        }
        if (string.IsNullOrWhiteSpace(value))
            return "unknown_prop";
        value = value.Replace("(Clone)", string.Empty).Trim();
        var cleaned = new char[Math.Min(value.Length, 48)];
        var next = 0;
        for (var index = 0; index < value.Length && next < cleaned.Length; index++)
        {
            var character = value[index];
            if (!char.IsControl(character))
                cleaned[next++] = character;
        }
        return next == 0
            ? "unknown_prop"
            : new string(cleaned, 0, next);
    }

    private static bool IsGrounded(PlayerCharacter character)
    {
        return character?.ground != null && character.ground.isGrounded;
    }

    private static int ResolveLayerMask(PlayerCharacter human)
    {
        return human != null && human.caster != null &&
               human.caster.layerMask.value != 0
            ? human.caster.layerMask.value
            : Physics.DefaultRaycastLayers;
    }

    private bool HasLineOfSight(
        Vector3 origin,
        Transform targetRoot,
        Vector3 targetPoint,
        int layerMask)
    {
        var delta = targetPoint - origin;
        var distance = delta.magnitude;
        if (distance < 0.05f)
            return true;
        var direction = delta / distance;
        var rayOrigin = origin + direction * VisibilityRayStartOffset;
        var remaining = Mathf.Max(0f, distance - VisibilityRayStartOffset);
        for (var step = 0; step < VisibilityRaySteps && remaining > 0f; step++)
        {
            RaycastHit hit;
            if (!Physics.Raycast(
                    rayOrigin,
                    direction,
                    out hit,
                    remaining + VisibilitySelfAdvance,
                    layerMask,
                    QueryTriggerInteraction.Ignore))
            {
                return true;
            }

            var transform = hit.collider == null ? null : hit.collider.transform;
            if (IsUnderRoot(transform, targetRoot))
                return true;
            if (_body != null && _body.Contains(transform))
            {
                var advance = Mathf.Max(
                    VisibilitySelfAdvance,
                    hit.distance + VisibilitySelfAdvance);
                rayOrigin += direction * advance;
                remaining -= advance;
                continue;
            }
            return false;
        }
        return false;
    }

    private static bool IsUnderRoot(Transform candidate, Transform root)
    {
        return candidate != null && root != null &&
               (candidate == root || candidate.IsChildOf(root));
    }

    private static string BearingLabel(Vector3 forward, Vector3 offset)
    {
        var flatForward = new Vector3(forward.x, 0f, forward.z);
        var flatOffset = new Vector3(offset.x, 0f, offset.z);
        if (flatForward.sqrMagnitude < 0.0001f ||
            flatOffset.sqrMagnitude < 0.0001f)
        {
            return "same_position";
        }

        flatForward.Normalize();
        flatOffset.Normalize();
        var angle = Mathf.Atan2(
            Vector3.Cross(flatForward, flatOffset).y,
            Vector3.Dot(flatForward, flatOffset)) * Mathf.Rad2Deg;
        var absolute = Mathf.Abs(angle);
        if (absolute <= 22.5f)
            return "front";
        if (absolute <= 67.5f)
            return angle > 0f ? "front_right" : "front_left";
        if (absolute <= 112.5f)
            return angle > 0f ? "right" : "left";
        if (absolute <= 157.5f)
            return angle > 0f ? "back_right" : "back_left";
        return "behind";
    }

    private static string PostureLabel(CompanionPosture posture)
    {
        return posture.ToString().ToLowerInvariant();
    }

    private static float Round1(float value)
    {
        return (float)Math.Round(value, 1, MidpointRounding.AwayFromZero);
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        return HorizontalMagnitude(left - right);
    }

    private static float HorizontalMagnitude(Vector3 value)
    {
        return new Vector2(value.x, value.z).magnitude;
    }

    private static Vector3 Midpoint(Vector3 left, Vector3 right)
    {
        return (left + right) * 0.5f;
    }
}
