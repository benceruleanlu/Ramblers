using System;
using System.Collections.Generic;
using System.Text.Json;
using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// One nonverbal context item captured for a human utterance. Text is always
/// present; a recent passive view is attached at most once.
/// </summary>
internal sealed class CompanionAwarenessTurnContext
{
    internal AgentContinuationItem Message;
    internal int EventCount;
    internal int NearbyPropCount;
    internal int NearbyPlayerCount;
    internal float VisualAgeSeconds = -1f;

    internal bool HasImage =>
        Message?.ImageBytes != null && Message.ImageBytes.Length > 0;
}

/// <summary>
/// A natural ambient glance that has visibly settled and can therefore become
/// passive visual memory without inventing a second, invisible gaze.
/// </summary>
internal sealed class CompanionAmbientObservationCandidate
{
    internal Vector3 TargetPoint;
    internal Vector3 ViewDirection;
    internal bool Anchored;
}

/// <summary>
/// Bounded embodied awareness for the Realtime model. Deterministic C# owns
/// state collection, identity, event detection, visual cadence and freshness;
/// the model receives only a compact nonverbal report and decides what matters
/// conversationally.
/// </summary>
internal sealed class CompanionAwareness
{
    private const int MaximumJournalEntries = 8;
    private const float JournalLifetimeSeconds = 120f;
    private const int MaximumNearbyProps = 6;
    private const int MaximumNearbyPlayers = 3;
    private const float NearbyPropRadius = 10f;
    private const float NearbyPlayerRadius = 15f;

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

    private struct HeldObservation
    {
        internal int Key;
        internal string Id;
        internal string Kind;
        internal string Name;
    }

    private readonly Queue<JournalEntry> _journal = new Queue<JournalEntry>();
    private readonly LogLatch _passiveFailureLog = new LogLatch();
    private readonly LogLatch _tickFailureLog = new LogLatch();

    private CompanionBody _body;
    private PlayerCharacter _humanAtSpawn;
    private CompanionActionCoordinator _actions;

    private long _nextEventSequence;
    private long _lastDeliveredEventSequence;
    private HeldObservation _humanHeld;
    private HeldObservation _companionHeld;
    private bool _companionCarried;
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
                    : "the human released the companion");
            _companionCarried = carried;
        }

        var followRequested = _actions?.FollowRequested == true;
        if (followRequested != _followRequested)
        {
            RecordEvent(
                now,
                followRequested
                    ? "the companion started following the human"
                    : "the companion stopped following and is staying put");
            _followRequested = followRequested;
        }

        var posture = _actions == null
            ? CompanionPosture.Standing
            : _actions.Posture;
        if (posture != _posture)
        {
            RecordEvent(
                now,
                "the companion changed posture to " + PostureLabel(posture));
            _posture = posture;
        }

        var activeAction = _actions?.ActiveJobName;
        if (!string.Equals(activeAction, _activeAction, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(activeAction))
                RecordEvent(now, "the companion started action " + activeAction);
            else if (!string.IsNullOrEmpty(_activeAction))
                RecordEvent(now, "the companion's " + _activeAction + " action ended");
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
                $"the human and companion became separated by {Round1(separation):F1}m");
            _separated = true;
        }
        else if (_separated && separation <= ReunionDistance)
        {
            RecordEvent(now, "the human and companion came back together");
            _separated = false;
        }

        var midpoint = Midpoint(bodyPosition, humanPosition);
        var areaDistance = HorizontalDistance(midpoint, _areaAnchor);
        if (areaDistance >= AreaTransitionDistance &&
            now - _lastAreaTransitionAt >= AreaTransitionCooldownSeconds)
        {
            RecordEvent(
                now,
                $"the walk progressed about {Round1(areaDistance):F1}m into a different area");
            _areaAnchor = midpoint;
            _lastAreaTransitionAt = now;
        }

        RemoveExpiredEvents(now);
    }

    /// <summary>
    /// Captures at most one frame for a settled natural glance. The interval,
    /// movement/direction/event novelty gates and one-shot delivery keep this a
    /// rolling memory rather than a growing ambient video stream.
    /// </summary>
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
        var nearbyProps = CaptureNearbyProps(human);
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

        var companionPosition = _body.Position;
        var humanPosition = human.transform.position;
        var humanOffset = humanPosition - companionPosition;
        var visualStatus = attachVisual
            ? "attached_recent_ambient_view"
            : "no_new_visual_frame";
        var payload = new
        {
            schema = "ramblers.game_context.v1",
            captured_at = "human_utterance_boundary",
            companion = new
            {
                follow_mode = _actions?.FollowRequested == true ? "follow" : "stay",
                follow_state = _actions?.FollowStateLabel ?? "unavailable",
                posture = PostureLabel(
                    _actions == null ? CompanionPosture.Standing : _actions.Posture),
                moving = _actions?.IsMoving == true,
                grounded = IsGrounded(_body.Character),
                carried_by_human = _actions?.IsCarried == true,
                active_action = _actions?.ActiveJobName ?? "none",
                jump_queued = _actions?.JumpQueued == true,
                held_item = ToPayload(CaptureHeld(_body.Character))
            },
            human = new
            {
                distance_from_companion_m = Round1(HorizontalMagnitude(humanOffset)),
                height_from_companion_m = Round1(humanOffset.y),
                bearing_from_companion = BearingLabel(
                    _body.Transform.forward,
                    humanOffset),
                visible_from_companion = HasLineOfSight(
                    _body.HeadPosition,
                    human.transform,
                    CompanionBody.HeadPositionOf(human),
                    ResolveLayerMask(human)),
                grounded = IsGrounded(human),
                held_item = ToPayload(CaptureHeld(human))
            },
            nearby_props = nearbyProps,
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
            VisualAgeSeconds = attachVisual ? visualAge : -1f
        };

        _lastDeliveredEventSequence = _nextEventSequence;
        if (attachVisual)
            _passiveDelivered = true;
        return true;
    }

    internal void Release()
    {
        _body = null;
        _humanAtSpawn = null;
        _actions = null;
        _journal.Clear();
        _nextEventSequence = 0;
        _lastDeliveredEventSequence = 0;
        _humanHeld = default;
        _companionHeld = default;
        _companionCarried = false;
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
        _passiveFailureLog.Reset();
        _tickFailureLog.Reset();
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

        if (previous.Key != 0 && current.Key == 0)
        {
            RecordEvent(
                now,
                actor + " released " + DescribeHeld(previous));
        }
        else if (current.Key != 0 && previous.Key == 0)
        {
            RecordEvent(
                now,
                actor + " picked up " + DescribeHeld(current));
        }
        else if (current.Key != 0)
        {
            RecordEvent(
                now,
                actor + " switched from " + DescribeHeld(previous) +
                " to " + DescribeHeld(current));
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
                    $"after {Round1(seconds):F1}s airborne");
            }
        }

        wasGrounded = grounded;
    }

    private void RecordEvent(float now, string description)
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
        Plugin.Logger.LogInfo(
            $"[AWARENESS] EVENT sequence={_nextEventSequence}, description={description}.");
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

    private NearbyPropPayload[] CaptureNearbyProps(PlayerCharacter human)
    {
        var result = new List<NearbyPropPayload>();
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
            left.distance_from_companion_m.CompareTo(
                right.distance_from_companion_m));
        if (result.Count > MaximumNearbyProps)
            result.RemoveRange(MaximumNearbyProps, result.Count - MaximumNearbyProps);
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
        if (prop == null)
            return "prop:unavailable";
        var identity = prop.GetComponentInParent<NetworkIdentity>();
        return identity != null && identity.netId != 0u
            ? "prop:net:" + identity.netId
            : "prop:local:" + prop.GetInstanceID();
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
