# Jupyter-PowerShellSDK

Jupyter kernel implementation for Windows PowerShell SDK and Windows PowerShell 5.

## Installation

A common approach is to download the install script directly from GitHub and execute it in place.

### PowerShell 5 kernel

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope process
& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/sakaztk/Jupyter-PowerShell/powershellsdk/Install/Install-PowerShell5Kernel.ps1).Content))
```

By default, this installs both x64 and x86 kernels system-wide and requires administrator privileges.

To install to the current user's area without administrator privileges, use `-User`:

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope process
& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/sakaztk/Jupyter-PowerShell/powershellsdk/Install/Install-PowerShell5Kernel.ps1).Content)) -User
```

If you need to pass other arguments such as `-X64` or `-X86`, save the script locally first and then run it with parameters.

### PowerShell 7 kernel

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope process
& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/sakaztk/Jupyter-PowerShell/powershellsdk/Install/Install-PowerShell7Kernel.ps1).Content)) -User
```

The PowerShell 7 kernel starts an external `pwsh.exe` process and requires Python/Jupyter and .NET 11 Runtime or SDK. Use `-ExecutablePath` to select a specific pwsh executable.

### PowerShell SDK kernel

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope process
& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/sakaztk/Jupyter-PowerShell/powershell/Install/Install-PowerShellSDKKernel.ps1).Content)) -User
```

The PowerShell SDK kernel requires Python/Jupyter and .NET 11 Runtime or SDK.


```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope process
& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/sakaztk/Jupyter-PowerShellSDK/powershellsdk/Install/Install-PowerShellSDKKernel.ps1).Content)) -User
```

## Uninstallation

You can also download and run the uninstall mode directly from GitHub.
Uninstall mode checks **both user-scope and system-scope** locations and removes any matching kernels found.
It reqires administrator privileges to remove system-scope kernels.

### PowerShell 5 kernel

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope process
& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/sakaztk/Jupyter-PowerShellSDK/powershellsdk/Install/Install-PowerShell5Kernel.ps1).Content)) -Uninstall
```

### PowerShell 7 kernel

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope process
& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/sakaztk/Jupyter-PowerShellSDK/powershellsdk/Install/Install-PowerShell7Kernel.ps1).Content)) -Uninstall
```

### PowerShell SDK kernel

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope process
& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/sakaztk/Jupyter-PowerShellSDK/powershellsdk/Install/Install-PowerShellSDKKernel.ps1).Content)) -Uninstall
```

## Logo
This procedure will install the PowerShell logo in JupyterLab that used with permission from Microsoft. The assets are trademarked by Microsoft (https://github.com/PowerShell/PowerShell/tree/master/assets).

## Notes

- Jupyter kernels are registered under the current Python environment.
- The PowerShell 7 kernel requires .NET 11 and an external `pwsh.exe`.
- The SDK kernel requires .NET 11.
- The PowerShell 5 installer creates both x64 and x86 kernels unless a specific variant is selected.

## Third-Party Notices

This project uses third-party libraries including NetMQ, which is licensed under the GNU Lesser General Public License v3 (LGPL-3.0) with a special linking exception. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.
