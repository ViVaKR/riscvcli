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
//   --rv                    : 확장자를 생략했을 때 .rv(전처리기 없음)로 생성 (기본은 .riscv)
//   --force                 : 기존 파일 있어도 덮어쓰기
//   --stdout                : 파일로 쓰지 않고 터미널에만 출력
// -----------------------------------------------------------------------

string labelName = Path.GetFileNameWithoutExtension(fileNameArg);
string templateKey = "function";
string outputDir = ".";
int xlen = 64;
bool force = false;
bool toStdout = false;
bool plain = false;

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
    case "--rv":
      plain = true;
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
//   .riscv / .rv (그리고 구 .S / .s) 를 명시하면 그대로 존중
//     .riscv : 전처리기(cpp) 통과 — #include / #define 사용 가능 (구 .S)
//     .rv    : 전처리 없이 순수 어셈블 (구 .s)
//   확장자 없이 치면 .riscv 를 기본값으로 쓰고, --rv 를 주면 .rv 를 쓴다
// -----------------------------------------------------------------------
string fileName = AsmExt.IsSource(fileNameArg)
    ? fileNameArg
    : fileNameArg + (plain ? AsmExt.Plain : AsmExt.Preprocessed);

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
      riscvcli new <filename> [-n <label>] [-t <template>] [-o <dir>] [--xlen 64|32] [--rv] [--force] [--stdout]
      riscvcli init -n <ProjectName> [-o <dir>] [--xlen 64|32] [--rust] [--pwsh] [--force]
      riscvcli doctor
      riscvcli list
      riscvcli --version

    새 파일 예시:
      riscvcli new hello                     # hello.riscv, function 템플릿, RV64
      riscvcli new hello.rv -t bare          # 전처리기 안 거치는 최소형 템플릿
      riscvcli new hello --rv                # 확장자 생략 + 전처리 없음 -> hello.rv
      riscvcli new count -t loop --xlen 32   # RV32 문법(sw/lw)으로 생성
      riscvcli new say -t uart --stdout      # UART 출력 예제를 터미널에 미리보기만
      riscvcli new HelloWorld -n main -o .   # ./HelloWorld.riscv, 라벨은 main

    새 프로젝트 예시:
      riscvcli init -n HelloWorld -o .                 # Boot.S + Main.S + link.ld + Zig/.NET 오케스트레이터
      riscvcli init -n HelloWorld -o . --rust --pwsh   # Rust(no_std) 라이브러리, PowerShell 오케스트레이터 추가
      riscvcli init -n Hello32 -o . --xlen 32          # RV32 (riscv32imac, ilp32)

    실행 환경 점검:
      riscvcli doctor                        # 툴체인 / QEMU 설치 여부와 설치 명령 안내

    파일 확장자 (GitHub Linguist 에서 Assembly 로 인식 — init 이 .gitattributes 도 만들어줘요):
      .riscv      어셈블리 소스, 전처리기(cpp) 통과 — #include / #define 사용 가능  (기본값)
      .rv         어셈블리 소스, 전처리 없이 순수 어셈블
      .rvmacros   매크로 모음 (.macro / .endm)
      .rvinclude  상수 / 심볼 인클루드 (.equ)
    """);
}
