param([switch]$Test)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'src/SweetTools.Loader'
$output = Join-Path $source 'bin/Release'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$install) { throw 'Install Visual Studio C++ desktop build tools and a Windows SDK.' }
Import-Module (Join-Path $install 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $install -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
Push-Location $output
try {
    $exports = foreach ($line in Get-Content (Join-Path $source 'winmm.def')) {
        if ($line -match '^\s*(\S+)\s+@(\d+)(.*)$') {
            $forward = $Matches[1]
            $ordinal = $Matches[2]
            $suffix = if ($Matches[3] -match 'NONAME') { ',NONAME' } else { '' }
            '#pragma comment(linker, "/export:' + $forward + ',@' + $ordinal + $suffix + '")'
        }
    }
    Set-Content Exports.h -Value $exports -Encoding ascii
    & cl.exe /nologo /std:c++17 /c /FIExports.h /I"$output" /O2 /MT /EHsc /W4 /WX /Brepro (Join-Path $source 'Loader.cpp')
    if ($LASTEXITCODE -ne 0) { throw 'Native loader compilation failed.' }
    & link.exe /nologo /DLL Loader.obj /OUT:winmm.dll /Brepro advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native loader build failed.' }
    if ($Test) {
        & cl.exe /nologo /O2 /MT /EHsc /W4 /WX (Join-Path $source 'PolicyTests.cpp') /Fe:PolicyTests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Native test build failed.' }
        & ./PolicyTests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Native policy tests failed.' }
        & cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX (Join-Path $source 'IntegrationTests.cpp') /Fe:IntegrationTests.exe /link advapi32.lib
        if ($LASTEXITCODE -ne 0) { throw 'Native integration test build failed.' }
        & ./IntegrationTests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Native integration tests failed.' }
        Copy-Item -LiteralPath "$env:WINDIR/System32/winmm.dll" -Destination winmm_real.dll -Force
        & cl.exe /nologo /O2 /MT /EHsc /W4 /WX (Join-Path $source 'ForwardingTests.cpp') /Fe:ForwardingTests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Forwarding test build failed.' }
        & ./ForwardingTests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Native forwarding tests failed.' }
    }
} finally { Pop-Location }
