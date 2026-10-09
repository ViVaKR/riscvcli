#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
    PowerShell.ModuleForge 를 빌드·검증하고 PowerShell Gallery 에 게시합니다.
.DESCRIPTION
    1. PowerShell.ModuleForge.psd1 에서 버전(ModuleVersion + Prerelease) 읽기
    2. Output/PowerShell.ModuleForge 로 깨끗하게 빌드 + 매니페스트 복사
    3. Test-ModuleManifest + 별도 pwsh 프로세스에서 Import 스모크 테스트
    4. 갤러리에 같은 버전이 이미 있는지 확인
    5. SecretStore 의 API 키로 Publish-PSResource
    6. (-Tag) git 태그 + push, gh 가 있으면 GitHub Release 까지
.EXAMPLE
    ./publish.ps1 -WhatIf       # 리허설: 빌드/검증만 하고 게시·태그는 하지 않음
.EXAMPLE
    ./publish.ps1 -Tag          # 게시 후 v0.1.0-alpha001 태그 + GitHub Release
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Repository = 'PSGallery',
    [string]$SecretName = 'ViVaKRKey',
    [switch]$Tag
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $true
Set-Location $PSScriptRoot

$moduleName = 'PowerShell.ModuleForge'
$csproj = Join-Path $PSScriptRoot 'PowerShell_ModuleForge.csproj'
$srcManifest = Join-Path $PSScriptRoot "$moduleName.psd1"
$outDir = Join-Path $PSScriptRoot 'Output' $moduleName      # 폴더 이름 = 모듈 이름 (게시 규칙)
$outManifest = Join-Path $outDir "$moduleName.psd1"

# ------------------------------------------------------------------
# 1. 버전 읽기
# ------------------------------------------------------------------
$data = Import-PowerShellDataFile $srcManifest
$psData = $data.PrivateData.PSData
$version = [string]$data.ModuleVersion
$prerelease = if ($psData.ContainsKey('Prerelease')) { [string]$psData.Prerelease } else { '' }
$fullVersion = if ($prerelease) { "$version-$prerelease" } else { $version }
$tagName = "v$fullVersion"

Write-Host "▶ $moduleName $fullVersion  →  $Repository" -ForegroundColor Cyan

# 태그를 달 거라면 게시 전에 미리 점검 (게시 후에 실패하면 곤란하므로)
if ($Tag) {
    if (git status --porcelain) { throw "커밋되지 않은 변경이 있습니다. 먼저 커밋하고 push 해주세요." }
    if (git tag -l $tagName) { throw "태그가 이미 있습니다: $tagName" }
}

# ------------------------------------------------------------------
# 2. 깨끗하게 빌드
# ------------------------------------------------------------------
Write-Host "▶ 빌드" -ForegroundColor Yellow
# 주의: -WhatIf 리허설에서도 로컬 빌드 산출물 작업은 실제로 수행해야 하므로 -WhatIf:$false 를 명시합니다.
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force -WhatIf:$false }
dotnet build $csproj -c Release -o $outDir

Copy-Item $srcManifest $outManifest -Force -WhatIf:$false
# 게시물에 필요 없는 파일 정리 (SMA 는 배포물에 섞이지 않게 해 두었으므로 DLL + psd1 만 남습니다)
Get-ChildItem $outDir -File |
    Where-Object { $_.Extension -eq '.pdb' -or $_.Name -like '*.deps.json' } |
    Remove-Item -Force -WhatIf:$false

# ------------------------------------------------------------------
# 3. 검증: 매니페스트 + 실제 Import (DLL 잠김 방지를 위해 별도 프로세스)
# ------------------------------------------------------------------
Write-Host "▶ 검증" -ForegroundColor Yellow
$null = Test-ModuleManifest -Path $outManifest

$exported = & pwsh -NoProfile -Command "Import-Module '$outManifest' -ErrorAction Stop; (Get-Command -Module $moduleName).Name"
if ($exported -notcontains 'New-ModuleForge') {
    throw "스모크 테스트 실패: New-ModuleForge 가 내보내지지 않았습니다. (결과: $exported)"
}
Write-Host "  ✔ Import 성공, 내보낸 명령: $($exported -join ', ')" -ForegroundColor Green

# ------------------------------------------------------------------
# 4. 같은 버전이 이미 올라가 있는지 확인 (갤러리는 같은 버전을 덮어쓰지 못함)
# ------------------------------------------------------------------
$existing = Find-PSResource -Name $moduleName -Repository $Repository -Version '*' -Prerelease -ErrorAction SilentlyContinue |
    Where-Object { "$($_.Version)" -eq $version -and "$($_.Prerelease)" -eq $prerelease }
if ($existing) { throw "이미 게시된 버전입니다: $fullVersion — psd1 의 버전을 올려주세요." }

# ------------------------------------------------------------------
# 5. 게시 (API 키는 SecretStore 에서 꺼내 쓰고 바로 지움)
# ------------------------------------------------------------------
if ($PSCmdlet.ShouldProcess("$moduleName $fullVersion", "$Repository 에 게시")) {
    $apiKey = Get-Secret -Name $SecretName -AsPlainText
    try {
        Publish-PSResource -Path $outDir -Repository $Repository -ApiKey $apiKey
    }
    finally {
        Remove-Variable apiKey -ErrorAction SilentlyContinue
    }
    Write-Host "✔ 게시 완료: $moduleName $fullVersion" -ForegroundColor Cyan
    Write-Host "  확인: Find-PSResource -Name $moduleName -Repository $Repository -Prerelease"
}

# ------------------------------------------------------------------
# 6. (선택) git 태그 + GitHub Release
# ------------------------------------------------------------------
if ($Tag -and $PSCmdlet.ShouldProcess($tagName, "git 태그 생성 및 push")) {
    git tag -a $tagName -m "$moduleName $fullVersion"
    git push origin $tagName

    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $ghArgs = @('release', 'create', $tagName, '--title', "$moduleName $fullVersion", '--generate-notes')
        if ($prerelease) { $ghArgs += '--prerelease' }
        gh @ghArgs
    }
    else {
        Write-Warning "gh CLI 가 없어 GitHub Release 는 건너뜁니다. 태그 $tagName 는 push 되었습니다."
    }
}
