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

    private static bool? _apiAvailable;

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
            // CopyFrom each capture keeps the companion's view in step with the
            // player's current field of view, clip planes, and culling mask.
            captureCamera.CopyFrom(sourceCamera);
            captureCamera.enabled = false;
            captureCamera.targetTexture = null;
            captureCamera.transform.position = eyePosition + forward * EyeForwardOffset;
            var up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.98f
                ? Vector3.forward
                : Vector3.up;
            captureCamera.transform.rotation = Quaternion.LookRotation(forward, up);
            captureCamera.aspect = CaptureWidth / (float)CaptureHeight;
            captureCamera.rect = new Rect(0f, 0f, 1f, 1f);
            captureCamera.nearClipPlane = Mathf.Max(0.05f, captureCamera.nearClipPlane);

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
            var encoded = PngEncoder.EncodeRgb24(
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
                MediaType = PngEncoder.MediaType,
                Width = CaptureWidth,
                Height = CaptureHeight,
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
    /// Verifies the Unity entry points this capture depends on before the first
    /// use. Every one of them is reachable in Big Walk's build, but that is a
    /// property of the shipped game rather than of the Unity version, so it is
    /// checked rather than assumed.
    /// </summary>
    private static bool IsCaptureSupported()
    {
        if (_apiAvailable.HasValue)
            return _apiAvailable.Value;

        var available =
            UnityApiProbe.IsMethodPresent(
                UnityApiProbe.CoreModule,
                "UnityEngine.Rendering",
                "RenderPipeline",
                "SubmitRenderRequest",
                2) &&
            UnityApiProbe.IsMethodPresent(
                UnityApiProbe.CoreModule,
                "UnityEngine",
                "Camera",
                "CopyFrom",
                1) &&
            UnityApiProbe.IsMethodPresent(
                UnityApiProbe.CoreModule,
                "UnityEngine",
                "Texture2D",
                "ReadPixels",
                4) &&
            UnityApiProbe.IsMethodPresent(
                UnityApiProbe.CoreModule,
                "UnityEngine",
                "Texture2D",
                "GetRawTextureData",
                0) &&
            UnityApiProbe.IsMethodPresent(
                UnityApiProbe.CoreModule,
                "UnityEngine",
                "Renderer",
                "set_forceRenderingOff",
                1);

        _apiAvailable = available;
        if (!available)
        {
            Plugin.Logger.LogWarning(
                "[VISION] DISABLED reason=required Unity render APIs are absent " +
                "from this build; inspect_reference will report failure instead " +
                "of capturing.");
        }

        return available;
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
        var renderers = body.GameObject.GetComponentsInChildren<Renderer>(true);
        var previousStates = new bool[renderers.Length];
        try
        {
            for (var index = 0; index < renderers.Length; index++)
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
            for (var index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];
                if (renderer != null)
                    renderer.forceRenderingOff = previousStates[index];
            }
        }
    }
}
