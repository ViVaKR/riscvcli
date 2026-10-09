using System.Text;

// =========================================================================
// ProjectInit.cs
// `riscvcli init` — RISC-V 베어메탈(QEMU virt) 학습용 프로젝트 골격을 생성한다.
//   Boot.S(_start) + Main.S(main) + platform.S(UART/종료) + link.ld
//   + Zig / .NET(/PowerShell) 오케스트레이터 + (선택) Rust no_std 라이브러리
// =========================================================================
internal static class ProjectInit
{
  public static void Run(string[] args)
  {
    string? projectNameArg = null;
    string outputDir = ".";
    int xlen = 64;
    bool force = false;
    bool withRust = false; // armcli 와 달리 기본 꺼짐 — rustup target 추가 설치가 필요하기 때문
    bool withPwsh = false;

    for (int i = 1; i < args.Length; i++)
    {
      switch (args[i])
      {
        case "-n" when i + 1 < args.Length:
          projectNameArg = args[++i];
          break;
        case "-o" when i + 1 < args.Length:
          outputDir = args[++i];
          break;
        case "--xlen" when i + 1 < args.Length:
          if (!int.TryParse(args[++i], out xlen) || (xlen != 64 && xlen != 32))
          {
            Console.WriteLine("Error: --xlen 은 64 또는 32 만 가능해.");
            return;
          }
          break;
        case "--force":
          force = true;
          break;
        case "--rust":
          withRust = true;
          break;
        case "--no-rust":
          withRust = false;
          break;
        case "--pwsh":
          withPwsh = true;
          break;
        case "--no-pwsh":
          withPwsh = false;
          break;
        case "--go":
        case "--dotnet":
          Console.WriteLine($"Error: {args[i]} 는 riscvcli 에서 지원하지 않아.");
          Console.WriteLine("  Go / .NET Native AOT 는 베어메탈 RISC-V(OS 없음) 타깃으로 빌드할 수 없어서 제외했어.");
          Console.WriteLine("  언어 라이브러리는 Rust(no_std)만 제공해: --rust");
          return;
        case "--os" when i + 1 < args.Length:
          i++; // armcli 호환용 — RISC-V 는 항상 ELF 라 심볼 접두사 규칙이 필요 없어서 무시한다
          Console.WriteLine("Note: --os 는 riscvcli 에서 의미가 없어서 무시했어 (항상 ELF, 접두사 없음).");
          break;
      }
    }

    if (string.IsNullOrWhiteSpace(projectNameArg))
    {
      Console.WriteLine("Error: 프로젝트 이름이 필요해. -n <ProjectName> 으로 지정해줘.");
      Console.WriteLine("예) riscvcli init -n HelloWorld -o .");
      return;
    }

    var arch = new Arch(xlen);
    string pascalName = ToPascalCase(projectNameArg);
    string root = Path.Combine(outputDir, pascalName);

    if (Directory.Exists(root) && !force)
    {
      Console.WriteLine($"Error: {root} 디렉토리가 이미 존재해. --force 로 덮어써줘.");
      return;
    }

    Console.WriteLine("════════════════════════════════════════");
    Console.WriteLine($" riscvcli init: {pascalName}");
    Console.WriteLine($" 위치: {Path.GetFullPath(root)}");
    Console.WriteLine($" 대상: {arch.Name} ({arch.March}, {arch.Mabi}) — QEMU virt 베어메탈");
    Console.WriteLine($" 구성: Assembly(src/Boot.S, Main.S, libs/platform.S) + link.ld"
      + " + build.zig + hun-build.cs"
      + (withPwsh ? " + hun-build.ps1" : "")
      + (withRust ? " + Rust(no_std, app/RustLibs/rust_core)" : ""));
    Console.WriteLine("════════════════════════════════════════");

    CreateDirectories(root, withRust);

    // --- 링커 스크립트 / 오케스트레이터 ---
    File.WriteAllText(Path.Combine(root, "link.ld"), LinkerTemplates.LinkLd() + "\n");
    File.WriteAllText(Path.Combine(root, "build.zig"), ZigTemplates.BuildZig(pascalName, arch, withRust) + "\n");

    string hunCs = Path.Combine(root, "hun-build.cs");
    File.WriteAllText(hunCs, BuildTemplates.HunBuildCs(pascalName, arch) + "\n");
    MakeExecutable(hunCs);

    if (withPwsh)
    {
      string ps1 = Path.Combine(root, "hun-build.ps1");
      File.WriteAllText(ps1, BuildTemplates.HunBuildPs1(pascalName, arch) + "\n");
      MakeExecutable(ps1);
    }

    // --- Rust (no_std) ---
    if (withRust)
    {
      string rustCore = Path.Combine(root, "app", "RustLibs", "rust_core");
      File.WriteAllText(Path.Combine(rustCore, "Cargo.toml"), RustTemplates.CargoToml(pascalName, arch) + "\n");
      File.WriteAllText(Path.Combine(rustCore, "src", "lib.rs"), RustTemplates.LibRs() + "\n");
      File.WriteAllText(Path.Combine(rustCore, "src", "console.rs"), RustTemplates.ConsoleRs() + "\n");
    }

    // --- 어셈블리 ---
    File.WriteAllText(Path.Combine(root, "src", "Boot.S"), AsmTemplates.BootS(pascalName, arch) + "\n");
    File.WriteAllText(Path.Combine(root, "src", "Main.S"), AsmTemplates.MainS(pascalName, arch, withRust) + "\n");
    File.WriteAllText(Path.Combine(root, "src", "libs", "platform.S"), AsmTemplates.PlatformS(arch) + "\n");
    File.WriteAllText(Path.Combine(root, "src", "includes", "hun.macros.inc"), AsmTemplates.HunMacrosInc(arch) + "\n");

    // --- 빈 디렉토리 자리 표시(.gitkeep) ---
    foreach (var dir in new[] { "constants", "data" })
    {
      File.WriteAllText(Path.Combine(root, "src", dir, ".gitkeep"), "");
    }

    File.WriteAllText(Path.Combine(root, ".gitignore"), MiscTemplates.Gitignore() + "\n");
    File.WriteAllText(Path.Combine(root, "README.md"), ReadmeTemplates.ProjectReadme(pascalName, arch, withRust, withPwsh) + "\n");

    Console.WriteLine("생성 완료!");
    Console.WriteLine();
    Console.WriteLine("다음 단계:");
    Console.WriteLine($"  cd {root}");
    Console.WriteLine("  dotnet ./hun-build.cs         # 또는: zig build run");
    if (withPwsh) Console.WriteLine("  pwsh ./hun-build.ps1");
    Console.WriteLine();
    Console.WriteLine("도구가 없다면:  riscvcli doctor");
  }

  private static void CreateDirectories(string root, bool withRust)
  {
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(Path.Combine(root, "app", "scripts"));

    if (withRust)
      Directory.CreateDirectory(Path.Combine(root, "app", "RustLibs", "rust_core", "src"));

    foreach (var dir in new[] { "constants", "data", "includes", "libs" })
    {
      Directory.CreateDirectory(Path.Combine(root, "src", dir));
    }
  }

  private static void MakeExecutable(string path)
  {
    if (OperatingSystem.IsWindows()) return;
    File.SetUnixFileMode(path,
      UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
      | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
      | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
  }

  /// <summary>
  /// "hello-world", "hello_world", "hello world" 등을 "HelloWorld" 형태의
  /// 파스칼 케이스로 변환한다. 이미 파스칼/카멜 케이스인 입력은 그대로 유지한다.
  /// </summary>
  private static string ToPascalCase(string input)
  {
    var parts = input.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return input;

    var sb = new StringBuilder();
    foreach (var part in parts)
    {
      if (part.Length == 0) continue;
      sb.Append(char.ToUpperInvariant(part[0]));
      if (part.Length > 1) sb.Append(part[1..]);
    }
    return sb.ToString();
  }
}
