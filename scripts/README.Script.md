# homebrew armcli

## 최종 배포 스크립트

```bash

```

---

## 스크립트 배포

```bash
# 프로젝트 루트에서
./scripts/release.sh 0.3.6
brew update
brew upgrade
armcli --version
```

---

## 수작업 배포

```bash
cd armcli
dotnet build
dotnet run -- init -n HelloWorld -o /tmp/test-out --go --dotnet
tree /tmp/test-out

cd ~/GitWorkspace/armcli   # 실제 경로에 맞게
dotnet build               # 컴파일 확인
dotnet run -- init -n TestProj -o /tmp/final-check --go --dotnet --force
cd /tmp/final-check/TestProj
zig build run               # 이제 8/8 성공해야 정상

dotnet publish -c Release -r osx-arm64
tar -czvf armcli-v0.1.0-osx-arm64.tar.gz -C bin/Release/net10.0/osx-arm64/publish armcli
shasum -a 256 armcli-v0.1.0-osx-arm64.tar.gz

git add .
git commit -m "feat: release v0.2.0"
git push origin main

# CLI 로 태그 생성, Release 발행, 바이너리 업로드 한번에 실행
gh release create v0.2.0 armcli-v0.2.0-osx-arm64.tar.gz \
  --title "v0.2.0 - Release Title" \
  --notes "Release release notes here"
```

### armcli.rb 내용 수정:

>- url 버전, sha256 해시값, version 세 곳을 새 정보로 갱신

```rb
class Armcli < Formula
  desc "ARM64 Assembly Template Generator CLI for Students"
  homepage "https://github.com/ViVaKR/armcli"
  url "https://github.com/ViVaKR/homebrew-armcli/releases/download/v0.2.0/armcli-v0.2.0-osx-arm64.tar.gz"
  sha256 "새로_뽑은_SHA256_해시값"
  version "0.2.0"

  def install
    bin.install "armcli"
  end
end
```

```bash
git add armcli.rb
git commit -m "bump: version to v0.2.0"
git push origin main

[ 3단계 ] 로컬 테스트 및 업그레이드
brew update
brew upgrade armcli
armcli --version
```

**루비 파일 생성 및 작성 규칙**

---
