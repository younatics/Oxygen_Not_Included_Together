# 합격 조건 — 식물 복제 3차 시도 (실행 전 작성)

## 1단계는 끝났다 (빌드 B1DC57B1)

```
클라 errors                                   0
AnimEventHandler / GetPivotSymbolPosition     없음   ← 1차 시도를 죽인 서명
cell 53105  +4초      host 1 object   client 1 object
cell 53105  +3분19초  host 1 object   client 0 objects
annNotReplPlant                               1      ← 계기가 드디어 이 사건을 본다
```

**클라는 식물을 지을 수 있다.** 그리고 `Grid.Objects` 에 양쪽 다 들어간다 — 두 번째 시도가 겨눴던
"`Object.Instantiate` 라서 등록이 안 된다"는 **측정으로 반증됐다.**

프로브 식물이 3분 뒤 사라진 것은, 프로브가 식물을 **아무 데도 붙이지 않고** 세웠기 때문이다.
실제 복제 경로는 `plot.ReplacePlant` + `ReceptacleMonitor.SetReceptacle` 로 붙인다.

## 접근을 바꿨다 — `NeedsReplication` 은 건드리지 않는다

앞서 적었던 2단계는 `NeedsReplication` 이 `GameTags.Plant` 를 받아들이게 하는 것이었다.
**그러면 붙지 않은 식물이 생긴다** — 방금 그것이 3분 만에 사라지는 것을 쟀다.

대신 이미 있는 **식물 전용 사건 경로**를 넓혔다. 그 경로는 클라에서 화분에 붙인다.

| 곳 | 전 | 후 |
|---|---|---|
| 호스트 `PlantablePlot` 후처리 | `__result` 에 `Growing` 필요 | `GameTags.Plant` |
| `BroadcastPlantLifecycle` / `TryBuildPlantData` | `Growing` 인자 | `GameObject` 오버로드, `Growing` 선택 |
| 클라 `ApplyPlant` | `Growing` 못 찾으면 **실패 반환** | 태그가 있으면 성공 (성장 상태 적용만 건너뜀) |
| 주기 sweep | `HashSet<Growing>` | **그대로 둔다** |

**sweep 을 안 건드린 것이 안전장치다.** 그 sweep 은 부재로 정리하고 과거에 클라 식물 294그루를
지운 적이 있다. `AllPlants` 가 `HashSet<Growing>` 이므로 `Growing` 없는 식물은 그 walk 에
**구조적으로 나타날 수 없고**, 따라서 부재로 지워질 수도 없다. 사건 경로는 더하기만 한다.

## 합격 조건

| # | 조건 | 뜻 |
|---|---|---|
| 1 | 호스트 `plantPlot > 0` | 넓힌 호스트 분기가 발동했다 (지금까지 0) |
| 2 | 클라 `plantNoGrow > 0` | 클라가 `Growing` 없는 식물을 실제로 지었다 |
| 3 | ColdBreather 셀 수 **호스트 == 클라** | 지금 19 대 18. **이게 결함의 크기다** |
| 4 | 클라 `errors = 0` | 1차 시도가 여기서 죽었다 (0 → 159) |
| 5 | `flag|ColdBreather@53105` host-only 소멸 | 매 실행 나오던 행 |

**1·2 없이 3만 맞으면 판정 불가로 적는다.** 셀 수가 우연히 맞는 실행이 있을 수 있고,
전용 카운터 둘이 그것과 진짜 발동을 가른다.

**4가 깨지면 되돌린다.** 지금 상태의 비용은 세션당 식물 하나에 오류 0 이다. 그보다 나쁘게
만드는 변경은 이 저장소에서 두 번 되돌려졌고, 세 번째도 같게 처리한다.

## 읽지 말아야 할 것

- `plants=` 는 **안 움직인다.** `HashSet<Growing>` 이라 이 종을 못 센다. 458 그대로가 정상이다.
- `annNotReplPlant` 도 **1 그대로일 수 있다.** 일반 announce 경로는 여전히 식물을 거부하고,
  복제는 식물 전용 경로가 한다. 이 값이 0 이 되는 것은 이번 변경의 목표가 아니다.
- `plantSeen` 은 `Growing.OnSpawn` 이므로 이 종에 대해 계속 0 이다.
