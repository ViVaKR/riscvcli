using System.Text;

// =========================================================================
// ProjectTemplates.cs
// riscvcli init 이 생성하는 파일들의 템플릿 모음
//   - AsmTemplates    : Boot.S / Main.S / platform.S / hun.macros.inc
//   - LinkerTemplates : link.ld (QEMU virt, RAM 0x80000000)
//   - RustTemplates   : no_std staticlib (UART 직접 접근)
//   - ZigTemplates    : build.zig (riscv freestanding 오케스트레이터)
//   - ReadmeTemplates / MiscTemplates
//
// 원시 문자열(""") 의 닫는 따옴표를 0열에 두어, 파일 내용이 들여쓰기 없이 그대로 출력된다.
// =========================================================================

internal static class AsmTemplates
{
    private static readonly string[] SavedRegs =
      ["ra", "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8", "s9", "s10", "s11"];

    // ---------------------------------------------------------------------
    // src/Boot.S — 리셋 직후 가장 먼저 실행되는 코드 (_start)
    // ---------------------------------------------------------------------
    public static string BootS(string projectName, Arch a) => $"""
#-----------------------------------------------------
# {projectName} — Boot (src/Boot.S)
# QEMU virt 머신은 -bios none 일 때 RAM 시작 주소 0x80000000 부터 실행한다.
# 링커 스크립트(link.ld)가 .text.init 을 맨 앞에 배치하므로 _start 가 그 주소가 된다.
# 하는 일: gp/sp 설정 -> BSS 0 으로 초기화 -> main 호출 -> 종료 코드로 QEMU 종료
#-----------------------------------------------------
    .section .text.init
    .global _start
    .balign 4

_start:
    # gp(global pointer) 설정 — 링커 relaxation 이 gp 기준 주소를 쓰기 때문에 필요
    .option push
    .option norelax
    la      gp, __global_pointer$
    .option pop

    # hart(코어) 0 번만 진행하고 나머지는 대기시킨다
    csrr    t0, mhartid
    bnez    t0, .Lpark

    # 스택 포인터를 RAM 맨 끝으로 (스택은 아래 방향으로 자란다)
    la      sp, __stack_top

    # BSS 영역을 0 으로 초기화
    la      t0, __bss_start
    la      t1, __bss_end
.Lbss_loop:
    bgeu    t0, t1, .Lbss_done
    {a.Store}      zero, 0(t0)
    addi    t0, t0, {a.Sz}
    j       .Lbss_loop
.Lbss_done:

    call    main                    # main 의 반환값(a0)이 종료 코드가 된다
    call    qemu_exit               # a0 = 종료 코드 (0 이면 성공)

.Lpark:
    wfi
    j       .Lpark
""";

    // ---------------------------------------------------------------------
    // src/Main.S — 사용자 진입점 (main)
    // ---------------------------------------------------------------------
    public static string MainS(string projectName, Arch a, bool withRust)
    {
        int raOff = 16 - a.Sz;
        int s0Off = 16 - 2 * a.Sz;
        string raSt = $"ra, {raOff}(sp)".PadRight(24);
        string s0St = $"s0, {s0Off}(sp)".PadRight(24);

        string rustCall = withRust
          ? $"""

    # --- Rust(no_std) 라이브러리 호출 — riscvcli init --rust 로 자동 활성화됨 ---
    call    rust_hello

    PRINT   msg_sum                 # "3 + 4 = "
    li      a0, 3
    li      a1, 4
    call    add_two_numbers         # Rust: a0=3, a1=4 -> 반환값 a0 = 7
    call    uart_put_dec            # 숫자를 10진수로 출력 (16진수는 uart_put_hex)
    PUTC    0x0a                    # 줄바꿈
"""
          : "";

        string rustData = withRust
          ? "\nmsg_sum:\n    .asciz  \"3 + 4 = \"\n"
          : "";

        return $"""
#-----------------------------------------------------
# {projectName} — Entry Point (src/Main.S)
# Target: {a.Name} ({a.March}, {a.Mabi}) — QEMU virt 베어메탈
# riscvcli init 으로 생성됨
#-----------------------------------------------------
    .include "src/includes/hun.macros.inc"

    CODE_SECTION
    .global main

main:
    # --- Prologue ---
    addi    sp, sp, -16
    {a.Store}      {raSt}# Return address 저장
    {a.Store}      {s0St}# Frame pointer 저장
    addi    s0, sp, 16              # Frame pointer 설정

    # --- Main Logic ---
    PRINT   msg_hello               # UART 로 문자열 출력 (src/libs/platform.S 의 uart_puts)
{rustCall}
    # 예) 레지스터 값을 16진수로 찍어보기:
    #   li      a0, 255
    #   call    uart_put_hex        # 0x00000000000000ff
    #   PUTC    0x0a

    li      a0, 0                   # Return value (0) = QEMU 종료 코드

    # --- Epilogue ---
    {a.Load}      {raSt}# Return address 복원
    {a.Load}      {s0St}# Frame pointer 복원
    addi    sp, sp, 16
    ret                             # Boot.S 로 돌아가면 QEMU 가 종료된다

    RODATA_SECTION
msg_hello:
    .asciz  "Hello, RISC-V ({projectName})!\n"{rustData}
""";
    }

    // ---------------------------------------------------------------------
    // src/libs/platform.S — QEMU virt 플랫폼 런타임 (UART, 종료)
    // ---------------------------------------------------------------------
    public static string PlatformS(Arch a)
    {
        int raOff = 32 - a.Sz;
        return $"""
#-----------------------------------------------------
# platform.S — QEMU virt 플랫폼 런타임
#   uart_putc    : a0 = 문자 하나 출력
#   uart_puts    : a0 = NUL 로 끝나는 문자열 주소
#   uart_put_hex : a0 = 값을 0x... 16진수로 출력
#   uart_put_dec : a0 = 값을 부호 없는 10진수로 출력
#   qemu_exit    : a0 = 종료 코드 (0 이면 성공) — 돌아오지 않음
# 모두 호출 규약(a0~a7 인자, t0~t6 임시)을 지키므로 일반 함수처럼 call 하면 된다.
#-----------------------------------------------------
    .include "src/includes/hun.macros.inc"

    CODE_SECTION

    .global uart_putc
uart_putc:
    li      t0, UART0_BASE
    UART_WAIT t0, t1
    sb      a0, 0(t0)
    ret

    .global uart_puts
uart_puts:
    li      t0, UART0_BASE
.Lputs_next:
    lbu     t2, 0(a0)
    beqz    t2, .Lputs_done
    UART_WAIT t0, t1
    sb      t2, 0(t0)
    addi    a0, a0, 1
    j       .Lputs_next
.Lputs_done:
    ret

    .global uart_put_hex
uart_put_hex:
    li      t0, UART0_BASE
    UART_WAIT t0, t1
    li      t1, 0x30                # 문자 0
    sb      t1, 0(t0)
    UART_WAIT t0, t1
    li      t1, 0x78                # 문자 x
    sb      t1, 0(t0)
    li      t2, {a.Xlen - 4}                  # 시프트 양 (최상위 니블부터 출력)
.Lhex_loop:
    srl     t3, a0, t2
    andi    t3, t3, 0xf
    addi    t3, t3, 0x30            # 0~9 를 문자로
    li      t4, 0x39
    ble     t3, t4, .Lhex_emit
    addi    t3, t3, 39              # 10~15 는 a~f 로 (0x61 - 0x3a)
.Lhex_emit:
    UART_WAIT t0, t1
    sb      t3, 0(t0)
    addi    t2, t2, -4
    bgez    t2, .Lhex_loop
    ret

    .global uart_put_dec
uart_put_dec:
    addi    sp, sp, -32
    {a.Store}      ra, {raOff}(sp)
    addi    t2, sp, 23              # 문자 버퍼 끝 (sp+0 ~ sp+23)
    sb      zero, 0(t2)             # NUL 종단
    li      t3, 10
.Ldec_loop:
    remu    t4, a0, t3              # 나머지 = 마지막 자릿수
    divu    a0, a0, t3              # 몫 = 나머지 자릿수들
    addi    t4, t4, 0x30
    addi    t2, t2, -1
    sb      t4, 0(t2)
    bnez    a0, .Ldec_loop
    mv      a0, t2
    call    uart_puts
    {a.Load}      ra, {raOff}(sp)
    addi    sp, sp, 32
    ret

# QEMU 의 sifive_test 장치(0x100000)에 값을 쓰면 에뮬레이터가 종료된다.
#   성공 종료 : 0x5555 를 쓴다 (QEMU 프로세스 종료 코드 0)
#   실패 종료 : (코드 << 16) | 0x3333 을 쓴다 (QEMU 프로세스 종료 코드 = 코드)
    .global qemu_exit
qemu_exit:
    li      t0, SYSCON_BASE
    beqz    a0, .Lexit_pass
    slli    a0, a0, 16
    li      t1, SYSCON_FAIL
    or      a0, a0, t1
    sw      a0, 0(t0)
    j       .Lexit_hang
.Lexit_pass:
    li      t1, SYSCON_PASS
    sw      t1, 0(t0)
.Lexit_hang:
    wfi
    j       .Lexit_hang
""";
    }

    // ---------------------------------------------------------------------
    // src/includes/hun.macros.inc — 공용 매크로 (GNU as / LLVM 공용 문법)
    // ---------------------------------------------------------------------
    public static string HunMacrosInc(Arch a)
    {
        int sz = a.Sz;
        int frameMin = (13 * sz + 15) / 16 * 16; // ra + s0~s11 = 13개 레지스터, 16바이트 정렬

        var save = new List<string>();
        var restore = new List<string>();
        for (int i = 0; i < SavedRegs.Length; i++)
        {
            save.Add($"        {a.Store}      {SavedRegs[i],-3}, {i * sz}(sp)");
            restore.Add($"        {a.Load}      {SavedRegs[i],-3}, {i * sz}(sp)");
        }
        string saveBlock = string.Join("\n", save);
        string restoreBlock = string.Join("\n", restore);

        int raOff = 16 - sz;
        int s0Off = 16 - 2 * sz;

        return $"""
# =================================================
#  제목: 공용 매크로 모음 (hun.macros.inc) — RISC-V {a.Name}
#  목적: 섹션 선언, 함수 프롤로그/에필로그, UART 출력을 짧게 쓰기 위한 매크로
#        GNU as 와 LLVM(clang) 양쪽에서 동작하도록 .macro / .endm 만 사용
# =================================================
    .ifndef HUN_MACROS_INC
    .set    HUN_MACROS_INC, 1

# --- 플랫폼 상수 (QEMU -machine virt) ---
    .equ    XLEN,           {a.Xlen}
    .equ    SZREG,          {sz}
    .equ    UART0_BASE,     0x10000000      # NS16550A UART
    .equ    UART_LSR,       5               # Line Status Register 오프셋
    .equ    UART_LSR_THRE,  0x20            # 송신 버퍼 비어 있음 비트
    .equ    SYSCON_BASE,    0x100000        # sifive_test (종료 장치)
    .equ    SYSCON_PASS,    0x5555
    .equ    SYSCON_FAIL,    0x3333

# ------------------------------------------------------
# [섹션 선언] 링커 스크립트(link.ld)의 섹션 이름과 맞춰져 있다.
#   .text   : 실행 코드 (명령어는 4바이트 정렬)
#   .rodata : 읽기 전용 데이터 (문자열, 상수 테이블)
#   .data   : 초기값이 있는 변수
#   .bss    : 0 으로 초기화되는 변수 (파일 용량을 차지하지 않음)
# ------------------------------------------------------
    .macro CODE_SECTION
        .section .text
        .balign 4
    .endm
    # 사용 예)
    #   CODE_SECTION
    #   add_one:
    #       addi    a0, a0, 1       # a0(인자) + 1 을 a0(반환값)에 저장
    #       ret

    .macro RODATA_SECTION
        .section .rodata
        .balign 8
    .endm
    # 사용 예)
    #   RODATA_SECTION
    #   msg:        .asciz "안녕, RISC-V!\n"
    #   fib_table:  .dword 0, 1, 1, 2, 3, 5, 8, 13

    .macro DATA_SECTION
        .section .data
        .balign 8
    .endm
    # 사용 예)
    #   DATA_SECTION
    #   counter:    .dword 0        # 실행 중 값이 바뀌는 전역 변수
    #   hp:         .word 100

    .macro BSS_SECTION
        .section .bss
        .balign 8
    .endm
    # 사용 예)
    #   BSS_SECTION
    #   input_buffer: .skip 256     # 256바이트 버퍼, 0 으로 초기화됨

# ------------------------------------------------------
# [UART 출력] 임시 레지스터 t0, t1 을 사용한다 (호출 규약상 마음대로 써도 되는 레지스터)
# ------------------------------------------------------
    # UART 송신 준비가 될 때까지 대기. base 에는 UART 베이스 주소가 든 레지스터를 준다.
    .macro UART_WAIT base, tmp
.Luart_wait_\@:
        lbu     \tmp, UART_LSR(\base)
        andi    \tmp, \tmp, UART_LSR_THRE
        beqz    \tmp, .Luart_wait_\@
    .endm

    # 문자 하나 출력 (t0, t1 사용). 사용 예) PUTC 0x41  -> A
    .macro PUTC ch
        li      t0, UART0_BASE
        UART_WAIT t0, t1
        li      t1, \ch
        sb      t1, 0(t0)
    .endm

    # NUL 종단 문자열 출력 (a0 사용, uart_puts 를 call 하므로 ra 가 덮어써진다)
    # 호출하는 함수가 프롤로그에서 ra 를 저장해 둔 상태여야 한다.
    .macro PRINT label
        la      a0, \label
        call    uart_puts
    .endm

    # 종료 코드를 지정하고 QEMU 종료 (돌아오지 않음)
    .macro EXIT code
        li      a0, \code
        call    qemu_exit
    .endm

# --- [함수 프롤로그 / 에필로그] --- #

# ========================================================================
# [기본형] ra, s0(프레임 포인터) 만 저장하는 16바이트 프레임
#   사용 예)  FUNC_START  my_func
#             ...
#             FUNC_END
# ========================================================================
    .macro FUNC_START name
        .global \name
        .balign 4
\name:
        addi    sp, sp, -16
        {a.Store}      ra, {raOff}(sp)
        {a.Store}      s0, {s0Off}(sp)
        addi    s0, sp, 16
    .endm

    .macro FUNC_END
        {a.Load}      ra, {raOff}(sp)
        {a.Load}      s0, {s0Off}(sp)
        addi    sp, sp, 16
        ret
    .endm

# ========================================================================
# [풀 스펙] ra, s0~s11 전원 백업 (callee-saved 레지스터 12개 + ra)
# RISC-V 규칙상 sp 는 16바이트 정렬이어야 하므로 stack_size 는 16의 배수,
# 최소 {frameMin} 바이트 (= 레지스터 13개 x {sz}바이트, 16바이트 올림).
#   사용 예)  FUNC_START_FULL big_func, {frameMin}
#             ...
#             FUNC_EXIT_FULL {frameMin}
# ========================================================================
    .macro FUNC_START_FULL name, stack_size
        .if     \stack_size < {frameMin}
            .error "FUNC_START_FULL: stack_size 는 최소 {frameMin} 이상이어야 합니다 (ra + s0~s11 저장)"
        .endif
        .if     (\stack_size & 15) != 0
            .error "FUNC_START_FULL: stack_size 는 16의 배수여야 합니다 (RISC-V sp 정렬 규칙)"
        .endif
        .global \name
        .balign 4
\name:
        addi    sp, sp, -\stack_size
{saveBlock}
        addi    s0, sp, \stack_size
    .endm

    .macro FUNC_EXIT_FULL stack_size
{restoreBlock}
        addi    sp, sp, \stack_size
        ret
    .endm

    .endif
""";
    }
}

internal static class LinkerTemplates
{
    public static string LinkLd() => """
/* =========================================================================
 * link.ld — QEMU virt 머신용 베어메탈 링커 스크립트
 *   - QEMU virt 의 RAM 은 0x80000000 부터 시작한다 (기본 크기 128MB)
 *   - -bios none 으로 실행하면 이 주소의 첫 명령부터 실행되므로
 *     _start(.text.init)를 반드시 맨 앞에 배치한다.
 * ========================================================================= */
OUTPUT_ARCH(riscv)
ENTRY(_start)

MEMORY
{
    RAM (rwx) : ORIGIN = 0x80000000, LENGTH = 128M
}

/* 세그먼트를 권한별로 분리한다 (코드 R+X / 상수 R / 데이터 RW).
 * 하나로 합치면 RWX 세그먼트가 생겨서 최신 ld 가 경고를 출력한다. */
PHDRS
{
    text   PT_LOAD FLAGS(5);   /* R + X */
    rodata PT_LOAD FLAGS(4);   /* R     */
    data   PT_LOAD FLAGS(6);   /* R + W */
}

SECTIONS
{
    .text : ALIGN(4)
    {
        *(.text.init)
        *(.text .text.*)
    } > RAM :text

    .rodata : ALIGN(8)
    {
        *(.rodata .rodata.* .srodata .srodata.*)
    } > RAM :rodata

    .data : ALIGN(8)
    {
        __data_start = .;
        *(.data .data.*)
        . = ALIGN(8);
        __global_pointer$ = . + 0x800;   /* gp 는 .sdata 중간을 가리킨다 (relaxation 용) */
        *(.sdata .sdata.*)
        __data_end = .;
    } > RAM :data

    .bss (NOLOAD) : ALIGN(8)
    {
        __bss_start = .;
        *(.sbss .sbss.* .bss .bss.* COMMON)
        . = ALIGN(8);
        __bss_end = .;
    } > RAM :data

    /* 스택은 RAM 맨 끝에서 아래로 자란다 */
    __stack_top = ORIGIN(RAM) + LENGTH(RAM);
}
""";
}

internal static class RustTemplates
{
    public static string CargoToml(string projectName, Arch a) => $"""
[package]
name = "rust_core"
version = "0.1.0"
edition = "2021"
description = "{projectName} 용 Rust 코어 라이브러리 — no_std, {a.RustTarget} (riscvcli init 생성)"

[lib]
name = "rust_core"
crate-type = ["staticlib"]

[dependencies]

[profile.dev]
panic = "abort"

[profile.release]
panic = "abort"
opt-level = "s"
""";

    public static string LibRs() => """
#![no_std]

mod console;
pub use console::*;

use core::panic::PanicInfo;

/// 두 정수를 더한 합계를 반환한다.
/// RISC-V 호출 규약: a0 = a, a1 = b 로 전달받고, 결과를 a0 으로 반환한다.
#[no_mangle]
pub extern "C" fn add_two_numbers(a: i64, b: i64) -> i64 {
    a.wrapping_add(b)
}

/// no_std 에서는 panic 처리기를 직접 정의해야 한다.
#[panic_handler]
fn panic(_info: &PanicInfo) -> ! {
    loop {}
}
""";

    public static string ConsoleRs() => """
use core::ffi::c_char;
use core::ptr::{read_volatile, write_volatile};

// QEMU virt 의 NS16550A UART — 어셈블리의 uart_putc 와 같은 일을 Rust 로 한다.
const UART0_THR: *mut u8 = 0x1000_0000 as *mut u8;
const UART0_LSR: *const u8 = 0x1000_0005 as *const u8;
const LSR_THRE: u8 = 0x20;

fn putc(byte: u8) {
    unsafe {
        while read_volatile(UART0_LSR) & LSR_THRE == 0 {}
        write_volatile(UART0_THR, byte);
    }
}

fn puts(s: &str) {
    for b in s.bytes() {
        putc(b);
    }
}

/// 기본 인사 함수 — 프로젝트 스캐폴딩이 잘 연결됐는지 확인용
#[no_mangle]
pub extern "C" fn rust_hello() {
    puts("Hello from Rust (rust_core)!\n");
}

/// 어셈블리에서 넘어온 NUL 종단 C 문자열을 출력하고 줄바꿈한다.
/// msg 는 유효한 NUL 종단 문자열을 가리켜야 한다.
#[no_mangle]
pub unsafe extern "C" fn rust_println(msg: *const c_char) {
    if !msg.is_null() {
        let mut p = msg as *const u8;
        while *p != 0 {
            putc(*p);
            p = p.add(1);
        }
    }
    putc(b'\n');
}
""";
}

internal static class ZigTemplates
{
    public static string BuildZig(string projectName, Arch a, bool withRust)
    {
        string name = projectName.ToLowerInvariant();
        string features = a.Xlen == 64
          ? ".m, .a, .f, .d, .c, .zicsr"
          : ".m, .a, .c, .zicsr";

        string rustBlock = !withRust ? "" : $$"""

    // --- Rust(no_std) 라이브러리 (app/RustLibs/rust_core) ---
    // 사전 준비: rustup target add {{a.RustTarget}}
    const rust_build = b.addSystemCommand(&.{
        "cargo",
        "build",
        "--release",
        "--target",
        "{{a.RustTarget}}",
        "--manifest-path",
        "app/RustLibs/rust_core/Cargo.toml",
    });
    exe.step.dependOn(&rust_build.step);
    exe.root_module.addObjectFile(b.path("app/RustLibs/rust_core/target/{{a.RustTarget}}/release/librust_core.a"));
""";

        return $$"""
const std = @import("std");

// {{projectName}} — Zig 오케스트레이터 (RISC-V 베어메탈, QEMU virt)
// Zig 가 내장 clang/lld 로 어셈블 + 링크하므로 riscv 툴체인을 따로 설치하지 않아도 된다.
// QEMU(qemu-system-riscv{{a.Xlen}}) 만 있으면 된다.
// (riscvcli init 으로 생성됨 — zig 0.16 기준, 다른 버전에서는 API가 다를 수 있으니 확인해줘)

// 어셈블리 소스 목록 — 새 .S 파일을 추가하면 여기에도 한 줄 추가해줘.
const asm_sources = [_][]const u8{
    "src/Boot.S",
    "src/Main.S",
    "src/libs/platform.S",
};

pub fn build(b: *std.Build) void {
    const rv = std.Target.riscv;
    const target = b.resolveTargetQuery(.{
        .cpu_arch = .{{a.ZigArch}},
        .os_tag = .freestanding,
        .cpu_features_add = rv.featureSet(&.{ {{features}} }),
    });
    const optimize = b.standardOptimizeOption(.{});

    const exe = b.addExecutable(.{
        .name = "{{name}}.elf",
        .root_module = b.createModule(.{
            .target = target,
            .optimize = optimize,
            // 0x80000000 주소 대역은 medlow 로 표현할 수 없어서 medany(=medium) 필수
            .code_model = .medium,
            .single_threaded = true,
        }),
    });

    for (asm_sources) |src| {
        exe.root_module.addCSourceFile(.{ .file = b.path(src), .flags = &.{} });
    }
    exe.root_module.addIncludePath(b.path("."));
    exe.setLinkerScript(b.path("link.ld"));
{{rustBlock}}
    b.installArtifact(exe);

    // --- zig build run : QEMU 로 실행 (종료: 프로그램이 스스로 끝남, 강제 종료는 Ctrl-A X) ---
    const qemu_base = [_][]const u8{
        "qemu-system-riscv{{a.Xlen}}", "-machine", "virt", "-m", "128M", "-smp", "1", "-nographic", "-bios", "none",
    };

    const run_cmd = b.addSystemCommand(&qemu_base);
    run_cmd.addArg("-kernel");
    run_cmd.addArtifactArg(exe);
    run_cmd.step.dependOn(b.getInstallStep());
    const run_step = b.step("run", "{{projectName}} 를 QEMU 에서 실행");
    run_step.dependOn(&run_cmd.step);

    // --- zig build debug : QEMU 를 멈춘 채로 시작하고 gdb 를 기다린다 (tcp::1234) ---
    const debug_cmd = b.addSystemCommand(&qemu_base);
    debug_cmd.addArgs(&.{ "-S", "-s", "-kernel" });
    debug_cmd.addArtifactArg(exe);
    debug_cmd.step.dependOn(b.getInstallStep());
    const debug_step = b.step("debug", "QEMU 를 gdb 대기 상태로 시작 (gdb: target remote :1234)");
    debug_step.dependOn(&debug_cmd.step);
}
""";
    }
}

internal static class MiscTemplates
{
    public static string Gitignore() => """
# 빌드 결과물
bin/
zig-out/
.zig-cache/
zig-cache/

# Rust
app/RustLibs/rust_core/target/
""";
}

internal static class ReadmeTemplates
{
    public static string ProjectReadme(string projectName, Arch a, bool withRust, bool withPwsh)
    {
        string rustLine = withRust
          ? $"- **Rust (no_std)** — `app/RustLibs/rust_core` (staticlib, `rust_hello`, `add_two_numbers`) — 사전 준비: `rustup target add {a.RustTarget}`"
          : "- (언어 라이브러리 없음 — 순수 어셈블리 프로젝트. `--rust` 로 Rust 라이브러리를 추가할 수 있어요)";
        string pwshRow = withPwsh ? "\n├── hun-build.ps1          # 오케스트레이터 (PowerShell 7)" : "";
        string rustTree = withRust ? "\n├── app/RustLibs/rust_core/   # no_std Rust 라이브러리" : "";
        string pwshRun = withPwsh ? "\n\n**PowerShell 오케스트레이터** (`hun-build.ps1`):\n```bash\npwsh ./hun-build.ps1\n```" : "";

        return $"""
# {projectName}

`riscvcli init` 으로 생성된 RISC-V 베어메탈 학습용 프로젝트 ({a.Name}, `{a.March}` / `{a.Mabi}`).
OS 없이 **QEMU virt 머신**에서 직접 실행되며, Ubuntu 와 Apple Silicon Mac 에서 똑같이 동작한다.

## 동작 원리

1. QEMU 가 `-bios none -kernel x.elf` 로 ELF 를 RAM `0x80000000` 에 올리고 첫 명령부터 실행
2. `src/Boot.S` 의 `_start` 가 스택/BSS 를 준비한 뒤 `main` 호출
3. `src/Main.S` 의 `main` 이 UART(`0x10000000`)로 문자 출력
4. `main` 이 돌아오면 `qemu_exit` 가 종료 장치(`0x100000`)에 값을 써서 QEMU 종료

## 구성

{rustLine}

## 디렉토리

```
{projectName}/
├── link.ld                # 링커 스크립트 (RAM 0x80000000, 스택/BSS 심볼)
├── build.zig              # 오케스트레이터 (Zig)
├── hun-build.cs           # 오케스트레이터 (.NET file-based app){pwshRow}{rustTree}
└── src/
    ├── Boot.S             # _start — 리셋 직후 코드
    ├── Main.S             # main — 여기서부터 작성
    ├── libs/platform.S    # uart_putc/puts/put_hex/put_dec, qemu_exit
    ├── includes/hun.macros.inc   # 섹션/함수/UART 매크로
    ├── constants/
    └── data/
```

## 필요한 도구

- **QEMU** — `qemu-system-riscv{a.Xlen}`
  - Ubuntu: `sudo apt install qemu-system-misc`
  - macOS: `brew install qemu`
- **어셈블러/링커** (아래 중 하나)
  - Ubuntu: `sudo apt install gcc-riscv64-unknown-elf`
  - macOS: `brew install riscv64-elf-binutils riscv64-elf-gcc`
  - clang + ld.lld (RISC-V 타깃이 있는 LLVM, macOS 는 `brew install llvm lld`)
  - Zig 오케스트레이터를 쓰면 위 툴체인 없이 Zig 만으로도 된다
- 오케스트레이터 중 하나: **Zig** / **.NET SDK 10** / **PowerShell 7**

설치 상태는 `riscvcli doctor` 로 한 번에 점검할 수 있다.

## 빌드 & 실행

**Zig 오케스트레이터**:
```bash
zig build run
```

**.NET 오케스트레이터** (`hun-build.cs`, 툴체인 자동 탐지: GNU `riscv64-unknown-elf-` → `riscv64-elf-` → `riscv64-linux-gnu-` → clang/lld):
```bash
dotnet ./hun-build.cs              # 빌드 + 실행
dotnet ./hun-build.cs --no-run     # 빌드만
dotnet ./hun-build.cs --gdb        # QEMU 를 gdb 대기 상태로 시작
dotnet ./hun-build.cs --clean      # bin/ 삭제
```{pwshRun}

## gdb 로 한 줄씩 따라가기

터미널 1: `dotnet ./hun-build.cs --gdb` (또는 `zig build debug`)

터미널 2:
```bash
gdb-multiarch bin/{projectName.ToLowerInvariant()}.elf     # macOS: riscv64-elf-gdb
(gdb) set architecture riscv:rv{a.Xlen}
(gdb) target remote :1234
(gdb) break main
(gdb) continue
(gdb) stepi          # 명령어 한 개씩 실행
(gdb) info registers a0 a1 sp ra
```

## 참고

- QEMU 를 강제로 끝내려면 `Ctrl-A` 누른 뒤 `X`
- `src/` 아래 `.S`/`.s` 파일은 `hun-build.cs`/`hun-build.ps1` 이 자동으로 모두 어셈블한다.
  `build.zig` 는 `asm_sources` 목록에 직접 추가해야 한다.
- 새 파일은 `riscvcli new src/Foo -t function -n foo` 로 만들 수 있다.
""";
    }
}
