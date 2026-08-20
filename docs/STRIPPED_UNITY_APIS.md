# Stripped Unity APIs

> The API survey referenced from [README.md](../README.md). It is specific to Big Walk `1.4.9` (build `2608141617`) rather than to Unity 6 in general.

Big Walk ships a managed-stripped IL2CPP build, and BepInEx generates its interop assemblies from the full Unity API surface. A Unity method the game itself never calls therefore still compiles against those assemblies and is simply absent at runtime, where Il2CppInterop resolves a null method pointer and the failure path corrupts memory. The process dies on an access violation before any `catch` runs — compiling proves nothing, and neither does a `try`/`catch`.

This is what killed `0.9.0`: a single line setting `Camera.stereoTargetEye`, whose setter is stripped while its getter survives. Three more were waiting behind it.

| API | Consequence if used |
| --- | --- |
| `Camera.stereoTargetEye` setter | the `0.9.0` crash |
| `RenderPipeline.SupportsRenderRequest` | the natural guard for the render call would crash identically |
| `ImageConversion.EncodeToJPG` / `EncodeToPNG` / `LoadImage` | capture would die at encode after a successful render |
| `Camera.CopyFrom` | silently does nothing, so each needed camera property requires its own guarded fallback |

The capture therefore uses none of them: no stereo settings, an unguarded `RenderPipeline.SubmitRenderRequest` in place of `Camera.Render()` (a built-in-pipeline entry point, unsupported under URP), and a pinned managed JPEG encoder that requires no native binaries. When `CopyFrom` is unavailable, the capture separately probes both `Camera.get_fieldOfView` and `Camera.set_fieldOfView` before matching the player's framing; other unconfigured camera properties retain Unity's defaults.

The bot-eye transport stays at `640x360`, matching the game's widescreen framing without paying to send pixels that this scene-description tool does not need. OpenAI does not prescribe one Realtime capture resolution; its [image guidance](https://developers.openai.com/api/docs/guides/images-vision#image-input-requirements) instead requires a human-readable input and defines processing through `detail`. Ramblers explicitly sends `detail: "high"`, encodes at JPEG quality 82, and logs both encoded and base64 byte counts. The [Realtime item schema](https://platform.openai.com/docs/api-reference/realtime-client-events) accepts JPEG and PNG; JPEG XL is not in its image-input contract.

`UnityApiProbe` asks the IL2CPP runtime whether each dependency exists. Only four are treated as required — `SubmitRenderRequest`, `Camera.set_targetTexture`, `Texture2D.ReadPixels`, and `GetRawTextureData` — so a missing optional API degrades the image and logs `[VISION] DEGRADED` rather than disabling the capability. Any future capability reaching for an uncommon Unity API should probe it the same way.

**Do not survey the API surface by string-searching `global-metadata.dat`.** IL2CPP stores method names unqualified, so a bare name cannot be attributed to a type — `CopyFrom` occurs 28 times there and none of the hits are `Camera`'s. Use `UnityApiProbe.DescribeType`, which enumerates a type's methods through the runtime; a non-zero method count also proves the type resolved, separating a real strip from a failed class lookup.
