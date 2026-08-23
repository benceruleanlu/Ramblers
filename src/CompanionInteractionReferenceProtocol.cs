using System;
using System.Collections.Generic;
using System.Text;

namespace Ramblers;

internal enum CompanionInteractionStructuralKind
{
    None,
    WorldSwitch,
    PlayerPose,
    PropHome,
    Multiple
}

/// <summary>
/// Pure policy helpers for model-visible interaction references. Keeping ID,
/// naming, radius, and expiry rules free of Unity makes their boundary
/// executable without loading the game.
/// </summary>
internal static class CompanionInteractionReferenceProtocol
{
    internal const int MaximumNaturalNameLength = 56;

    internal static CompanionInteractionStructuralKind MergeStructuralKind(
        CompanionInteractionStructuralKind current,
        CompanionInteractionStructuralKind candidate)
    {
        if (candidate == CompanionInteractionStructuralKind.None)
            return current;
        if (current == CompanionInteractionStructuralKind.None ||
            current == candidate)
        {
            return candidate;
        }
        return CompanionInteractionStructuralKind.Multiple;
    }

    internal static string KindLabel(
        CompanionInteractionStructuralKind kind)
    {
        switch (kind)
        {
            case CompanionInteractionStructuralKind.WorldSwitch:
                return "world_switch";
            case CompanionInteractionStructuralKind.PlayerPose:
                return "player_pose";
            case CompanionInteractionStructuralKind.PropHome:
                return "prop_home";
            case CompanionInteractionStructuralKind.Multiple:
                return "interactable";
            default:
                return "none";
        }
    }

    internal static string DefaultNaturalName(
        CompanionInteractionStructuralKind kind)
    {
        switch (kind)
        {
            case CompanionInteractionStructuralKind.WorldSwitch:
                return "button or switch";
            case CompanionInteractionStructuralKind.PlayerPose:
                return "seat or pose";
            case CompanionInteractionStructuralKind.PropHome:
                return "item holder";
            default:
                return "interactable object";
        }
    }

    internal static int CompareRememberedForEviction(
        float leftSeenAt,
        string leftStableId,
        float rightSeenAt,
        string rightStableId)
    {
        var timeOrder = leftSeenAt.CompareTo(rightSeenAt);
        return timeOrder != 0
            ? timeOrder
            : string.CompareOrdinal(leftStableId, rightStableId);
    }

    internal static string BuildStableId(uint networkId, int castableInstanceId)
    {
        return networkId == 0u
            ? "interaction:local:" + castableInstanceId
            : "interaction:net:" + networkId +
              ":castable:" + castableInstanceId;
    }

    internal static bool IsNearby(
        float companionDistance,
        float humanDistance,
        float radius)
    {
        return radius >= 0f &&
               (companionDistance <= radius || humanDistance <= radius);
    }

    internal static bool IsRecent(
        float ageSeconds,
        float currentDistance,
        float lifetimeSeconds,
        float maximumDistance)
    {
        return ageSeconds >= 0f && ageSeconds <= lifetimeSeconds &&
               currentDistance >= 0f && currentDistance <= maximumDistance;
    }

    internal static string NaturalName(string rawName, string fallback)
    {
        var naturalName = SanitizeNaturalName(rawName);
        if (!string.IsNullOrEmpty(naturalName))
            return naturalName;
        naturalName = SanitizeNaturalName(fallback);
        return string.IsNullOrEmpty(naturalName)
            ? "interactable object"
            : naturalName;
    }

    private static string SanitizeNaturalName(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        source = source.Replace("(Clone)", string.Empty);
        var result = new StringBuilder(
            Math.Min(source.Length + 8, MaximumNaturalNameLength));
        var previousWasSpace = true;
        var previousWasLowerOrDigit = false;
        for (var index = 0;
             index < source.Length && result.Length < MaximumNaturalNameLength;
             index++)
        {
            var character = source[index];
            if (char.IsControl(character))
                continue;

            var separator = char.IsWhiteSpace(character) ||
                            character == '_' || character == '-';
            if (separator)
            {
                if (!previousWasSpace && result.Length > 0)
                {
                    result.Append(' ');
                    previousWasSpace = true;
                }
                previousWasLowerOrDigit = false;
                continue;
            }

            if (char.IsUpper(character) && previousWasLowerOrDigit &&
                !previousWasSpace)
            {
                if (result.Length >= MaximumNaturalNameLength - 1)
                    break;
                result.Append(' ');
            }
            if (result.Length >= MaximumNaturalNameLength)
                break;
            result.Append(character);
            previousWasSpace = false;
            previousWasLowerOrDigit = char.IsLower(character) ||
                                      char.IsDigit(character);
        }

        var naturalName = result.ToString().Trim();
        return string.IsNullOrEmpty(naturalName) ? null : naturalName;
    }

    internal static string NaturalNameFromHierarchy(
        IList<string> namesFromTargetToParent,
        string fallback)
    {
        if (namesFromTargetToParent != null)
        {
            for (var index = 0; index < namesFromTargetToParent.Count; index++)
            {
                var rawName = namesFromTargetToParent[index];
                if (string.IsNullOrWhiteSpace(rawName))
                    continue;
                var candidate = SanitizeNaturalName(rawName);
                if (string.IsNullOrEmpty(candidate))
                    continue;
                if (!IsGenericPlumbingName(candidate))
                    return candidate;
            }
        }
        return NaturalName(null, fallback);
    }

    internal static bool IsGenericPlumbingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;
        var compact = name.Replace(" ", string.Empty)
            .ToLowerInvariant();
        switch (compact)
        {
            case "castabletarget":
            case "collider":
            case "boxcollider":
            case "spherecollider":
            case "capsulecollider":
            case "meshcollider":
            case "trigger":
            case "interactiontrigger":
            case "interactable":
            case "interaction":
            case "gameobject":
                return true;
            default:
                return false;
        }
    }
}
