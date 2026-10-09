// =========================================================================
// BuildTemplates.cs
// riscvcli init 이 프로젝트 루트에 심어주는 오케스트레이터 스크립트 2종
//   - hun-build.cs  : .NET 10 file-based app  (dotnet ./hun-build.cs)
//   - hun-build.ps1 : PowerShell 7            (pwsh ./hun-build.ps1)
//
// 두 스크립트는 같은 순서로 동작한다.
//   1) (선택) Rust no_std 라이브러리 빌드
//   2) 툴체인 탐지: GNU riscv64-unknown-elf- → riscv64-elf- → riscv64-linux-gnu- → clang + ld.lld
//   3) src/ 아래 모든 .S/.s 어셈블 → link.ld 로 링크 → bin/<이름>.elf
//   4) qemu-system-riscv{64|32} -machine virt -bios none -kernel 로 실행
//
// 내용 안의 __XLEN__ / __PROJECT__ 토큰은 생성 시점에 치환된다.
// (스크립트 안에 중괄호가 많아서 문자열 보간 대신 단순 Replace 를 쓴다)
// =========================================================================

internal static class BuildTemplates
{
  public static string HunBuildCs(string projectName, Arch a)
    => HunBuildCsRaw.Replace("__XLEN__", a.Xlen.ToString()).Replace("__PROJECT__", projectName);

  public static string HunBuildPs1(string projectName, Arch a)
    => HunBuildPs1Raw.Replace("__XLEN__", a.Xlen.ToString()).Replace("__PROJECT__", projectName);

  private const string HunBuildCsRaw = """""
#!/usr/bin/env -S dotnet --
// =========================================================================
// [Hun-RISCV] .NET file-based 오케스트레이터 (hun-build.cs)
//   RISC-V 베어메탈(QEMU virt) 용: 어셈블 -> 링크 -> QEMU 실행
//   사용법:  dotnet ./hun-build.cs [--no-run] [--gdb] [--clean]
// =========================================================================
#:property TargetFramework=net10.0

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;

const int Xlen = __XLEN__;
const string ProjectName = "__PROJECT__";

string march = Xlen == 64 ? "rv64gc" : "rv32imac_zicsr";
string mabi = Xlen == 64 ? "lp64d" : "ilp32";
string ldEmulation = Xlen == 64 ? "elf64lriscv" : "elf32lriscv";
string rustTarget = Xlen == 64 ? "riscv64gc-unknown-none-elf" : "riscv32imac-unknown-none-elf";
string qemuName = $"qemu-system-riscv{Xlen}";

bool noRun = args.Contains("--no-run");
bool gdb = args.Contains("--gdb");
bool clean = args.Contains("--clean");

string binDir = "bin";
string objDir = Path.Combine(binDir, "obj");
string elf = Path.Combine(binDir, ProjectName.ToLowerInvariant() + ".elf");

Console.WriteLine($"[Hun-RISCV] {ProjectName} — RV{Xlen} 베어메탈 오케스트레이터");

if (!Directory.Exists("src") || !File.Exists("link.ld"))
{
    Console.Error.WriteLine("프로젝트 루트(src/ 와 link.ld 가 있는 폴더)에서 실행해줘.");
    return 1;
}

if (clean)
{
    if (Directory.Exists(binDir)) Directory.Delete(binDir, true);
    Console.WriteLine("bin/ 삭제 완료");
    return 0;
}

// --- 0단계: Rust(no_std) 라이브러리 (있으면 자동으로 함께 빌드) ---
string? rustLib = null;
string rustManifest = Path.Combine("app", "RustLibs", "rust_core", "Cargo.toml");
if (File.Exists(rustManifest))
{
    Console.WriteLine("\n▶ 0단계: Rust(no_std) 빌드");
    if (Which("cargo") is null)
    {
        Console.Error.WriteLine("cargo 를 찾지 못했어. https://rustup.rs 로 Rust 를 먼저 설치해줘.");
        return 1;
    }
    int rc = Exec("cargo", ["build", "--release", "--target", rustTarget, "--manifest-path", rustManifest]);
    if (rc != 0)
    {
        Console.Error.WriteLine($"cargo 빌드 실패. 타깃이 없다면:  rustup target add {rustTarget}");
        return rc;
    }
    rustLib = Path.Combine("app", "RustLibs", "rust_core", "target", rustTarget, "release", "librust_core.a");
    if (!File.Exists(rustLib))
    {
        Console.Error.WriteLine($"{rustLib} 를 찾지 못했어. cargo 로그를 확인해줘.");
        return 1;
    }
}

// --- 1단계: 툴체인 탐지 ---
var tc = DetectToolchain();
if (tc is null)
{
    PrintToolchainHelp();
    return 1;
}
Console.WriteLine($"\n▶ 툴체인: {tc.Name}");

// --- 2단계: 어셈블리 파일 수집 ---
var asmFiles = Directory.EnumerateFiles("src", "*", SearchOption.AllDirectories)
    .Where(f => f.EndsWith(".S", StringComparison.Ordinal) || f.EndsWith(".s", StringComparison.Ordinal))
    .Select(f => f.Replace('\\', '/'))
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToList();

if (asmFiles.Count == 0)
{
    Console.Error.WriteLine("src/ 아래에 어셈블리 파일(.S/.s)이 한 개도 없네!");
    return 1;
}

Directory.CreateDirectory(binDir);
if (Directory.Exists(objDir)) Directory.Delete(objDir, true);
Directory.CreateDirectory(objDir);

// --- 3단계: 어셈블 ---
Console.WriteLine("\n▶ 1단계: 어셈블");
var sw = Stopwatch.StartNew();
var objFiles = new List<string>();
foreach (var src in asmFiles)
{
    string obj = Path.Combine(objDir, src.Replace('/', '_') + ".o");
    var asmArgs = new List<string>(tc.AsmArgs);
    if (tc.UsesDriver)
    {
        // .S 는 전처리기(cpp)를 통과시키고, .s 는 그대로 어셈블한다
        asmArgs.Add("-x");
        asmArgs.Add(src.EndsWith(".S", StringComparison.Ordinal) ? "assembler-with-cpp" : "assembler");
    }
    asmArgs.AddRange(["-I", ".", "-o", obj, src]);
    if (Exec(tc.AsmExe, asmArgs) != 0) return 1;
    objFiles.Add(obj);
}

// --- 4단계: 링크 ---
Console.WriteLine("\n▶ 2단계: 링크 (link.ld)");
var ldArgs = new List<string>(tc.LdArgs) { "-T", "link.ld", "-nostdlib", "-o", elf };
ldArgs.AddRange(objFiles);
if (rustLib is not null) ldArgs.Add(rustLib);
if (Exec(tc.LdExe, ldArgs) != 0) return 1;
sw.Stop();

try { Directory.Delete(objDir, true); } catch { }
Console.WriteLine($"✔ 빌드 성공 ({sw.ElapsedMilliseconds}ms) -> ./{elf}");

if (noRun) return 0;

// --- 5단계: QEMU 실행 ---
string? qemu = Which(qemuName);
if (qemu is null)
{
    Console.Error.WriteLine($"{qemuName} 를 찾지 못했어.");
    Console.Error.WriteLine(OperatingSystem.IsMacOS()
        ? "  brew install qemu"
        : "  sudo apt install qemu-system-misc");
    return 1;
}

var qemuArgs = new List<string> { "-machine", "virt", "-m", "128M", "-smp", "1", "-nographic", "-bios", "none" };
if (gdb)
{
    qemuArgs.AddRange(["-S", "-s"]);
    Console.WriteLine("\n[gdb 모드] QEMU 가 첫 명령 앞에서 멈춰서 기다려. 다른 터미널에서:");
    Console.WriteLine($"  gdb-multiarch {elf}      (macOS: riscv64-elf-gdb)");
    Console.WriteLine($"  (gdb) set architecture riscv:rv{Xlen}");
    Console.WriteLine("  (gdb) target remote :1234");
}
qemuArgs.AddRange(["-kernel", elf]);

Console.WriteLine("\n▶ QEMU 실행 (강제 종료: Ctrl-A 누른 뒤 X)\n------------------------------------------------");
int exit = Exec(qemu, qemuArgs);
Console.WriteLine("------------------------------------------------\n종료 코드: " + exit);
return exit;


// =========================================================================
// 함수들
// =========================================================================
string? Which(string name)
{
    var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Concat(["/opt/homebrew/bin", "/opt/homebrew/opt/llvm/bin", "/opt/homebrew/opt/lld/bin",
                 "/usr/local/bin", "/usr/local/opt/llvm/bin", "/usr/local/opt/lld/bin"]);
    foreach (var dir in dirs)
    {
        var candidate = Path.Combine(dir, name);
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

string Capture(string exe, params string[] a)
{
    try
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var x in a) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o;
    }
    catch { return ""; }
}

int Exec(string exe, IEnumerable<string> a)
{
    var list = a.ToList();
    Console.WriteLine($"  $ {Path.GetFileName(exe)} {string.Join(' ', list.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
    foreach (var x in list) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    return p.ExitCode;
}

Toolchain? DetectToolchain()
{
    // 1순위: GNU 툴체인 (Ubuntu: riscv64-unknown-elf- / Homebrew: riscv64-elf- / Ubuntu: riscv64-linux-gnu-)
    foreach (var prefix in new[] { "riscv64-unknown-elf-", "riscv64-elf-", "riscv64-linux-gnu-" })
    {
        var ld = Which(prefix + "ld");
        if (ld is null) continue;

        var gcc = Which(prefix + "gcc");   // 있으면 gcc 드라이버로 .S 전처리까지 처리
        if (gcc is not null)
            return new Toolchain($"GNU {prefix}gcc", gcc, ["-march=" + march, "-mabi=" + mabi, "-g", "-c"], true, ld, ["-m", ldEmulation]);

        var asx = Which(prefix + "as");    // gcc 가 없으면 as 로 직접 (전처리 없음)
        if (asx is not null)
            return new Toolchain($"GNU {prefix}as (전처리 없음)", asx, ["-march=" + march, "-mabi=" + mabi, "-g"], false, ld, ["-m", ldEmulation]);
    }

    // 2순위: LLVM (clang 에 RISC-V 타깃이 있고 ld.lld 가 있을 때)
    var clang = Which("clang");
    var lld = Which("ld.lld");
    if (clang is not null && lld is not null && Capture(clang, "--print-targets").Contains($"riscv{Xlen}"))
        return new Toolchain("LLVM clang + ld.lld", clang, [$"--target=riscv{Xlen}-unknown-elf", "-march=" + march, "-mabi=" + mabi, "-g", "-c"], true, lld, ["-m", ldEmulation]);

    return null;
}

void PrintToolchainHelp()
{
    Console.Error.WriteLine("\nRISC-V 툴체인을 찾지 못했어. 아래 중 하나를 설치해줘.");
    if (OperatingSystem.IsMacOS())
    {
        Console.Error.WriteLine("  brew install riscv64-elf-binutils riscv64-elf-gcc qemu");
        Console.Error.WriteLine("  (또는 brew install llvm lld — 단 PATH 에 /opt/homebrew/opt/llvm/bin 필요)");
    }
    else
    {
        Console.Error.WriteLine("  sudo apt install gcc-riscv64-unknown-elf qemu-system-misc");
    }
    Console.Error.WriteLine("  (설치 상태 점검: riscvcli doctor)");
}

record Toolchain(string Name, string AsmExe, string[] AsmArgs, bool UsesDriver, string LdExe, string[] LdArgs);
""""";

  private const string HunBuildPs1Raw = """""
#!/usr/bin/env pwsh
#requires -Version 7.0
# =========================================================================
# [Hun-RISCV] PowerShell 오케스트레이터 (hun-build.ps1)
#   RISC-V 베어메탈(QEMU virt) 용: 어셈블 -> 링크 -> QEMU 실행
# =========================================================================
[CmdletBinding()]
param(
    [switch]$NoRun,   # 빌드만 하고 실행하지 않음
    [switch]$Gdb,     # QEMU 를 gdb 대기 상태(-S -s)로 시작
    [switch]$Clean    # bin/ 삭제 후 종료
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location $PSScriptRoot

$Xlen        = __XLEN__
$ProjectName = '__PROJECT__'

$march       = if ($Xlen -eq 64) { 'rv64gc' } else { 'rv32imac_zicsr' }
$mabi        = if ($Xlen -eq 64) { 'lp64d' } else { 'ilp32' }
$ldEmulation = if ($Xlen -eq 64) { 'elf64lriscv' } else { 'elf32lriscv' }
$rustTarget  = if ($Xlen -eq 64) { 'riscv64gc-unknown-none-elf' } else { 'riscv32imac-unknown-none-elf' }
$qemuName    = "qemu-system-riscv$Xlen"

function Find-Tool([string]$Name) {
    $cmd = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    foreach ($dir in '/opt/homebrew/bin', '/opt/homebrew/opt/llvm/bin', '/opt/homebrew/opt/lld/bin',
                     '/usr/local/bin', '/usr/local/opt/llvm/bin', '/usr/local/opt/lld/bin') {
        $p = Join-Path $dir $Name
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Invoke-Native {
    param([Parameter(Mandatory)][string]$Exe, [string[]]$ArgList = @())
    Write-Host ("  $ " + (Split-Path $Exe -Leaf) + ' ' + ($ArgList -join ' ')) -ForegroundColor DarkGray
    & $Exe @ArgList
    if ($LASTEXITCODE -ne 0) { throw "'$Exe' 실패 (exit $LASTEXITCODE)" }
}

function Find-Toolchain {
    # 1순위: GNU 툴체인
    foreach ($prefix in 'riscv64-unknown-elf-', 'riscv64-elf-', 'riscv64-linux-gnu-') {
        $ld = Find-Tool "${prefix}ld"
        if (-not $ld) { continue }
        $gcc = Find-Tool "${prefix}gcc"
        if ($gcc) {
            return [pscustomobject]@{ Name = "GNU ${prefix}gcc"; AsmExe = $gcc; UsesDriver = $true
                AsmArgs = @("-march=$march", "-mabi=$mabi", '-g', '-c'); LdExe = $ld; LdArgs = @('-m', $ldEmulation) }
        }
        $asx = Find-Tool "${prefix}as"
        if ($asx) {
            return [pscustomobject]@{ Name = "GNU ${prefix}as (전처리 없음)"; AsmExe = $asx; UsesDriver = $false
                AsmArgs = @("-march=$march", "-mabi=$mabi", '-g'); LdExe = $ld; LdArgs = @('-m', $ldEmulation) }
        }
    }
    # 2순위: LLVM (clang 에 RISC-V 타깃 + ld.lld)
    $clang = Find-Tool 'clang'
    $lld   = Find-Tool 'ld.lld'
    if ($clang -and $lld) {
        $targets = (& $clang --print-targets 2>&1) -join "`n"
        if ($targets -match "riscv$Xlen") {
            return [pscustomobject]@{ Name = 'LLVM clang + ld.lld'; AsmExe = $clang; UsesDriver = $true
                AsmArgs = @("--target=riscv$Xlen-unknown-elf", "-march=$march", "-mabi=$mabi", '-g', '-c'); LdExe = $lld; LdArgs = @('-m', $ldEmulation) }
        }
    }
    return $null
}

Write-Host "[Hun-RISCV] $ProjectName — RV$Xlen 베어메탈 오케스트레이터" -ForegroundColor Cyan

$binDir = Join-Path $PSScriptRoot 'bin'
$objDir = Join-Path $binDir 'obj'
$elf    = Join-Path 'bin' ($ProjectName.ToLower() + '.elf')

if ($Clean) { Remove-Item $binDir -Recurse -Force -ErrorAction SilentlyContinue; Write-Host 'bin/ 삭제 완료'; return }

# 0. Rust(no_std) 라이브러리 (있으면 자동으로 함께 빌드)
$rustLib = $null
$rustManifest = 'app/RustLibs/rust_core/Cargo.toml'
if (Test-Path $rustManifest) {
    Write-Host '▶ 0단계: Rust(no_std) 빌드' -ForegroundColor Yellow
    if (-not (Find-Tool 'cargo')) { throw 'cargo 를 찾지 못했어. https://rustup.rs 로 Rust 를 먼저 설치해줘.' }
    try {
        Invoke-Native cargo @('build', '--release', '--target', $rustTarget, '--manifest-path', $rustManifest)
    } catch {
        Write-Warning "타깃이 없다면:  rustup target add $rustTarget"
        throw
    }
    $rustLib = "app/RustLibs/rust_core/target/$rustTarget/release/librust_core.a"
    if (-not (Test-Path $rustLib)) { throw "$rustLib 를 찾지 못했어." }
}

# 1. 툴체인 탐지
$tc = Find-Toolchain
if (-not $tc) {
    Write-Error 'RISC-V 툴체인을 찾지 못했어.'
    if ($IsMacOS) { Write-Host '  brew install riscv64-elf-binutils riscv64-elf-gcc qemu' }
    else          { Write-Host '  sudo apt install gcc-riscv64-unknown-elf qemu-system-misc' }
    Write-Host '  (설치 상태 점검: riscvcli doctor)'
    exit 1
}
Write-Host "▶ 툴체인: $($tc.Name)" -ForegroundColor Yellow

# 2. 어셈블리 파일 수집 (src/ 아래 .S / .s)
$asmFiles = Get-ChildItem -Path 'src' -Recurse -File |
    Where-Object { $_.Extension -ceq '.S' -or $_.Extension -ceq '.s' } |
    ForEach-Object { ([System.IO.Path]::GetRelativePath($PSScriptRoot, $_.FullName)) -replace '\\', '/' } |
    Sort-Object { $_ } -CaseSensitive
if (-not $asmFiles) { Write-Warning '어셈블리 파일(.S/.s)이 없습니다.'; exit 1 }

New-Item -ItemType Directory -Force -Path $binDir | Out-Null
if (Test-Path $objDir) { Remove-Item $objDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $objDir | Out-Null

# 3. 어셈블
Write-Host '▶ 1단계: 어셈블' -ForegroundColor Yellow
$objFiles = @()
foreach ($src in $asmFiles) {
    $obj = Join-Path $objDir (($src -replace '/', '_') + '.o')
    $a = @() + $tc.AsmArgs
    if ($tc.UsesDriver) {
        # .S 는 전처리기(cpp)를 통과시키고, .s 는 그대로 어셈블한다
        $a += '-x'
        $a += $(if ($src.EndsWith('.S', [StringComparison]::Ordinal)) { 'assembler-with-cpp' } else { 'assembler' })
    }
    $a += @('-I', '.', '-o', $obj, $src)
    Invoke-Native $tc.AsmExe $a
    $objFiles += $obj
}

# 4. 링크
Write-Host '▶ 2단계: 링크 (link.ld)' -ForegroundColor Green
$ldArgs = @() + $tc.LdArgs + @('-T', 'link.ld', '-nostdlib', '-o', $elf) + $objFiles
if ($rustLib) { $ldArgs += $rustLib }
Invoke-Native $tc.LdExe $ldArgs

Remove-Item $objDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "✔ 빌드 성공 -> $elf" -ForegroundColor Cyan

if ($NoRun) { return }

# 5. QEMU 실행
$qemu = Find-Tool $qemuName
if (-not $qemu) {
    Write-Error "$qemuName 를 찾지 못했어."
    if ($IsMacOS) { Write-Host '  brew install qemu' } else { Write-Host '  sudo apt install qemu-system-misc' }
    exit 1
}

$qemuArgs = @('-machine', 'virt', '-m', '128M', '-smp', '1', '-nographic', '-bios', 'none')
if ($Gdb) {
    $qemuArgs += @('-S', '-s')
    Write-Host '[gdb 모드] QEMU 가 첫 명령 앞에서 멈춰서 기다려. 다른 터미널에서:'
    Write-Host "  gdb-multiarch $elf      (macOS: riscv64-elf-gdb)"
    Write-Host "  (gdb) set architecture riscv:rv$Xlen"
    Write-Host '  (gdb) target remote :1234'
}
$qemuArgs += @('-kernel', $elf)

Write-Host "`n▶ QEMU 실행 (강제 종료: Ctrl-A 누른 뒤 X)`n------------------------------------------------"
& $qemu @qemuArgs
$code = $LASTEXITCODE
Write-Host "------------------------------------------------`n종료 코드: $code"
exit $code
""""";
}
