# 2-PC LAN 테스트 하네스

목적: **고치기 전에, 고쳐졌는지 증명할 수 있는 루프를 먼저 만든다.**

이 폴더는 Windows PC 2대로 host/client LAN 세션을 돌리고, 두 `Player.log` 를 비교해
**상태가 처음 갈라진 지점**을 찾는 도구다.

---

## 0. 핵심 아이디어 — 코드를 안 고치고도 divergence 를 증명할 수 있다

`NetIdHelper` 는 이미 발급하는 모든 NetId 를 로그에 남긴다:

```
[ONI_Together] Registered entity IronOre with id: -998877
[ONI_Together] Registered workable Diggable with id: 12345 for workable type Diggable at cell 67890
```

workable 쪽은 **cell 을 포함**한다. 따라서 host.log 와 client.log 에서 `(prefab, cell) → id` 를
뽑아 비교하면, **계측을 추가하지 않고도** "같은 셀의 같은 물건에 두 PC 가 다른 ID 를 붙였다" 를
바로 증명할 수 있다. `diff_logs.py` 가 이걸 한다.

이게 Day 1 에 할 일이다. 코드 수정은 그 다음.

---

## 1. 토폴로지

```
PC-A (dev + host)                        PC-B (client)
├ ONI (Steam 계정 #1)                    ├ ONI (Steam 계정 #2)
├ .NET SDK 8 + 이 레포                    └ mods\dev\ONI_Together_dev\  ← A 에서 복사
├ 빌드 → mods\dev\ONI_Together_dev\
└ LAN 호스트 (UDP 8080 / TCP 8081)
```

- **UDP 8080** = Riptide 세션 (`LanSettings.Port` 기본값 8080)
- **TCP 8081** = 세이브 파일 전송 (`riptidePort + 1`, `TcpFileTransferServer.Start`)
- 두 포트 모두 PC-A 방화벽에서 인바운드 허용이 필요하다. **TCP 8081 이 막히면
  세이브 전송이 UDP 폴백으로 떨어지는데, 그 경로가 감사에서 나온 버그 밀집 구역이다**
  (AUDIT.md #6). 즉 방화벽 설정을 빠뜨리면 훨씬 나쁜 코드 경로를 테스트하게 된다.

---

## 2. PC-A (host / dev) 셋업

```powershell
# 관리자 PowerShell 에서 1회
cd <레포>\testing
.\bootstrap.ps1 -Role host
```

`bootstrap.ps1` 이 하는 일:
1. ONI 설치 경로 자동 탐색 → `Directory.Build.props.user` 생성 (`GameLibsFolder`, `ModFolder`)
2. .NET SDK 8 존재 확인
3. `dotnet tool restore` (publicizer + ILRepack)
4. `dotnet build` → 산출물이 `mods\dev\ONI_Together_dev\` 로 자동 복사됨 (`Directory.Build.targets`)
5. 방화벽 규칙 추가 (UDP 8080, TCP 8081)
6. Workshop 버전 충돌 경고

## 3. PC-B (client) 셋업

```powershell
.\bootstrap.ps1 -Role client
```
빌드는 하지 않고 방화벽/폴더만 준비한다. 모드 바이너리는 PC-A 에서 밀어넣는다:

```powershell
# PC-A 에서
.\deploy-to-peer.ps1 -PeerHost 192.168.0.42 -PeerUser <계정>
```

---

## 4. ⚠️ 반드시 확인 — Workshop 버전을 끈다

Steam Workshop 에 구독된 "ONI Together" 가 켜져 있으면 **모드가 두 개 로드되어**
Harmony 패치가 두 번 적용된다. 감사 항목 상당수(중복 실행·피드백 루프)가 여기서
위증된다.

게임 → Mods → **Workshop 버전 비활성화**, dev 버전만 활성화. 두 PC 모두.

> 참고: 레포의 `mod/oni_mp/ONI_MP.dll` 은 옛 이름(`ONI_MP`)의 커밋된 산출물이다.
> 로컬 빌드는 `ONI_Together` staticID 로 나가므로 별개 모드로 인식된다 — 그래서
> 동시 활성화가 가능하고, 그래서 위험하다.

---

## 5. 설정값 맞추기

두 PC 모두 게임 내 Mods → ONI Together 옵션(PLib)에서:

| 항목 | 값 | 이유 |
|---|---|---|
| `Host.LanSettings.Port` | 8080 | 기본값 유지 |
| `Client.LanSettings.Ip` | PC-A 의 LAN IP | 기본 `127.0.0.1` 이라 반드시 바꿔야 한다 |
| `Host/Client TimeoutSeconds` | **먼저 120** | 기본 30초는 큰 콜로니 로드보다 짧아 AUDIT #12 로드 루프를 항상 유발한다. 베이스라인에서는 이 변수를 제거해두고, 나중에 30으로 되돌려 재현한다 |
| `EnablePacketQueue` | **false 유지** | true 면 AUDIT 의 `BulkSenderPacket` 참조 앨리어싱 버그가 별도로 터진다 |

---

## 6. 테스트 실행

`SCENARIOS.md` 의 시나리오를 순서대로 돈다. 각 실행 후:

```powershell
# 각 PC 에서
.\collect-logs.ps1 -Role host   -Label S1-baseline    # PC-A
.\collect-logs.ps1 -Role client -Label S1-baseline    # PC-B
```

로그를 한 곳에 모아:

```bash
python3 diff_logs.py runs/S1-baseline/host.log runs/S1-baseline/client.log
```

---

## 7. 무엇을 보는가

`diff_logs.py` 출력의 우선순위:

1. **`NETID DIVERGENCE`** — 같은 `(prefab, cell)` 에 다른 id. 나오면 AUDIT #3 확정.
2. **`ASYMMETRIC SIGNATURES`** — 한쪽에만 나온 경고. 어느 쪽이 무엇을 놓쳤는지가 보인다.
3. **`Lookup failed` 카운트** — NetId 불일치의 하류 증상. 시간에 따라 단조 증가하면 누적 divergence.
4. **`TRANSFER`** 블록 — 같은 파일에 `Started transfer` 가 2회 이상이면 AUDIT #6 확정.
5. **`FIRST DIVERGENCE`** — 두 로그의 공통 이벤트 타임라인이 갈라지는 첫 지점.

---

## 8. 알려진 함정 (감사에서 나온 것)

- **재접속이 localhost 로 간다.** `RiptideClient.CleanupRiptide()` 가 끊길 때마다
  `ServerIp/ServerPort` 를 `127.0.0.1:7777` 로 덮어쓰고, `ReconnectToSession()` 이
  그 값을 읽는다. 즉 **한 번 끊기면 재접속은 절대 성공하지 못한다** — 재접속을
  테스트할 때 이걸 버그로 인지하고 있어야 한다. (기본 포트가 8080인데 7777로
  되돌리는 것도 별개의 불일치다.)
- **로딩 중 끊기면 LAN 은 메인 메뉴로 강퇴된다** (Steam 경로에는 있는 가드가 없다).
- **큰 콜로니는 하드싱크 때 호스트가 수십 초 멈춘다** — 네트워크 장애로 오인하지 말 것.
