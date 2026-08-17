# 합격 조건 — 수트 착용 키프레임 (수트 3차, 마지막)

실행 **전에** 적는다.

## 표적이 바뀐 근거

3차의 첫 단계는 진단이었고, 그 한 줄이 두 번의 실패를 모두 설명했다:

```
[SuitChoreState] after replay: equip=on chore=null currentTarget=니콜라 equippable=ok
                 assignee=니콜라 isEquipped=True ownerEquipment=ok sameEquipment=True IsEquipped=True
```

재생이 돌아온 시점에 **네 조건 전부 참이고 chore 는 없다.** 그러므로

- 1차의 "락커 chore 를 끄면 된다" — 대상이 틀렸다 (이미 확인)
- 2차의 "한 프레임 이르다" — **틀렸다.** `Equipment.Equip` 은 동기이고 여기서 이미 착용이다
- 남은 errand 는 **우리가 건드린 수트가 아닌 다른 수트**의 것이다

무엇이 다른가: `suitNoWorn` 이 매 실행 2~4다. 그건 클라가 **unequip 을 거절**한 횟수 —
그 듀플이 애초에 수트를 안 입고 있었다는 뜻이다. 이벤트 창 밖(참가 전, 하드싱크 중, 재접속 끊긴
몇 초)에 일어난 착용은 아무것도 나르지 않는다.

**델타만 있고 키프레임이 없다.** 이 저장소가 `StructureSyncerBase` 를 쓰는 모든 syncer 에 대해
이미 적어둔 것과 같은 결함이고, `LogicStateSyncer` 가 15초 키프레임을 받은 이유와 같다.

## 무엇을 만들었나

`SuitWornSyncer` — 호스트가 15초마다 착용 중인 수트를 듀플별로 한 건씩 알린다.
클라는 **자기 락커를 스스로 찾아** 게임의 `EquipTo` 로 재생한다 (호스트 락커는 이미 수트를
놓았으므로 클라 것의 위치를 답할 수 없다).

**받는 쪽은 절대 벗기지 않는다.** 부재로 정리하는 수신자가 클라 식물 294그루를 지운 그 구조다.
벗기는 것은 이벤트가 계속 담당한다.

## 합격 조건

| # | 조건 | 뜻 |
|---|---|---|
| 1 | `wornSent > 0` **그리고** `wornRecv > 0` | 키프레임이 실제로 오간다 |
| 2 | `wornApplied > 0` | 클라가 없던 수트를 실제로 입혔다. **0 이면 판정 불가** |
| 3 | `suitNoWorn` 이 2~4 → **0~1** | 잔여물이 줄었다. 이게 이 변경의 목적 |
| 4 | `wornIneffective = 0`, `wornThrew = 0` | 불렀는데 안 붙은 경우가 없다 |
| 5 | 클라 `errors = 0`, `netid_compare exit 0` | **안전 조건 — 깨지면 되돌린다** |

`wornApplied = 0` 이고 `wornWorn` 만 크면 **이 콜로니에서는 창 밖 착용이 없었다**는 뜻이다.
그 경우 3번이 저절로 맞을 수 있으므로 **판정 불가**로 적고, 2번 없이 3번을 근거로 쓰지 않는다.

## 읽지 말아야 할 것

- `chore DIFFERENT` 는 이 변경의 표적이 **아니다.** 그 행은 간헐적이고(3회 중 2회, 1회 중 0회)
  진단이 보여준 대로 우리 재생과 무관한 수트의 것이다. 줄면 부수 효과, 안 줄면 실패가 아니다.
- `wornWorn` 이 큰 것은 정상이다. 대부분의 키프레임은 "이미 맞다"로 끝나야 하고, 그게 이 방식이
  15초마다 돌아도 싼 이유다.

## 1차 결과 — 판정 불가, 그리고 가설이 또 반증됐다

```
wornSent 65 → wornRecv 56 → wornWorn 56, wornApplied 0
wornIneffective 0  wornThrew 0
suitNoWorn 2~4 → 0, 1      errors 0   netid_compare exit 0   diff_logs exit 0 clean
```

`wornWorn=56` 은 **매 15초 검사마다 클라가 호스트와 같은 수트를 입고 있었다**는 뜻이다.
그러므로 `suitNoWorn` 의 원인은 "클라가 착용을 놓쳤다"가 **아니다.** 세 번째 가설도 반증됐다.

미리 적은 대로 `wornApplied=0` → **판정 불가**. `suitNoWorn` 감소를 근거로 쓰지 않는다.

## 2차 — 키프레임에 그 존재 이유를 줘본다

키프레임이 덮는 상황은 이 시나리오가 구조적으로 못 만드는 것이다: 참가, 하드싱크, 재접속.
`soak.ps1 -AlwaysReconnect` 로 매 실행 클라를 끊고 재접속시킨다.

| # | 조건 | 뜻 |
|---|---|---|
| 1 | 재접속 실행에서 `wornApplied > 0` | 키프레임이 실제로 복구한다 → **유지할 값이 있다** |
| 2 | 클라 `errors = 0`, `netid_compare exit 0` | |
| 3 | `wornIneffective = 0` | 불렀는데 안 붙은 경우 없음 |

**1번이 0 이면 되돌린다.** 재접속에서도 복구할 것이 없다면, 이 콜로니에서 이 키프레임은
측정 가능한 이득이 없다 — 이 저장소의 규칙 그대로다.
