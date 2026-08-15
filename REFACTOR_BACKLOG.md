# 리팩토링 백로그

작성 근거는 추측이 아니라 `mp-stability` 작업에서 실제로 진단·수정하며 부딪힌 것들이다.
각 항목은 "이것 때문에 무엇이 몇 번 잘못됐는지"를 같이 적는다. 그게 없으면 취향 논쟁이 된다.

**지금 하지 말 것.** 안정화 중에 구조를 바꾸면 어떤 증상이 원래 버그이고 어떤 게 리팩토링 부작용인지
구분할 수 없다. 이 파일은 "언젠가"가 왔을 때 무엇부터 손대야 하는지에 대한 목록이다.

---

## 1. 침묵이 제어 흐름이다 (최우선)

핸들러가 처리할 수 없는 경우에 그냥 `return` 한다. 받는 쪽에서 **"안 왔다"와 "왔는데 무응답"이
구분되지 않는다.**

`EntityResolveRequestPacket` 에 답 없는 `return` 이 세 개 있었다 — `Pickupable` 아님,
`PrimaryElement` 없음, substance 없음. 한 실행에서 **요청 118건 중 56건만 응답**, 62건은 무응답.
클라는 3번 재시도하고 포기하고 "영구 미해결 id"로 기록했다. 매 실행 남던 마지막 실패가 이것이었다.

같은 파일 주석이 이미 *"침묵이 두 가지를 뜻해서 문제였다"*고 적어놓고 한쪽만 고쳐둔 상태였다.
즉 이건 이미 한 번 발견된 패턴이 재발한 것이고, 개별 수정으로는 막히지 않는다.

**방향:** 요청/응답 패킷은 응답을 **필수**로 만든다. `IRequestPacket` 이 항상
`Answer`(성공 / 없음 / 있지만 못 줌 / 거절+이유)를 반환하도록 타입으로 강제하고, 무응답 경로가
컴파일되지 않게 한다.

## 2. 개체 생성이 복제되지 않는다 — 모든 ID 버그의 뿌리

`CLAUDE.md` 에 이미 적혀 있다: 양쪽이 각자 객체를 만들고 각자 id 를 발급한다. 그래서 id 를
**해시로 맞추려고** 하는데, 해시가 할 수 없는 일을 시키고 있다.

증거: `NetIdHelper` 의 자유 슬롯 탐색이 "이 자리가 찼나"만 묻고 "누가 갖고 있나"를 묻지 않아서,
이미 올바른 id 에 앉은 객체가 **자기 자신을 보고** 한 칸 옆으로 밀려났다. 두 peer 의 알 id 가
항상 정확히 1 차이 나고 방향이 실행마다 뒤바뀐 이유가 이것이다. 여섯 번의 시도가 이 모양을
설명하지 못했다.

같은 탐색 루프가 **네 군데 복사**돼 있었다(듀플, 저장된 아이템, workable, 엔티티).

**방향:** 호스트 권한 생성. 클라는 프리뷰만 그리고 id 는 받아 쓴다. 해시는 호스트가 자기 안에서
유일한 이름을 만드는 용도로만 쓰고, 두 peer 의 일치는 **전파**가 보장한다. 그러면 결정론적 해시,
셀 기반 수렴, 재입양, 셀 인덱스 같은 기계장치 대부분이 사라진다.

## 2-1. 원소 덩어리는 양쪽이 각자 만든다 (측정 완료, 수용 중)

2번의 구체적 사례이고, **차단이 불가능한 것이 확인된** 유일한 항목이라 따로 적는다.

실행당 클라에만 있는 기체·액체 덩어리가 **44~60개**(3회 측정: 55/44/60, 구성 매번 동일 —
Hydrogen ~11, Oxygen ~7, CarbonDioxide ~6, Water ~5). 기체 응축·액체 낙하 같은 물리 사건에
대해 **양쪽 peer 가 각자 `SubstanceChunk` 를 만들고 각자 id 를 붙인다.**

**차단은 NullReferenceException 이다.** `GeneratedOre.CreateChunk` 가 공통 말단이고(어셈블리
IL 전수 조사), 실제 소비자 9곳 중 **8곳이 반환값을 역참조**한다 — 5곳은 바로 다음 명령에서
`get_gameObject` / `get_transform` / `GetComponent`. Unity 가짜 null 이 아니라 진짜 예외다.
버리는 곳은 `ToiletWorkableClean` 하나뿐.

소비자: `Storage.AddLiquid`, `Storage.AddGasChunk`, `Moppable.OnCellMopped`,
`SweepStates.TryMop`, `LiquidPumpingStation.OnStopWork`, `ToiletWorkableClean`,
`MilkProductionMonitor.ExtractMilk`, `StarmapHexCellInventory.ExtractAndSpawnItemMass`.

**지금은 수용한다.** 근거: 미해결 id 2,400개 중 92%는 순서 문제로 저절로 해결되고, 남는 50개
내외도 상당수가 곧 합쳐지거나 소비된다. 반면 Postfix 로 지우면 질량 보존이 깨지고, 진입점 8곳을
개별 패치하면 반환값을 각각 살려야 한다. 2번(호스트 권한 생성)이 해결되면 자동으로 사라진다.

## 2-1-b. 덧없는 물질에서 id 를 걷어냄 (2-1 의 대안, 검증 중)

2-1 을 "차단 불가"로 수용한 뒤, **막지 말고 주소를 주지 않는** 쪽으로 돌렸다.
`NetworkIdentity.OnSpawn` 에서 `PrimaryElement.Element` 가 기체·액체면 id 없이 반환한다
(`ephemeral=` 카운터). 질량 보존은 건드리지 않으므로 NRE 문제가 없다.

선을 그은 기준은 **"누가 집어들 수 있는가"** 다. 고체 광석은 듀플이 운반·저장하므로 주소를
유지하고, 용기(`Pickupable.storage`) 안의 기체·액체도 유지한다 — 누군가 의도적으로 넣은 것이다.
**반대 방향으로 틀리면 실제 객체가 조용히 복제되지 않는데**, 그게 지목 불가능한 기체 덩어리보다
훨씬 나쁘다. 억제를 너무 넓게 잡아 되돌린 것이 이미 세 번(입양 완화, 공지 인식, id 은퇴)이다.

## 2-3. "건설 예정인데 이미 지어져 있다" — 가설 여섯 번째 반증, 계측으로 전환

라이브 보고("호스트는 타일 설치됨, 클라는 건설 예정"). `BuildCompletePacket` 이 한 레이어만
보고 발판을 지운다는 가설로 전 레이어 쓸기를 넣었다. 두 번의 측정 결과:

| | soak01 | soak02 |
|---|---|---|
| host `finishbuild` | 4/4 완료 | 4/4 완료 |
| client `Finalized` | 4 | 4 |
| `scaffoldsCleared` | **0** | **0** |
| `scaffoldsSpared` | 0 | 0 |

완성 패킷 4개가 실제로 처리됐는데 남은 발판이 0 — **정상 경로가 이미 지웠다.** 즉 이 가설은
반증됐다. 쓸기는 프리팹 한정이라 안전하므로 가드로 남기되, **보고된 버그를 고쳤다고 말하지 않는다.**

대신 그 증상을 **한쪽 peer 만으로 판정하는 불변식**으로 바꿨다(`GhostSiteScan`, `ghostSites=`):
한 셀이 완성된 건물과 **그 건물 자신의** 미완성 발판을 동시에 가질 수 없다. 상대 로그도, 시나리오도,
두 번째 PC 도 필요 없다 — 보고가 실제 플레이에서 나왔고 랩에서 재현되지 않았으므로 이게 맞는 형태다.
남은 후보는 완성 패킷이 아예 도달/적용되지 않는 경로이고, 그건 쓸기가 아니라 **주기적 정합 맞추기**로
고칠 문제다.

## 2-4. 저장고 불일치: 네 개의 독립 결함, 전부 측정으로 닫음

"클라가 자원량을 다르게 본다"는 하나의 증상이 **서로 무관한 네 개 결함**이었다. 각각 다른 수정이
필요했고, 하나만 고쳤을 때는 지표가 안 움직여서 "효과 없음"으로 보였다.

| 결함 | 증거 | 수정 |
|---|---|---|
| syncer 가 4개 타입에만 붙음 | 어긋난 컨테이너가 **전부** 그 목록 밖(Refrigerator, SuitLocker, Compost, PlanterBox) | `Storage.OnSpawn` 전체 부착 |
| 형식이 객체 단위 → 적용 불가 | host x3 → client x1 (ONI 가 스택으로 합침), `TryUpdateInPlace` 영구 실패 → **초당 2회 clear+재생성** | 프리팹당 총 질량으로 단위 변경 |
| 0kg 항목 비대칭 | 인코더는 건너뛰는데 수신자는 "비었다"로 읽고 **클라 펌프 버퍼를 삭제** (`kept=1329`) | 양쪽 대칭: 안 보낸 건 안 건드림 |
| 델타만 있고 키프레임 없음 | `MetalRefinery` 310 vs 800 — **틀린 채 멈추면 영구히 안 고쳐짐** | 15초 키프레임, 위상 분산, **시야 컬링 면제** |
| 조립기의 `Storage` 가 3개 | `GetComponent<Storage>()` 는 하나. `MicrobeMusher` 75 vs **150**(정확히 2배) | 인덱스 키로 전부(`keyPrefix` 가 이미 있었음) |

결과: `MASS` 불일치 **99 → 6**, **5kg 이상 16 → 0**, 프레임 시간 변화 없음(17.7ms 유지).

**"확장성 없다"는 원 주석의 우려는 반증됐다** — 413개 저장고에 붙여도 `StorageStateSyncer` 는
비용 상위 8위에 안 든다. 실제로 비싼 건 `BuildingDamageSyncer=2002ms/3402frames` 다.

**아직 열림:** 4.5kg 이하 6건(기계 버퍼, `BottleEmptier` 4.5kg). netid `HOST-ONLY` 는 안 줄었다
(0kg 버퍼 + 스택 중복 객체이므로 저장고 질량과 별개 문제).

## 2-6. 재접속: 두 결함이었고 둘 다 닫힘 (2026-08-12)

라이브 재접속을 처음 시험해서 잡았다. 단위 테스트(`ReconnectTests`)는 **통과하고 있었다** —
원인(주소가 `127.0.0.1:7777` 로 덮이는지)만 확인했고 그 뒤를 아무도 안 봤다.

| | 수정 전 | 수정 후 |
|---|---|---|
| 클라 `registry` | 8085 → **41** | **8172** |
| `lookupFails` | 4,586 → **90,068** | 4,886 |
| 공유 id | 8105 → **31** | 8074 |
| `HOST-ONLY` | 158 → **8019** | 155 |

기전: `NetworkIdentityRegistry.Clear()` 가 딕셔너리만 비우고 컴포넌트의 `NetId` 와
`IsRegistered=true` 를 남긴다. `RegisterIdentity()` 첫 줄이 `if (IsRegistered && NetId != 0) return;`
이므로 **8000개가 "나는 등록됐다"고 믿고 레지스트리는 모른다.** 등록은 `OnSpawn` 뿐이고 재접속엔
스폰이 없다. **연결은 정상이고 아무것도 지목할 수 없었다.**

해결: `Clear()` → `ForgetRegistration()`(믿음만 지우고 **번호 유지**) + `ReattachAll()`.
번호를 유지하는 것이 핵심 — 양쪽이 동일하게 로드한 세이브에서 온 값이라 같은 번호로 다시 등록하면
매핑이 정확히 복원되고 개명·새 id·이견이 없다.

**발동을 join 훅이 아니라 상태로 잡았다** — 레지스트리를 비우는 곳이 4군데고 세션 진입 경로가 여럿이며,
그중 하나를 고르는 것이 2-5 의 실수와 같은 형태다. `RegistryPopulationTests` 가 **레지스트리를 세계와
비교**해 한 대에서 판정한다(기존 검사는 전부 "등록된 id 가 맞는가"만 물었고, 남아 있던 41개는 맞았다).

## 2-7. 우선순위 0: 여섯 패킷의 같은 결함 (닫힘)

`if (screen != null) Priority = ...` 가드가 **예외만 막고 미설정 구조체 기본값(0)을 전송**했다.
받는 쪽은 그 0을 자기 우선순위 화면에 밀어넣고 도구를 실행 → `Priority Value Out Of Range: 0`.
같은 모양이 `DragTool`/`Build`/`UtilityBuild`/`AttackTool`/`Diggable`/`CaptureTool` **전부**에 있었고,
두 건설 패킷은 `PlanScreen.Instance` 를 가드 없이 역참조했다.

`PriorityWire` 로 관문화: 보내는 쪽은 없으면 **유효 기본값(basic,5)**, 받는 쪽은 범위 밖이면
**교정하고 센다**(`prioFixed`). 실측 `prioFixed=3~4/실행` — 가드가 있는 동안에도 계속 나가고 있었다.

## 2-5. "무엇을 추적하는가"에 답하는 곳이 없다 (구조 문제)

저장고 격차의 **원인이 아니라 그것이 탐지되지 않은 이유.** syncer 부착 지점이 게임 타입마다
손으로 쓴 **Harmony 패치 7개**다:

```
Battery / Generator / FlushToilet / Toilet / Reactor / Storage(4타입 하드코딩) / 크리터·듀플
```

명세가 없으므로 **빠진 것을 알 방법이 없다.** 저장고는 두 PC 로그 비교가 우연히 잡았다.

**추상화의 값은 목록을 한곳에 모으는 것이 아니라 "미분류"를 빨간 테스트로 만드는 것이다.**
옮기기만 하면 다음에 또 조용히 빠진다. 그래서 순서:

1. `coverage` 시나리오 verb — 건물 종류별로 어떤 게임 컴포넌트가 있고 우리 syncer 가 몇 개
   붙었는지 덤프. **컴포넌트 목록을 객체에서 직접 읽는다**(큐레이션 목록은 이 조사가 대체하려는
   그 추측이다).
2. 그 데이터로 `ReplicationPolicy.Decide(go)` 작성 — identity 등급 + 필요한 syncer + **이유 문자열**.
   지금 세 군데 흩어진 규칙(`RefusesAddressAtSpawn`, `RefusesAddressAlways`, 저장고 제외)이 모인다.
3. 살아 있는 콜로니를 훑어 **정책이 분류 못 한 상태 보유 객체가 있으면 실패**하는 테스트.

## 2-2. id 은퇴는 시도하지 말 것 (두 번 실패, 이유 확정)

죽은 객체의 번호를 재사용하지 않게 하려는 시도. **두 번 했고 두 번 다 비용만 측정됐다.**

- 1차(단순 은퇴): 호스트 `collisions` 0 → 13. 객체는 정상 플레이 중에도 등록을 풀었다
  다시 한다(셀 이동, 저장 이동). 자기 번호를 못 되찾아 새 번호로 밀린다.
- 2차(이전 소유자는 되찾기 허용): 여전히 14, 17. **ONI 가 Pickupable 을 풀링**해서
  재활용된 객체가 **새 인스턴스 ID** 로 돌아오므로 "같은 객체" 판정이 불가능하다.

두 번 다 불일치(`pointing at different things`)는 개선되지 않았다.

**재사용 사슬 자체는 실재한다** — 드롭이 번호를 받아 클라에 알려지고, 죽고, 그 번호를 새 객체가
가져가는데 클라는 죽은 것을 계속 들고 있다. 다만 **끊을 자리가 은퇴가 아니다.** 2번(호스트 권한
생성)에서 생성·소멸이 전파되면 사슬 자체가 사라진다.

## 3. "클라는 수동적이다"를 아무도 강제하지 않는다

클라 AI 는 컴포넌트를 꺼서 비활성화한다(`ChoreConsumer`, `MinionBrain`, `CreatureBrain`).
그런데 **상태 기계는 계속 돈다.** 생성 시점 계측으로 클라가 스스로 만드는 프리팹 30종이 나왔고
그중 시뮬레이션 개체는:

- `IncubationMonitor.SpawnBaby` — 알이 클라에서 따로 부화 (`PacuBaby` 호스트 1 / 클라 2)
- `ComplexFabricator.SpawnOrderProduct` — 제작기 산출물 (`MushBar`)
- `GasSourceManager` / `LiquidSourceManager.CreateChunk` — 기체·물 자체 방출
- `EntitySplitter.Split` — 아이템 분할

이건 "버그 4개"가 아니라 **경계가 없다는 사실 하나**다. 하나씩 찾아 막고 있고, 계측을 새로
넣을 때마다 새 경로가 나온다.

**방향:** 한 지점에서 판정한다 — "이 생성은 시뮬레이션 개체인가, 로컬 표현인가". 로컬 표현은
신원을 받지 않고(오늘 `IsLocalOnlyName` 으로 178개 제외), 시뮬레이션 개체는 클라에서 생성 자체가
막히고 호스트 공지로만 생긴다. 예외 목록이 아니라 규칙이어야 한다.

## 4. 억제 방식이 세 가지고, 어느 것인지 코드를 봐야 안다

- 컴포넌트 비활성화 (`MinionMultiplayerInitializer`)
- Harmony Prefix 가 `false` 반환 (`EnergyConsumer_Patches`)
- 메서드 중간 탈출 (`WorldDamagePatch` — 셀은 지우고 광석만 호스트에 남김)

세 방식이 섞여 있어서 **"클라가 무엇을 해도 되는지"를 한눈에 알 수 없다.** 게다가 반환값이 있는
메서드를 막을 때 `__result` 처리가 각자 다르다 — 오늘 `SpawnOrderProduct` 는 빈 리스트를 줘야
했고(`null` 이면 호출자가 터진다), `EntitySplitter.Split` 은 호출자가 객체를 쓰기 때문에 **막지
못하고 남겨뒀다.**

**방향:** 억제를 한 종류로 통일하고, 반환값 정책을 타입에 적는다.

## 5. 재진입 플래그가 패킷마다 즉흥적이다

`DuplicantPriorityPacket.IsApplying`, `ComplexFabricatorSpawnProductPacket.IsApplying` — 같은
문제("호스트가 시킨 것"과 "내 시뮬레이션이 결정한 것"의 구분)를 패킷마다 따로 푼다. 새 억제를
넣을 때마다 이 플래그를 잊으면 **클라가 호스트의 지시조차 못 받는다.**

**방향:** `ApplyingRemoteState` 스코프 하나. 핸들러 진입 시 자동으로 켜지고 빠질 때 꺼진다.

## 6. 계측을 넣고 아무 데도 연결하지 않는다

`idMoves`, `reassignments`, `plantIdsAbandoned`, `prioritiesDropped` — 카운터가 존재하는데
읽는 곳이 없었다. **없는 것보다 나쁘다**: 코드는 계측된 것처럼 보이고 실행은 아무것도 보고하지 않는다.
오늘 HEALTH 행에 연결하고 나서야 `idMoves` 가 실행당 44라는 걸 알았다.

**방향:** 카운터는 정의와 동시에 출력에 연결한다. 안 읽는 카운터는 삭제한다.

## 7. 두 transport 가 비대칭인데 주석이 거짓말을 한다

Riptide payload 한도는 1000B 인데 여러 syncer 주석이 "Steam 1200B MTU 에 맞춘다"고 적고
1100B 패킷을 만든다 → **LAN 에서만 조각화된다.** 최상위 버그 여러 개의 원인이었다.

**방향:** 한도를 상수 하나로, 크기 검사를 전송 계층에서. 주석이 아니라 테스트가 지키게 한다.

---

## 테스트 쪽 품질 (별개로 심각)

오늘 내가 만든 실패 중 상당수가 **테스트가 설계에 없는 약속을 요구한 것**이었다:

- "다시 계산한 id = 지금 쥔 id" → id 는 발급 시점에 고정되고 객체가 움직여도 유지되는 게 정상.
  정상인 객체 6개를 실패로 보고했다.
- "한 셀에 한 id" → 건물엔 참, 주울 수 있는 것엔 거짓. 모래 두 더미가 정당하게 각자 id 를 갖는다.
- 연속 id 휴리스틱 → 같은 실수의 첫 번째 형태.

세 번 같은 모양으로 틀렸다: **불변식을 그것이 적용되지 않는 대상에 적용했다.**

그리고 `Latency` 는 **측정이 자기 부하를 재고 있었다** — 테스트 배터리가 신원 8천 개를 덤프하는
동안 순간 RTT 를 읽어서 9회 연속 실패했다. 조용한 구간의 중앙값은 58ms 다.

**방향:** 테스트는 (1) 어떤 대상에 적용되는 불변식인지 명시하고, (2) 검증할 표본이 없으면
통과가 아니라 **skip** 하고, (3) 측정 대상에 자기 부하를 섞지 않는다.

---

## 왜 코드를 읽는 것만으로는 진단이 안 되는지

이게 이 코드베이스의 핵심 성질이다. 오늘 해결된 것은 전부 **계측**으로 나왔고, 코드를 읽고
원인을 지목한 것은 대부분 틀렸다.

| 문제 | 어떻게 찾았나 |
|---|---|
| 클라가 만드는 프리팹 30종 | `KInstantiate` Prefix 에서 호출 스택 (`CallerMemberName` 계열) |
| 알 id ±1 불일치 | 두 peer 로그를 나란히 놓고 IdConverge 방향 비교 |
| 지연 261ms | 10초 간격 시계열 — 기울기도 계단도 없음이 답이었다 |
| 미해결 id 1건 | 패킷 카운터 `sent=118 / recv=56` 의 차이 |
| `build` NRE (3번째 시도 중) | 단계별 로그. 코드를 읽어 null 을 지목한 두 번 다 틀렸다 |

이유는 위의 1번이다. **실패가 조용하다.** 코드는 의도를 말해주지만 실제로 무슨 일이 일어나는지는
말해주지 않고, 조용한 실패는 유실·경합·미구현과 겉모습이 같다. 그래서 이 코드베이스에서는
"읽고 고치기"보다 "재고 고치기"가 항상 빨랐다.

리팩토링의 진짜 목표도 그것이다 — 코드를 예쁘게 만드는 게 아니라 **실패가 스스로 말하게** 만드는 것.
1번과 6번이 그 목표에 직접 닿아 있고, 2번은 버그의 공급원을 끊는다. 이 셋이 먼저다.

---

## 2026-08-14 세션 이후 열린 항목 (전부 두 대 6런 배치로 측정)

이 세션에서 이산 상태는 거의 전부 닫혔다. 남은 것은 아래 셋이고, **각 항목에 이미 시도해서
반증된 것**을 같이 적는다 — 다음 사람이 같은 데를 다시 파지 않도록.

### 1. 클라의 `CrabBaby` 가 주소를 못 받는다 (6런 중 2런)

**증상:** `1 of 82 creatures have no NetId: CrabBaby`. 그 크리터는 클라에서 움직이지 않는다.

**이미 반증된 것 — 다시 시도하지 말 것:**

| 시도 | 결과 |
|---|---|
| 신원 게이트에서 `KBatchedAnimController` 요구 제거 | `critterNoAnim=0` — 그 조건이 아니었음 |
| `Navigator.OnSpawn` 2차 훅 추가 | 변화 없음 |
| 호스트가 크리터 생성을 `SpawnPrefabPacket` 으로 알림 | `critterSent=5~8`, `spawnDup=0`, 변화 없음 |
| 스폰 시점 converge (`!hadIdentity` 조건) | **조건이 뒤집혀 있었음** — lazy 객체는 이미 identity 를 가짐 |
| lazy 목록 기반 converge (스폰 시점) | 10/12 통과까지 감. 남은 것은 스폰→질문 순서 |
| lazy 부착 지점에서 converge | 늦은 신원 게이트 24/24 통과. 크리터는 잔존 |
| 클라가 `NetId==0` 일 때 converge 허용 | 실패 6행 → 2행 |

**남은 가설(미검증):** 클라의 CrabBaby 는 세이브에서 왔고 생성 로그가 없다. 호스트는 converge
로 결정적 id 를 갖는데 클라는 그 값을 계산해도 다르거나, 계산 시점의 셀이 다르다.
**다음 수:** 두 peer 에서 그 객체의 `ComputeDeterministicId()` 입력(프리팹·셀)을 같이 찍어
비교한다. 지금까지는 결과만 봤지 입력을 나란히 본 적이 없다.

### 2. `netid_compare` — 서로 다른 것을 가리키는 id 4~8건/런

**증상:** `MISMATCH 4, 6, 7, 7, 8, 8`. 게이트가 항상 red 인 주된 이유.

**주의:** 이 숫자는 1번과 별개인지 아직 모른다. 1번이 닫히면 같이 줄어드는지부터 본다.

### 3. `ghostSites 1` — `Wire@53893`

**증상:** 한 셀이 완성 전선과 그 전선의 미완성 발판을 동시에 갖는다.

**중요:** **양쪽 peer 가 동일하게 본다.** desync 가 아니라 세이브에 멈춰 있는 발판이다.
멀티플레이 결함으로 다루지 말 것. 새 콜로니로 재현되는지부터 확인한다.

### 측정이 진단을 뒤집은 횟수: 5

클램프 가설, `buildRefused`, 복제 경로, 재접속 재생, 저장고 제거자 — 다섯 번 다 **수정 전에
붙여둔 카운터**가 뒤집었다. 특히 마지막은 제거자가 우리 코드(`ClearStorage`)였다.

### 1런 검증은 간헐적 결함에 쓰지 말 것

이 세션 후반에 1런으로 다섯 라운드를 진동했다(통과→실패→통과). 6런 배치 한 번이 그 다섯
라운드보다 많은 것을 알려줬다 — 무엇이 고쳐졌는지, 무엇이 아닌지, **어느 방향으로** 실패하는지.
간헐적 항목은 배치로만 판정한다.

---

## 2026-08-14 후반 — 우선순위와 게이트 정리

### 닫힌 것

**채굴 표시 우선순위** — `DigPlacer` 는 id 를 못 받아 우선순위 변경이 런당 7~9건 통째로 버려지고
있었다. 셀이 그 객체의 유일한 정체성이므로 패킷이 셀로도 지목할 수 있게 했다. 호스트 유실 7~9 → 0.

**`SpawnPrefabPacket` 이 수신 불가였다** — 기본 생성자가 없었다. 수신자는
`Activator.CreateInstance` 로 만들고 역직렬화하므로 **도착하는 족족 예외**였다. 아무도 안 보내서
안 보였고, 크리터 알림에 쓰기 시작하자 드러났다. `critterSent=5~8` 이 아무 효과 없던 이유도 이것.

**그걸 잡았어야 할 테스트가 건너뛰고 있었다** — `no parameterless constructor` 를 제외 사유로
취급했다. 그건 제외 사유가 아니라 결함이다. 이제 실패로 처리하고, 첫 실행에서 진짜를 하나 잡았다.
(열린 제네릭은 제외 — `ModApiPacket<T>` 는 패킷이 아니라 형태다.)

**ghost site 게이트** — `Wire@53893` 은 **원본 세이브에 있는 것**이다. 매 실행 같은 셀, 양쪽 peer
동일, 시나리오는 그 셀을 건드리지 않는다. 세션 시작 시 기준선을 잡고 **이번 세션에 생긴 것만**
실패로 본다. 두 숫자(`ghostSites`, `ghostNew`)를 다 남긴다 — 기존 것을 숨기면 오탐을 맹점과 바꾼다.

### 우선순위 판정 (처음 측정됨)

`prio shared 28,050 / DIFFERENT 0` — **class 도 value 도 한 건도 안 틀린다.**
다른 21건은 전부 `active`(할 일 남음) 필드이고, 클라가 chore 를 못 끝내서 남는 표시다.
우선순위와 작업여부를 별도 사실로 찍는다.

첫 버전은 `IsPrioritizable()` 로 걸러서 "클라에만 44건" 이 나왔는데, 확인해보니 **같은 셀·같은
id·같은 우선순위의 전선 22개** 였다 — 우선순위 차이가 아니라 **비교 모집단이 달랐다.**

### 현재 게이트 상태

호스트 실패 테스트 **0건**, 클라 **1건**(`2 NetIds unresolved`), 양쪽 보고 오류 **0건**.

## 2026-08-15 — what a 25-minute session left open

Three run lengths were compared on the same build: six minutes (the length every
measurement in this project had used until now) and twenty-five minutes.

Length found two defects that six minutes could not, and both are fixed:

- `AssignmentPacket`'s cell fallback renamed whatever building stood at the cell.
  An atmo suit's id landed on a suit locker, and it was the only id the two peers
  disagreed about in the whole run. Fixed by carrying the sender's prefab and
  refusing a mismatch. NOTE: `assignRefused` read 0 in the run after the fix, so
  that run did not exercise the guard - `netid_compare exit 0` is the only
  evidence so far, and a repeat is worth having.

- Repair proxies were addressed but not announced. `NeedsReplication` asked for a
  Pickupable or a Navigator and `RepairableStorageProxy` is neither, so the host
  sent worker and progress packets to an id the client had never heard of.

What is still open, with what is known about each:

1. **Loose matter each simulation makes for itself.** About 70 objects per peer
   in 25 minutes, scaling with length. Two approaches are closed off by
   measurement and should not be retried without new evidence: widening the
   adoption search (of 215 failures, 133 had no candidate of that prefab anywhere
   and 68 had one only far away) and suppressing the client's own spawns (12,124
   client errors - the game's callers dereference what `SpawnResource` returns).
   What is left needs the copies matched by something other than position.

2. **Four duplicants' stamina, about 1.4 apart on a 0-100 scale.** Only in long
   runs. All four in the same direction and within 0.1 of the same magnitude,
   which is a lag rather than noise: 1.4 at a measured 0.133/s is about ten
   seconds. Every other vital agrees. Not explained by the snapshot gap, which is
   0.23s.

3. **Three containers of 953 - narrowed, and it is a distribution difference.**
   Each syncer now reports how long ago a packet last set its state, and the
   answer is that all 1,396 of them read the same 15.4 seconds: the three that
   disagree are not staler than the 950 that agree, so this is not an update that
   was in flight.

   What differs is where the mass sits, not how much. One building holds 1.0 in
   its second container and 2.0 in its first on the host, and 0 and 3.0 on the
   client - the same three kilograms of the same prefab, distributed differently
   between two containers of one building. Both peers emit the same container
   keys, so the index mapping is not the problem.

   **Settled.** A `churn` command samples every container, waits four seconds of
   running colony, and samples again - which needs no cross-peer comparison at
   all, because a peer that redistributes shows it against itself. The host moved
   106 of 598 containers in four seconds and the client moved 111 of 598: the
   same rate, on both sides. And the client's log caught the residue's exact
   shape in the act, one building shifting 2.0 kg from its first container to its
   second inside that window.

   So three containers of 953 disagreeing at a frozen instant is not a defect. It
   is the race between how fast containers change and how fast corrections
   arrive, and at any instant a few are mid-move. The number to watch is the
   churn rate, which the two peers now report side by side; a divergence would
   show as those rates differing, not as a handful of rows in a snapshot.

   The idea recorded here earlier - dump twice while paused - does not work and
   is retired: a paused colony says the same thing both times.

4. **The chore-waiting flag, by design.** 64 rows, every one a job the host's
   duplicants have claimed and the client's cannot, because a client runs no
   chores. It is reported under its own category now rather than as a priority
   disagreement. Closing it would mean replicating chore assignment, which is a
   different project.
