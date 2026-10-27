# 셐 매크로 (prsk-friend-macro)

LD플레이어 9에서 프로젝트 세카이(일본판)를 여러 VM으로 돌리며 새 계정 튜토리얼부터 친구 초대 등록까지 자동으로 진행하는 Windows용 매크로입니다.

## 설치

1. [Releases](https://github.com/RilakKuma2/prsk-friend-macro/releases/latest)에서 `SekaiMacro-win-x64.zip`을 받아 원하는 폴더에 압축을 풉니다.
2. `SekaiMacro.exe`를 실행합니다. .NET은 함께 들어 있어 따로 설치하지 않아도 됩니다.

필요한 것

- Windows 10/11 64비트, [WebView2 런타임](https://developer.microsoft.com/microsoft-edge/webview2/) (Windows 11에는 기본 포함)
- [LD플레이어 9](https://www.ldplayer.net/) — 설치 폴더를 찾지 못하면 앱에서 `ldconsole.exe`가 있는 폴더를 물어봅니다.

## 업데이트

앱을 켤 때마다 이 저장소의 최신 릴리스를 확인하고, 새 버전이 있으면 업데이트할지 묻습니다. 예를 누르면 받아서 덮어쓴 뒤 다시 켜집니다. 매크로가 돌고 있는 동안에는 업데이트하지 않습니다.
설정과 기록이 들어 있는 `local\` 폴더는 업데이트해도 그대로 남습니다.

## 쓰는 법

- **VM 관리**에서 VM을 만들고(게임 설치·튜토리얼까지 세팅) 필요한 만큼 복제합니다.
- 카드를 골라 **처음부터**(계정 초기화 후 전체 단계), **단계 실행**, **이어하기** 중 하나로 시작합니다.
- 유닛은 로고를 눌러 고릅니다. 지금 진행 중인 이벤트가 `<유닛> Memories` 이벤트면 그 유닛이 자동으로 선택되고, 다른 유닛으로 시작하려 하면 한 번 더 확인합니다.
- 초대 코드는 일회용/영구(횟수 지정)로 넣을 수 있고, 대기열에 여러 개를 쌓아둘 수 있습니다.
- 원격 접속을 켜면 같은 네트워크의 휴대폰이나 다른 PC에서 `http://<이 PC 주소>:8787`로 열 수 있습니다(인증 없음). 창 없이 웹 UI만 띄우려면 `web-ui.ps1`을 실행합니다.

## 소스에서 빌드

.NET 10 SDK가 필요합니다.

```powershell
dotnet build -c Release -o bin\gui     # 개발용: bin\gui\SekaiMacro.exe 실행 (데이터는 저장소 폴더에서 읽음)
.\build-release.ps1                    # 배포용: dist\SekaiMacro-win-x64.zip
```

릴리스는 `SekaiMacro.csproj`의 `<Version>`을 올리고 `v<버전>` 태그로 zip을 올리면 설치된 앱들이 그 버전으로 업데이트됩니다.

## 구성

| 경로 | 내용 |
| --- | --- |
| `flow.json`, `stages.json`, `plan.json`, `handlers.json` | 화면 인식 단계와 실행 순서 |
| `templates\` | 화면 인식에 쓰는 기준 이미지 (960×540) |
| `web\` | 제어판 UI (실행 파일에 포함됨) |
| `local\` | 인스턴스별 진행 기록·초대 코드·설정 (실행 중 생성) |

이 프로젝트는 개인이 만든 비공식 도구이며, 사용에 따른 책임은 사용자에게 있습니다.
