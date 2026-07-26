param(
    [string]$PythonPath = "",
    [switch]$SkipModels
)

$ErrorActionPreference = "Stop"
$svsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$vendorRoot = Join-Path $svsRoot "vendor"
$soulxRoot = Join-Path $vendorRoot "SoulX-Singer"
$venvRoot = Join-Path $svsRoot ".venv"

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 turns a native program's stderr into a
        # NativeCommandError when ErrorActionPreference is Stop. Native
        # programs communicate failure through their exit code, so capture
        # that code ourselves instead of aborting before it can be checked.
        $ErrorActionPreference = "Continue"
        # Keep stderr attached to the console. Redirecting it through the
        # PowerShell pipeline wraps ordinary progress/warnings in a red
        # NativeCommandError record on Windows PowerShell 5.1.
        & $FilePath @ArgumentList | Out-Host
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "$FailureMessage (exit code: $exitCode)"
    }
}

function Test-Python310 {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$PrefixArguments = @()
    )

    if (-not (Test-Path -LiteralPath $FilePath) -and
        -not (Get-Command $FilePath -ErrorAction SilentlyContinue)) {
        return $false
    }

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & $FilePath @PrefixArguments -c "import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 10) else 1)" 2>&1 |
            Out-Null
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return $exitCode -eq 0
}

function Remove-ChildDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedParent
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullParent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\')
    if (-not $fullPath.StartsWith($fullParent + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unexpected path: $fullPath"
    }

    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Install-SoulXSource {
    if (Test-Path -LiteralPath (Join-Path $soulxRoot "requirements.txt")) {
        Write-Host "[SoulX] Reusing existing source: $soulxRoot"
        return
    }

    Remove-ChildDirectory -Path $soulxRoot -ExpectedParent $vendorRoot
    $gitCommand = Get-Command git -ErrorAction SilentlyContinue
    if ($gitCommand) {
        Write-Host "[SoulX] Downloading source with Git..."
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            & $gitCommand.Source clone --depth 1 https://github.com/Soul-AILab/SoulX-Singer.git $soulxRoot |
                Out-Host
            $cloneExitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($cloneExitCode -eq 0 -and
            (Test-Path -LiteralPath (Join-Path $soulxRoot "requirements.txt"))) {
            return
        }

        Write-Warning "GitHub clone failed. Switching to the official ZIP download."
        Remove-ChildDirectory -Path $soulxRoot -ExpectedParent $vendorRoot
    } else {
        Write-Warning "Git was not found. Using the official ZIP download."
    }

    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("neeeva-soulx-" + [Guid]::NewGuid().ToString("N"))
    $zipPath = Join-Path $tempRoot "SoulX-Singer.zip"
    $extractRoot = Join-Path $tempRoot "expanded"
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

    try {
        $zipUri = "https://codeload.github.com/Soul-AILab/SoulX-Singer/zip/refs/heads/main"
        $downloaded = $false
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Write-Host "[SoulX] Downloading official ZIP ($attempt/3)..."
                Invoke-WebRequest -UseBasicParsing -Uri $zipUri -OutFile $zipPath
                $downloaded = $true
                break
            } catch {
                if ($attempt -eq 3) {
                    throw "Both Git and the official ZIP download failed. Check the GitHub connection and retry. $($_.Exception.Message)"
                }
                Start-Sleep -Seconds (2 * $attempt)
            }
        }

        if (-not $downloaded) {
            throw "The SoulX-Singer source download did not complete."
        }

        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
        $expandedSource = Get-ChildItem -LiteralPath $extractRoot -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "requirements.txt") } |
            Select-Object -First 1
        if (-not $expandedSource) {
            throw "The downloaded SoulX-Singer ZIP has an unexpected layout."
        }

        Move-Item -LiteralPath $expandedSource.FullName -Destination $soulxRoot
    } finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

function Resolve-Python310 {
    if (-not [string]::IsNullOrWhiteSpace($PythonPath)) {
        if (-not (Test-Python310 -FilePath $PythonPath)) {
            throw "PythonPath must point to a Python 3.10 executable: $PythonPath"
        }
        return @{
            FilePath = $PythonPath
            Arguments = @()
        }
    }

    $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($pyLauncher) {
        if (-not (Test-Python310 -FilePath $pyLauncher.Source -PrefixArguments @("-3.10"))) {
            Write-Host "[Python] Python 3.10 is not installed. Installing it with the Python launcher..."
            Invoke-NativeChecked `
                -FilePath $pyLauncher.Source `
                -ArgumentList @("install", "3.10") `
                -FailureMessage "Python 3.10 installation failed"
        }

        if (-not (Test-Python310 -FilePath $pyLauncher.Source -PrefixArguments @("-3.10"))) {
            throw "Python 3.10 is still unavailable after installation."
        }

        return @{
            FilePath = $pyLauncher.Source
            Arguments = @("-3.10")
        }
    }

    foreach ($candidateName in @("python3.10", "python")) {
        $candidate = Get-Command $candidateName -ErrorAction SilentlyContinue
        if ($candidate -and (Test-Python310 -FilePath $candidate.Source)) {
            return @{
                FilePath = $candidate.Source
                Arguments = @()
            }
        }
    }

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host "[Python] Installing Python 3.10 with winget..."
        Invoke-NativeChecked `
            -FilePath $winget.Source `
            -ArgumentList @(
                "install",
                "--id", "Python.Python.3.10",
                "--exact",
                "--scope", "user",
                "--accept-package-agreements",
                "--accept-source-agreements"
            ) `
            -FailureMessage "Python 3.10 installation failed"

        $installedPython = Join-Path $env:LocalAppData "Programs\Python\Python310\python.exe"
        if (Test-Python310 -FilePath $installedPython) {
            return @{
                FilePath = $installedPython
                Arguments = @()
            }
        }
    }

    throw "Python 3.10 is required. Install it with 'py install 3.10', then run this installer again."
}

if (-not (Test-Path -LiteralPath $vendorRoot)) {
    New-Item -ItemType Directory -Path $vendorRoot -Force | Out-Null
}

Install-SoulXSource

$pythonExe = Join-Path $venvRoot "Scripts\python.exe"
if (-not (Test-Python310 -FilePath $pythonExe)) {
    if (Test-Path -LiteralPath $venvRoot) {
        Write-Warning "Removing an incomplete or incompatible virtual environment."
        Remove-ChildDirectory -Path $venvRoot -ExpectedParent $svsRoot
    }

    $bootstrapPython = Resolve-Python310
    Write-Host "[Python] Creating the SoulX Python 3.10 environment..."
    $venvArguments = @($bootstrapPython.Arguments) + @("-m", "venv", $venvRoot)
    Invoke-NativeChecked `
        -FilePath $bootstrapPython.FilePath `
        -ArgumentList $venvArguments `
        -FailureMessage "Failed to create the SoulX virtual environment"
}

if (-not (Test-Python310 -FilePath $pythonExe)) {
    throw "The SoulX virtual environment was not created correctly: $pythonExe"
}

Write-Host "[Python] Installing SoulX dependencies..."
Invoke-NativeChecked `
    -FilePath $pythonExe `
    -ArgumentList @("-m", "pip", "install", "--upgrade", "pip") `
    -FailureMessage "Failed to upgrade pip"
$useCudaTorch = (Get-Command nvidia-smi -ErrorAction SilentlyContinue) -and
    $env:NEEEVA_SVS_CPU_ONLY -ne "1"
Invoke-NativeChecked `
    -FilePath $pythonExe `
    -ArgumentList @("-m", "pip", "install", "--prefer-binary", "-r", (Join-Path $soulxRoot "requirements.txt")) `
    -FailureMessage "Failed to install SoulX-Singer requirements"
Invoke-NativeChecked `
    -FilePath $pythonExe `
    -ArgumentList @(
        "-m", "pip", "install",
        "fastapi",
        "uvicorn",
        "python-multipart",
        "soundfile",
        "lightning",
        "fiddle",
        "nemo_toolkit[asr]==2.6.1",
        "huggingface_hub",
        "hf_xet"
    ) `
    -FailureMessage "Failed to install the NeEEvA SVS bridge requirements"
if ($useCudaTorch) {
    Write-Host "[Python] Installing NeMo-compatible CUDA 12.1 PyTorch..."
    Invoke-NativeChecked `
        -FilePath $pythonExe `
        -ArgumentList @(
            "-m", "pip", "install",
            "--force-reinstall",
            "--no-deps",
            "--index-url", "https://download.pytorch.org/whl/cu121",
            "torch==2.4.1+cu121",
            "torchaudio==2.4.1+cu121"
        ) `
        -FailureMessage "Failed to install CUDA PyTorch for SoulX-Singer"
}
Invoke-NativeChecked `
    -FilePath $pythonExe `
    -ArgumentList @(
        "-m", "pip", "install",
        "--force-reinstall",
        "--no-deps",
        "numpy==1.26.4",
        "transformers==4.41.2",
        "tokenizers==0.19.1",
        "fsspec==2024.12.0",
        "packaging==24.2",
        "tqdm==4.67.1"
    ) `
    -FailureMessage "Failed to restore SoulX-compatible NumPy/Transformers pins"
Invoke-NativeChecked `
    -FilePath $pythonExe `
    -ArgumentList @(
        "-c",
        "import nltk; assert nltk.download('averaged_perceptron_tagger_eng'); assert nltk.download('cmudict')"
    ) `
    -FailureMessage "Failed to install SoulX English phoneme data"
if ($useCudaTorch) {
    Invoke-NativeChecked `
        -FilePath $pythonExe `
        -ArgumentList @(
            "-c",
            "import torch; assert torch.cuda.is_available(), 'CUDA PyTorch installed but no CUDA device is available'; print(torch.__version__, torch.cuda.get_device_name(0))"
        ) `
        -FailureMessage "SoulX CUDA runtime verification failed"
}

if (-not $SkipModels) {
    $hfExe = Join-Path $venvRoot "Scripts\hf.exe"
    if (-not (Test-Path -LiteralPath $hfExe)) {
        throw "The Hugging Face downloader was not installed: $hfExe"
    }

    $activeHfProcesses = @(
        Get-Process -Name "hf" -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [string]::Equals(
                        $_.Path,
                        $hfExe,
                        [StringComparison]::OrdinalIgnoreCase
                    )
                } catch {
                    $false
                }
            }
    )
    if ($activeHfProcesses.Count -gt 0) {
        $processIds = ($activeHfProcesses | ForEach-Object { $_.Id }) -join ", "
        throw "Another SoulX model download is still running (hf.exe PID: $processIds). Stop or wait for it before rerunning this installer."
    }

    # Slow or reset connections need longer than huggingface_hub's default
    # per-read timeout. Its own downloader already retries and resumes partial
    # files, so an outer retry loop would only risk two processes sharing one
    # cache lock.
    if ([string]::IsNullOrWhiteSpace($env:HF_HUB_DOWNLOAD_TIMEOUT)) {
        $env:HF_HUB_DOWNLOAD_TIMEOUT = "120"
    }
    if ([string]::IsNullOrWhiteSpace($env:HF_HUB_ETAG_TIMEOUT)) {
        $env:HF_HUB_ETAG_TIMEOUT = "30"
    }

    Push-Location -LiteralPath $soulxRoot
    try {
        Write-Host "[SoulX] Downloading the singing model (downloads can resume after interruption)..."
        Invoke-NativeChecked `
            -FilePath $hfExe `
            -ArgumentList @(
                "download",
                "Soul-AILab/SoulX-Singer",
                "model.pt",
                "--local-dir", "pretrained_models/SoulX-Singer"
            ) `
            -FailureMessage "Failed to download the SoulX-Singer model"

        Write-Host "[SoulX] Downloading preprocessing models..."
        Invoke-NativeChecked `
            -FilePath $hfExe `
            -ArgumentList @(
                "download",
                "Soul-AILab/SoulX-Singer-Preprocess",
                "--local-dir", "pretrained_models/SoulX-Singer-Preprocess"
            ) `
            -FailureMessage "Failed to download the SoulX preprocessing models"
    } finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "[OK] SoulX-Singer SVS is installed."
Write-Host "Start it with: .\start_svs_server.cmd"
