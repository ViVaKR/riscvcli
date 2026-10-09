// =========================================================================
// Templates.cs
//  - Arch      : RV64 / RV32 에 따라 달라지는 값 모음 (ISA 문자열, 로드/스토어 명령 등)
//  - Templates : `riscvcli new` 가 생성하는 단일 파일 템플릿
//
// 주의: 생성되는 .S 파일은 전처리기(cpp)를 통과할 수 있으므로
//       주석 줄을 영문 지시어(if, else, include, define ...)나 숫자로 시작하지 말고,
//       주석 안에 작은따옴표(')를 쓰지 않는다.
// =========================================================================

internal sealed record Arch(int Xlen)
{
    public string Name => $"RV{Xlen}";
    public string March => Xlen == 64 ? "rv64gc" : "rv32imac_zicsr";
    public string Mabi => Xlen == 64 ? "lp64d" : "ilp32";
    public string Qemu => $"qemu-system-riscv{Xlen}";
    public string RustTarget => Xlen == 64 ? "riscv64gc-unknown-none-elf" : "riscv32imac-unknown-none-elf";
    public string LdEmulation => Xlen == 64 ? "elf64lriscv" : "elf32lriscv";
    public string ZigArch => Xlen == 64 ? "riscv64" : "riscv32";

    /// <summary>레지스터 폭에 맞는 저장/로드 명령 (sd/ld, sw/lw)</summary>
    public string Store => Xlen == 64 ? "sd" : "sw";
    public string Load => Xlen == 64 ? "ld" : "lw";

    /// <summary>레지스터 하나의 바이트 수 (8 또는 4)</summary>
    public int Sz => Xlen / 8;
}

internal static class Templates
{
    public static readonly Dictionary<string, string> Descriptions = new()
    {
        ["bare"] = "프롤로그/에필로그 없는 최소형 (가장 기초 단계용)",
        ["function"] = "표준 함수 골격 — ra/s0 저장·복원 포함 (기본값)",
        ["loop"] = "카운터 기반 루프 골격이 잡힌 템플릿",
        ["uart"] = "UART(0x10000000)로 문자열을 출력하는 자체 완결형 예제",
    };

    public static string Render(string key, string fileName, string labelName, Arch a)
    {
        string label = labelName;
        string header = $"""
#-----------------------------------------------------
# RISC-V Assembly Template: {fileName}
# Target: {a.Name} ({a.March}, {a.Mabi}) — QEMU virt 베어메탈
# 심볼 접두사 없음 (ELF 관례)
#-----------------------------------------------------
""";

        // 함수 프롤로그/에필로그에서 쓰는 오프셋 (16바이트 프레임: ra, s0 저장)
        int raOff = 16 - a.Sz;
        int s0Off = 16 - 2 * a.Sz;

        string raSt = $"ra, {raOff}(sp)".PadRight(24);
        string s0St = $"s0, {s0Off}(sp)".PadRight(24);

        string prologue = $"""
    # --- Prologue ---
    addi    sp, sp, -16
    {a.Store}      {raSt}# Return address 저장
    {a.Store}      {s0St}# Frame pointer 저장
    addi    s0, sp, 16              # Frame pointer 설정
""";

        string epilogue = $"""
    # --- Epilogue ---
    {a.Load}      {raSt}# Return address 복원
    {a.Load}      {s0St}# Frame pointer 복원
    addi    sp, sp, 16
    ret                             # Return
""";

        return key switch
        {
            "bare" => $"""
{header}
    .section .text
    .global {label}
    .balign 4

{label}:
    # 프롤로그/에필로그 없는 최소형 — 스택을 건드리지 않는 짧은 코드에 적합
    li      a0, 0
    ret
""",

            "loop" => $"""
{header}
    .section .text
    .global {label}
    .balign 4

{label}:
{prologue}

    li      t0, 0                   # 카운터 초기화
    li      t1, 10                  # 반복 횟수 (예시)

.Lloop_start:
    bge     t0, t1, .Lloop_end      # 카운터 >= 반복 횟수 이면 종료

    # --- 루프 본문 ---
    addi    t0, t0, 1
    j       .Lloop_start

.Lloop_end:
    li      a0, 0                   # Return value (0)

{epilogue}
""",

            "uart" => $"""
{header}
# UART(NS16550A)는 QEMU virt 머신에서 0x10000000 에 매핑돼 있다.
#   +0 : THR (송신 버퍼)  — 여기에 바이트를 쓰면 터미널에 출력됨
#   +5 : LSR (상태)       — bit5(0x20)=1 이면 송신 준비 완료
    .section .text
    .global {label}
    .balign 4

{label}:
    li      t0, 0x10000000          # UART0 베이스 주소
    la      t1, .Lmsg               # 문자열 시작 주소

.Lnext_char:
    lbu     t2, 0(t1)               # 문자 하나 읽기
    beqz    t2, .Ldone              # NUL(0) 이면 끝

.Lwait_tx:
    lbu     t3, 5(t0)               # LSR 읽기
    andi    t3, t3, 0x20            # THRE 비트만 남기기
    beqz    t3, .Lwait_tx           # 아직 준비 안 됐으면 대기

    sb      t2, 0(t0)               # THR 에 쓰기 = 화면 출력
    addi    t1, t1, 1
    j       .Lnext_char

.Ldone:
    li      a0, 0                   # Return value (0)
    ret

    .section .rodata
.Lmsg:
    .asciz  "Hello, RISC-V!\n"
""",

            // function (기본값)
            _ => $"""
{header}
    .section .text
    .global {label}
    .balign 4

{label}:
{prologue}

    # --- Main Logic ---
    li      a0, 0                   # Return value (0)

{epilogue}
""",
        };
    }
}
