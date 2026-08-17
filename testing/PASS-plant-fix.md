# 합격 조건 — 식물 복제 (3차 시도)

실행 **전에** 적는다. 이 항목은 두 번 시도해 두 번 되돌렸고, 되돌린 이유가 코드에 그대로 적혀 있다.

## 순서를 지킨다

`NetworkIdentity.cs` 가 3차 시도의 조건을 직접 적어놨다:

> probe on the client, leave the object standing for several minutes, and watch the
> error count rather than the return value

그래서 **필터를 먼저 건드리지 않는다.** 두 단계다.

## 1단계 — 계기와 프로브 (지금 실행)

**계기.** `annNotReplPlant` = `NeedsReplication` 이 거부한 것 중 `GameTags.Plant` 를 가진 것.
`GameTags.Plant` 는 `ExtendEntityToBasicPlant` 가 **모든** 식물에 붙이는 태그이고, `Growing` 은
작물에만 붙는다. 기존 세 계기(`plantSeen`·`plantPlot`·`plants=`)가 전부 `Growing` 을 요구해
Wheezewort 를 못 봤다.

**프로브.** 기존 `spawn-probe` 는 위치 없이 `Object.Instantiate(prefab)` 를 불러 **원점**에 만들었다.
셀도 씬 레이어도 없는 물건이었고, 그걸로 얻은 "안 던진다"가 159개 오류를 낸 변경의 근거가 됐다.
이제 셀을 받아 `GameUtil.KInstantiate` + `SetActive` 로 실제 셀에 만든다.

| # | 조건 | 뜻 |
|---|---|---|
| 1 | 호스트 `annNotReplPlant > 0` | 계기가 이 사건을 **볼 수 있다**. 0 이면 계기가 틀린 것이고 2단계로 못 간다 |
| 2 | 클라 로그에 `spawn-probe :: 'ColdBreather' built at cell 53105` | 프로브가 클라에서 실제로 지었다 |
| 3 | 클라 `errors = 0` (settle 180초를 버틴 뒤) | **클라가 식물을 들고 있을 수 있다** |

**3번이 이 실행의 전부다.** 1차 시도가 죽은 지점이 정확히 여기이고, 그때 오류는 호출 지점이
아니라 Unity 의 LateUpdate 에서 나왔으므로 반환값으로는 영원히 안 보인다.

`errors > 0` 이면 **2단계로 가지 않는다.** 그 경우 스택을 읽고, 필터는 손대지 않는다.
지금 상태의 비용은 세션당 식물 하나이고 오류 0 이다 — 그보다 나쁘게 만들지 않는다.

## 2단계 — 필터 (1단계 3번이 통과할 때만)

`NeedsReplication` 이 `GameTags.Plant` 를 받아들이게 한다. 그때 합격 조건:

| # | 조건 |
|---|---|
| 1 | `annNotReplPlant` 가 **0** 으로 떨어진다 |
| 2 | 셀 기준 ColdBreather 수가 **호스트 = 클라** (지금 19 대 18) |
| 3 | 클라 `errors = 0` |
| 4 | `state_compare` 의 host-only 3줄(`sync:BuildingFlagsSyncer|-2145270536|*`)이 사라진다 |

4번은 그 id 가 이 식물이라는 것을 오늘 확인했으므로 독립적인 확인이 된다.

## 읽지 말아야 할 것

- `plantSeen`·`plantPlot`·`plants=` 는 **이 종에 대해 아무 뜻이 없다.** 셋 다 `Growing` 을 요구한다.
  2단계가 성공해도 이 셋은 안 움직인다. 안 움직이는 것을 실패로 읽지 않는다.
- `spawn-probe` 의 반환 메시지는 판정이 아니다. 판정은 **클라 오류 수**다.
