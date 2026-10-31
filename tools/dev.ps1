<#
.SYNOPSIS
  Builds this checkout's Recto, registers it as a PDF app and starts it.

.DESCRIPTION
  Every checkout (the main clone or any git worktree) is registered under its own name, "Recto
  Dev (<branch>)", or the folder if it's on no branch, so builds from several worktrees can be
  installed side by side without replacing each other, and each runs as its own instance. Test one
  by passing PDFs to this script, or from File Explorer: right-click a PDF > Open with > Recto Dev
  (<branch>). The About dialog shows which branch and commit a running build came from.

  Registration is per user (HKCU) and needs no admin rights. Every run renames the registered builds
  after their checkouts' current branches, and removes the registrations of checkouts that no longer
  exist and, if gh is installed, of branches whose pull request was merged or closed.

  Release (the default) publishes with Native AOT, as CI ships. Debug is a plain JIT build, which
  compiles faster.

  A build from this checkout that's still running is closed first, as it locks the output files.

  The File Explorer thumbnail handler (src/Recto.Thumbnails) is built too, with Native AOT in either
  configuration, and registered for the file type of every registered build. As with the Store
  build, Explorer uses it only while one of them is the default PDF app (-SetDefault). Its COM class
  ID is fixed, so it's shared by every checkout: thumbnails come from the build registered most
  recently.

.EXAMPLE
  tools\dev.ps1                         # build, register and start
  tools\dev.ps1 a.pdf b.pdf             # ...and open files
  tools\dev.ps1 -NoLaunch               # build and register only
  tools\dev.ps1 -Configuration Debug    # faster JIT build
  tools\dev.ps1 -SetDefault             # also open Settings to make this build the default PDF app
  tools\dev.ps1 -List                   # show every registered build
  tools\dev.ps1 -Unregister             # remove this checkout's registration (-All: every one)
#>
[CmdletBinding(DefaultParameterSetName = "Build", PositionalBinding = $false)]
param(
    [Parameter(ParameterSetName = "Build", ValueFromRemainingArguments)] [string[]]$Files = @(),
    [Parameter(ParameterSetName = "Build")] [ValidateSet("Debug", "Release")] [string]$Configuration = "Release",
    [Parameter(ParameterSetName = "Build")] [switch]$NoLaunch,
    [Parameter(ParameterSetName = "Build")] [switch]$SetDefault,
    [Parameter(ParameterSetName = "List")] [switch]$List,
    [Parameter(ParameterSetName = "Unregister")] [switch]$Unregister,
    [Parameter(ParameterSetName = "Unregister")] [switch]$All
)

$ErrorActionPreference = "Stop"

# Names a build after its branch, which says what it's for. Worktree folders are often just a hash.
function Get-Label([string]$Checkout) {
    $branch = git -C $Checkout branch --show-current
    if ($branch) { $branch } else { Split-Path $Checkout -Leaf }
}

$checkout = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$label = Get-Label $checkout
$sha = [Security.Cryptography.SHA256]::Create()
$id = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($checkout.ToUpperInvariant()))[0..3] | ForEach-Object { $_.ToString("x2") })
$displayName = "Recto Dev ($label)"

# Per build: a ProgID for .pdf, and Capabilities under Software\RectoDev\<id> so that Settings >
# Default apps lists it. The key also records the checkout, to find registrations that are stale.
$classes = "HKCU:\Software\Classes"
$builds = "HKCU:\Software\RectoDev"
$registeredApps = "HKCU:\Software\RegisteredApplications"
# Must match ThumbnailProvider.Clsid in src/Recto.Thumbnails.
$thumbnailClsid = "{54e57302-4f85-47eb-8be3-76ee7e671857}"
$thumbnailServer = "$classes\CLSID\$thumbnailClsid"

function Get-Registrations {
    Get-ChildItem $builds -ErrorAction SilentlyContinue | ForEach-Object {
        $properties = Get-ItemProperty $_.PSPath
        [pscustomobject]@{ Id = $_.PSChildName; Name = $properties.Name; Checkout = $properties.Checkout; Exe = $properties.Exe }
    }
}

function Remove-Registration([string]$Id) {
    Remove-Item "$classes\RectoDev.$Id" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty "$classes\.pdf\OpenWithProgids" -Name "RectoDev.$Id" -ErrorAction SilentlyContinue
    Remove-ItemProperty $registeredApps -Name "RectoDev.$Id" -ErrorAction SilentlyContinue
    Remove-Item "$builds\$Id" -Recurse -Force -ErrorAction SilentlyContinue
}

function Set-DisplayName([string]$Id, [string]$Name) {
    Set-Value "$classes\RectoDev.$Id\shell\open" "FriendlyAppName" $Name
    Set-Value "$classes\RectoDev.$Id\Application" "ApplicationName" $Name
    Set-Value "$builds\$Id" "Name" $Name
    Set-Value "$builds\$Id\Capabilities" "ApplicationName" $Name
}

# The state of the pull request from a checkout's branch, if there is one. --head also matches forks'
# branches of the same name, such as main.
function Get-PullRequestState([string]$Checkout) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { return }
    $branch = git -C $Checkout branch --show-current
    if (-not $branch) { return }
    Push-Location $Checkout
    try { gh pr list --head $branch --state all --json state,isCrossRepository --jq "map(select(.isCrossRepository | not))[0].state" 2>$null }
    catch { }
    finally { Pop-Location }
}

# Explorer loads the thumbnail handler into a COM surrogate (dllhost.exe), which keeps it and the
# pdfium.dll beside it loaded, and so locked. It never unloads them, so it also goes on using the DLL
# it loaded first, whichever is registered later. Explorer starts a new one when it next needs a thumbnail.
function Stop-ThumbnailSurrogates {
    $surrogates = @(Get-Process dllhost -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Modules | Where-Object ModuleName -EQ "Recto.Thumbnails.dll" } catch { $false }
    })
    if (-not $surrogates) { return }
    Write-Host "Closing the thumbnail handler's COM surrogate..."
    $surrogates | Stop-Process -Force -ErrorAction SilentlyContinue
    $surrogates | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    $left = @($surrogates | Where-Object { -not $_.HasExited })
    if ($left) { throw "Couldn't close the thumbnail handler's COM surrogate (dllhost.exe, PID $($left.Id -join ', '))." }
}

function Set-ThumbnailServer([string]$Dll) {
    Set-Value $thumbnailServer "(default)" "Recto PDF Thumbnails"
    Set-Value "$thumbnailServer\InprocServer32" "(default)" $Dll
    Set-Value "$thumbnailServer\InprocServer32" "ThreadingModel" "Both"
    # A surrogate started before the change would keep the previous DLL.
    Stop-ThumbnailSurrogates
}

# The thumbnail handler is shared by every build. After builds are unregistered, point it at one that's
# left, or remove it if none is.
function Update-ThumbnailServer {
    $dlls = @(Get-Registrations | ForEach-Object { Join-Path (Split-Path $_.Exe) "Recto.Thumbnails.dll" } | Where-Object { Test-Path $_ })
    $current = (Get-ItemProperty "$thumbnailServer\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
    if ($current -in $dlls) { return }
    if ($dlls) { Set-ThumbnailServer $dlls[0]; return }
    Remove-Item $thumbnailServer -Recurse -Force -ErrorAction SilentlyContinue
    Stop-ThumbnailSurrogates
}

function Send-AssociationsChanged {
    if (-not ("RectoDev.Shell" -as [type])) {
        Add-Type -Namespace RectoDev -Name Shell -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, System.IntPtr item1, System.IntPtr item2);
"@
    }
    [RectoDev.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED
}

function Set-Value([string]$Key, [string]$Name, [string]$Value) {
    if (-not (Test-Path $Key)) { New-Item $Key -Force | Out-Null }
    Set-ItemProperty $Key -Name $Name -Value $Value
}

if ($List) {
    $registrations = @(Get-Registrations)
    if (-not $registrations) { Write-Host "No builds are registered."; return }
    $registrations | ForEach-Object {
        $state = if (-not (Test-Path $_.Exe)) { "not built" } elseif (Get-Process Recto -ErrorAction SilentlyContinue | Where-Object Path -EQ $_.Exe) { "running" } else { "" }
        [pscustomobject]@{ Name = $_.Name; State = $state; Exe = $_.Exe }
    } | Format-Table -AutoSize
    return
}

if ($Unregister) {
    $targets = if ($All) { @(Get-Registrations).Id } else { @($id) }
    foreach ($target in $targets) { Remove-Registration $target }
    Update-ThumbnailServer
    Send-AssociationsChanged
    Write-Host $(if ($All) { "Unregistered every Recto Dev build." } else { "Unregistered $displayName." })
    Write-Host "If one was your default PDF app, pick another in Settings > Apps > Default apps."
    return
}

# ------------------------------------------------------------------ build

$project = Join-Path $checkout "src\Recto\Recto.csproj"
$platform = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq "Arm64") { @("-p:Platform=ARM64") } else { @() }
# Release publishes to out\dev. Debug stays in bin: built with -o, it would leave out the compiled XAML.
$output = if ($Configuration -eq "Release") { Join-Path $checkout "out\dev\Release" }
    else { (dotnet msbuild $project -getProperty:TargetDir -p:Configuration=Debug @platform).Trim().TrimEnd("\") }
$exe = Join-Path $output "Recto.exe"

# A running build from this folder locks its files.
$running = Get-Process Recto -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith("$output\", [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "Closing the running build..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}
# So does the thumbnail handler's surrogate, if it loaded them from this folder.
Stop-ThumbnailSurrogates

# Native AOT finds the linker through vcvarsall.bat, which fails unless vswhere.exe is on PATH.
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path $installer)) { $env:PATH += ";$installer" }

# Shown in the About dialog.
$env:RectoBuildLabel = $label

if ($Configuration -eq "Release") {
    # Publishing is what compiles with Native AOT; a Release build would still run on the JIT.
    dotnet publish $project -c Release -o $output --nologo @platform
} else {
    dotnet build $project -c Debug --nologo @platform
}
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# The thumbnail handler is a native COM DLL, so it's always published with Native AOT. It goes next to
# Recto.exe, as it loads the pdfium.dll beside it.
$thumbnails = Join-Path $checkout "out\dev\thumbnails-$Configuration"
$rid = if ($platform) { "win-arm64" } else { "win-x64" }
dotnet publish (Join-Path $checkout "src\Recto.Thumbnails\Recto.Thumbnails.csproj") -c $Configuration -r $rid -o $thumbnails --nologo
if ($LASTEXITCODE -ne 0) { throw "Building the thumbnail handler failed." }
$thumbnailDll = Join-Path $output "Recto.Thumbnails.dll"
# Explorer may have loaded the handler again during the build.
for ($attempt = 1; ; $attempt++) {
    Stop-ThumbnailSurrogates
    try { Copy-Item (Join-Path $thumbnails "Recto.Thumbnails.dll") $thumbnailDll -Force; break }
    catch { if ($attempt -ge 3) { throw } }
}

# ------------------------------------------------------------------ register

foreach ($stale in @(Get-Registrations | Where-Object { -not (Test-Path (Join-Path "$($_.Checkout)" "Recto.sln")) })) {
    Remove-Registration $stale.Id
    Write-Host "Removed the registration of $($stale.Checkout), which no longer exists."
}
foreach ($finished in @(Get-Registrations | Where-Object { $_.Id -ne $id -and (Get-PullRequestState $_.Checkout) -in "MERGED", "CLOSED" })) {
    Remove-Registration $finished.Id
    Write-Host "Removed the registration of $($finished.Name), as its pull request was merged or closed."
}

$progId = "$classes\RectoDev.$id"
$command = "`"$exe`" `"%1`""
$icon = "`"$exe`",0"
Set-Value $progId "(default)" "PDF Document"
Set-Value $progId "FriendlyTypeName" "PDF Document"
Set-Value "$progId\DefaultIcon" "(default)" $icon
Set-Value "$progId\shell\open\command" "(default)" $command
Set-Value "$progId\Application" "ApplicationIcon" $icon
# The thumbnail handler, which this build's DLL now provides to every registered build, and the badge
# Explorer draws in the corner of the thumbnails: each build's own icon.
$others = @(Get-Registrations | Where-Object { $_.Id -ne $id -and (Test-Path "$classes\RectoDev.$($_.Id)") })
foreach ($target in @([pscustomobject]@{ Id = $id; Exe = $exe }) + $others) {
    Set-Value "$classes\RectoDev.$($target.Id)" "TypeOverlay" "$($target.Exe),0"
    Set-Value "$classes\RectoDev.$($target.Id)\ShellEx\{e357fccd-a995-4576-b01f-234630154e96}" "(default)" $thumbnailClsid
}
Set-ThumbnailServer $thumbnailDll
# Offered under Open with for .pdf.
if (-not (Test-Path "$classes\.pdf\OpenWithProgids")) { New-Item "$classes\.pdf\OpenWithProgids" -Force | Out-Null }
New-ItemProperty "$classes\.pdf\OpenWithProgids" -Name "RectoDev.$id" -PropertyType None -Value ([byte[]]@()) -Force | Out-Null

$build = "$builds\$id"
Set-Value $build "Checkout" $checkout
Set-Value $build "Exe" $exe
Set-Value "$build\Capabilities" "ApplicationDescription" "Development build of Recto from $checkout"
Set-Value "$build\Capabilities" "ApplicationIcon" $icon
Set-Value "$build\Capabilities\FileAssociations" ".pdf" "RectoDev.$id"
Set-Value $registeredApps "RectoDev.$id" "Software\RectoDev\$id\Capabilities"
# Every build's, not just this one's: another checkout's branch may have been renamed or switched.
foreach ($registration in @(Get-Registrations)) {
    Set-DisplayName $registration.Id "Recto Dev ($(Get-Label $registration.Checkout))"
}
Send-AssociationsChanged
Write-Host "Registered $displayName -> $exe"

if ($SetDefault) {
    Write-Host "Opening Settings: choose $displayName for .pdf."
    Start-Process "ms-settings:defaultapps?registeredAppUser=RectoDev.$id"
}

if (-not $NoLaunch) {
    $paths = @($Files | ForEach-Object { "`"$((Resolve-Path $_).Path)`"" })
    if ($paths) { Start-Process $exe -ArgumentList $paths } else { Start-Process $exe }
    Write-Host "Started $displayName."
}
