# Third-party components (shipped next to HallowEmail.exe)

All from NuGet (nuget.org), the .NET Framework builds (`lib/net48` for MailKit and MimeKit.Core, `lib/net462` / `lib/net47` for the
others). Every one is under a permissive licence; the texts are in this folder.

| Package | Version | DLL | Licence | nupkg SHA-256 |
|---|---|---|---|---|
| MailKit (Jeffrey Stedfast / .NET Foundation) | 5.0.0 | MailKit.dll | MIT | 1807e35af3a3ebe25e28a26c67303fb8d381d8fc9e2c5e9c3eef67db45f1cf99 |
| MimeKit.Core (the core of MimeKit 5 - MailKit 5 needs only this) | 5.0.0 | MimeKit.Core.dll | MIT | cdb6cb8d07b3f08fc229133eff3159d48489a9320faf28d01ab05c75294cfc28 |
| System.Formats.Asn1 | 10.0.0 | System.Formats.Asn1.dll | MIT (.NET Foundation) | 2bd2b191aece6d3172789241c8fb5e482cea136e97d002a4940ccfc9c9834a3f |
| Microsoft.Bcl.AsyncInterfaces | 10.0.0 | Microsoft.Bcl.AsyncInterfaces.dll | MIT (.NET Foundation) | 9463c891bfd03be08936a5a35365377bbc24e48eb2ca47fbb5db12e96d8edf8d |
| System.Threading.Tasks.Extensions | 4.6.3 | System.Threading.Tasks.Extensions.dll | MIT (.NET Foundation) | 1abc92c7517a021ead7e79d0b7f3c70be75bce0fac7cfe3b38e157d32245ff1a |
| System.Memory | 4.6.3 | System.Memory.dll | MIT (.NET Foundation) | 26078aeb758c9ae985e8bf851f973026061da6a5eb4837204d0c2d2204c72955 |
| System.Buffers | 4.6.1 | System.Buffers.dll | MIT (.NET Foundation) | b00451e91d016fbec091ad1e361f3a7015e1d91d4047f7e48a74455b2a673d79 |
| System.Numerics.Vectors | 4.6.1 | System.Numerics.Vectors.dll | MIT (.NET Foundation) | 2bc500a86dcb02f2032d6d877f9e2d6e9e4a79080e57239b4198679d4031f2c7 |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | System.Runtime.CompilerServices.Unsafe.dll | MIT (.NET Foundation) | 5f6a7f53af3465f92beb6da873ebe0e496206c313313b98badee4355a6b25937 |
| System.ValueTuple | 4.6.1 | System.ValueTuple.dll | MIT (.NET Foundation) | 1dbf3b30f70d747411951103cc510a53c66ab4a376e9b8f20228a62669ba2d62 |
| Microsoft.Web.WebView2 (SDK, reused from Hallow Browser) | 1.0.4314.35 | Microsoft.Web.WebView2.Core.dll, .Wpf.dll, WebView2Loader.dll (x64) | Microsoft's BSD-style licence (WebView2-LICENSE.txt, WebView2-NOTICE.txt) | 90778deb1b77e36292828b4b5833c9255c27533f40e0ddd55891ae718944edc4 |

Not shipped: MimeKit's S/MIME / PGP part (MimeKit.Cryptography) and BouncyCastle.Cryptography - MailKit 5 doesn't need them, and
Hallow Email reads your provider's SPF / DKIM / DMARC verdicts from the Authentication-Results header instead of checking signatures
itself. The Microsoft Edge WebView2 Runtime is part of Windows 11 and is not shipped.

Licence files: MailKit-MimeKit-LICENSE.txt (MailKit, MimeKit.Core), dotnet-packages-LICENSE.txt (the System.* / Microsoft.Bcl
packages), dotnet-THIRD-PARTY-NOTICES.txt (the .NET runtime's third-party notices, shipped inside those packages), WebView2-LICENSE.txt
and WebView2-NOTICE.txt. Font Awesome Free icons: CC BY 4.0 (FontAwesome-LICENSE.txt).
