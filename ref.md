# 참조

## 배포전 테스트

```bash
cd ~/GitWorkspace/riscvcli   # 실제 경로에 맞게
dotnet build               # 컴파일 확인
dotnet run -- init -n TestProj -o /tmp/final-check --force
cd /tmp/final-check/TestProj

zig build run               # 이제 8/8 성공해야 정상
pwsh ./hun-build.ps1 -NoRun

```

## 배포스크립트

```bash

git tag -l | grep v0.5.1

./scripts/release.sh 0.5.1

brew uninstall armcli 2>/dev/null; brew untap ViVaKR/armcli 2>/dev/null
brew tap ViVaKR/armcli
brew install armcli
armcli --version
armcli init -n QuickTest -o /tmp --go --dotnet --pwsh  # init 명령까지 잘 배포됐는지

# 배포전 테스트
dotnet build
dotnet run -- init -n HelloWorld -o /tmp/test-helloworld --go --dotnet
dotnet run -- init -n TestProj -o /tmp/final-check --go --dotnet --force

dotnet run -- init -n T -o /tmp/t --pwsh --go --dotnet --pwsh --force
cd /tmp/t/T
pwsh ./hun-build.ps1 -NoRun   # 먼저 빌드만
pwsh ./hun-build.ps1          # 실행까지
# 최초 생성
armcli init -n Demo -o . --go --dotnet
```
