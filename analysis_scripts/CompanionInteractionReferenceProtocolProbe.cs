using System;
using Ramblers;

internal static class CompanionInteractionReferenceProtocolProbe
{
    private static int Main()
    {
        AssertEqual(
            "interaction:net:17:castable:101",
            CompanionInteractionReferenceProtocol.BuildStableId(17u, 101),
            "network ID must retain the exact castable instance");
        AssertEqual(
            "interaction:net:17:castable:102",
            CompanionInteractionReferenceProtocol.BuildStableId(17u, 102),
            "two castables under one network root must remain distinct");
        AssertEqual(
            "interaction:local:44",
            CompanionInteractionReferenceProtocol.BuildStableId(0u, 44),
            "a local castable needs an explicitly scoped ID");

        AssertEqual(
            "Push Button",
            CompanionInteractionReferenceProtocol.NaturalName(
                "PushButton(Clone)",
                "button or switch"),
            "component-style names should read naturally");
        AssertEqual(
            "Bell switch",
            CompanionInteractionReferenceProtocol.NaturalName(
                "Bell_switch",
                "button or switch"),
            "underscores should become spaces");
        AssertEqual(
            "button or switch",
            CompanionInteractionReferenceProtocol.NaturalName(
                "\r\n",
                "button or switch"),
            "empty names should use a natural kind fallback");
        AssertEqual(
            "button or switch",
            CompanionInteractionReferenceProtocol.NaturalName(
                "_-\r\n",
                "button or switch"),
            "names emptied by sanitization should use the kind fallback");
        AssertEqual(
            "interactable object",
            CompanionInteractionReferenceProtocol.NaturalName(
                "_-\r\n",
                "_-\r\n"),
            "an empty sanitized fallback should still produce a natural name");
        AssertEqual(
            new string('a', 56),
            CompanionInteractionReferenceProtocol.NaturalName(
                new string('a', 57),
                "button or switch"),
            "natural names should truncate at exactly the public limit");
        AssertEqual(
            new string('a', 55),
            CompanionInteractionReferenceProtocol.NaturalName(
                new string('a', 55) + "B",
                "button or switch"),
            "camel-case splitting should not leave a partial boundary space");
        AssertEqual(
            "Courtyard Bell",
            CompanionInteractionReferenceProtocol.NaturalNameFromHierarchy(
                new[] { "CastableTarget", "CourtyardBell(Clone)" },
                "button or switch"),
            "a generic interaction child should inherit a meaningful bounded parent name");
        AssertEqual(
            "button or switch",
            CompanionInteractionReferenceProtocol.NaturalNameFromHierarchy(
                new[] { "Collider", "InteractionTrigger" },
                "button or switch"),
            "an all-plumbing hierarchy should preserve the native-kind fallback");
        AssertEqual(
            "Courtyard Bell",
            CompanionInteractionReferenceProtocol.NaturalNameFromHierarchy(
                new[] { "_-", "CourtyardBell(Clone)" },
                "button or switch"),
            "sanitized-empty child names should not hide a meaningful parent");

        AssertTrue(
            CompanionInteractionReferenceProtocol.MergeStructuralKind(
                CompanionInteractionStructuralKind.None,
                CompanionInteractionStructuralKind.PropHome) ==
            CompanionInteractionStructuralKind.PropHome,
            "the first raw structural kind should be retained");
        AssertTrue(
            CompanionInteractionReferenceProtocol.MergeStructuralKind(
                CompanionInteractionStructuralKind.PropHome,
                CompanionInteractionStructuralKind.PropHome) ==
            CompanionInteractionStructuralKind.PropHome,
            "repeated raw structural kinds should remain stable");
        AssertTrue(
            CompanionInteractionReferenceProtocol.MergeStructuralKind(
                CompanionInteractionStructuralKind.PropHome,
                CompanionInteractionStructuralKind.WorldSwitch) ==
            CompanionInteractionStructuralKind.Multiple,
            "different raw structural kinds should be described as one mixed target");
        AssertEqual(
            "interactable",
            CompanionInteractionReferenceProtocol.KindLabel(
                CompanionInteractionStructuralKind.Multiple),
            "mixed structural targets should have a natural model-facing kind");
        AssertEqual(
            "item holder",
            CompanionInteractionReferenceProtocol.DefaultNaturalName(
                CompanionInteractionStructuralKind.PropHome),
            "structural prop homes should have a natural fallback name");

        AssertTrue(
            CompanionInteractionReferenceProtocol.CompareRememberedForEviction(
                4f,
                "interaction:local:20",
                5f,
                "interaction:local:10") < 0,
            "older observations must be evicted first");
        AssertTrue(
            CompanionInteractionReferenceProtocol.CompareRememberedForEviction(
                5f,
                "interaction:local:10",
                5f,
                "interaction:local:20") < 0,
            "equal-age eviction must use stable-ID order");

        AssertTrue(
            CompanionInteractionReferenceProtocol.IsNearby(20f, 11.9f, 12f),
            "either actor may make an interactable nearby");
        AssertFalse(
            CompanionInteractionReferenceProtocol.IsNearby(12.1f, 12.1f, 12f),
            "outside both actor radii must not be nearby");
        AssertTrue(
            CompanionInteractionReferenceProtocol.IsRecent(45f, 30f, 45f, 30f),
            "recent bounds should be inclusive");
        AssertFalse(
            CompanionInteractionReferenceProtocol.IsRecent(45.1f, 5f, 45f, 30f),
            "expired observations must not survive");
        AssertFalse(
            CompanionInteractionReferenceProtocol.IsRecent(2f, 30.1f, 45f, 30f),
            "far observations must not remain actionable context");

        Console.WriteLine("Interaction-reference protocol probe passed.");
        return 0;
    }

    private static void AssertEqual(
        string expected,
        string actual,
        string description)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                description + ": expected '" + expected +
                "' but got '" + actual + "'.");
        }
    }

    private static void AssertTrue(bool value, string description)
    {
        if (!value)
            throw new InvalidOperationException(description);
    }

    private static void AssertFalse(bool value, string description)
    {
        if (value)
            throw new InvalidOperationException(description);
    }
}
