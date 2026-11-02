$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'

if (-not (Test-Path $csc)) {
    throw "C# compiler not found: $csc"
}

$output = Join-Path $root 'WheaMonitor.exe'
$icon = Join-Path $root 'WheaMonitor.ico'
$manifest = Join-Path $root 'app.manifest'
$xaml = Join-Path $root 'MainWindow.xaml'

if (Test-Path $output) {
    $running = Get-Process -Name 'WheaMonitor' -ErrorAction SilentlyContinue
    if ($running) {
        throw 'WheaMonitor.exe is running. Close it before building.'
    }
}

$references = @(
    (Join-Path $wpf 'PresentationFramework.dll'),
    (Join-Path $wpf 'PresentationCore.dll'),
    (Join-Path $wpf 'WindowsBase.dll'),
    (Join-Path $framework 'System.Xaml.dll'),
    (Join-Path $framework 'System.Core.dll'),
    (Join-Path $framework 'System.dll'),
    (Join-Path $framework 'System.Xml.dll')
)

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    ('/out:' + $output),
    ('/win32manifest:' + $manifest),
    ('/resource:' + $xaml + ',MainWindow.xaml')
)

if (Test-Path $icon) {
    $arguments += ('/win32icon:' + $icon)
}

foreach ($reference in $references) {
    $arguments += ('/reference:' + $reference)
}

$arguments += @(
    (Join-Path $root 'Program.cs'),
    (Join-Path $root 'WheaChart.cs')
)

& $csc $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

$file = Get-Item $output
Write-Host ("Built {0} ({1:N0} bytes)" -f $file.FullName, $file.Length)
