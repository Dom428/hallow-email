# Builds HallowEmail.exe with the C# compiler that ships with Windows (.NET Framework 4.8) against MailKit / MimeKit.Core and
# Microsoft's WebView2 SDK in lib\ (versions, hashes and licences in licenses\THIRD-PARTY.md). The DLLs are copied next to the exe.
#   .\build.ps1          the app (x64 - WebView2Loader.dll is the x64 one)
#   .\build.ps1 -Tests   also tests\EmailTests.exe (unit tests + a local IMAP4rev1 / SMTP stand-in, stand-in web endpoints for
#                        unsubscribe, Cloudflare and OAuth, and tests\FakeMpCmdRun.exe, a Microsoft Defender stand-in)
param([switch]$Tests)
$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$src = Join-Path $PSScriptRoot 'src'
$lib = Join-Path $PSScriptRoot 'lib'
$out = Join-Path $PSScriptRoot 'HallowEmail.exe'
$refs = "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Xaml.dll",
    "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Runtime.Serialization.dll", "$fw\System.Core.dll",
    "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Xml.dll", "$fw\System.Security.dll",
    "$fw\System.Net.Http.dll", "$fw\netstandard.dll", "$fw\System.Runtime.dll", "$fw\System.Threading.Tasks.dll"
$refs += Get-ChildItem $lib -Filter *.dll | Where-Object { $_.Name -ne 'WebView2Loader.dll' } | ForEach-Object FullName
$common = @('/nologo', '/optimize+', '/platform:x64', '/nowarn:1685,618', "/win32icon:$src\hallow.ico", "/resource:$src\Theme.xaml,HallowEmail.Theme.xaml") + ($refs | ForEach-Object { "/reference:$_" })
$files = Get-ChildItem $src -Filter *.cs | ForEach-Object FullName
& "$fw\csc.exe" @($common + @('/target:winexe', "/out:$out") + $files)
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item "$lib\*.dll" $PSScriptRoot -Force
"Built $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
if ($Tests) {
    $tdir = Join-Path $PSScriptRoot 'tests'
    $t = Join-Path $tdir 'EmailTests.exe'
    & "$fw\csc.exe" @($common + @('/target:exe', "/out:$t", '/main:HallowEmail.Tests.Run') + $files + (Get-ChildItem $tdir -Filter *.cs | Where-Object { $_.Name -ne 'FakeMpCmdRun.cs' } | ForEach-Object FullName))
    if ($LASTEXITCODE -ne 0) { throw "Test build failed" }
    Copy-Item "$lib\*.dll" $tdir -Force
    & "$fw\csc.exe" /nologo /optimize+ /target:exe "/out:$(Join-Path $tdir 'FakeMpCmdRun.exe')" (Join-Path $tdir 'FakeMpCmdRun.cs')
    if ($LASTEXITCODE -ne 0) { throw "FakeMpCmdRun build failed" }
    "Built $t"
}
