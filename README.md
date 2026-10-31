# 셐 매크로 (prsk-friend-macro)

안드로이드 에뮬레이터에서 프로젝트 세카이(일본판)를 여러 VM으로 돌리며 새 계정 튜토리얼부터 친구 초대 등록까지 자동으로 진행하는 매크로입니다. Windows에서는 LD플레이어 9, 뮤뮤 플레이어, 블루스택 5, 미뮤, 녹스를 쓰고([에뮬레이터](#에뮬레이터) 참고), 맥에서는 BlueStacks Air로 돌아갑니다([맥](#맥-bluestacks-air) 참고).

## 설치

1. [Releases](https://github.com/RilakKuma2/prsk-friend-macro/releases/latest)에서 `SekaiMacro-win-x64.zip`을 받아 원하는 폴더에 압축을 풉니다.
2. `SekaiMacro.exe`를 실행합니다. .NET은 함께 들어 있어 따로 설치하지 않아도 됩니다.

필요한 것

- Windows 10/11 64비트, [WebView2 런타임](https://developer.microsoft.com/microsoft-edge/webview2/) (Windows 11에는 기본 포함)
- 에뮬레이터 하나 이상: [LD플레이어 9](https://www.ldplayer.net/), [뮤뮤 플레이어](https://www.mumuplayer.com/), [블루스택 5](https://www.bluestacks.com/), [미뮤](https://www.memuplay.com/), [녹스](https://www.bignox.com/). 설치 폴더는 자동으로 찾고, LD플레이어를 못 찾으면 앱에서 `ldconsole.exe`가 있는 폴더를 물어봅니다.

## 업데이트

앱을 켤 때마다 이 저장소의 최신 릴리스를 확인하고, 새 버전이 있으면 업데이트할지 묻습니다. 예를 누르면 받아서 덮어쓴 뒤 다시 켜집니다. 매크로가 돌고 있는 동안에는 업데이트하지 않습니다.
설정과 기록이 들어 있는 `local\` 폴더는 업데이트해도 그대로 남습니다.

## 쓰는 법

- **VM 관리**에서 VM을 만들고(게임 설치·튜토리얼까지 세팅) 필요한 만큼 복제합니다.
- 카드를 골라 **처음부터**(계정 초기화 후 전체 단계), **단계 실행**, **이어하기** 중 하나로 시작합니다.
- 유닛은 로고를 눌러 고릅니다. 지금 진행 중인 이벤트가 `<유닛> Memories` 이벤트면 그 유닛이 자동으로 선택되고, 다른 유닛으로 시작하려 하면 한 번 더 확인합니다.
- 초대 코드는 기본이 영구(횟수 제한 없음)이고, 일회용 버튼을 누르면 몇 번 쓰고 지울지 정할 수 있습니다. 대기열에 여러 개를 쌓아둘 수 있습니다.
- 반복은 초대 코드가 있어야 하고, 일회용 코드면 사용 횟수만큼만 반복할 수 있습니다.
- 여러 개를 골라 시작·중지하거나 초대 코드를 바꾸면, 보낸 인스턴스는 선택에서 빠집니다. 카드를 고른 뒤 아래 **초대 코드**로 고른 인스턴스 모두의 코드를 한 번에 바꾸거나 대기열에 넣을 수 있습니다.
- 원격 접속을 켜면 같은 네트워크의 휴대폰이나 다른 PC에서 `http://<이 PC 주소>:8787`로 열 수 있습니다(인증 없음). 창 없이 웹 UI만 띄우려면 `web-ui.ps1`을 실행합니다.

## 에뮬레이터

윈도우에서는 제목 옆 드롭다운에서 에뮬레이터를 고르고, 고른 에뮬레이터의 VM이 그 에뮬레이터의 번호(0번부터)로 보입니다. 처음에는 LD플레이어가 골라져 있고(LD 없이 다른 에뮬레이터만 있으면 그것), 설치되지 않은 에뮬레이터를 고르면 설치 안내가 뜹니다. 해상도는 모두 960×540, DPI 160으로 맞춥니다. VM이 꺼져 있으면 매크로가 바꾸고, 켜져 있는데 다르면 껐다가 다시 켭니다.

VM을 켜기 전에 가벼운 설정도 함께 맞춥니다: CPU 2개, 메모리 3GB, 30FPS, 수직 동기화 끔, 그리고 VM마다 정할 수 있는 에뮬레이터(뮤뮤·미뮤)는 소리 끔. 게임은 30FPS에서도 똑같이 돌고 PC 부담은 크게 줄어요. 녹스는 FPS와 소리가 녹스 전체 설정이라 모든 녹스 VM에 적용됩니다.

| 에뮬레이터 | 확인한 버전 | 안드로이드 | VM 만들기·복제·삭제 | 창 정렬 |
| --- | --- | --- | --- | --- |
| LD플레이어 9 | 9.5.6.0 | 9 | 매크로에서 | 지원 |
| 뮤뮤 플레이어 | 6.8.9.0 | 15 | 매크로에서 | 지원 |
| 블루스택 5 | 5.22.286 | 9 (Pie64) | 블루스택 멀티 인스턴스 매니저에서 | 없음 |
| 미뮤 | 9.5.8.0 | 9 | 매크로에서 | 지원 |
| 녹스 | 7.0.6.2 | 12 | 매크로에서 | 지원 |

- adb는 각 에뮬레이터에 들어 있는 것을 씁니다. 블루스택·녹스의 adb는 오래된 버전이라, LD플레이어·뮤뮤·미뮤 중 하나가 함께 설치돼 있으면 그쪽 adb로 블루스택·녹스도 다룹니다. 버전이 다른 adb끼리는 서로의 서버를 끊어서(블루스택·녹스는 켜질 때 자기 adb를 띄워요), 매크로는 연결이 끊긴 adb 명령을 다시 연결해 최대 세 번까지 다시 보냅니다. 여러 에뮬레이터를 한꺼번에 켜는 순간에는 그래도 끊길 수 있으니, 켠 뒤에 시작하는 걸 권장합니다.
- 입력칸은 시스템 언어(한국어·영어·일본어)와 IME(뮤뮤의 Sogou, 녹스의 Pinyin, 미뮤 IME)에 상관없이 넣은 값을 다시 읽어 확인합니다. 미뮤 IME는 영문 뒤에 숫자를 치면 영문을 지워버려서, 그런 코드는 영문 묶음마다 확정해 가며 나눠 입력합니다.
- 블루스택은 모든 인스턴스의 설정을 한 파일에 두고 켜져 있는 동안 덮어써서, 블루스택이 모두 꺼져 있을 때만 매크로가 해상도와 ADB를 맞춥니다. 켜 둔 채라면 설정 › 디스플레이에서 960×540(사용자 지정)·DPI 160, 설정 › 고급에서 ADB를 켜 둡니다. 끌 때는 안드로이드를 먼저 종료한 뒤 플레이어 창을 닫습니다.
- 녹스 VM은 처음 한 번 켜야 adb 포트가 정해집니다(첫 VM 62001, 그다음부터 62025, 62026…).

## 맥 (BlueStacks Air)

Apple silicon 맥에서는 LD플레이어 대신 BlueStacks Air로 돌아갑니다. (macOS 15, BlueStacks Air 5.21.790에서 확인)

1. [Releases](https://github.com/RilakKuma2/prsk-friend-macro/releases/latest)에서 `SekaiMacro-mac-arm64.dmg`를 받아 열고, `SekaiMacro.app`을 응용 프로그램 폴더로 끌어 넣습니다. 서명되지 않은 앱이라 처음 한 번은 앱을 Control-클릭 › 열기로 엽니다.
2. 앱을 열면 제어판 창이 뜹니다. 창을 닫거나 Cmd+Q로 끄면 제어판이 꺼지고, 이미 시작한 매크로는 계속 돌다가 카드의 중지 버튼으로 멈춥니다.

설정과 기록은 `~/Library/Application Support/SekaiMacro`에 남습니다(제어판 로그는 그 안의 `local/panel.log`). 앱을 새 버전으로 바꿔도 그대로이고, 흐름 데이터와 템플릿은 앱을 열 때마다 새 버전으로 맞춰집니다.

필요한 것

- BlueStacks Air — 쓸 인스턴스마다 설정 › 디스플레이에서 해상도 960×540(사용자 지정)과 DPI 160, 설정 › 고급에서 ADB를 켜 둡니다.
- adb는 Homebrew나 Android SDK에 있으면 그것을, 없으면 BlueStacks에 들어 있는 것을 씁니다.
- 일본어 OCR은 macOS Vision을 씁니다(앱에 들어 있음).

윈도우판과 다른 점

- VM은 BlueStacks 멀티 인스턴스 매니저에서 만들고 복제·삭제합니다. **＋ VM 세팅**은 만들어 둔 VM을 골라 게임 설치와 튜토리얼 세팅을 합니다. 번호는 매니저의 이름을 따릅니다(BlueStacks Air = 0번, BlueStacks Air 1 = 1번).
- 해상도·DPI·ADB 설정은 매크로가 바꾸지 않고, 다르면 알려만 줍니다.
- 게임 받기는 기본 브라우저에서 apkpure 페이지를 열고, 다운로드 폴더에 받은 파일을 가져갑니다.
- 창 정렬과 자동 업데이트는 없습니다. 새 버전은 dmg를 받아 앱을 바꿔 넣습니다.
- 매크로가 손대지 않아야 하는 인스턴스는 카드에서 잠가 둡니다.

## 소스에서 빌드

.NET 10 SDK가 필요합니다. 윈도우에서 빌드하면 윈도우 에뮬레이터용(LD플레이어·뮤뮤·블루스택 5·미뮤·녹스), 맥에서 빌드하면 BlueStacks Air용이 만들어집니다.

```powershell
dotnet build -c Release -o bin\gui     # 개발용: bin\gui\SekaiMacro.exe 실행 (데이터는 저장소 폴더에서 읽음)
.\build-release.ps1                    # 배포용: dist\SekaiMacro-win-x64.zip
```

```bash
dotnet build -c Release -o bin/mac     # 개발용: SekaiMacro.command 실행 (브라우저로 열림, 데이터는 저장소 폴더에서 읽음)
./build-release-mac.sh                 # 배포용: dist/SekaiMacro-mac-arm64.dmg (Xcode 명령줄 도구 필요)
```

맥에서 윈도우판이 컴파일되는지만 확인하려면 `dotnet build -p:SekaiPlatform=windows -p:EnableWindowsTargeting=true`를 씁니다.

릴리스는 `SekaiMacro.csproj`의 `<Version>`을 올리고 `v<버전>` 태그로 zip을 올리면 설치된 앱들이 그 버전으로 업데이트됩니다.

## 구성

| 경로 | 내용 |
| --- | --- |
| `flow.json`, `stages.json`, `plan.json`, `handlers.json` | 화면 인식 단계와 실행 순서 |
| `templates\` | 화면 인식에 쓰는 기준 이미지 (960×540) |
| `web\` | 제어판 UI (실행 파일에 포함됨) |
| `mac\` | 맥 OCR 도구 소스 (Vision), 앱 번들의 실행 스크립트·Info.plist·아이콘 |
| `SekaiMacro.ico`, `icon.png` | 윈도우 앱 아이콘과 그 원본 그림 (1024×1024) |
| `local\` | 인스턴스별 진행 기록·초대 코드·설정 (실행 중 생성) |

이 프로젝트는 개인이 만든 비공식 도구이며, 사용에 따른 책임은 사용자에게 있습니다.

## 릴리스 설명 형식

릴리스 설명 맨 위에는 운영체제별 직접 다운로드 링크를 항상 넣습니다. 아래 `<태그>`를 실제 버전 태그로 바꾸고, 해당 릴리스에 첨부한 파일을 연결합니다.

```markdown
윈도우: [**SekaiMacro-win-x64.zip**](https://github.com/RilakKuma2/prsk-friend-macro/releases/download/<태그>/SekaiMacro-win-x64.zip)을 받아 압축을 푼 뒤 실행하세요.

맥: [**SekaiMacro-mac-arm64.dmg**](https://github.com/RilakKuma2/prsk-friend-macro/releases/download/<태그>/SekaiMacro-mac-arm64.dmg)를 받아 앱을 응용 프로그램 폴더로 옮기세요.
```
