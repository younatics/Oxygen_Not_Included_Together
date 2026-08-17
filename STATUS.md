# 현재 상태 — 2026-08-17

`mp-stability` 브랜치. 새 세션이 여기부터 읽고 바로 착수할 수 있도록 쓴 문서다.
**작업 방식과 함정은 [CLAUDE.md](CLAUDE.md) 에 있다. 그것을 먼저 읽는다.**

## 한 줄 요약

세션을 끊거나 화면이 크게 어긋나는 문제는 닫혔다. 대기복 · 식물 · 전선 수리 표시도 닫혔다.
남은 둘은 **잡동사니 병합**(화면에 안 보임)과 **수트 작업 표시**(간헐적 0~2행)이고, 둘 다
세 번씩 시도해 세 번씩 되돌렸다 — 그 과정에서 각각 어디를 봐야 하는지로 좁혀졌다.
성능은 모드가 아니라 콜로니가 무겁다.

## 게이트 (최근 실행 기준)

```
클라 오류         0        최근 실행 전부
netid_compare     exit 0   공유 약 10,280개 중 다른 것을 가리키는 것 0
diff_logs         exit 0   마지막 4회 전부 clean (아침에는 매번 DIVERGENCE CONFIRMED)
hp DIFFERENT      0
conduit           0        1,700~1,800셀
circuit           0 ~ 1    (조사 시작 시점 474)
plant             셀 기준 호스트 = 클라 (19 대 18 이었다)
chore DIFFERENT   0 ~ 1    간헐적, 전부 Atmo_Suit — 아래 "남은 것"
state_compare     exit 1   대부분 잡동사니. 플레이어에게 보이는 행은 위 chore 뿐
```

`diff_logs` 가 clean 으로 바뀐 것을 개선으로 **주장하지 않는다.** 실행 방식이 섞여 있고
단일 배치의 범주 합계로는 추세를 말할 수 없다 — CLAUDE.md 의 "실행 1회로 판정하면" 항목.

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

## 남은 것 — 둘이 닫혔고 둘이 남았다

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

### 2. 식물 복제 ✅ 닫힘 (2026-08-17, 3차 시도)

```
plantPlot        0 → 1, 1          호스트 분기 발동
plantNoGrow      0 → 1, 1          클라가 Growing 없는 식물을 지음
ColdBreather 셀  호스트 19 / 클라 18  →  19 / 19  (2회 실행 다)
클라 errors      0                 1차 시도는 여기서 159 로 끝났다
ColdBreather@53105 host-only 행, -2145270536 3줄:  사라짐
diff_logs        exit 0 clean      2회 다
```

**`NeedsReplication` 은 건드리지 않았다.** 일반 announce 경로로 보내면 아무 데도 붙지 않은 식물이
생기고, 클라 프로브가 그 결말을 측정했다 — 4초 뒤엔 셀에 있고 3분 뒤엔 없다. 식물 전용 사건
경로는 `plot.ReplacePlant` + `SetReceptacle` 로 붙인다.

**주기 sweep 은 일부러 안 건드렸다.** 그것은 부재로 정리하고 과거에 클라 식물 294그루를 지웠으며,
`HashSet<Growing>` 을 훑는다. `Growing` 없는 식물은 그 walk 에 나타날 수 없으므로 지워질 수도
없다. 사건 경로는 더하기만 한다.

"무엇이 식물인가"를 **두 번 틀렸고, 두 번 다 카운터가 한 실행에 이름을 댔다:**

| 축 | 결과 |
|---|---|
| `Growing` | Wheezewort 제외 — 원래 결함 |
| `GameTags.Plant` | `plotSeen=1 plotNoTag=1` — `ColdBreatherConfig` 는 `CreatePlacedEntity` 만 쓰고 `ExtendEntityToBasicPlant` 를 안 부른다. 태그가 아예 없다 |

정답은 `SpawnOccupyingObject` 본문에 있었다: 씨앗이면 식물을 만들어 **그것을** 돌려주고, 아니면
넣은 것을 그대로 돌려준다. **반환값이 다르면 심긴 것이다.** 종·태그·컴포넌트를 하나도 안 고른다.

`plants=` 는 458 그대로이고 `plantSeen` 도 0 이다. 둘 다 `Growing` 파생이라 이 종을 못 본다 —
증거가 아니다.

### 2b. 그전에: 검사가 없다는 것을 먼저 발견했다

**배관은 양쪽 다 이미 있었다.** 호스트에 화분용·야생용 패치가 둘 다 있고, 클라도 만들고 붙인다.
(이 자리에 한때 "클라는 `Util.KInstantiate` 를 쓰므로 `Grid.Objects` 에 등록된다"고 적혀 있었다.
**틀렸다** — 클라는 지금도 `Object.Instantiate` 를 쓰고, 어느 쪽도 `Grid.Objects` 에 등록하지 않는다.
등록은 활성화 후 `OccupyArea.OnSpawn` 이 한다. 아래 프로브 결과가 그것이다.)

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

이전 두 시도는 `Grid.Objects` 등록을 겨눴는데, **그 전제는 측정으로 반증됐다** — 클라 프로브가
지은 식물이 양쪽 `Grid.Objects` 에 다 들어간다. `Util.KInstantiate` 는 모드가 손으로 하던 것과
같은 일을 하고(등록은 활성화 후 `OccupyArea.OnSpawn` 이 한다), 차이는 씬 레이어 Z 하나였다.

### 3. 잡동사니 병합 — 화면에 안 보임. **3회 시도, 3회 되돌림**

`state_compare` 행의 대부분이 이것이고 값은 전부 기본값이라 플레이어에게 안 보인다.
두 시뮬레이션이 기체·액체 더미를 **다르게 합친다** — 질량과 온도는 같고 살아남은 객체가 다르다.

크기는 매 실행 측정된다: 클라가 포기한 id 41개 중 30개가 이것이었고 **전부** Oxygen · DirtyWater ·
Water · Methane · CarbonDioxide · Hydrogen · Dirt 였다. 건물 0, 듀플 0.
`gaveUpRetired` 가 그 크기다.

| 시도 | 측정 결과 |
|---|---|
| 리졸버 요청 억제 | 요청 1,862건 절약, 클라가 객체 30여 개 더 놓침 |
| 병합 시 id 승계 (`Pickupable.Absorb` 로컬) | `mergeIdMoved 2` vs `mergeBothNamed 51` — 안전하고 무용 |
| 호스트가 병합 주도 (사건 전송 + 클라가 `Absorb` 재생) | 회계 완벽(`68 = 51+11+2+4`), `gaveUpRetired` 불변 |

3차가 왜 안 되는지가 결론이다: 같은 실행에 **이름 없는 병합이 255건·109건**이었고 번호가 없어
실을 수 없다. 클라의 자체 병합을 막으려면 **모든 더미가 주소를 가져야** 한다 —
`REFACTOR_BACKLOG.md` 5번에 숫자째 있다.

### 4. 클라에 남는 수트 작업 표시 — **3회 시도, 3회 되돌림**

`chore|Atmo_Suit#…|waiting host=0 client=1`, 간헐적으로 0~2행. 클라가 이미 끝난 일을 보여준다.
**보이는 게 다르면 행동이 다르므로** 값이 있는 항목이다.

| 가설 | 반증한 측정 |
|---|---|
| 락커의 반납 errand | `AddRef` 프로브: `EquipChore..ctor ← EquippableWorkable.CreateChore` — **수트 아이템**의 것 |
| 재생이 한 프레임 이르다 | 재생 직후 `IsEquipped=True chore=null`. `Equipment.Equip` 은 동기 |
| 창 밖 착용을 놓쳤다 | 15초 키프레임 118건 **전부** "이미 맞다"(`wornApplied 0`), 재접속 실행 포함 |

**4차 시작점:** 그 행은 양쪽 다 배정됐지만 안 입은 수트의 것이다 → 착용이 아니라 **배정 복제**를
본다. 양쪽에서 수트별 `assignee` 와 `refCount` 를 나란히 덤프해, assignee 는 같은데 숫자가 다른
수트를 찾는다. `SuitEquipPacket` 주석에 세 반증이 그대로 있다.

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

## 시도 대비 성공률

**2026-08-16~17 오전: 성공 6 · 되돌림 4** (와이어 쓸기, 리졸버 억제, 식물 알림 ×2)
**2026-08-17: 성공 4 · 되돌림 5**

닫은 것: 대기복 이동 · 식물 복제 · NetId 게이트 오분류 · 전선 수리 표시
되돌린 것: 병합 id 승계 · 병합 호스트 주도 · 수트 errand ×2 · 수트 착용 키프레임

예외 없는 패턴이었고 이틀째 유지됐다:

> **먼저 재고 고친 것은 전부 성공했고, 그럴듯해서 고친 것은 전부 되돌렸다.**

되돌린 아홉 건 전부 **실행 전에 합격 조건을 적어둔 덕에** 결과를 보고 기준을 고르지 않았다.
그리고 되돌린 것마다 **발동 카운터와 표적 지표를 나란히** 남겼기 때문에, 남은 두 항목은
"안 된다"가 아니라 **"어디를 봐야 하는지"** 로 좁혀져 있다(위 3번·4번).

### 오늘 반복된 하나의 교훈

닫은 넷 중 셋이 같은 교정이었다 — **게임 함수를 부르지 않고 상태를 손으로 만들면 화면이
따라오지 않는다.**

| 손으로 한 것 | 게임의 진입점 |
|---|---|
| 수트 위치 전송 | `SuitLocker.EquipTo` / `UnequipFrom` |
| `Object.Instantiate` 로 식물 생성 | `PlantablePlot.ForceDeposit` |
| 리플렉션으로 HP 필드 쓰기 | `BuildingHP.Repair` (완전 수리 트리거가 errand 를 끝낸다) |

그리고 그 진입점들을 **추측하지 않고 어셈블리에서 읽었다** — `testing/decomp`.
CLAUDE.md 에 오늘 나온 오진 항목 아홉 개가 정리돼 있다.
