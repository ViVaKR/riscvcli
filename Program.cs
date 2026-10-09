using System.Reflection;

// ---------------------------------------------------------------------
// --version / -v
// ---------------------------------------------------------------------
switch (args.Length)
{
  case > 0 when args[0] == "--version" || args[0] == "-v":
    {
      var version = Assembly.GetExecutingAssembly()
                            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                            .InformationalVersion ?? "0.1.0";
      Console.WriteLine($"riscvcli version {version}");
      return;
    }

  case > 0 when args[0] == "list":
    {
      Console.WriteLine("사용 가능한 템플릿:");
      foreach (var (key, desc) in Templates.Descriptions)
      {
        Console.WriteLine($"  {key,-10} {desc}");
      }
      return;
    }

  case > 0 when args[0] == "doctor":
    Environment.ExitCode = Doctor.Run();
    return;
  case > 0 when args[0] == "init":
    ProjectInit.Run(args);
    return;
  case > 0 when args[0] == "--help" || args[0] == "-h" || args[0] == "help":
    PrintUsage();
    return;
  default:
    break;
}

// ---------------------------------------------------------------------
// list — 사용 가능한 템플릿 종류 보여주기
// ---------------------------------------------------------------------
if (args.Length > 0 && args[0] == "list")
{
  Console.WriteLine("사용 가능한 템플릿:");
  foreach (var (key, desc) in Templates.Descriptions)
  {
    Console.WriteLine($"  {key,-10} {desc}");
  }
  return;
}

// ---------------------------------------------------------------------
// doctor — 툴체인 / QEMU / (선택) Zig, Rust 설치 상태 점검
// ---------------------------------------------------------------------
if (args.Length > 0 && args[0] == "doctor")
{
  Environment.ExitCode = Doctor.Run();
  return;
}

// ---------------------------------------------------------------------
// init — 완전한 프로젝트(오케스트레이터 + 부트 코드 + 링커 스크립트) 생성
// ---------------------------------------------------------------------
if (args.Length > 0 && args[0] == "init")
{
  ProjectInit.Run(args);
  return;
}

if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "help"))
{
  PrintUsage();
  return;
}

if (args.Length < 2 || args[0] != "new")
{
  PrintUsage();
  return;
}

string fileNameArg = args[1];

// -----------------------------------------------------------------------
// 옵션 파싱
//   -n <label>              : 라벨 이름 (기본값: 파일 이름)
//   -t <template>           : 템플릿 종류 (bare | function | loop | uart), 기본값 function
//   -o <dir>                : 출력 디렉토리 (기본값: 현재 디렉토리)
//   --xlen <64|32>          : RV64(기본) / RV32
//   --force                 : 기존 파일 있어도 덮어쓰기
//   --stdout                : 파일로 쓰지 않고 터미널에만 출력
// -----------------------------------------------------------------------

string labelName = Path.GetFileNameWithoutExtension(fileNameArg);
string templateKey = "function";
string outputDir = ".";
int xlen = 64;
bool force = false;
bool toStdout = false;

for (int i = 2; i < args.Length; i++)
{
  switch (args[i])
  {
    case "-n" when i + 1 < args.Length:
      labelName = args[++i];
      break;
    case "-t" when i + 1 < args.Length:
      templateKey = args[++i].ToLowerInvariant();
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
    case "--stdout":
      toStdout = true;
      break;
  }
}

if (!Templates.Descriptions.ContainsKey(templateKey))
{
  Console.WriteLine($"Error: 알 수 없는 템플릿 '{templateKey}'. 'riscvcli list'로 확인해줘.");
  return;
}

// -----------------------------------------------------------------------
// 파일 확장자 결정
//   .s / .S 를 명시하면 그대로 존중 (전처리기 통과 여부는 사용자 선택)
//   확장자 없이 치면 .S (전처리기 통과 버전)를 기본값으로 사용
// -----------------------------------------------------------------------
string fileName = fileNameArg.EndsWith(".S") || fileNameArg.EndsWith(".s")
    ? fileNameArg
    : $"{fileNameArg}.S";

if (!toStdout && !string.IsNullOrWhiteSpace(outputDir) && outputDir != ".")
{
  Directory.CreateDirectory(outputDir);
  fileName = Path.Combine(outputDir, fileName);
}

if (!force && !toStdout && File.Exists(fileName))
{
  Console.WriteLine($"Error: {fileName} 파일이 이미 존재합니다. (--force 로 덮어쓸 수 있어)");
  return;
}

var arch = new Arch(xlen);
string template = Templates.Render(templateKey, fileName, labelName, arch);

if (toStdout)
{
  Console.WriteLine(template);
  return;
}

File.WriteAllText(fileName, template + "\n");
Console.WriteLine($"Created: {fileName} (Label: {labelName}, Arch: {arch.Name}, Template: {templateKey})");
return;


// =========================================================================
static void PrintUsage()
{
  Console.WriteLine("""
    riscvcli — RISC-V 어셈블리 학습용 템플릿/프로젝트 생성기 (QEMU virt 베어메탈)

    Usage:
      riscvcli new <filename> [-n <label>] [-t <template>] [-o <dir>] [--xlen 64|32] [--force] [--stdout]
      riscvcli init -n <ProjectName> [-o <dir>] [--xlen 64|32] [--rust] [--pwsh] [--force]
      riscvcli doctor
      riscvcli list
      riscvcli --version

    새 파일 예시:
      riscvcli new hello                     # hello.S, function 템플릿, RV64
      riscvcli new hello.s -t bare           # 전처리기 안 거치는 최소형 템플릿
      riscvcli new count -t loop --xlen 32   # RV32 문법(sw/lw)으로 생성
      riscvcli new say -t uart --stdout      # UART 출력 예제를 터미널에 미리보기만
      riscvcli new HelloWorld -n main -o .   # ./HelloWorld.S, 라벨은 main

    새 프로젝트 예시:
      riscvcli init -n HelloWorld -o .                 # Boot.S + Main.S + link.ld + Zig/.NET 오케스트레이터
      riscvcli init -n HelloWorld -o . --rust --pwsh   # Rust(no_std) 라이브러리, PowerShell 오케스트레이터 추가
      riscvcli init -n Hello32 -o . --xlen 32          # RV32 (riscv32imac, ilp32)

    실행 환경 점검:
      riscvcli doctor                        # 툴체인 / QEMU 설치 여부와 설치 명령 안내
    """);
}
