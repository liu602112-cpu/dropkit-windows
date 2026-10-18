param([string]$OutputDirectory = 'release')
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$releaseDir = Join-Path $projectDir $OutputDirectory
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $frameworkDir 'csc.exe'))) { $frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(62,99,221))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),4
$graphics.DrawRectangle($pen,14,26,36,26)
$graphics.DrawLine($pen,25,38,39,38)
$graphics.DrawLine($pen,32,10,32,29)
$graphics.DrawLine($pen,24,21,32,29)
$graphics.DrawLine($pen,40,21,32,29)
$iconHandle = $bitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($iconHandle)
$iconPath = Join-Path $projectDir 'app.ico'
$iconStream = [System.IO.File]::Create($iconPath)
$icon.Save($iconStream)
$iconStream.Dispose(); $icon.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $pen.Dispose()
$references = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Security.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkDir $_) }
$sourceFiles = Get-ChildItem (Join-Path $projectDir 'src\*.cs') | ForEach-Object FullName
$compilerArgs = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',('/out:' + (Join-Path $releaseDir 'DropKit.exe')),('/win32manifest:' + (Join-Path $projectDir 'app.manifest')),('/win32icon:' + $iconPath),('/resource:' + (Join-Path $projectDir 'src\Main.xaml') + ',Main.xaml'),('/resource:' + $iconPath + ',app.ico')) + $references + $sourceFiles
& (Join-Path $frameworkDir 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item (Join-Path $projectDir 'DropKit.exe.config') $releaseDir -Force
Copy-Item (Join-Path $projectDir 'LICENSE') $releaseDir -Force
Copy-Item (Join-Path $projectDir '使用说明.txt') $releaseDir -Force
Write-Output ('Built: ' + (Join-Path $releaseDir 'DropKit.exe'))
