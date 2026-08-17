# 합격 조건 — 클라에 남는 수리 작업 표시

실행 **전에** 적는다.

## 먼저, 크기를 정정한다

`chore PEER-ONLY 39~60` 을 "화면에 보이는 차이"라고 말했는데 **틀렸다.** 그 행들은 값이 전부 `0`
이고, 클라만 갖고 있는 잡동사니 더미다. 실제로 보이는 것은 `chore DIFFERENT` 쪽이고 최근 실행에서
**2~3개**, 전부 같은 모양이다:

```
chore|Wire@38797|waiting  host=0 client=1
chore|Wire@41845|waiting  host=0 client=1
chore|Wire@38796|waiting  host=0 client=1
```

같은 실행에서 `hp DIFFERENT 0` 이다. **양쪽 다 그 전선이 수리 완료인 걸 아는데, 클라만 수리
작업 표시를 들고 있다.** 보이는 게 다르면 행동이 달라지므로 이건 고칠 값이 있다.

## 원인 (코드에 이미 적혀 있던 것)

게임의 damage 이벤트는 **빼기만** 한다. 수리(음수 델타)는 거절당한다. 그래서 모드는
`ForceHitPoints` 로 **리플렉션으로 필드를 직접 썼다.** HP 는 올라가지만 그게 전부다.

`BuildingHP.Repair(amount)` 를 어셈블리에서 읽으니 HP 를 올린 뒤 **트리거 두 개**를 쏜다 —
변경 하나, 그리고 완전 수리 시 하나. errand 를 끝내는 것이 그 두 번째다. 필드 쓰기는 둘 다 안 쏜다.

## 무엇을 바꿨나

리플렉션보다 **먼저** `buildingHP.Repair(부족분)` 을 부른다. 리플렉션은 그래도 도달 못 했을 때만
남는 마지막 수단으로 둔다. 둘 다 카운터가 있다.

## 합격 조건

| # | 조건 | 뜻 |
|---|---|---|
| 1 | `hpRepaired > 0` | 게임 경로가 발동했다. **0 이면 판정 불가** |
| 2 | `chore DIFFERENT` = 0, 그리고 `Wire@...|waiting host=0 client=1` 행 소멸 | 표시가 일치한다 |
| 3 | `hpForced` = 0 | 리플렉션이 더 이상 필요 없다 |
| 4 | `hp DIFFERENT 0` 유지 | HP 자체는 계속 맞는다 |
| 5 | 클라 `errors = 0` | |

**1 없이 2만 맞으면 판정 불가로 적는다.** `chore DIFFERENT` 는 2~3 짜리 작은 값이라 우연히 0 이
나오는 실행이 있을 수 있다.

3번이 0 이 아니면 **부분 성공**으로 적는다 — 게임 경로가 일부만 처리하고 나머지는 여전히 필드
쓰기라는 뜻이고, 그 나머지는 표시가 계속 남는다.

## 읽지 말아야 할 것

- `chore PEER-ONLY` 는 **안 줄어든다.** 그건 잡동사니 병합이고 이 변경과 무관하다.
  줄지 않는 것을 실패로 읽지 않는다.
- `hp DIFFERENT` 는 전부터 0 이었다. 0 이 유지되는 것은 성공 근거가 아니라 **회귀가 없다는 것**뿐이다.

---

# 2단계 — 수트 반납 errand (같은 모양, 다른 대상)

전선은 닫혔다. 남은 `chore DIFFERENT` 행은 이것이다:

```
chore|Atmo_Suit#1776225778|waiting  host=0 client=1
chore|Atmo_Suit#1776225777|waiting  host=0 client=1
```

`suitApplied=2` 인 실행에만 나오고 `suitApplied=0` 인 실행엔 안 나온다 — **우리 재생이 만든다.**
`SuitLocker.EquipTo` 마지막 줄이 `returnSuitWorkable.CreateChore()` 이고, 클라는 그 errand 를
영영 수행할 수 없다. 호스트는 듀플이 집어서 0 이 되고, 클라는 1 로 남는다.

재생 직후 `returnSuitWorkable.CancelChore()` 를 부른다. 락커의 상태기계 자신이 쓰는 호출이라
errand 와 화면 표시가 같이 사라진다. 클라는 그것이 필요 없다 — 호스트 듀플이 수트를 반납하면
호스트가 unequip 을 보내고 이 패킷이 재생한다.

| # | 조건 | 뜻 |
|---|---|---|
| 1 | `suitChoreCancelled > 0` | 발동했다. **0 이면 판정 불가** |
| 2 | `Atmo_Suit#…\|waiting host=0 client=1` 행 소멸 | 표시가 일치한다 |
| 3 | `suitApplied > 0` 유지 | 취소가 적용 자체를 막지 않았다 |
| 4 | `suitSent = suitApplied + suitNoWorn` 회계 유지 | 오늘 세운 서명 |
| 5 | 클라 `errors = 0` | |

**`suitApplied = 0` 인 실행은 판정 불가로 적는다.** 그 실행엔 수트가 안 움직였으므로 2번이
저절로 맞는다 — 오늘 첫 배치에서 이미 겪은 함정이다.

상태기계가 errand 를 **다시 만들 수 있다.** 그러면 행이 돌아오고, 그건 취소 지점이 틀렸다는 뜻이지
접근이 틀렸다는 뜻은 아니다. 그 경우 SM 쪽을 봐야 한다.

## 2단계 결과 — 실패, 되돌림

3회 실행:

```
suitChoreCancelled   1, 1, 1     매 실행 발동
suitApplied          1, 1, 1     적용은 유지
Atmo_Suit#1776225778|waiting host=0 client=1   남음, 남음, 없음
클라 errors          0, 0, 0
```

**조건 2 실패.** 취소는 발동했는데 행이 그대로다. 이유는 행의 키에 있었다 — `Atmo_Suit#…` 는
**수트 아이템**이고, 내가 취소한 것은 **락커의** `returnSuitWorkable` 이다. 다른 객체의 errand 를 껐다.

`EquipTo` 본문을 읽고 "마지막 줄이 `CreateChore()` 니 그것이 원인"이라고 이었는데, 그 chore 가
어느 객체의 `Prioritizable` 을 올리는지는 확인하지 않았다. 오늘 `GameTags.Plant` 에서 한 것과 같은
종류의 비약이다.

**다음 사람에게:** 고칠 대상은 수트 아이템에 붙은 errand 다. 무엇이 그 `refCount` 를 올리는지부터
잰다 — `Prioritizable.AddRef` 호출자를 세거나, 그 객체의 chore 목록을 덤프한다. 락커 쪽이 아니다.
