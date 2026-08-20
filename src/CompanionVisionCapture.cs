using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ramblers;

/// <summary>
/// A single bounded visual observation captured from the companion's eyes.
/// The managed image bytes are safe to hand to the background WebSocket client.
/// </summary>
internal sealed class CompanionVisionObservation
{
    internal byte[] ImageBytes;
    internal string MediaType;
    internal int Width;
    internal int Height;
    internal int EncodingQuality;
    internal bool FieldOfViewMatched;
    internal float SourceFieldOfView;
    internal float CaptureFieldOfView;
    internal Vector3 TargetPoint;
    internal bool ReferenceRayHit;
    internal bool AlignmentTimedOut;
}

/// <summary>
/// Renders one off-screen world frame. Unity camera and texture work remains on
/// the main thread; no image is written to disk.
///
/// Three APIs the obvious implementation would reach for are stripped from Big
/// Walk's IL2CPP build and would kill the process rather than throw:
/// <c>Camera.stereoTargetEye</c>'s setter, <c>RenderPipeline.SupportsRenderRequest</c>,
/// and every <c>ImageConversion</c> encoder. None of them are used here, and
/// what remains is probed before first use.
/// </summary>
internal static class CompanionVisionCapture
{
    private const int CaptureWidth = 640;
    private const int CaptureHeight = 360;
    private const float EyeForwardOffset = 0.06f;
    // Far enough forward of the eye to exclude the companion's own head, close
    // enough to keep a held or nearby object in frame.
    private const float NearClipPlane = 0.05f;

    private static bool _probed;
    private static bool _captureSupported;
    private static bool _canCopyFrom;
    private static bool _canCopyFieldOfView;
    private static bool _canSetAspect;
    private static bool _canSetNearClip;
    private static bool _canHideRenderers;

    internal static bool TryCapture(
        CompanionBody body,
        PlayerCharacter human,
        Vector3 targetPoint,
        Vector3 viewDirection,
        bool referenceRayHit,
        bool alignmentTimedOut,
        out CompanionVisionObservation observation,
        out string error)
    {
        observation = null;
        error = null;

        if (!IsCaptureSupported())
        {
            error = "vision_api_unavailable";
            return false;
        }

        if (body == null || !body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }

        var sourceCamera = ResolveSourceCamera(human);
        if (sourceCamera == null)
        {
            error = "world_camera_unavailable";
            return false;
        }

        var eyePosition = body.HeadPosition;
        var forward = viewDirection;
        if (forward.sqrMagnitude < 0.0001f)
            forward = targetPoint - eyePosition;
        if (forward.sqrMagnitude < 0.0001f)
        {
            error = "reference_direction_unavailable";
            return false;
        }

        forward.Normalize();
        var cameraObject = default(GameObject);
        var captureCamera = default(Camera);
        var renderTexture = default(RenderTexture);
        var texture = default(Texture2D);
        var fieldOfViewMatched = false;
        var sourceFieldOfView = -1f;
        var captureFieldOfView = -1f;
        var previousActive = RenderTexture.active;

        try
        {
            // A dedicated off-screen camera, rather than borrowing the player's.
            // Big Walk runs URP with post-processing and HBAO, so rendering the
            // live camera from a second pose would pollute the temporal history
            // and exposure state of the player's own view, and it carries the
            // AudioListener.
            cameraObject = new GameObject("Ramblers Vision Camera");
            cameraObject.hideFlags = HideFlags.HideAndDontSave;
            captureCamera = cameraObject.AddComponent<Camera>();
            // CopyFrom is optional and stripped from the tested game build.
            // Field of view has its own independently probed fallback so one
            // missing bulk-copy API does not force the capture to 60 degrees.
            if (_canCopyFrom)
                captureCamera.CopyFrom(sourceCamera);
            if (_canCopyFieldOfView)
            {
                sourceFieldOfView = sourceCamera.fieldOfView;
                if (sourceFieldOfView > 0f && sourceFieldOfView < 180f)
                {
                    captureCamera.fieldOfView = sourceFieldOfView;
                    captureFieldOfView = captureCamera.fieldOfView;
                    fieldOfViewMatched = Mathf.Abs(
                        captureFieldOfView - sourceFieldOfView) <= 0.01f;
                }
            }
            captureCamera.enabled = false;
            captureCamera.targetTexture = null;
            captureCamera.transform.position = eyePosition + forward * EyeForwardOffset;
            var up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.98f
                ? Vector3.forward
                : Vector3.up;
            captureCamera.transform.rotation = Quaternion.LookRotation(forward, up);
            if (_canSetAspect)
                captureCamera.aspect = CaptureWidth / (float)CaptureHeight;
            if (_canSetNearClip)
                captureCamera.nearClipPlane = NearClipPlane;

            renderTexture = RenderTexture.GetTemporary(
                CaptureWidth,
                CaptureHeight,
                24,
                RenderTextureFormat.ARGB32);
            captureCamera.targetTexture = renderTexture;
            RenderWithoutCompanion(captureCamera, renderTexture, body);

            RenderTexture.active = renderTexture;
            texture = new Texture2D(
                CaptureWidth,
                CaptureHeight,
                TextureFormat.RGB24,
                false);
            texture.ReadPixels(
                new Rect(0f, 0f, CaptureWidth, CaptureHeight),
                0,
                0,
                false);
            texture.Apply(false, false);

            var raw = texture.GetRawTextureData();
            var expected = CaptureWidth * CaptureHeight * 3;
            if (raw == null || raw.Length < expected)
            {
                error = "image_readback_failed";
                return false;
            }

            var rgb = new byte[expected];
            for (var index = 0; index < expected; index++)
                rgb[index] = raw[index];

            // Unity reads back with the first row at the bottom of the image.
            var encoded = JpegEncoder.EncodeRgb24(
                rgb,
                CaptureWidth,
                CaptureHeight,
                true);
            if (encoded == null || encoded.Length == 0)
            {
                error = "image_encoding_failed";
                return false;
            }

            observation = new CompanionVisionObservation
            {
                ImageBytes = encoded,
                MediaType = JpegEncoder.MediaType,
                Width = CaptureWidth,
                Height = CaptureHeight,
                EncodingQuality = JpegEncoder.DefaultQuality,
                FieldOfViewMatched = fieldOfViewMatched,
                SourceFieldOfView = sourceFieldOfView,
                CaptureFieldOfView = captureFieldOfView,
                TargetPoint = targetPoint,
                ReferenceRayHit = referenceRayHit,
                AlignmentTimedOut = alignmentTimedOut
            };
            return true;
        }
        catch (Exception exception)
        {
            error = "image_capture_failed";
            Plugin.Logger.LogError($"[VISION] Capture failed: {exception}");
            return false;
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (captureCamera != null)
                captureCamera.targetTexture = null;
            if (renderTexture != null)
                RenderTexture.ReleaseTemporary(renderTexture);
            if (texture != null)
                UnityEngine.Object.Destroy(texture);
            if (cameraObject != null)
                UnityEngine.Object.Destroy(cameraObject);
        }
    }

    /// <summary>
    /// Verifies the Unity entry points this capture depends on before first use.
    ///
    /// Only four are genuinely required. The rest improve the capture but have
    /// usable defaults, so a stripped one degrades the image instead of
    /// disabling the whole capability. Every probe runs — none short-circuit —
    /// so one run reports the complete picture rather than the first failure.
    /// </summary>
    private static bool IsCaptureSupported()
    {
        if (_probed)
            return _captureSupported;
        _probed = true;

        // Ground truth for anything the probes report as missing: a non-zero
        // method count proves the type resolved, so a negative result is a real
        // strip rather than a failed class lookup.
        UnityApiProbe.DescribeType(
            UnityApiProbe.CoreModule,
            "UnityEngine",
            "Camera",
            new[]
            {
                "CopyFrom",
                "fieldOfView",
                "targetTexture",
                "aspect",
                "ClipPlane",
                "stereo"
            });

        var canSubmitRender = Probe("UnityEngine.Rendering", "RenderPipeline", "SubmitRenderRequest", 2);
        var canSetTargetTexture = Probe("UnityEngine", "Camera", "set_targetTexture", 1);
        var canReadPixels = Probe("UnityEngine", "Texture2D", "ReadPixels", 4);
        var canReadRaw = Probe("UnityEngine", "Texture2D", "GetRawTextureData", 0);

        _canCopyFrom = Probe("UnityEngine", "Camera", "CopyFrom", 1);
        var canGetFieldOfView = Probe(
            "UnityEngine",
            "Camera",
            "get_fieldOfView",
            0);
        var canSetFieldOfView = Probe(
            "UnityEngine",
            "Camera",
            "set_fieldOfView",
            1);
        _canCopyFieldOfView = canGetFieldOfView && canSetFieldOfView;
        _canSetAspect = Probe("UnityEngine", "Camera", "set_aspect", 1);
        _canSetNearClip = Probe("UnityEngine", "Camera", "set_nearClipPlane", 1);
        _canHideRenderers = Probe("UnityEngine", "Renderer", "set_forceRenderingOff", 1);

        _captureSupported = canSubmitRender && canSetTargetTexture &&
                            canReadPixels && canReadRaw;
        if (!_captureSupported)
        {
            Plugin.Logger.LogWarning(
                "[VISION] DISABLED reason=a required Unity render API is absent " +
                "from this build; inspect_reference will report failure instead " +
                "of capturing.");
        }
        else if (!_canCopyFrom || !_canHideRenderers)
        {
            Plugin.Logger.LogWarning(
                $"[VISION] DEGRADED copyFrom={_canCopyFrom}, " +
                $"fieldOfViewParity={_canCopyFieldOfView}, " +
                $"hideCompanion={_canHideRenderers}; using individually " +
                "guarded fallbacks where available and Unity defaults otherwise.");
        }

        return _captureSupported;
    }

    private static bool Probe(
        string namespaceName,
        string typeName,
        string methodName,
        int argumentCount)
    {
        return UnityApiProbe.IsMethodPresent(
            UnityApiProbe.CoreModule,
            namespaceName,
            typeName,
            methodName,
            argumentCount);
    }

    private static Camera ResolveSourceCamera(PlayerCharacter human)
    {
        if (human != null && human.cameraMinder != null)
        {
            var references = human.cameraMinder.playerCameraReferences;
            if (references != null && references.playerCamera != null)
                return references.playerCamera;
        }

        return Camera.main;
    }

    private static void RenderWithoutCompanion(
        Camera camera,
        RenderTexture destination,
        CompanionBody body)
    {
        // Without forceRenderingOff the companion may appear in its own frame.
        // The eye offset and near plane hide most of it, so this degrades the
        // image rather than invalidating it.
        var renderers = _canHideRenderers
            ? body.GameObject.GetComponentsInChildren<Renderer>(true)
            : null;
        var count = renderers == null ? 0 : renderers.Length;
        var previousStates = new bool[count];
        try
        {
            for (var index = 0; index < count; index++)
            {
                var renderer = renderers[index];
                if (renderer == null)
                    continue;
                previousStates[index] = renderer.forceRenderingOff;
                renderer.forceRenderingOff = true;
            }

            // Camera.Render is a built-in-pipeline entry point and is not
            // supported under URP. SupportsRenderRequest would be the natural
            // guard, but it is stripped from this build, so the request is
            // simply submitted and any failure surfaces as a capture error.
            var request = new RenderPipeline.StandardRequest
            {
                destination = destination
            };
            RenderPipeline.SubmitRenderRequest(camera, request);
        }
        finally
        {
            for (var index = 0; index < count; index++)
            {
                var renderer = renderers[index];
                if (renderer != null)
                    renderer.forceRenderingOff = previousStates[index];
            }
        }
    }
}
