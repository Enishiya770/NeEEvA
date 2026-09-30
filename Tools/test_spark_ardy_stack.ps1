<#
.SYNOPSIS
    Exercise Spark deployment and startup against isolated Linux fixtures.
.DESCRIPTION
    Requires SSH access to a Linux host with Bash. Imports functions through the
    PowerShell AST; never executes the controller's main entry point. All files
    and fake processes belong to a unique /tmp fixture. No model is loaded and
    no production service is started or stopped. Run with Windows PowerShell 5.1
    to also cover native argument quoting in the production SSH transport.
#>
[CmdletBinding()]
param(
    [string]$ArdySshHost = '192.168.114.163',
    [int]$ArdySshPort = 22,
    [string]$SshKeyPath = (Join-Path $env:USERPROFILE '.ssh\id_ed25519_dgx_spark')
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'neeeva_spark_ardy_stack.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors) { throw "Controller has PowerShell syntax errors: $parseErrors" }
foreach ($name in @('Bash-Quote', 'Invoke-NativeText', 'Invoke-SshText', 'Get-RemoteDeployCommand', 'Start-RemoteArdy', 'Assert-RemoteArdyRunning')) {
    $functionAst = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $functionAst) { throw "Missing controller function: $name" }
    Invoke-Expression $functionAst.Extent.Text
}
$realSsh = (Get-Item Function:Invoke-SshText).ScriptBlock

function Assert-TextContains([string]$Text, [string]$Expected, [string]$Context) {
    if (-not $Text.Contains($Expected)) { throw "$Context expected [$Expected], got [$Text]" }
}

function Assert-SshFailure([string]$Command, [int]$ExitCode, [string[]]$ExpectedText = @()) {
    $failure = $null
    try { & $realSsh $Command | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure) { throw "SSH command unexpectedly succeeded; expected exit $ExitCode" }
    Assert-TextContains $failure "(exit $ExitCode)" 'SSH failure'
    foreach ($expected in $ExpectedText) { Assert-TextContains $failure $expected 'SSH failure diagnostic' }
    if ($ErrorActionPreference -ne 'Stop') { throw 'SSH failure changed ErrorActionPreference' }
}

$fixture = ((& $realSsh 'mktemp -d /tmp/neeeva-ardy-stack-test.XXXXXXXX') -join '').Trim()
if ($fixture -notmatch '^/tmp/neeeva-ardy-stack-test\.[A-Za-z0-9]+$') {
    throw "Unexpected fixture path: $fixture"
}

# Only processes marked with this freshly allocated fixture path may be cleaned.
$cleanup = @'
set -eu
fixture={FIXTURE}
if [ -f "$fixture/children" ]; then
  while IFS= read -r p; do
    case "$p" in ""|*[!0-9]*|0|1) continue ;; esac
    line="$(tr '\0' ' ' 2>/dev/null < /proc/$p/cmdline || true)"
    case "$line" in *"$fixture"*) kill "$p" 2>/dev/null || true ;; esac
  done < "$fixture/children"
fi
rm -rf -- "$fixture"
'@
$cleanup = $cleanup.Replace('{FIXTURE}', (Bash-Quote $fixture))
try {
    $RemoteRoot = "$fixture/root with spaces"
    $RemoteCoreDir = "$RemoteRoot/checkpoints"
    $RemotePython = ''
    $ArdyPort = 8093
    $FeatureUrl = 'http://127.0.0.1:8890'
    $AllowIndependentFeatureModel = $false
    $AllowUnverifiedAdapter = $false

    # Real native stderr must reach the caller as text without becoming an exception.
    $warningOnly = @(& $realSsh "printf '%s\n' fixture-warning-only >&2; exit 0")
    Assert-TextContains ($warningOnly -join "`n") 'fixture-warning-only' 'SSH stderr-only success'
    $mixedOutput = @(& $realSsh "printf '%s\n' fixture-stdout; printf '%s\n' fixture-warning-mixed >&2; exit 0")
    Assert-TextContains ($mixedOutput -join "`n") 'fixture-stdout' 'SSH mixed success stdout'
    Assert-TextContains ($mixedOutput -join "`n") 'fixture-warning-mixed' 'SSH mixed success stderr'
    foreach ($line in @($warningOnly) + @($mixedOutput)) {
        if ($line -isnot [string]) { throw 'SSH success returned a native ErrorRecord instead of text' }
    }
    if ($ErrorActionPreference -ne 'Stop') { throw 'SSH success changed ErrorActionPreference' }
    Assert-SshFailure "printf '%s\n' fixture-failed-stdout; printf '%s\n' fixture-failed-stderr >&2; exit 17" 17 @('fixture-failed-stdout', 'fixture-failed-stderr')
    Assert-SshFailure "printf '%s\n' fixture-failed-without-stderr; exit 19" 19 @('fixture-failed-without-stderr')
    Assert-SshFailure 'exit 23' 23
    Write-Output 'PASS SSH warning-only/mixed success and nonzero exits preserve diagnostics and error preference'

    $futureArchive = "$RemoteRoot/.runtime/future source.tar.gz"
    $badArchive = "$RemoteRoot/.runtime/bad source.tar.gz"
    $vendorName = 'fixture-vendor'
    $vendor = "$RemoteRoot/vendor/$vendorName"
    $prepareArchives = @'
set -eu
fixture={FIXTURE}
root={ROOT}
mkdir -p "$fixture/source/Server/ARDY" "$root/.runtime" "$root/Server/ARDY/vendor" "$root/vendor/fixture-vendor"
printf '#!/usr/bin/env bash\nexit 0\n' > "$fixture/source/Server/ARDY/start_motion_service.sh"
touch -d '+1 hour' "$fixture/source/Server/ARDY/start_motion_service.sh"
tar -czf {FUTURE_ARCHIVE} -C "$fixture/source" Server
printf '%s\n' 'fixture deliberately invalid gzip archive' > {BAD_ARCHIVE}
'@
    $prepareArchives = $prepareArchives.Replace('{FIXTURE}', (Bash-Quote $fixture)).Replace('{ROOT}', (Bash-Quote $RemoteRoot))
    $prepareArchives = $prepareArchives.Replace('{FUTURE_ARCHIVE}', (Bash-Quote $futureArchive)).Replace('{BAD_ARCHIVE}', (Bash-Quote $badArchive))
    & $realSsh $prepareArchives | Out-Null

    # Execute the same generated deployment command used by Deploy-Source.
    $futureOutput = (& $realSsh (Get-RemoteDeployCommand $futureArchive $vendor $vendorName)) -join "`n"
    Assert-TextContains $futureOutput 'in the future' 'Future timestamp tar warning'
    $verifyDeploy = @'
set -eu
root={ROOT}
test -x "$root/Server/ARDY/start_motion_service.sh"
test -L "$root/Server/ARDY/vendor/fixture-vendor"
test "$(readlink "$root/Server/ARDY/vendor/fixture-vendor")" = "$root/vendor/fixture-vendor"
test ! -e {FUTURE_ARCHIVE}
echo 'PASS production deployment completes after future timestamp tar warning'
'@
    $verifyDeploy = $verifyDeploy.Replace('{ROOT}', (Bash-Quote $RemoteRoot)).Replace('{FUTURE_ARCHIVE}', (Bash-Quote $futureArchive))
    & $realSsh $verifyDeploy | Write-Output
    Assert-SshFailure (Get-RemoteDeployCommand $badArchive $vendor $vendorName) 2 @('tar:')
    & $realSsh ("test -f {0}" -f (Bash-Quote $badArchive)) | Out-Null
    Write-Output 'PASS production deployment preserves tar failure instead of masking it with cleanup'

    # Capture production-generated commands without running them yet.
    function Invoke-SshText([string]$Command) { $script:capturedCommand = $Command }
    Start-RemoteArdy
    $startCommand = $script:capturedCommand
    Assert-RemoteArdyRunning
    $assertCommand = $script:capturedCommand
    Assert-RemoteArdyRunning -IncludeLogs
    $logsCommand = $script:capturedCommand
    Set-Item Function:Invoke-SshText $realSsh

    $test = @'
set -eu
fixture={FIXTURE}
root={ROOT}
mkdir -p "$root/Server/ARDY" "$root/.runtime"
pid_file="$root/.runtime/ardy.pid"
err_log="$root/.runtime/ardy.err.log"

# This intentionally contains the quoting that Windows PowerShell used to lose.
empty=""
case "$empty" in ""|*[!0-9]*) ;; *) exit 90 ;; esac
quoted='literal "$value" and spaces'
[ "$quoted" = 'literal "$value" and spaces' ] || exit 91
echo 'PASS SSH multiline quote transport'

cat > "$root/Server/ARDY/start_motion_service.sh" <<'FAKE_START'
#!/usr/bin/env bash
set -eu
fixture="${NEEEVA_ARDY_ROOT%/root with spaces}"
printf '%s\n' "$$" >> "$fixture/children"
exec -a "fixture=$fixture python -m Server.ARDY.motion_service.app --port 8093" sleep 300
FAKE_START
chmod +x "$root/Server/ARDY/start_motion_service.sh"

run_start() (
{START}
)
run_assert() (
{ASSERT}
)
run_logs() (
{LOGS}
)
fail() { echo "FAIL: $*"; exit 1; }
assert_equal() { [ "$1" = "$2" ] || fail "expected [$2], got [$1]"; }
wait_for_fixture() {
  local p="$1" line i
  for i in $(seq 1 100); do
    line="$(tr '\0' ' ' 2>/dev/null < /proc/$p/cmdline || true)"
    case "$line" in *"fixture=$fixture"*) return 0 ;; esac
    sleep 0.02
  done
  fail "fixture process did not exec"
}
assert_started() {
  local output="$1" expected="$2" p
  assert_equal "$output" "$expected"
  p="$(cat "$pid_file")"
  wait_for_fixture "$p"
  run_assert || fail 'new ARDY fixture is not recognized'
}
spawn_fixture() {
  bash -c 'exec -a "$1" sleep 300' bash "fixture=$fixture $1" </dev/null >/dev/null 2>&1 &
  spawned=$!
  printf '%s\n' "$spawned" >> "$fixture/children"
  wait_for_fixture "$spawned"
}

assert_started "$(run_start)" started
first="$(cat "$pid_file")"
assert_equal "$(run_start)" reused
assert_equal "$(cat "$pid_file")" "$first"
echo 'PASS missing PID starts; matching live process is reused'

expected=$'cleared-stale-pid\nstarted'
for stale in '' invalid 0 1 2147483647; do
  printf '%s\n' "$stale" > "$pid_file"
  assert_started "$(run_start)" "$expected"
done
echo 'PASS empty, malformed, reserved and dead PIDs restart'

spawn_fixture 'unrelated-worker'
unrelated="$spawned"
printf '%s\n' "$unrelated" > "$pid_file"
assert_started "$(run_start)" "$expected"
kill -0 "$unrelated" || fail 'unrelated process was killed'
echo 'PASS unrelated live process is preserved'

spawn_fixture 'python -m Server.ARDY.motion_service.app --port 80930'
wrong_port="$spawned"
printf '%s\n' "$wrong_port" > "$pid_file"
assert_started "$(run_start)" "$expected"
kill -0 "$wrong_port" || fail 'different-port process was killed'
echo 'PASS port prefix is rejected without killing the process'

printf '%s\n' fixture-startup-error > "$err_log"
logs="$(run_logs)"
case "$logs" in *running=yes*fixture-startup-error*) ;; *) fail 'live timeout diagnostic lacks stderr' ;; esac
printf '%s\n' 2147483647 > "$pid_file"
set +e
logs="$(run_assert)"
status=$?
set -e
assert_equal "$status" 3
case "$logs" in *running=no*fixture-startup-error*) ;; *) fail 'dead process diagnostic lacks stderr' ;; esac
echo 'PASS exit detection and timeout diagnostics include remote stderr'
'@
    $test = $test.Replace('{FIXTURE}', (Bash-Quote $fixture)).Replace('{ROOT}', (Bash-Quote $RemoteRoot))
    $test = $test.Replace('{START}', $startCommand).Replace('{ASSERT}', $assertCommand).Replace('{LOGS}', $logsCommand)
    & $realSsh $test | Write-Output
} finally {
    & $realSsh $cleanup | Out-Null
}
