#!/usr/bin/env pwsh
<#
.SYNOPSIS
    riscvcli 릴리스 자동화 스크립트 (PowerShell 전단 정예 에디션)
.DESCRIPTION
    1. csproj 버전 갱신
    2. .NET Native AOT (osx-arm64) 바이너리 빌드
    3. tar.gz 압축 및 SHA256 해시 계산
    4. Git 태그 및 GitHub Release 발행
    5. homebrew-riscvcli 탭 레포 자동 갱신 및 릴리스 배포
.EXAMPLE
    ./release.ps1 0.1.0
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0, HelpMessage = "새로운 릴리스 버전을 입력하세요 (예: 0.1.0)")]
  [ValidatePattern('^\d+\.\d+\.\d+$')]
  [string]$NewVersion
)

# ------------------------------------------------------------------------------
# 0. 엄격 모드 활성화 (set -euo pipefail 완벽 대체)
# ------------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $true

$Tag = "v$NewVersion"
$Rid = "osx-arm64"
$DevRepoDir = $PWD.Path
$TapRepoDir = [System.IO.Path]::GetFullPath((Join-Path $DevRepoDir "../homebrew-riscvcli"))
$AssetName = "riscvcli-$Tag-$Rid.tar.gz"
$GitHubRepo = "ViVaKR/riscvcli"
$TapGitHubRepo = "ViVaKR/homebrew-riscvcli"

if (-not (Test-Path (Join-Path $DevRepoDir '.git')) -or -not (Test-Path (Join-Path $DevRepoDir 'riscvcli.csproj'))) {
  throw "riscvcli 레포 루트에서 실행해주세요."
}

Write-Host "════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " 🚀 riscvcli 릴리스 가동: $Tag" -ForegroundColor Green
Write-Host " 📂 개발 레포: $DevRepoDir"
Write-Host " 📂 탭 레포:   $TapRepoDir"
Write-Host "════════════════════════════════════════"

# ------------------------------------------------------------------------------
# 1. 개발 레포: 버전 번호 갱신 (csproj)
# ------------------------------------------------------------------------------
$csproj = Join-Path $DevRepoDir "riscvcli.csproj"
if (-not (Test-Path $csproj)) {
  throw "!! $csproj 파일을 찾을 수 없습니다. 경로를 확인해주세요."
}

Write-Host "==> 1. csproj 버전을 $NewVersion 으로 갱신" -ForegroundColor Yellow
(Get-Content $csproj) -replace '<Version>.*</Version>', "<Version>$NewVersion</Version>" | Set-Content $csproj
Select-String -Path $csproj -Pattern '<Version>' | ForEach-Object { Write-Host "    $($_.Line.Trim())" -ForegroundColor Gray }

# ------------------------------------------------------------------------------
# 2. AOT 게시(publish) — 단일 바이너리 생성
# ------------------------------------------------------------------------------
Write-Host "`n==> 2. dotnet publish (AOT, $Rid)" -ForegroundColor Yellow
Remove-Item -Recurse -Force bin, obj, publish_out -ErrorAction SilentlyContinue

dotnet publish -c Release -r $Rid -o "publish_out"

$binPath = Join-Path $DevRepoDir "publish_out/riscvcli"
if (-not (Test-Path $binPath)) {
  throw "!! $binPath 가 생성되지 않았습니다. dotnet publish 로그를 확인해주세요."
}

# ------------------------------------------------------------------------------
# 3. tar.gz 압축 + SHA256 계산 (mktemp 없이 바로 직격 사격!)
# ------------------------------------------------------------------------------
Write-Host "`n==> 3. 압축 및 sha256 계산" -ForegroundColor Yellow
$tarOutPath = Join-Path $DevRepoDir $AssetName

# publish_out 폴더 안의 riscvcli 바이너리만 콕 집어서 tar 아카이브 생성
tar -czf $tarOutPath -C "publish_out" riscvcli

# 순정 파워쉘 Get-FileHash로 sha256 추출 (awk 불필요!)
$Sha256 = (Get-FileHash -Path $tarOutPath -Algorithm SHA256).Hash.ToLower()

Write-Host "    자산 파일: $AssetName" -ForegroundColor Green
Write-Host "    sha256:    $Sha256" -ForegroundColor Green

# ------------------------------------------------------------------------------
# 4. 개발 레포: 커밋 + 태그 + push
# ------------------------------------------------------------------------------
Write-Host "`n==> 4. 개발 레포 커밋/태그/push" -ForegroundColor Yellow
git add $csproj
git commit -m "release: v$NewVersion`n`n- riscvcli.csproj 버전을 $NewVersion 으로 갱신`n- $Rid 대상 AOT 바이너리 게시 준비"
git tag -a $Tag -m "riscvcli $Tag"
git push origin main
git push origin $Tag

# ------------------------------------------------------------------------------
# 5. GitHub Release 생성 + 바이너리 업로드
# ------------------------------------------------------------------------------
if (Get-Command gh -ErrorAction SilentlyContinue) {
  Write-Host "`n==> 5. GitHub Release 생성 및 자산 업로드 (gh CLI)" -ForegroundColor Yellow
  gh release create $Tag $tarOutPath `
    --repo $GitHubRepo `
    --title "riscvcli $Tag" `
    --notes "riscvcli $Tag — 자동 릴리스 (release.ps1)`n`n뜻을 펴지 못하매, 내 이를 어여삐 여겨 자유의 실리콘 RISC-V 빗장을 열어 반포하노라. 🪷 Freedom of Architecture."
}
else {
  Write-Warning "!! gh CLI가 설치되어 있지 않습니다. 수동으로 릴리스를 올려주세요:"
  Write-Host "   1) https://github.com/$GitHubRepo/releases/new (태그: $Tag)"
  Write-Host "   2) 첨부 파일: $tarOutPath"
  Read-Host "   업로드를 완료하셨으면 [Enter]를 눌러 계속 진행하십시오..."
}

# ------------------------------------------------------------------------------
# 6. 탭 레포: riscvcli.rb Formula 갱신
# ------------------------------------------------------------------------------
Write-Host "`n==> 6. homebrew-riscvcli 레포의 riscvcli.rb 갱신" -ForegroundColor Yellow
if (-not (Test-Path $TapRepoDir)) {
  throw "!! $TapRepoDir 디렉토리를 찾을 수 없습니다. 탭 레포지토리 위치를 확인해주세요."
}

$rbFile = Join-Path $TapRepoDir "riscvcli.rb"
$newUrl = "https://github.com/$GitHubRepo/releases/download/$Tag/$AssetName"

# Here-String 문법으로 깔끔하게 Ruby Formula 작성 (출사표 설명문 장착!)
$formulaContent = @"
class Riscvcli < Formula
  desc "RISC-V Assembly Template Generator CLI for Students"
  homepage "https://github.com/$GitHubRepo"
  url "$newUrl"
  sha256 "$Sha256"
  version "$NewVersion"

  def install
    bin.install "riscvcli"
  end
end
"@

Set-Content -Path $rbFile -Value $formulaContent -Encoding utf8
Write-Host "    $rbFile 갱신 완료:" -ForegroundColor Green
Get-Content $rbFile | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }

# ------------------------------------------------------------------------------
# 7. 탭 레포: 커밋 + push 및 Release 발행
# ------------------------------------------------------------------------------
Write-Host "`n==> 7. 탭 레포 커밋/push 및 $Tag 릴리스 자동 선포" -ForegroundColor Yellow
Push-Location $TapRepoDir
try {
  # 기존 로컬 태그 충돌 방지 클린업
  try { git tag -d $Tag 2>$null | Out-Null } catch { }

  git add riscvcli.rb
  git commit -m "chore: bump riscvcli to $Tag`n`n- url/sha256/version 을 $Tag 릴리스 자산 기준으로 갱신`n- 자산: $AssetName`n- sha256: $Sha256"

  # 원격 푸시 및 태그 전송
  git push origin main
  git tag -a $Tag -m "homebrew-riscvcli $Tag 릴리스"
  git push origin $Tag

  # 탭 레포 웹에도 공식 릴리스 선포!
  if (Get-Command gh -ErrorAction SilentlyContinue) {
    gh release create $Tag `
      --repo $TapGitHubRepo `
      --target main `
      --title "riscvcli $Tag" `
      --notes "homebrew-riscvcli $Tag — Homebrew 공식 자산 갱신 완료"
  }
}
finally {
  Pop-Location
}

# ------------------------------------------------------------------------------
# 8. 정리 및 완료 검증 안내
# ------------------------------------------------------------------------------
Remove-Item $tarOutPath -Force -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $DevRepoDir "publish_out") -ErrorAction SilentlyContinue

Write-Host "`n════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " 🎉 riscvcli 릴리스 $Tag 작전 완료!" -ForegroundColor Green
Write-Host ""
Write-Host " 검증 명령어:" -ForegroundColor Yellow
Write-Host "   brew uninstall riscvcli 2>/dev/null; brew untap $TapGitHubRepo 2>/dev/null"
Write-Host "   brew tap $TapGitHubRepo"
Write-Host "   brew install riscvcli"
Write-Host "   riscvcli --version   # $NewVersion 이 찍히는지 확인!"
Write-Host "════════════════════════════════════════"