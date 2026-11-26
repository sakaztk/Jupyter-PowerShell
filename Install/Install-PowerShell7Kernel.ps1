#Requires -Version 5.1
<#
.SYNOPSIS
	Installs the Jupyter PowerShell 7 Kernel (requires .NET 11).

.PARAMETER WorkingFolder
	Temporary folder used for downloaded files. Defaults to the current directory.

.PARAMETER User
	Install into the current user's area. No elevation is required.

.PARAMETER ExecutablePath
	Optional full path to pwsh.exe.

.PARAMETER Uninstall
	Remove the installed kernel. Both user-scope and system-scope locations
	are checked and cleaned.
#>
[CmdletBinding()]
param (
	[string]$WorkingFolder = '.',
	[switch]$User,
	[string]$ExecutablePath,
	[switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$WorkingFolder = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkingFolder)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
	[Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $User -and -not $isAdmin) {
	Write-Host 'Administrator privileges required. Re-launching as administrator...'

	$argList = @()
	if ($Uninstall) { $argList += '-Uninstall' }
	if ($WorkingFolder -ne '.') { $argList += "-WorkingFolder `"$WorkingFolder`"" }
	if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) { $argList += "-ExecutablePath `"$ExecutablePath`"" }

	$scriptPath = $PSCommandPath
	if ([string]::IsNullOrEmpty($scriptPath)) {
		$scriptPath = Join-Path $env:TEMP 'Install-PowerShell7Kernel_elevated.ps1'
		$MyInvocation.MyCommand.Definition | Set-Content -Path $scriptPath -Encoding UTF8
	}

	$psExe = (Get-Process -Id $PID).Path
	$argFull = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" $($argList -join ' ')"
	Start-Process -FilePath $psExe -ArgumentList $argFull -Verb RunAs -Wait
	exit
}

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
	throw 'Python/Jupyter is required. Install Jupyter, or activate an environment containing python.'
}

if ($User) {
	$packagePath = $(python -c 'import site; print(site.getusersitepackages())')
	$kernelPath = Join-Path (python -m jupyter --data-dir) 'kernels'
}
else {
	Try {
		$packagePath = $(python -c 'import site; print(site.getsitepackages()[0])')
		$kernelPath = Join-Path (python -c "import jupyter_core.paths as p; print(p.SYSTEM_JUPYTER_PATH[0])") 'kernels'
	}
	Catch {
		throw "Could not determine system package or kernel path. Please use elevated PowerShell."
	}
}

Write-Host "Scope        : $(if ($User) {'User'} else {'System'})"
Write-Host "Package path : $packagePath"
Write-Host "Kernel path  : $kernelPath"

New-Item -ItemType Directory -Path $kernelPath -Force | Out-Null
New-Item -ItemType Directory -Path $WorkingFolder -Force | Out-Null

function Remove-TargetDirectory {
	param ([string]$Path, [string]$Label)
	if (Test-Path $Path) {
		try {
			Remove-Item -Path $Path -Recurse -Force
			Write-Host "Removed ${Label}: $Path"
		}
		catch {
			Write-Warning "Could not remove ${Label} at $Path : $_"
		}
	}
	else {
		Write-Host "$Label not found: $Path"
	}
}

if ($Uninstall) {
	Write-Host 'Uninstall mode selected. Checking both user and system locations...'

	$userPackagePath = $(python -c 'import site; print(site.getusersitepackages())')
	$userKernelPath = Join-Path (python -m jupyter --data-dir) 'kernels'

	try {
		$sysPackagePath = $(python -c 'import site; print(site.getsitepackages()[0])')
		$sysKernelPath = Join-Path (python -c "import jupyter_core.paths as p; print(p.SYSTEM_JUPYTER_PATH[0])") 'kernels'
	}
	catch {
		throw "Could not determine system package or kernel path. Please use elevated PowerShell."
	}

	foreach ($kp in @($userKernelPath, $sysKernelPath)) {
		Remove-TargetDirectory -Path (Join-Path $kp 'powershell7') -Label 'PowerShell 7 kernelspec'
	}
	foreach ($pp in @($userPackagePath, $sysPackagePath)) {
		Remove-TargetDirectory -Path (Join-Path $pp 'powershell7_kernel') -Label 'PowerShell 7 package folder'
	}

	Write-Host 'Uninstallation complete.'
	return
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
	$pwshCommand = Get-Command pwsh.exe -ErrorAction SilentlyContinue
	if ($pwshCommand) {
		$ExecutablePath = $pwshCommand.Source
	}
	else {
		$defaultPath = Join-Path ${env:ProgramFiles} 'PowerShell\7\pwsh.exe'
		if (Test-Path $defaultPath) {
			$ExecutablePath = $defaultPath
		}
		else {
			throw 'pwsh.exe was not found. Pass -ExecutablePath.'
		}
	}
}

function Test-DotNet11Installed {
	try {
		$runtimes = & dotnet --list-runtimes 2>$null
		if ($runtimes -match 'Microsoft\.NETCore\.App 11\.') { return $true }
		$sdks = & dotnet --list-sdks 2>$null
		if ($sdks -match '^11\.') { return $true }
	}
	catch { }
	return $false
}

Write-Host 'Checking .NET 11 installation status...'
if (-not (Test-DotNet11Installed)) {
	if ($User) {
		Write-Warning '.NET 11 Runtime or SDK is not installed. Install it manually before starting the kernel.'
	}
	elseif (Get-Command winget -ErrorAction SilentlyContinue) {
		Write-Host 'Installing .NET 11 Runtime with winget...'
		winget install Microsoft.DotNet.Runtime.11.0 --accept-package-agreements --accept-source-agreements
	}
	else {
		throw '.NET 11 is required. Install it before running this installer.'
	}
}
else {
	Write-Host '.NET 11 Runtime or SDK is already installed.'
}

Write-Host 'Fetching latest release information...'
$releaseApi = 'https://api.github.com/repos/sakaztk/Jupyter-PowerShellSDK/releases/latest'
$latestRelease = Invoke-RestMethod -Uri $releaseApi -Headers @{ 'User-Agent' = 'Mozilla/5.0' }
if (-not $latestRelease.tag_name) {
	throw "Could not determine latest release tag from $releaseApi"
}

$zipName = 'PowerShell7.zip'
$zipPath = Join-Path $WorkingFolder $zipName
$versionString = $latestRelease.tag_name
$releaseURI = 'https://github.com/sakaztk/Jupyter-PowerShellSDK/releases'
$links = (Invoke-WebRequest -Uri "$releaseURI/expanded_assets/$versionString" -UseBasicParsing).Links.href
$fileLink = $links | Select-String -Pattern 'PowerShell7\.zip|Jupyter-PowerShell7.*\.zip' | Select-Object -First 1
if (-not $fileLink) {
	throw "Could not locate PowerShell7 zip asset in release assets for $versionString"
}
$fileUri = 'https://github.com' + $fileLink.ToString().Trim()

$installDir = Join-Path $packagePath 'powershell7_kernel'
try {
	Write-Host "Downloading $zipName from $fileUri ..."
	Invoke-WebRequest -Uri $fileUri -UseBasicParsing -OutFile $zipPath

	$shaLink = $links | Select-String -Pattern 'PowerShell7\.zip\.sha256|PowerShell7\.sha256' | Select-Object -First 1
	if ($shaLink) {
		$shaUri = 'https://github.com' + $shaLink.ToString().Trim()
		$expectedHash = ((Invoke-WebRequest -Uri $shaUri -UseBasicParsing).Content -split '\\s+')[0].Trim().ToLowerInvariant()
		$actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
		if ($actualHash -ne $expectedHash) {
			throw "SHA-256 validation failed. Expected $expectedHash but downloaded $actualHash."
		}
	}

	Write-Host "Extracting to $installDir ..."
	Expand-Archive -Path $zipPath -DestinationPath $installDir -Force
}
finally {
	if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
}

$kernelSpecDir = Join-Path $kernelPath 'powershell7'
New-Item -ItemType Directory -Path $kernelSpecDir -Force | Out-Null

$logo64 = Join-Path $kernelSpecDir 'logo-64x64.png'
$logo32 = Join-Path $kernelSpecDir 'logo-32x32.png'
Write-Host 'Downloading kernel logo...'
Invoke-WebRequest -UseBasicParsing `
	-Uri 'https://raw.githubusercontent.com/PowerShell/PowerShell/master/assets/Powershell_black_64.png' `
	-OutFile $logo64

Add-Type -AssemblyName System.Drawing
$image = [System.Drawing.Image]::FromFile($logo64)
$bitmap32 = New-Object System.Drawing.Bitmap(32, 32)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap32)
$graphics.DrawImage($image, 0, 0, 32, 32)
$bitmap32.Save($logo32, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap32.Dispose()
$image.Dispose()

$exePath = Join-Path $installDir 'Jupyter_PowerShell7.exe'
$kernelJson = [ordered]@{
	argv = @($exePath, '{connection_file}', $ExecutablePath)
	display_name = 'PowerShell 7'
	language = 'powershell'
}
[System.IO.File]::WriteAllText((Join-Path $kernelSpecDir 'kernel.json'), ($kernelJson | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))

$extraPngs = Join-Path $installDir '*.png'
if (Test-Path $extraPngs) {
	Move-Item -Path $extraPngs -Destination $kernelSpecDir -Force
}

Write-Host 'Installation complete.'
