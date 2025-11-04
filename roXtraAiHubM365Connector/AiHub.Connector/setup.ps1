#Requires -RunAsAdministrator
<#
.SYNOPSIS
Installs the AiHub Connector as a Windows Service running under LocalService.

.DESCRIPTION
- Copies binaries from the script folder ($PSScriptRoot) to the chosen install directory.
- Creates a Windows Service that runs under `NT AUTHORITY\LocalService` with the specified name and display name.
- Prompts to stop/delete and reinstall if the service already exists.
 - Does not start the service automatically after installation.
 - Grants write permission on the install directory to the service account.

.EXAMPLE
pwsh -File .\setup.ps1

.EXAMPLE
pwsh -File .\setup.ps1 -ServiceName roXtraAiHubM365Connector -InstallDir "C:\\Program Files\\roXtraAiHubM365Connector"

 

.NOTES
Run this script from an elevated PowerShell prompt (as Administrator).
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param (
	[string]$ServiceName = "roXtraAiHubM365Connector",
	[string]$DisplayName = "roXtra AI Hub M365 Connector",
	[string]$Description = "roXtra AI Hub M365 Connector Windows Service",
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'roXtraAiHubM365Connector'),
	# Executable name inside the artifact/install directory
	[string]$ExeName = 'AiHub.Connector.exe'
)

try {
	$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
	if ($existing) {
		$caption = 'Confirm Reinstall'
		$query = "Service '$ServiceName' already exists. Stop, delete, and reinstall?"
		if (-not $PSCmdlet.ShouldContinue($query, $caption)) {
			Write-Host "Aborted by user. Existing service retained."
			return
		}
		try {
			if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue }
		} catch { }
		Start-Sleep -Seconds 2
		$svcDel = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
		if ($null -ne $svcDel) { Invoke-CimMethod -InputObject $svcDel -MethodName Delete | Out-Null }
		Start-Sleep -Seconds 2
	}


	if (-not (Test-Path $InstallDir)) {
		New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
	}

	$exePath = Join-Path $InstallDir $ExeName

	# Copy from the script directory to install directory
	Write-Host "Copying from directory: $PSScriptRoot -> $InstallDir"
	Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination $InstallDir -Recurse -Force

	if (-not (Test-Path $exePath)) {
		# Try to auto-detect a single .exe if ExeName not found
		$foundExe = Get-ChildItem $InstallDir -Filter '*.exe' -Recurse | Select-Object -First 1
		if ($foundExe) { $exePath = $foundExe.FullName } else { throw "Application executable not found at '$exePath'. Use -ExeName to specify the executable name." }
	}

	# Grant Modify permission on the install directory to LocalService
	Write-Host "Granting folder permissions to LocalService on $InstallDir"
	try {
		$localServiceSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-19')
		$localServiceAccount = $localServiceSid.Translate([System.Security.Principal.NTAccount])
		$acl = Get-Acl -Path $InstallDir
		$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($localServiceAccount, 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
		$acl.SetAccessRule($rule)
		Set-Acl -Path $InstallDir -AclObject $acl
	} catch {
		throw "Failed to set folder permissions for LocalService on '$InstallDir'. Error: $_"
	}

	# Create service with Automatic startup
	$bin = '"{0}"' -f $exePath
	New-Service -Name $ServiceName -BinaryPathName $bin -DisplayName $DisplayName -StartupType Automatic | Out-Null

	# Switch logon to LocalService via CIM (no password)
	$svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
	if ($null -ne $svc) {
		Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments @{ StartName = 'NT AUTHORITY\LocalService'; StartPassword = $null } | Out-Null
		try { Set-Service -Name $ServiceName -Description $Description -ErrorAction Stop } catch { try { Set-CimInstance -InputObject $svc -Property @{ Description = $Description } | Out-Null } catch { } }
	}

	# Configure service recovery options using sc.exe
	sc.exe failure "$ServiceName" reset= 86400 actions= restart/60000/restart/60000/""/60000 | Out-Null
	sc.exe failureflag "$ServiceName" 1 | Out-Null

	Write-Host "Service '$ServiceName' installed. Start it manually when ready."

	Write-Host "Installed to: $InstallDir"
}
catch {
	Write-Error $_
	exit 1
}
