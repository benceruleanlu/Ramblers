using System;

namespace Mirror
{
    internal static class NetworkServer
    {
        internal static bool active;
    }
}

namespace UnityEngine
{
    internal class GameObject
    {
        internal bool activeInHierarchy;
    }

    internal class Behaviour
    {
        internal bool enabled;
        internal GameObject gameObject;
    }

    internal class Transform
    {
        internal Vector3 forward;
    }

    internal struct Vector3
    {
        internal float x;
        internal float y;
        internal float z;

        internal static Vector3 zero => new Vector3();

        internal float magnitude =>
            (float)Math.Sqrt(x * x + y * y + z * z);

        public static Vector3 operator -(Vector3 left, Vector3 right)
        {
            return new Vector3
            {
                x = left.x - right.x,
                y = left.y - right.y,
                z = left.z - right.z
            };
        }

        public static Vector3 operator /(Vector3 value, float divisor)
        {
            return new Vector3
            {
                x = value.x / divisor,
                y = value.y / divisor,
                z = value.z / divisor
            };
        }
    }
}

namespace Ramblers
{
    internal enum CompanionAffordanceKind
    {
        WorldSwitch,
        HeldItemSwitch,
        PlayerPose,
        PropHome
    }

    internal enum CompanionAffordanceSource
    {
        HumanReference
    }

    internal sealed class Prop
    {
    }

    internal sealed class PeckContext
    {
    }

    internal sealed class TrackedPeckState
    {
    }

    internal struct ShellReference
    {
        internal bool isEmpty;
        internal ushort ticket;
        internal uint netId;
        internal int index;

        internal PeckSwitch GetPeckSwitch()
        {
            return null;
        }
    }

    internal sealed class PeckSwitch : UnityEngine.Behaviour
    {
        internal TrackedPeckState trackedStateSystem;
        internal ShellReference shellReference;

        internal int GetInstanceID()
        {
            return 1;
        }
    }

    internal sealed class PlayerCaster
    {
        internal float GetMaxDistanceForDirection(UnityEngine.Vector3 direction)
        {
            return 1f;
        }
    }

    internal sealed class PlayerHands
    {
        internal PlayerCharacter heldCharacter;
    }

    internal sealed class PlayerRegistry
    {
        internal PlayerPose grabPose;
    }

    internal sealed class PlayerPose
    {
        internal PlayerCharacter occupant;
    }

    internal sealed class PlayerPoser
    {
        internal PlayerCharacter playerHoldingMe;
        internal PlayerPose currentPose;
    }

    internal sealed class PlayerCharacter
    {
        internal UnityEngine.GameObject gameObject;
        internal PlayerCaster caster;
        internal PlayerHands hands;
        internal PlayerRegistry registry;
        internal PlayerPoser poser;

        internal int GetInstanceID()
        {
            return 1;
        }
    }

    internal sealed class CompanionNetworking
    {
        internal bool isServer;
        internal bool isLocalPlayer;
    }

    internal sealed class CompanionBody
    {
        internal bool IsAlive;
        internal PlayerCharacter Character;
        internal CompanionNetworking Networking;
        internal UnityEngine.GameObject GameObject;
        internal UnityEngine.Transform Transform;
        internal UnityEngine.Vector3 HeadPosition;
    }

    internal static class CompanionAffordanceActivationProbe
    {
        private static int Main()
        {
            Assert(
                CompanionSwitchAffordanceActivation
                    .CanDispatchAfterPrerequisite(false, false, false),
                "a switch without a native prerequisite dispatches directly");
            Assert(
                !CompanionSwitchAffordanceActivation
                    .CanDispatchAfterPrerequisite(true, false, false),
                "an undispatched native prerequisite blocks the main switch");
            Assert(
                !CompanionSwitchAffordanceActivation
                    .CanDispatchAfterPrerequisite(true, true, false),
                "a dispatched but unobserved prerequisite blocks the main switch");
            Assert(
                !CompanionSwitchAffordanceActivation
                    .CanDispatchAfterPrerequisite(true, false, true),
                "an observation without the frozen dispatch cannot authorize the main switch");
            Assert(
                CompanionSwitchAffordanceActivation
                    .CanDispatchAfterPrerequisite(true, true, true),
                "the exact observed prerequisite authorizes the main switch");

            var activation = new CompanionSwitchAffordanceActivation(
                CompanionAffordanceKind.WorldSwitch);
            Assert(!activation.AuthorityCrossed, "new activation starts pre-authority");
            activation.MarkAuthorityCrossed();
            Assert(activation.AuthorityCrossed, "authority boundary is explicit and sticky");

            Console.WriteLine("Companion affordance activation probe passed.");
            return 0;
        }

        private static void Assert(bool condition, string description)
        {
            if (!condition)
                throw new InvalidOperationException("Failed: " + description);
        }
    }
}
