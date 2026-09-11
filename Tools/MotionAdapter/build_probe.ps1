$ErrorActionPreference = 'Stop'
$Lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'upstream-lock.json') -Raw | ConvertFrom-Json
$Source = Join-Path $PSScriptRoot ('runtime\llama.cpp-' + $Lock.llama_cpp)
$Build = Join-Path $PSScriptRoot 'runtime\probe-build'
New-Item -ItemType Directory -Force -Path $Build | Out-Null
$Vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$VS = & $Vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $VS) { throw 'MSVC x64 build tools are required.' }
$VCVars = Join-Path $VS 'VC\Auxiliary\Build\vcvars64.bat'
$Cpp = Join-Path $PSScriptRoot 'qwen_feature_probe.cpp'
$Exe = Join-Path $Build 'qwen_feature_probe.exe'
# This batch only initializes MSVC and compiles fixed source paths; no filesystem deletion/moving.
$Batch = Join-Path $Build 'compile.cmd'
$Text = @(
    '@echo off',
    ('call "' + $VCVars + '"'),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /utf-8 /std:c++17 /O2 /EHsc /D_CRT_SECURE_NO_WARNINGS /I"' + $Source + '\include" /I"' + $Source + '\ggml\include" "' + $Cpp + '" /Fe:"' + $Exe + '" /Fo:"' + $Build + '\probe.obj"'),
    'exit /b %errorlevel%'
)
[IO.File]::WriteAllLines($Batch, $Text, [Text.Encoding]::ASCII)
& cmd /d /c $Batch
if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed.' }
Write-Output $Exe
