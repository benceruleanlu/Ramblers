# Stripped Unity APIs

> The API survey referenced from [README.md](../README.md). It is specific to Big Walk `1.4.9` (build `2608141617`) rather than to Unity 6 in general.

Big Walk ships a managed-stripped IL2CPP build, and BepInEx generates its interop assemblies from the full Unity API surface. A Unity method the game itself never calls therefore still compiles against those assemblies and is simply absent at runtime, where Il2CppInterop resolves a null method pointer and the failure path corrupts memory. The process dies on an access violation before any `catch` runs — compiling proves nothing, and neither does a `try`/`catch`.

This is what killed `0.9.0`: a single line setting `Camera.stereoTargetEye`, whose setter is stripped while its getter survives. Three more were waiting behind it.

| API | Consequence if used |
| --- | --- |
| `Camera.stereoTargetEye` setter | the `0.9.0` crash |
| `RenderPipeline.SupportsRenderRequest` | the natural guard for the render call would crash identically |
| `ImageConversion.EncodeToJPG` / `EncodeToPNG` / `LoadImage` | capture would die at encode after a successful render |
| `Camera.CopyFrom` | silently does nothing, so the capture camera keeps Unity's defaults |

The capture therefore uses none of them: no stereo settings, an unguarded `RenderPipeline.SubmitRenderRequest` in place of `Camera.Render()` (a built-in-pipeline entry point, unsupported under URP), a managed PNG encoder over `System.IO.Compression`, and Unity's default camera configuration instead of `CopyFrom`.

`UnityApiProbe` asks the IL2CPP runtime whether each dependency exists. Only four are treated as required — `SubmitRenderRequest`, `Camera.set_targetTexture`, `Texture2D.ReadPixels`, and `GetRawTextureData` — so a missing optional API degrades the image and logs `[VISION] DEGRADED` rather than disabling the capability. Any future capability reaching for an uncommon Unity API should probe it the same way.

**Do not survey the API surface by string-searching `global-metadata.dat`.** IL2CPP stores method names unqualified, so a bare name cannot be attributed to a type — `CopyFrom` occurs 28 times there and none of the hits are `Camera`'s. Use `UnityApiProbe.DescribeType`, which enumerates a type's methods through the runtime; a non-zero method count also proves the type resolved, separating a real strip from a failed class lookup.
