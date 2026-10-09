using System.Diagnostics;
using System.Runtime.InteropServices;

// =========================================================================
// Doctor.cs
// `riscvcli doctor` — RISC-V 베어메탈 실습에 필요한 도구가 설치돼 있는지 점검한다.
// =========================================================================
internal static class Doctor
{
  /// <summary>GNU 툴체인 접두사 후보 (우선순위 순).</summary>
  public static readonly string[] GnuPrefixes =
  [
    "riscv64-unknown-elf-", // Ubuntu: gcc-riscv64-unknown-elf / 직접 빌드한 툴체인
    "riscv64-elf-",         // Homebrew: riscv64-elf-gcc, riscv64-elf-binutils
    "riscv64-linux-gnu-",   // Ubuntu: gcc-riscv64-linux-gnu (베어메탈 어셈블/링크에도 사용 가능)
  ];

  private static readonly string[] ExtraDirs =
  [
    "/opt/homebrew/bin", "/opt/homebrew/opt/llvm/bin", "/opt/homebrew/opt/lld/bin",
    "/usr/local/bin", "/usr/local/opt/llvm/bin", "/usr/local/opt/lld/bin",
  ];

  public static string? Which(string name)
  {
    var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
      .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
      .Concat(ExtraDirs);
    foreach (var dir in dirs)
    {
      var candidate = Path.Combine(dir, name);
      if (File.Exists(candidate)) return candidate;
    }
    return null;
  }

  private static string Capture(string exe, params string[] args)
  {
    try
    {
      var psi = new ProcessStartInfo(exe)
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      foreach (var a in args) psi.ArgumentList.Add(a);
      using var p = Process.Start(psi);
      if (p is null) return "";
      string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
      p.WaitForExit();
      return output;
    }
    catch
    {
      return "";
    }
  }

  private static void Row(bool ok, string label, string detail)
    => Console.WriteLine($"  {(ok ? "[ OK ]" : "[ -- ]")} {label,-24} {detail}");

  public static int Run()
  {
    bool mac = OperatingSystem.IsMacOS();
    Console.WriteLine("════════════════════════════════════════");
    Console.WriteLine($" riscvcli doctor — {(mac ? "macOS" : "Linux")} / {RuntimeInformation.OSArchitecture}");
    Console.WriteLine("════════════════════════════════════════");

    // --- 1. 어셈블러 + 링커 (GNU 또는 LLVM 중 하나면 충분) ---
    Console.WriteLine("\n[필수 1] 어셈블러/링커 툴체인 (둘 중 하나)");
    string? gnuFound = null;
    foreach (var prefix in GnuPrefixes)
    {
      var ld = Which(prefix + "ld");
      var gcc = Which(prefix + "gcc");
      var asx = Which(prefix + "as");
      if (ld is not null && (gcc is not null || asx is not null))
      {
        gnuFound = prefix;
        Row(true, "GNU " + prefix + "*", gcc ?? asx!);
        break;
      }
    }
    if (gnuFound is null) Row(false, "GNU riscv64-*-elf", "찾지 못함");

    var clang = Which("clang");
    var lld = Which("ld.lld");
    bool clangRiscv = clang is not null && Capture(clang, "--print-targets").Contains("riscv64");
    Row(clangRiscv && lld is not null, "LLVM clang + ld.lld",
        clang is null ? "clang 없음"
        : !clangRiscv ? "clang 에 RISC-V 타깃이 없음 (macOS 기본 clang 이 그래요)"
        : lld is null ? "ld.lld 없음" : $"{clang}, {lld}");

    bool toolchainOk = gnuFound is not null || (clangRiscv && lld is not null);

    // --- 2. QEMU ---
    Console.WriteLine("\n[필수 2] QEMU (system 모드)");
    var qemu64 = Which("qemu-system-riscv64");
    var qemu32 = Which("qemu-system-riscv32");
    Row(qemu64 is not null, "qemu-system-riscv64", qemu64 ?? "찾지 못함");
    Row(qemu32 is not null, "qemu-system-riscv32", qemu32 ?? "찾지 못함 (RV32 실습 시에만 필요)");

    // --- 3. 선택 도구 ---
    Console.WriteLine("\n[선택] 오케스트레이터 / 언어 라이브러리");
    var zig = Which("zig");
    Row(zig is not null, "zig", zig ?? "없음 (zig build run 을 쓸 때만 필요)");
    var dotnet = Which("dotnet");
    Row(dotnet is not null, "dotnet (SDK 10)", dotnet ?? "없음 (dotnet ./hun-build.cs 를 쓸 때만 필요)");
    var pwsh = Which("pwsh");
    Row(pwsh is not null, "pwsh (7+)", pwsh ?? "없음 (hun-build.ps1 을 쓸 때만 필요)");
    var cargo = Which("cargo");
    Row(cargo is not null, "cargo", cargo ?? "없음 (--rust 옵션을 쓸 때만 필요)");
    var rustup = Which("rustup");
    if (rustup is not null)
    {
      string installed = Capture(rustup, "target", "list", "--installed");
      Row(installed.Contains("riscv64gc-unknown-none-elf"), "rust target riscv64gc", "rustup target add riscv64gc-unknown-none-elf");
      Row(installed.Contains("riscv32imac-unknown-none-elf"), "rust target riscv32imac", "rustup target add riscv32imac-unknown-none-elf");
    }
    var gdb = Which("gdb-multiarch") ?? Which("riscv64-unknown-elf-gdb") ?? Which("riscv64-elf-gdb");
    Row(gdb is not null, "gdb (디버깅용)", gdb ?? "없음 (--gdb 로 디버깅할 때만 필요)");

    // --- 4. 설치 안내 ---
    bool qemuOk = qemu64 is not null;
    if (!toolchainOk || !qemuOk)
    {
      Console.WriteLine("\n설치 안내:");
      if (mac)
      {
        if (!toolchainOk) Console.WriteLine("  brew install riscv64-elf-binutils riscv64-elf-gcc");
        if (!qemuOk) Console.WriteLine("  brew install qemu");
      }
      else
      {
        var pkgs = new List<string>();
        if (!toolchainOk) pkgs.Add("gcc-riscv64-unknown-elf");
        if (!qemuOk) pkgs.Add("qemu-system-misc");
        Console.WriteLine($"  sudo apt update && sudo apt install -y {string.Join(' ', pkgs)}");
        Console.WriteLine("  (Ubuntu 에서 qemu-system-riscv64 는 qemu-system-misc 패키지에 들어 있어요)");
      }
    }

    Console.WriteLine();
    if (toolchainOk && qemuOk)
    {
      Console.WriteLine("준비 완료! riscvcli init -n HelloWorld -o . 로 시작해보세요.");
      return 0;
    }
    Console.WriteLine("필수 도구가 부족해요. 위 안내대로 설치한 뒤 다시 실행해주세요.");
    return 1;
  }
}
