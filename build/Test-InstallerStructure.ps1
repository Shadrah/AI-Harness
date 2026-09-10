[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallerPath,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$resolvedInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$database = $windowsInstaller.GetType().InvokeMember(
    'OpenDatabase', 'InvokeMethod', $null, $windowsInstaller, @($resolvedInstaller, 0))

function Read-MsiRows([string] $Query, [int] $FieldCount) {
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Query))
    try {
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        while ($record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)) {
            try {
                $values = for ($index = 1; $index -le $FieldCount; $index++) {
                    $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, $index)
                }
                Write-Output -NoEnumerate $values
            }
            finally {
                [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
            }
        }
    }
    finally {
        try {
            $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        }
        finally {
            [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
    }
}

try {
$properties = @{}
foreach ($row in Read-MsiRows 'SELECT `Property`,`Value` FROM `Property`' 2) {
    $properties[$row[0]] = $row[1]
}
if ($properties.ProductName -ne 'Harness') { throw 'MSI ProductName is not Harness.' }
if ($properties.ProductVersion -ne $ExpectedVersion) { throw "MSI version is $($properties.ProductVersion), expected $ExpectedVersion." }
if ($properties.UpgradeCode -ne '{0DD5486E-EA2A-4D2A-92D5-41E467EF695B}') { throw 'MSI UpgradeCode changed unexpectedly.' }
if ($properties.ALLUSERS -ne '1') { throw 'MSI is not registered as a per-machine package.' }

$files = @(Read-MsiRows 'SELECT `FileName`,`FileSize` FROM `File`' 2)
$harnessExecutable = @($files | Where-Object { $_[0] -eq 'Harness.exe' })
if ($harnessExecutable.Count -ne 1) { throw 'MSI must contain exactly one Harness.exe payload.' }
if ($files.Count -lt 20) { throw "MSI payload is unexpectedly small: $($files.Count) files." }

$fileLocations = @(Read-MsiRows 'SELECT `File`.`FileName`, `Component`.`Directory_` FROM `File`, `Component` WHERE `File`.`Component_` = `Component`.`Component`' 2)
$rootWebViewLoader = @($fileLocations | Where-Object {
    ($_[0] -split '\|')[-1] -eq 'WebView2Loader.dll' -and $_[1] -eq 'INSTALLFOLDER'
})
if ($rootWebViewLoader.Count -ne 1) {
    throw 'MSI must place exactly one WebView2Loader.dll beside Harness.exe for the embedded browser.'
}

$shortcuts = @(Read-MsiRows 'SELECT `Shortcut`,`Name`,`Target` FROM `Shortcut`' 3)
if (-not ($shortcuts | Where-Object { $_[0] -eq 'HarnessStartMenuShortcut' -and $_[2] -eq '[INSTALLFOLDER]Harness.exe' })) {
    throw 'MSI Start menu shortcut is missing or targets the wrong executable.'
}
if (-not ($shortcuts | Where-Object { $_[0] -eq 'HarnessDesktopShortcut' -and $_[2] -eq '[INSTALLFOLDER]Harness.exe' })) {
    throw 'MSI optional desktop shortcut is missing or targets the wrong executable.'
}

$features = @(Read-MsiRows 'SELECT `Feature`,`Title`,`Level` FROM `Feature`' 3)
$desktopFeature = $features | Where-Object { $_[0] -eq 'DesktopShortcutFeature' }
if (-not $desktopFeature -or $desktopFeature[2] -ne '2') {
    throw 'The desktop shortcut is not represented as an opt-in feature.'
}

Write-Host "MSI structure passed · $($files.Count) files · version $ExpectedVersion · browser bootstrap · stable upgrades · optional desktop shortcut"
}
finally {
    [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($windowsInstaller)
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
