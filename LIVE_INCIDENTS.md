# 라이브 세션 사건 기록

랩에서 재현되지 않은 것들의 목록. 시나리오 하네스는 사람이 하는 조작(인쇄기 선택, 화면 열기)을
대신할 수 없고, 오늘 가장 심각한 두 건이 정확히 그 영역에서 나왔다.

**규칙:** 여기 적는 것은 **관찰된 것과 증거**뿐이다. 원인 추정은 "추정"으로 표시하고,
확정된 것만 기전을 단정한다. 로그가 없으면 없다고 쓴다 — 채워 넣지 않는다.

---

## 2026-08-12 세션 (배포본 `3021F766F795B56C`)

### 1. 호스트 크래시 — 인쇄기 화면 (확정, 수정됨, 배포 대기)

**23:53:55** 호스트가 스스로 종료. 세션이 같이 죽었다.

```
NullReferenceException at Klei.AI.Modifier.AddTo (Attributes)
  Klei.AI.Traits.Add <- MinionStartingStats.ApplyTraits
  CharacterContainer.SetAnimator <- CharacterContainer.SetMinion
  ImmigrantScreenPatch.ApplyOptionsToScreen        (ImmigrantScreenPatch.cs:165)
  ImmigrantScreenInitializePatch.Postfix
  ImmigrantScreen.InitializeImmigrantScreen <- TelepadSideScreen 클릭
그 뒤: GameServer Stopped -> Game.OnApplicationQuit
```

**기전 확정:** `Util.KInstantiateUI` 의 `force_active` 를 넘기지 않아 컨테이너가
**부모의 그 순간 활성 상태에 의존**했다. 화면 초기화 중에는 부모가 비활성이고, 비활성 컨테이너에
`SetMinion` 을 부르면 특성이 인스턴스가 아니라 **프리팹**에 붙어 터진다.

**간헐적이다.** 사용자 보고: 이후 양쪽에서 듀플을 뽑아도 안 터진다. 부모가 활성인 타이밍이면
정상 동작하므로 관찰과 모순되지 않는다 — **수정이 타이밍 의존 자체를 없앤다.**

**수정:** `force_active: true` (`ImmigrantScreenPatch.cs`). 파일 주석이 이미
*"SetMinion needs the container live"* 라고 결론을 적어놨고 **코드가 그걸 안 하고 있었다.**

**테스트가 못 잡은 이유:** `ImmigrantScreenTests` 가 이 호출을 그대로 미러링하는데, 이미 표시된
화면에서 돌아 자식이 활성 부모를 상속받는다. 테스트 주석이 그 차이를 적어놨다. 그래서 예외를
기다리는 대신 **`SetMinion` 전 `activeInHierarchy` 를 단정**하도록 바꿨다 — 전제 조건 단정은
두 경로 모두에서 성립한다.

### 2. 클라 크래시 — 원인 미확정

**23:58:15** 경, 클라(Jerrie, 192.168.45.135)가 세션에서 이탈.

```
(Lan/Riptide): Client 2 (192.168.45.135:51022) disconnected: Disconnected.
```

**증거 없음.** 클라 `Player.log` 를 가져오지 못했다 — PC-B 의 `peer-agent.ps1` 이 응답하지 않는다
(`push-log` 180초, `ping` 60초 둘 다 무응답으로 **측정**됨). 클라 ONI 를 새 DLL 로 재시작할 때
에이전트 창이 닫힌 것으로 보인다.

**1번과 같은 것이라고 단정하지 않는다.** 사용자는 이후 듀플 선택이 정상이라고 보고했다.
세션 종료 후 에이전트를 다시 띄우고 클라 로그를 받아 확인해야 한다:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
\\KYLE\onimp\peer-agent.ps1 -Share \\KYLE\onimp
```

### 3. 금속 정련소: 클라 주문이 0으로 보임 (기전 확정, 수정 설계됨)

**일은 실제로 됐다.** 호스트 로그:

```
13:24:08  SetRecipeQueueCount count=1     <- 클라 패킷 적용
13:24:16  SetRecipeQueueCount count=2     <- 클라 패킷 적용
13:24:39  MetalRefinery 안 DirtyWater 생성  <- 1회차 완료
13:26:37  MetalRefinery 안 DirtyWater 생성  <- 2회차 완료
```

받은 `count` 는 보낸 쪽의 자기 값이므로 **클라 로컬 큐도 1, 2 로 정상 증가**했다.

**기전:** 클라 조립기를 막는 것은 `SpawnOrderProduct` 하나뿐이다. 주문 진행·완료·**큐 소비**는
클라에서도 돈다(계측 `productsBlocked` 가 그 증거). 그래서 클라 큐는 제품 없이 0으로 빠지고,
**레시피 큐에 권위자도 키프레임도 없어서** 아무것도 교정하지 않는다.

**수정 방향:** 조립기는 `Storage` 를 갖고 있으므로 레시피 큐를 `StorageStateSyncer` 의 optional
값에 실으면 델타 + 15초 키프레임을 그대로 얻는다. **"객체당 syncer 하나" 제약을 우회하지 않고 피한다.**

**부수 발견:** `SetRecipeQueueCount` 에 동일한 Postfix 패치가 둘
(`ComplexFabricatorSideScreenPatches.cs`, `StorageSideScreenPatches.cs`) — 큐 변경마다 같은 패킷이
두 번 나간다. 하나로 합칠 것.

### 4. 연구가 클라에서 절반으로 표시 (기전 확정, 한 줄 수정)

호스트는 기초 연구 완료, 클라는 중간 퍼센트에서 멈춤.

**두 경로가 모두 막혀 있다:**

- `SyncResearch()` — 완료 연구 전체(`UnlockedTechIds`)를 담아 보내도록 다 짜여 있고 클라 적용
  경로(`ProcessResearchState`)까지 있는데 **호출자가 없다**. 주기 디스패처
  (`WorldStateSyncer.cs:233-239`)는 `SyncResearchProgress()` 만 부른다. 즉 접속 시 요청-응답으로
  한 번 받고 그 뒤엔 **진행률 퍼센트만** 온다.
- `ResearchCompletePatch` — `TechInstance.Purchased()` 에 걸려 있고 그 메서드는 실제로 존재하지만
  (`api TechInstance` 로 확인) **세션 전체에서 0회 발동.** ONI 는 `Research.AddResearchPoints`
  내부의 다른 경로로 완료를 처리한다.

**수정:** 디스패처 회전에 `SyncResearch()` 를 넣는다. 이벤트 훅에 의존하지 않으므로 ONI 내부
경로가 무엇이든 무관하다.

**상태 (2026-08-13): 적용됐으나 아직 검증되지 않았다.** 이 수정은 b122~b125 빌드에 이미 실려
있었는데, `[state] research` 행이 그 세 빌드 내내 `DIFFERENT 1` 로 고정이다. 수정 **이전**의
research 측정값이 없어서(상태 덤프가 이 수정보다 나중에 들어왔다) 1→0 을 보인 적이 없다.
즉 규칙 1 의 전/후 대조가 성립하지 않는다.

남은 그 1건이 무엇인지도 아직 모른다 — `compare-state.ps1` 은 다른 행의 예시를 출력하지만
`soak.ps1` 의 필터가 `shared` 요약 줄만 걷어가서 **예시가 요약에 실리지 않는다.** 같은 파일에
적힌 "요약에 안 올라가는 판정은 없는 것과 같다" 를 새 자리에서 또 밟았다. 예시를 요약에
싣는 것이 이 항목의 다음 단계다.

**게임 영향:** 클라의 연구 트리가 미완료로 보이면 그 기술로 해금되는 건물을 놓을 수 없다.
우회: 해당 건물은 호스트에서 짓는다.

---

## 공통 패턴

3번과 4번, 그리고 오늘 랩에서 고친 저장고가 **같은 형태**다:

> 상태를 **이벤트로만** 알리고, 어긋났을 때 되돌아볼 방법이 없다.

저장고는 키프레임을 넣어 질량 불일치를 99 → 6, 5kg 이상 16 → 0 으로 닫았다. 레시피 큐와 연구는
같은 처방이 필요하고, 문·토글·배달량도 같은 상태로 남아 있다(`BuildingFlagsSyncer` 참조 —
"객체당 syncer 하나" 제약 때문에 붙이지 못했고, 패킷에 syncer 구분자가 선행 작업이다).

`REFACTOR_BACKLOG.md` 2-5 의 `ReplicationPolicy` 가 겨누는 것이 이것이고, 이제 실제 사례가 넷이다.
