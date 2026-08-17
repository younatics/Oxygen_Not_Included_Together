# 현재 상태 — 2026-08-17

`mp-stability` 브랜치. 새 세션이 여기부터 읽고 바로 착수할 수 있도록 쓴 문서다.
**작업 방식과 함정은 [CLAUDE.md](CLAUDE.md) 에 있다. 그것을 먼저 읽는다.**

## 한 줄 요약

세션을 끊거나 화면이 크게 어긋나는 문제는 닫혔다. 대기복도 닫혔다. 남은 둘은 화면에 거의
안 보이거나, 아직 **검사 자체가 없어서** 판정을 못 한 것이다. 성능은 모드가 아니라 콜로니가 무겁다.

## 게이트 (최근 실행 기준)

```
클라 오류        0        최근 실행 전부
netid_compare    exit 0   공유 약 10,277개 중 다른 것을 가리키는 것 0
hp DIFFERENT     0
conduit          0        1,700~1,800셀
circuit          0 ~ 1    (조사 시작 시점 474)
state_compare    exit 1   실행당 약 20~30행 — 아래 "남은 것" 참조
```

## 닫힌 것

| 항목 | 결과 |
|---|---|
| 클라 팅김 3종 | 굴착 알림 · BalloonStand · 애니메이션 오버라이드. 오류 0 유지 |
| 전기 회로 | 474행 → 0~1. 원인 둘: 하네스가 `FinishConstruction` 에 연결정보 `0` 을 넘김 + 전선 3개 미연결 |
| 쓸기 표시 | 8개 불일치 → 0. 인구조사에 표시 비트를 실음 |
| 기체·액체 배관 | 원래부터 0. `ConduitFlowSyncer` 의 강제 갱신 덕 |
| 자동화 신호 | `LogicStateSyncer` 에 15초 키프레임 신설 (없었음) |
| 레시피 대기열 | 사건 → 주기 상태로 이동 |
| 아이템 우선순위 | 인구조사에 실음 (건물만 되고 아이템은 안 됐음) |
| 타일 소실 | **반증** — 셀 기준 비교 시 차이 0 |

## 신설된 것

- **`IdCensus`** — 호스트가 초당 160개씩 자기 id 를 보내고 클라가 조회. 연속 두 바퀴 없는
  것만 진짜 부재로 센다. 우선순위·쓸기 표시도 같이 나른다. **없던 층**이고, 켜자마자
  결함 여러 개를 찾았다.
- **`BuildingHPIndex`** — `FindObjectsByType<BuildingHP>()` 를 목록 유지로 대체. 1분마다
  스스로 감사해 누락을 센다(`hpIndexMisses`).
- **진단 명령** — `wire-dump`, `sweep` / `sweep-dump`, `cell-dump <셀>`, `spawn-probe <프리팹> [keep]`,
  `tool-audit`

## 남은 것 — 하나가 닫혔다

### 1. 산소 체크포인트 — 대기복 소유 ✅ 닫힘 (2026-08-17)

호스트가 `SuitLocker.EquipTo` / `UnequipFrom` 을 끝낼 때 **사건**을 보내고, 클라가 같은 메서드를
그대로 부른다. 위치를 보내지 않으므로 아이템을 손으로 옮기지 않는다.

두 배치가 필요했고, 첫 배치가 잡은 것이 요점이다:

```
1차  sent=2 recv=2 적용 0     UnequipFrom 예외 2      ← 유실 0. 방향이 문제였다
     sent=5 recv=5 적용 2     UnequipFrom 예외 3
2차  sent=3       적용 2  noWorn 1  예외 0            ← 2+1 = 3
     sent=5       적용 2  noWorn 3  예외 0            ← 2+3 = 5
```

원인은 **게임 코드 본문에 있었다.** Mono 가 프레임을 인라인해 스택이 우리 줄만 가리켰으므로
어셈블리를 디컴파일했다:

```csharp
UnequipFrom:  var a = equipment.GetAssignable(Db.Get().AssignableSlots.Suit);
              a.Unassign();          // null 검사 없음 — 게임은 chore 뒤에만 부른다
EquipTo:      var s = GetStoredOutfit();
              if (!(s == null)) { ... }   // 비면 조용히 아무것도 안 한다
```

클라는 그 chore 를 안 돌리므로 안 입은 듀플에게 벗기가 온다. 그래서 **게임이 묻는 것을 먼저
묻고 같이 거절한다** — `suitNoWorn`, `suitNoStored`. 상태를 만들지 않는다.

`EquipTo` 의 조용한 무동작은 계측 결함도 드러냈다: 락커가 비어도 안 던지므로 `Applied++` 가
"옮겼다"가 아니라 "안 던졌다"를 세고 있었다. 지금은 게임이 옮길 것을 가졌을 때만 오른다.

결과: `Atmo_Suit` 불일치 행 **직전 9회 실행 11행 → 4회 실행 0행**, 클라 `errors=0`,
`suitSent = suitApplied + suitNoWorn` 이 두 실행 다 **정확히** 맞는다.

### 2. 식물 복제 — 고치기 전에, 검사가 없다는 것을 먼저 발견했다

**배관은 양쪽 다 이미 옳다.** 클라 생성은 `Util.KInstantiate` + `SetActive` 로 `Grid.Objects` 에
등록되는 경로이고(실패했던 `Object.Instantiate` 가 아니다), 호스트도 화분용·야생용 패치가 둘 다 있다.

`plant DIFFERENT 0` 은 복제된다는 뜻이 아니었다. 시나리오가 **아무것도 심지 않아서** 세션 전부터
있던 458그루만 비교한 것이다. 고장난 경우는 한 번도 검사된 적이 없다.

게이트 **앞**에 카운터를 달았고, 4회 실행에서 `plantSeen` 이 전부 0 이었다. 그래서 `plant` verb 를
만들어 시나리오가 실제로 심게 했다. **첫 실행에서 원인이 나왔다.**

```
sowed 'ColdBreatherSeed' into PlanterBoxComplete at cell 52849
  - occupant 'ColdBreather' at cell 53105 growing=False
```

**어긋나는 식물에는 `Growing` 컴포넌트가 없다.** 그리고 그것을 복제하거나 재는 경로가 전부
`Growing` 을 요구한다:

| 경로 | 요구 |
|---|---|
| `Growing.OnSpawn` 패치 | `Growing` 자체를 패치 |
| `PlantablePlot` 후처리 | `__result` 에 `Growing` 없으면 반환 |
| `PlantTracker.AllPlants` | `HashSet<Growing>` — HEALTH 행의 `plants=` 가 이것 |

그러므로 `plantSeen=0`·`plantPlot=0`·`plants=458` 은 이 식물에 대한 증거가 아니다.
**이 종을 볼 수 없는 계기 세 개**다. 양쪽이 독립적으로 내는 축인 셀로 세면:

```
호스트 ColdBreather 19셀 / 클라 18셀 — 없는 것이 매번 53105
```

호스트는 사유를 처음부터 적고 있었다:

```
[Announce] not replicating 'ColdBreather' (NetId -2145270536)
  - building=False minion=False pickupable=False navigator=False
```

그 NetId 가 매 실행 `state_compare` 의 host-only 3줄(`sync:BuildingFlagsSyncer|-2145270536|*`)과
`flag|ColdBreather@53105`, `chore|ColdBreather@53105` 전부다.

**아직 안 고쳤다.** 이전 두 시도는 `Grid.Objects` 등록을 겨눴는데, 이 식물을 막은 것은 그게
아니었다. 다음 수는 `NeedsReplication` 이 식물을 받아들이게 하는 것이고, 그 전에
**`Growing` 없는 식물도 세는 카운터**가 있어야 판정할 수 있다 — 지금 셋 다 눈이 멀어 있다.

### 3. 잡동사니 병합 — 화면에 안 보임
`state_compare` 행의 과반(약 20행)이 이것. 인구조사가 성격을 확정했다: 연속 두 바퀴 부재
50건 중 **47건이 "클라가 갖고 있다가 스스로 병합해 없앤 것"**. 두 시뮬레이션이 기체 더미를
다르게 합친다. 값은 전부 기본값이고 플레이어에게 안 보인다.

한 번 시도(리졸버 요청 억제)했다가 되돌렸다 — 요청 1,862건을 아꼈지만 클라가 객체 30여 개를
더 놓쳤다(HOST-ONLY 80대 → 110대).

## 성능 — 조사 완료, 모드 쪽엔 남은 게 없다

```
같은 콜로니, 같은 순간
  호스트  82.3ms      (듀플 AI 돌림)
  클라    17.3ms      (AI 꺼짐)
  모드 총 측정 비용 5.6ms/프레임, 세션 유무 차이 약 1ms
```
**호스트 프레임의 약 70%가 ONI 의 chore/경로탐색이다.** 모드가 손댈 곳이 아니다.
최악 프레임 260~290ms 도 세션을 끊어도 같다 → 콜로니가 원인.

실질 개선: `FindObjectsByType` 제거로 분당 4.3초 절감(건물 6,340개). 체감엔 안 나타나지만
더 큰 콜로니·긴 세션에서는 다르다.

**구조적 결론: 호스트가 항상 병목이다.** 더 빠른 PC 가 호스트를 맡는 것이 코드 변경 없는
가장 확실한 개선이다.

## 업스트림 기여

PR 9건 열려 있고 전부 `testing/network-backend-upgrades`(OxySync) 기준, `MERGEABLE`.

```
#170 protocol: 핸드셰이크에서 모드 버전 확인      코드로 확정 (Matches 가 모드버전 미확인)
#171 conduit: 화면 밖 배관 복제                   실측 (1,306셀 중 미전송 356셀에서만 불일치)
#172 battery_tracker: 로딩 중 실행 금지           관측 1회, 간헐적 — PR 본문에 명시
#173 spawn: SpawnPrefabPacket 기본 생성자         코드로 확정 (수신 시 전부 예외)
#174 assignment: 셀로 찾은 건물을 개명하지 않음   실측 — 리뷰 답변함(확장함수는 부작용 있음)
#175 storage: 컨테이너 동기화가 수트·동물 파괴    실측
#176 extensions: 붙이지 않는 NetworkIdentity 조회
#177 logic: 자동화 상태 주기 재전송
#178 census: 런타임 두 peer 대조
```

원작자(Lyraedan)가 기여자 역할을 주겠다며 디스코드 핸들을 물었고 `younatics` 로 답했다.
**아직 안 보낸 것**: 시나리오 러너, soak 하네스, 상태 비교기, `SyncedEntityBase` 추출.
크고 검증 방식을 전제하므로 요청이 오면 보낸다.

## 검증하는 법

```powershell
cd testing
.\deploy-to-peer.ps1 -PeerPath C:\ONI_MP_Share\mod
.\peer-cmd.ps1 -Verb stop-oni ; .\peer-cmd.ps1 -Verb pull-mod     # 양쪽 해시 일치 확인
.\soak.ps1 -Runs 2 -Save '싱크검증본' -Pristine '꾸밈 없는 우주 오두막' -SettleSeconds 180 `
           -Summary <경로>\soak-summary-<빌드>.txt
```

**두 PC 의 DLL 해시가 다르면 그 실행은 버린다.** 확인만 하려고 빌드해도 dev 폴더가 덮여
어긋나니, 빌드 후에는 반드시 다시 배포한다.

## 오늘 시도 대비 성공률 (2026-08-16~17)

**성공 6 · 되돌림 4.** 되돌린 것: 와이어 쓸기, 리졸버 억제, 식물 알림 ×2.

예외 없는 패턴이었다:

> **먼저 재고 고친 것은 전부 성공했고, 그럴듯해서 고친 것은 전부 되돌렸다.**

되돌린 넷 다 **실행 전에 합격 조건을 적어둔 덕에** 게임에 들어가기 전에 걸렀다.
