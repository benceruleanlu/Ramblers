# StbImageWriteSharp

Ramblers vendors the `netstandard2.0` binary from StbImageWriteSharp `1.16.7`,
a managed C# port of `stb_image_write.h` that requires no native binaries.

- Project: https://github.com/StbSharp/StbImageWriteSharp
- Package: https://www.nuget.org/packages/StbImageWriteSharp/1.16.7
- License stated by the upstream project: Public Domain
- NuGet package SHA-512 (base64): `ERymvYRNx+i3ceUZCHUVRTiiWAeCkAeogfpGH5p+zx1LmvDPNkFZsbcqkNTZa2xSWWkldOSQn0RsQBXsvLDN4A==`
- Vendored DLL SHA-256: `70921000BEB9CA762A8ACDB93AC6F6C39DB8A351A6FA12ACA3EDDBC652855F04`

The package hash was verified against NuGet's catalog entry before extracting
`lib/netstandard2.0/StbImageWriteSharp.dll`. The binary is unmodified, and
`build.ps1` checks its SHA-256 before every build.
