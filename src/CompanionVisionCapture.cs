using System;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// A single bounded visual observation captured from the companion's eyes.
/// The managed JPEG bytes are safe to hand to the background WebSocket client.
/// </summary>
internal sealed class CompanionVisionObservation
{
    internal byte[] JpegBytes;
    internal int Width;
    internal int Height;
    internal Vector3 TargetPoint;
    internal bool ReferenceRayHit;
    internal bool AlignmentTimedOut;
}

/// <summary>
/// Renders one off-screen world frame. Unity camera and texture work remains on
/// the main thread; no image is written to disk.
/// </summary>
internal static class CompanionVisionCapture
{
    private const int CaptureWidth = 640;
    private const int CaptureHeight = 360;
    private const int JpegQuality = 82;
    private const float EyeForwardOffset = 0.06f;

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
            cameraObject = new GameObject("Ramblers Vision Camera");
            cameraObject.hideFlags = HideFlags.HideAndDontSave;
            captureCamera = cameraObject.AddComponent<Camera>();
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
            captureCamera.stereoTargetEye = StereoTargetEyeMask.None;
            captureCamera.allowDynamicResolution = false;
            captureCamera.nearClipPlane = Mathf.Max(0.05f, captureCamera.nearClipPlane);

            renderTexture = RenderTexture.GetTemporary(
                CaptureWidth,
                CaptureHeight,
                24,
                RenderTextureFormat.ARGB32);
            captureCamera.targetTexture = renderTexture;
            RenderWithoutCompanion(captureCamera, body);

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

            var encoded = ImageConversion.EncodeToJPG(texture, JpegQuality);
            if (encoded == null || encoded.Length == 0)
            {
                error = "image_encoding_failed";
                return false;
            }

            var jpeg = new byte[encoded.Length];
            for (var index = 0; index < encoded.Length; index++)
                jpeg[index] = encoded[index];

            observation = new CompanionVisionObservation
            {
                JpegBytes = jpeg,
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

    private static void RenderWithoutCompanion(Camera camera, CompanionBody body)
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

            camera.Render();
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
