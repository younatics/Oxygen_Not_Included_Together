# CLAUDE.md — ONI Together 안정성 작업

이 파일은 fork (`younatics/Oxygen_Not_Included_Together`) 의 `mp-stability` 브랜치에만 있다.
upstream 에는 없다.

## 미션

이 모드는 잦은 disconnect / desync / host-client 상태 불일치가 있다. LAN(직접 IP)에서도 재현된다.
**목표는 원인을 고치는 것이고, 방법은 verifier-first 다** — 재현·측정 수단을 먼저 만들고 그 다음 고친다.

정적 감사가 이미 끝났다: **[AUDIT.md](AUDIT.md)** (3축 병렬 오딧 + 직접 재검증, 반증 9건 기록).
테스트 하네스도 준비돼 있다: **[testing/README.md](testing/README.md)**.

## 절대 규칙

1. **재현 없이 고치지 않는다.** 각 수정은 `testing/SCENARIOS.md` 의 시나리오 하나에 대응해야 하고,
   수정 전/후 `diff_logs.py` exit code 가 `1 → 0` 으로 바뀌는 것을 보여야 한다.
2. **두 PC 의 모드 DLL 은 byte-identical 이어야 한다.** 버전 핸드셰이크가 없어서 어긋난 쌍은
   조사 중인 버그와 똑같은 증상을 낸다. `deploy-to-peer.ps1` 이 sha256 을 검증한다.
   `collect-logs.ps1` 이 `*.meta.json` 에 해시를 기록한다 — 두 파일의 `modDllSha256` 이 다르면 그 실행은 버린다.
3. **Workshop 버전은 반드시 끈다.** 두 개 로드되면 Harmony 패치가 두 번 적용되어 결과가 위증된다.
4. **컴파일이 유일한 게이트다.** macOS 세션에서는 게임 DLL 이 없어 빌드를 못 한다 —
   Windows 세션이 `dotnet build` 를 통과시킨 것만 신뢰한다.
5. 한 번에 **한 가지만** 고친다. 감사 항목들은 서로 증상을 가린다.

## 수정 순서 (감사 결론)

| 순 | 대상 | 파일 | 크기 | 대응 시나리오 |
|---|---|---|---|---|
| 1 | 캔 광석 클라 2중 생성 | `Patches/World/WorldDamagePatch.cs:45` | **2줄** | S2 |
| 2 | 셀 단위 스택트레이스 로깅 → 호스트 동결 | `DebugTools/DebugConsole.cs`, `NetIdHelper.cs:47,78` | 1줄+삭제 | S6 |
| 3 | 하드싱크당 월드 N+1회 동기 저장 | `Misc/World/GameServerHardSync.cs:67` | 소 | S6 |
| 4 | 전체상태 패킷 페이징 | `PlantGrowthSyncer`, `WorldStateSyncer` | 중 | S3·S4 |
| 5 | NetId float 항 제거 **+ Unregister 소유자 확인 (반드시 같이)** | `NetIdHelper.cs:66`, `NetworkIdentityRegistry.cs:35` | 중 | S1 |

⚠️ **5번을 단독으로 적용하지 말 것.** float 항을 빼면 엔트로피가 줄어 충돌이 늘고,
`breakoff` 루프가 로컬 등록 순서에 의존하므로 새로운 divergence 를 만든다.
`Unregister` 를 소유자 확인형으로 바꾸는 것과 **한 커밋에** 가야 한다.

## Windows 명령

```powershell
# 최초 1회 (관리자 PowerShell)
cd testing
.\bootstrap.ps1 -Role host          # PC-A (dev/host)
.\bootstrap.ps1 -Role client        # PC-B

# 코드 수정 후 반복 루프
cd ..
dotnet build ONI_Together\ONI_Together.csproj -c Debug     # 자동으로 dev 모드 폴더에 배포
cd testing
.\deploy-to-peer.ps1 -PeerHost <PC-B IP> -PeerUser <계정>  # 클라에 밀어넣고 해시 검증
# → 양쪽 ONI 재시작 → 시나리오 실행 → 로그 수집 → diff_logs.py
```

## 이 코드베이스에서 알아둘 것

- **Transport 2종이고 비대칭이다.** Steam(`Transport/Steamworks/`)과 **Riptide=UDP**(`Transport/Riptide/`).
  Riptide 는 payload **1000 바이트** 한도가 있고 초과분을 `ChunkedPacket` 으로 쪼갠다.
  여러 syncer 가 주석에 "Steam 1200B MTU 에 맞춘다"고 적고 1100B 패킷을 만든다 → **LAN 에서만 조각화된다.**
  이 200바이트 차이가 최상위 버그 여러 개의 원인이다.
- **패킷 헤더에 sequence·tick·sender 가 없다.** `int packetType` 4바이트뿐. 그래서 손실·중복·재정렬을
  사후에 증명할 수 없다. AUDIT.md §7 이 최소 계측안이다.
- **핸들러 로직은 패킷 클래스의 `OnDispatched()` 안에 있다.** 별도 dispatch 테이블이 없다.
- **엔티티 생성은 복제되지 않는다** (`KInstantiatePatch` 의 큐 호출이 주석 처리됨). 양쪽이 각자
  객체를 만들고 각자 ID 를 발급한다 — 이것이 NetId 버그를 치명적으로 만드는 전제다.
- **클라 AI 는 꺼져 있다** (`ChoreConsumer`·`MinionBrain`·`CreatureBrain`, `AdvancePath` 차단).
  즉 divergence 는 "독립 AI" 가 아니라 **미복제/이중복제 이벤트**에서 온다.
- `Profiler.Scope()` 는 release 에서 no-op 이다. 단 **DEBUG 빌드는 호출마다 문자열 작업을 해서
  성능 측정에 쓸 수 없다** — perf 를 재려면 Release 로 빌드한다.

## 이미 알려진 함정 (버그로 인지하고 있어야 함)

- **재접속은 절대 성공하지 않는다.** `RiptideClient.CleanupRiptide()` 가 끊길 때마다
  `MultiplayerSession.ServerIp/ServerPort` 를 `127.0.0.1:7777` 로 덮어쓰고,
  `ReconnectToSession()` 이 그 값을 읽는다. (기본 LAN 포트는 8080인데 7777로 되돌리는 것도 별개 불일치.)
- **로딩 중 끊기면 LAN 은 메인 메뉴로 강퇴된다.** Steam 경로(`SteamworksClient.cs:240-244`)에는
  `LoadingWorld` 가드가 있는데 Riptide 에는 없다.
- **TCP 8081 이 막히면 조용히 UDP 청크 폴백으로 떨어진다** — 그게 버그 밀집 경로다. 방화벽 먼저.
