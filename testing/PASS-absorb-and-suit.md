# 합격 조건 — 병합을 호스트가 주도 + 수트 errand 계측

실행 **전에** 적는다. 두 변경이 한 빌드에 들어가지만 **카운터가 완전히 분리**돼 있어 서로의
증상을 가리지 않는다. 하나는 수정이고 하나는 계측이다.

## A. 잡동사니 — 호스트가 병합을 주도한다 (수정)

지역 규칙은 이미 실패했다: `mergeIdMoved 2` vs `mergeBothNamed 51` — 두 peer 가 같은 **쌍**을
합치지 않으므로 어느 쪽이 이기는지 로컬에서 정해봐야 수렴하지 않는다.

그래서 오늘 성공한 셋과 같은 모양으로 바꾼다: **사건을 보내고 게임 함수로 재생.**
호스트가 `Pickupable.Absorb` 프리픽스에서 "누가 누구를 먹었다"를 보내고, 클라가 같은
`Absorb` 를 부른다. 양쪽 번호가 다 있을 때만 보낸다(직전 측정 기준 실행당 약 50건).

| # | 조건 | 뜻 |
|---|---|---|
| A1 | `absorbSent > 0` **그리고** `absorbApplied > 0` | 보냈고 실제로 적용됐다. **둘을 짝으로 읽는다** |
| A2 | 클라 `gaveUpRetired` 가 37 → **20 이하** | 실제로 줄었다. 이게 이 변경의 목적 |
| A3 | `absorbThrew = 0` | 게임 함수가 안 터졌다 |
| A4 | 클라 `errors = 0` | |
| A5 | `netid_compare exit 0` 유지 | **안전 조건 — 깨지면 즉시 되돌린다** |

`absorbSent > 0` 인데 `absorbApplied = 0` 이면 **수신·적용 경로 결함**이다. 되돌리지 말고
`absorbNoSurvivor` / `absorbNoEaten` / `absorbRefused` 를 읽는다 — 셋이 이유를 나눠 갖는다.

`absorbNoEaten` 이 대부분이면 그건 예상된 경우다: 클라가 이미 다르게 합쳐서 먹힌 객체가 없다.
그 경우 A2 가 안 줄 수 있고, **그러면 이 접근으로는 못 닫는다**는 뜻이므로 되돌린다.

## B. 수트 errand — 계측이지 수정이 아니다

`chore|Atmo_Suit#…|waiting host=0 client=1` 은 1회 시도해 되돌렸다. 락커의 chore 를 껐는데
행은 **수트 아이템**의 것이었다.

이번엔 `Prioritizable.AddRef` 를 클라에서, 수트에 한해, 객체당 한 번 호출 스택과 함께 찍는다.

| # | 관측 | 결론 |
|---|---|---|
| B1 | `suitRefAdd = 0` | 이 실행에서 수트 errand 가 안 생겼다. **판정 불가** |
| B2 | `suitRefAdd > 0` 이고 로그에 `[SuitChore] ... from:` | **원인 확정** — 그 프레임이 다음 수정 지점 |
| B3 | `suitRefAdd > suitRefDel` | 올리고 안 내리는 것이 있다는 뜻 |

**B 는 이번 실행에서 고치지 않는다.** 스택을 받는 것이 목적이다. 같은 실수를 두 번 하지 않으려면
어느 객체의 어느 호출자인지 먼저 봐야 한다.

## 읽지 말아야 할 것

- `chore PEER-ONLY` 는 A 가 성공해도 크게 안 줄 수 있다. 그 행들은 값이 0 인 잡동사니이고
  클라에만 있는 것이다. A 가 겨누는 것은 `gaveUpRetired` 다.
- `absorbUnnamed` 는 실패가 아니다. 번호 없는 더미끼리의 병합은 보고할 것이 없다.
- 두 변경 중 **하나만 성공해도 그 하나는 유지한다.** 카운터가 분리돼 있어 판정이 섞이지 않는다.

---

# 결과와 2차

## A. 병합 주도 — 기계적으로 성공, 목적 실패. 되돌림

```
run1  sent 68 → applied 51 + noEaten 11 + noSurvivor 2 + refused 4 = 68
run2  sent 65 → applied 44 + noEaten 14 + noSurvivor 6 + refused 1 = 65
absorbThrew 0   클라 errors 0   netid_compare exit 0
gaveUpRetired   36, 40   (37, 37 에서 그대로)
chore PEER-ONLY 60, 53   (그대로)
```

회계가 정확히 맞는다 — 호스트의 병합은 클라가 그대로 따라간다. 그런데 **클라가 자기 병합을
계속 한다.** 같은 실행에서 이름 없는 병합이 255건·109건이었고, 그건 번호가 없어 실을 수 없다.
클라의 자체 병합을 막으려면 **모든 더미에 번호를 줘야** 하고, 그건 이 항목과 다른 크기의 설계다.

A2 실패 → 되돌린다. 근거 숫자는 `REFACTOR_BACKLOG.md` 로.

## B. 수트 errand — 프로브가 대상을 지목했다

```
[SuitChore] a client raised the errand count on 'Atmo_Suit' (refCount now 1), from:
  Prioritizable.AddRef <- EquipChore..ctor <- EquippableWorkable.CreateChore
  <- EquippableWorkable.RefreshChore <- Assignable...
suitRefAdd 4, 3   suitRefDel 3, 2   ← 매 실행 하나가 안 내려간다
```

**수트 아이템의 `EquippableWorkable`** 이다. 지난번에 껐던 락커의 `ReturnSuitWorkable` 이 아니다.

`RefreshChore` 본문이 나머지를 설명한다: 기존 chore 를 취소하고, **소유자가 이미 착용 중이 아닐
때만** 새로 만든다. 배정이 수트보다 먼저 복제되므로 클라에서는 듀플이 빈손일 때 실행돼 errand 를
만들고, 그 뒤 우리 재생이 입혀도 아무도 다시 조회하지 않는다.

그래서 재생 **직후** 게임의 그 조정 함수를 한 번 더 부른다. 상태를 손으로 만들지 않는다.

| # | 조건 | 뜻 |
|---|---|---|
| B1 | `suitChoreFixed > 0` | 발동했다. **0 이면 판정 불가** |
| B2 | `suitRefAdd == suitRefDel` | 올린 만큼 내렸다 |
| B3 | `Atmo_Suit#…\|waiting host=0 client=1` 행 소멸 | 표시가 일치한다 |
| B4 | `suitApplied > 0` 유지, 회계 유지 | 조정이 적용을 막지 않았다 |
| B5 | 클라 `errors = 0` | |

`suitApplied = 0` 인 실행은 **판정 불가**로 적는다 — 수트가 안 움직인 실행에서는 B3 이 저절로 맞는다.
