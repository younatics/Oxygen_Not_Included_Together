# ONI Together — 멀티플레이어 안정성 딥 오딧

- **대상** `Lyraedan/Oxygen_Not_Included_Together` @ `34d8207` (main, 2026-07-29) — 489 C# 파일 / 60,778 LOC
- **범위** Phase 1–3 (아키텍처 + 랭킹된 버그 리스트). **코드 수정 없음** (사용자 선택)
- **방법** 정적 추적 3축 병렬 + 내 직접 재검증. 게임 설치본이 없어 실행 재현은 불가
- **판정 규칙** 실패하는 실행 경로를 문장으로 쓸 수 있을 때만 "버그". 못 쓰면 OBSERVATION 으로 강등, 반증되면 폐기

---

## 1. 아키텍처

```
NetworkingComponent.Update()            ← Unity Update, 모든 것의 펌프
├ IsHost   → GameServer.Update()
│             ├ TransportServer.Update() / .OnMessageRecieved()
│             └ SaveFileTransferManager.CheckForLostChunks()
└ IsClient → GameClient.Poll()
              ├ TransportClient.Update()                     ← 항상
              └ State ∈ {Connected, InGame} → OnMessageRecieved()   ← 조건부
```

### Transport — 2종, 그리고 그 비대칭이 버그의 원천
| | Steam | LAN |
|---|---|---|
| 구현 | `Transport/Steamworks/*` | `Transport/Riptide/*` |
| 프로토콜 | SteamNetworkingSockets | **Riptide (UDP)** |
| 최대 payload | Steam 내부 처리 (~1200 B MTU) | **`MAX_PAYLOAD_BYTES = 1000`** → 초과 시 `ChunkedPacket` 분할 |
| 로딩 중 끊김 | `LoadingWorld` 가드 **있음** | 가드 **없음** |
| host 등록 | `ConnectedPlayers[id] =` (인덱서) | `.Add(1, …)` + host id **하드코딩 1** |

**이 200 바이트 차이(1200 vs 1000)가 최상위 버그 2개를 만든다.** 여러 syncer 가 "Steam MTU 에 맞춘다"고 주석에 적고 1100 B 패킷을 만드는데, LAN 에서는 그게 조각화된다.

호스트는 자기 서버에 자기 클라이언트로도 접속한다 (listen-server).

### 직렬화 / Dispatch
`SerializePacketForSending` = `int packetType` + `packet.Serialize(writer)`.
**헤더에 sequence·tick·sender 가 없다** → 손실·중복·재정렬을 코드가 감지할 수단이 원천적으로 없다.
`PacketHandler.HandleIncoming` → `readyToProcess` 게이트 → 타입 → `Create` → `Deserialize` → `packet.OnDispatched()`. 핸들러 로직은 패킷 클래스 자신에 있다.

### 전송의 두 갈래 (순서 위험의 근원)
- `IBulkablePacket` → **(connection, packetId) 별로 독립된 큐 + 독립된 interval 시계**, 각각 50~1000 ms
- 그 외 → 즉시 전송

→ 같은 프레임에 만든 두 패킷의 도착 순서가 뒤집힌다 (#10).

### 상태 복제
주기 syncer: `WorldStateSyncer`(824), `PlantGrowthSyncer`(593), `LogicStateSyncer`(439), `ConduitFlowSyncer`(393), `BuildingSyncer`, `AnimStateSyncer`, `StructureStateSyncers/`(7).
엔티티 생성(`KInstantiate`)은 **복제되지 않는다** — 패치가 주석 처리됨. 그래서 양쪽이 각자 객체를 만들고 **각자 ID 를 발급**한다. 이것이 #1 을 치명적으로 만드는 전제다.

### 참가 플로우 — 불안정의 핵심 경로
```
접속 → SaveFileRequest → 호스트가 SaveLoader.Save() 로 세이브 생성 → 전송(TCP 또는 UDP 청크)
클라: 디스크에 쓰기 → ReadyStatus(Loading) → 연결 끊기 → readyToProcess=false
     → LoadScreen.DoLoad (큰 콜로니면 수 분) → 새 Riptide ID 로 재접속
호스트: _loadingClients / _reconnectedFromLoad 로 재접속 여부 추적
```

### 스레드 경계
| 경계 | 안전한가 |
|---|---|
| Riptide → 메인 (`_incomingPackets` ConcurrentQueue) | 큐는 안전, **세션 간 미청소 · 드레인 예산 없음** |
| TCP 전송 스레드 → 메인 (`MainThreadExecutor`) | **불안전** — 락 없는 `List<Action>` |
| `BuildingConfigPacket.IsApplyingPacket` | **불안전** — `Task.Run` 스레드풀이 non-volatile static bool 을 씀 |
| TCP handler | 클라이언트당 `Thread` 1개, 상한 없음 |

---

> ⚠️ **§2 는 transport·determinism 두 축의 결과다. 세 번째 축(게임플레이/스케일링)이 §9 에 추가되었고, 그중 2건이 아래 #1 보다 상위다. 최종 통합 순위는 §9 끝의 마스터 표를 본다.**

## 2. Top 10 — severity × confidence 순위 (축 1·2)

### #1 엔티티 ID 를 시뮬레이션이 매 틱 바꾸는 float 에서 파생한다 — CRITICAL / High
**FILE** `ONI_Together/Networking/NetIdHelper.cs:66`

```csharp
hash = hash ^ go.GetProperName().GetHashCode() ^ primaryElement.ElementID.GetHashCode()
     ^ primaryElement.Mass.GetHashCode() ^ primaryElement.Temperature.GetHashCode();
```

**BUG** `Mass`·`Temperature` 는 sim 이 매 틱 바꾸는 `float` 다. `float.GetHashCode()` 는 **비트 패턴**이라 1 ULP 차이가 완전히 다른 ID 를 만든다.

**WHY** 두 시뮬레이션이 떨어진 광석의 온도를 소수점 끝자리까지 일치시킬 방법은 없다. 게다가 엔티티 생성이 복제되지 않으므로(위 참조) 양쪽이 **각자** `NetworkIdentity.OnSpawn` 에서 ID 를 발급한다 → `Building`·`Workable` 이 아닌 **모든 객체**(광석·음식·씨앗·알·크리터·바닥 아이템)가 호스트와 클라에서 **서로 다른 NetId** 를 갖는다.

**TRIGGER** LAN 2인. 타일 하나를 캔다. 양쪽이 같은 셀에 광석 `Pickupable` 을 생성 — 호스트 온도 `293.15000`, 클라 `293.14998`.

**SYMPTOM** 호스트 복제인간이 광석을 집으면 `PickupItemPacket{NetId=호스트ID}` 가 가고, 클라는 `TryGetComponent` 실패로 **조용히 `return`** 한다. 클라의 광석은 영원히 사라지지 않는다. 유령 아이템이 계속 쌓이고 클라 측 chore 가 그것을 계속 예약한다. **아이템 처리량에 비례해 악화.**

**FIX** 가장 작은 수정: 66행에서 `Mass`·`Temperature`·`GetProperName()` 항을 제거하고 `prefabID ^ cell ^ elementID` 만 남긴다. 근본 수정: ID 를 호스트가 단독 발급(`Interlocked.Increment`)해 spawn 패킷에 실어 보내고, 클라는 그 패킷으로만 객체를 만든다.

---

### #2 하드싱크·세이브 요청이 월드를 N+1 번 동기 저장해 호스트를 멈춘다 — CRITICAL / High / **콜로니 규모에 비례**
**FILE** `Misc/World/SaveHelper.cs:367-374` · `Packets/World/SaveFileRequestPacket.cs:57,96` · `Misc/World/GameServerHardSync.cs:67`

```csharp
public static byte[] GetWorldSave() {
    var path = SaveLoader.GetActiveSaveFilePath();
    SaveLoader.Instance.Save(path);      // 전체 월드를 디스크에 동기 저장
    return File.ReadAllBytes(path);
}
```

호출 지점: `SendSaveFile` (**클라이언트당 1회**), UDP 폴백에서 1회, 그리고 `HardSyncCoroutine:67` 에서 **단지 `.Length` 를 읽기 위해** 또 1회.

**BUG** 클라 N명 하드싱크 = **전체 월드 동기 저장 N+1 회**. 전부 메인 스레드다. `SendSaveFile` 은 `_server.Update()` 의 인라인 `MessageReceived` 콜백 **안에서** 실행된다.

**WHY** 저장 중 호스트는 `_server.Update()` 를 돌리지 못한다 → Riptide 하트비트가 멈춘다 → 클라가 `TimeoutTime`(기본 30초)에 걸린다. 후기 콜로니에서 저장 1회가 8초면 클라 3명 = **32초 동결** → 전원 타임아웃.

**TRIGGER** 후기 콜로니에서 하드싱크 1회, 또는 클라 2명이 동시에 참가.

**SYMPTOM** 사용자가 보는 바로 그 **"Host Lost"**. 초기 콜로니에서는 재현되지 않는다. Steam·LAN 양쪽 공통.

**FIX** 하드싱크 1회당 `byte[]` 를 **한 번만** 만들어 재사용한다 (`GameServerHardSync` 가 캐시해 `SendSaveFileToAll` 에 넘김). `.Length` 를 위한 67행 호출은 캐시 참조로 대체 — 이 한 줄만 지워도 저장 1회가 사라진다.

---

### #3 파이프/가스 동기화 패킷이 LAN 한도를 넘어 "신뢰성 없는 조각"으로 쪼개진다 — CRITICAL / High / **LAN 전용**
**FILE** `Networking/Components/ConduitFlowSyncer.cs:37-39,191` + `Transport/Riptide/RiptidePacketSender.cs:12,26-29,88`

```csharp
// 50 * 22 = 1100 bytes, fits Steam P2P unreliable MTU (~1200 B) without fragmentation.
private const int MAX_UPDATES_PER_PACKET = 50;
...
PacketSender.SendToAllClients(packet, PacketSendMode.Unreliable);
```

**BUG** 실제 와이어 크기 = 4(타입) + 4(count) + 50×22 = **1108 B**. Riptide 한도는 1000 → `SendChunked` 가 분할하고, **`sendType` 을 그대로 조각에 전파한다**(`:88`). 즉 **신뢰성 없는 조각들**이 된다.

**WHY** `ChunkedPacket.OnDispatched` 는 모든 조각이 모여야 재조립한다. Unreliable 조각은 재전송되지 않으므로 **조각 1개 손실 = 그 논리 패킷 영구 소실 + `_pendingChunks` 항목 영구 잔존**(만료 로직 없음). 4.5초마다 도는 `FORCE_REFRESH_INTERVAL` 전체 재전송조차 같은 경로라 **자기 치유가 불가능**하다.

**TRIGGER** LAN 호스트, 중반 콜로니. 한 틱에 45개 이상 파이프 셀 내용이 바뀌면(45×22+8 = 998 이 정확한 임계) 조각화 시작. 실제 기지에서는 4.5초 refresh 가 항상 50개 만원 패킷을 만든다.

**SYMPTOM** 클라의 파이프 내용(질량·온도·원소)이 멈추거나 틀리고 시간이 갈수록 악화. 메모리 지속 증가. **Steam 에서는 재현되지 않는다** — 사용자가 LAN 에서도 겪는 이유가 이것이다.

**FIX** ① `RiptidePacketSender.SendPacket` 에서 `SendChunked` 경로는 **무조건 Reliable 로 승격**(조각난 메시지에 "unreliable" 은 의미가 없다). ② `IsLanConfig()` 일 때 `MAX_UPDATES_PER_PACKET = 44` 로 낮춰 애초에 조각화되지 않게. ③ `ChunkedPacket` 에 TTL 스윕 추가.

---

### #4 세이브 전송에 전송 식별자가 없어 두 스냅샷이 한 버퍼에 섞인다 — CRITICAL / High
**FILE** `Packets/World/SaveFileRequestPacket.cs:139,160` · `Networking/SaveFileTransferManager.cs:56,68,83` · `Misc/World/SaveChunkAssembler.cs:92-124,226-246`

```csharp
string transferId = fileName.Replace(" ", "_").Replace(".sav", "");   // 전송마다 동일 = 월드 이름
ActiveTransfers[key] = transfer;          // 덮어쓰기
transfer.ChunkSent[chunkIndex] = true;    // 바운드 체크 없음
...
int chunkSize = chunk.Chunk.Length;       // 먼저 "도착한" 청크에서 추론
```

**BUG 세 가지가 맞물린다.**
1. **취소 없음**: 클라의 `RequestSpecificChunks` 가 재요청하면 호스트는 `GetWorldSave()` 로 **달라진 콜로니의 새 스냅샷**을 만든다. 이전 `StreamChunks` 코루틴은 취소되지 않고 계속 돈다 → 같은 `fileName` 키 아래 **길이가 다른 두 파일이 동시 스트리밍**.
2. **크기 검증 없음**: `ReceiveChunk` 는 `chunk.TotalSize` 를 기존 `save.TotalSize` 와 비교하지 않는다. 새 세이브가 더 크면 `Buffer.BlockCopy` 가 버퍼를 넘겨 `ArgumentException`.
3. **청크 크기 추론**: 마지막 청크만 길이가 짧다. Riptide `Reliable` 은 **순서를 보장하지 않고**, 각 청크가 다시 다중 조각 재조립이라 완성 순서는 사실상 임의다. 짧은 꼬리 청크가 먼저 완성되면 `TotalChunks` 와 이후 모든 `chunkIndex` 가 틀어져 **`IsComplete()` 가 영원히 true 가 되지 않는다** (데이터는 `chunk.Offset` 으로 정확히 채워지는데 장부만 틀린다).

**TRIGGER** 30초×3 정지 감지에 걸린 전송 → 전체 재전송 요청 → 그 사이 콜로니가 자람.

**SYMPTOM** "Downloading save file 9x%" 영구 정지, 로그에 `Requesting full resend` 반복, 매 라운드마다 호스트가 월드를 다시 저장하며 전원 수 초 동결 — **전형적인 참가 루프**.

**FIX** `transferId` 를 호스트 단조 카운터로 유일하게. 청크 패킷에 `ChunkSize`·`TotalChunks` 를 **명시적으로 실어** 추론을 없앤다(정답은 이미 `SecureTransferPacket.SequenceNumber` 에 있는데 `ReceiveChunk` 로 넘길 때 버려진다). `Offset + Chunk.Length <= TotalSize` 검증. 새 전송 시작 시 기존 코루틴 취소.

---

### #5 하드싱크가 추측 타이머로 끝나고, 클라는 멈추지 않고 호스트는 재개하지 않는다 — CRITICAL / High
**FILE** `Misc/World/GameServerHardSync.cs:34,40,60,67-78` · `Packets/Core/HardSyncPacket.cs:29-34` · `Packets/Core/HardSyncCompletePacket.cs:27`

```csharp
float estimatedTransferDuration = chunkCount * SaveFileRequestPacket.SAVE_DATA_SEND_DELAY;
yield return new WaitForSecondsRealtime(estimatedTransferDuration * numberOfClientsAtTimeOfSync);
hardSyncInProgress = false;
//SpeedControlScreen.Instance?.Unpause(false);   ← 3개 종료 경로 전부 주석
```

**BUG 네 가지.**
1. **중복 실행**: `hardSyncInProgress = true` 가 코루틴 **안**(`:60`)에서 설정되는데 가드는 `PerformHardSync` **입구**(`:34`)에 있다. 두 번 빠르게 트리거하면 둘 다 통과한다.
2. **추측 종료**: 실제 전송이 추정보다 길면(세이브는 콜로니와 함께 커지는데 `SAVE_DATA_SEND_DELAY` 는 고정) 청크가 아직 날아가는 중에 가드가 열린다.
3. **클라가 안 멈춘다**: `HardSyncPacket.OnDispatched` 는 커서만 숨기고 플래그만 세운다. `PauseScreenPatch` 는 세션 중 모달 일시정지를 억제한다. → **호스트가 멈춘 동안 클라는 계속 시뮬레이션한다** — 스냅샷 동기화가 요구하는 것과 정반대.
4. **호스트가 안 재개한다**: 세 종료 경로 모두 unpause 가 주석. `HardSyncCompletePacket` 은 `GameClient.IsHardSyncInProgress` 도 안 지운다.

**SYMPTOM** 클라가 곧 받을 스냅샷보다 앞서 진행하고, 받은 세이브가 되감는다. 그 창 동안 클라가 보낸 패킷은 이미 존재하지 않는 호스트 상태를 서술한다. 하드싱크 후 상태는 **어느 쪽의 것도 아니다.**

**FIX** 타이머를 명시적 완료 신호로 교체 — 전원 `Ready` 일 때만 해제(+ 하드 타임아웃은 명시적 abort). `hardSyncInProgress = true` 를 코루틴 시작 **전**으로. `HardSyncPacket.Sync()` 에서 클라도 일시정지하고 `HardSyncCompletePacket` 에서 해제 + 플래그 clear.

---

### #6 원소 그리드 델타: 그림자를 먼저 갱신하고 신뢰성 없이 보낸다 — HIGH / High
**FILE** `Networking/Components/WorldStateSyncer.cs:806-807` + `Misc/World/WorldUpdateBatcher.cs:93,110`

```csharp
_shadowElements[cell] = currentElement;   // 먼저 "보냈다"고 기록
_shadowMass[cell] = currentMass;
...
PacketSender.SendToAllClients(packet, sendType: PacketSendMode.Unreliable);
```

**BUG** 그림자가 이미 새 값이므로 이후 스캔에서 `changed == false` → **델타가 다시 만들어지지 않는다**. 원소 그리드에 절대값 재동기 경로가 없다(배경 순회 스캔도 같은 그림자와 비교한다).

**WHY** UDP 1개 손실 = 그 셀의 원소/질량이 **영구히 틀림**. `WorldUpdateBatcher` 는 바이트 예산이 있어 조각화는 안 되지만(#3 와 달리), 배치가 512B/1KB 예산을 채우므로 1개 손실이 약 95개 셀을 함께 가져간다.

**SYMPTOM** 클라에는 진공인데 호스트에는 가스가 있음. 이후 건물 `Operational`, 복제인간 경로탐색(호흡 가능성), `ConduitFlow` 가 그 셀들로부터 연쇄 divergence. **sim 활동량에 비례해 악화.**

**FIX** **원소 종류 변경**은 `Reliable`, 질량/온도 미세 드리프트만 `Unreliable`. 또는 전송 확정 후에 그림자를 갱신 (`_dirtyPending` 유지).

---

### #7 에코 억제 플래그를 스레드풀 타이머로 해제한다 — HIGH / High
**FILE** `Networking/Packets/World/BuildingConfigPacket.cs:111-121`

```csharp
IsApplyingPacket = true;
ApplyConfig(identity.gameObject);
finally {
    RefreshSideScreenIfOpen(identity.gameObject);
    Task.Run(async () => { await Task.Delay(15); IsApplyingPacket = false; });
}
```

**BUG** ~30개 side-screen 패치가 의존하는 억제 플래그를 **스레드풀에서, 15 ms 벽시계 뒤에** 끈다. non-volatile `static bool` 을 메인 스레드가 읽고 스레드풀이 쓴다.

**WHY 두 가지 실패.**
- **(a) 피드백 루프**: 패킷 A 가 t=0 에 적용하고 t=15 ms 해제를 예약. 패킷 B 가 t=10 ms 에 도착해 플래그를 켠다. t=15 ms 에 A 의 task 가 B 실행 중에 플래그를 **끈다** → B 의 setter postfix 가 억제되지 않고 **새 패킷을 발행** → 호스트가 되돌려 방송(`:127`) → 핑퐁. `Sender == LocalID` 체크는 1홉 에코만 막고 2노드 루프는 못 막는다.
- **(b) 정당한 로컬 편집 삼킴**: 그 15 ms 안에 수신자가 슬라이더를 만지면 조기 return 되어 **전송되지 않는다** → 그 건물 설정이 영구 불일치.

**TRIGGER** 자동화 임계값 2개를 15 ms 안에 변경(설정 복사 드래그가 버스트를 만든다), 또는 설정 패킷이 도착하는 순간 슬라이더 조작.

**SYMPTOM** 임계값/필터가 두 값 사이를 진동. 또는 한쪽 설정이 조용히 전파되지 않음. **자동화 규모에 비례.**

**FIX** 타이머를 없애고 메인 스레드 전용 **깊이 카운터**(`int`, try/finally)로. UI 갱신 지연이 필요하면 `Task.Run` 대신 `MainThreadExecutor` 경유.

---

### #8 벌크 배치에서 항목 1개가 던지면 나머지 499개가 조용히 사라진다 — HIGH / High
**FILE** `Networking/Packets/Core/BulkSenderPacket.cs:71-80`

```csharp
foreach (var packetData in SerializedInnerPackets) {
    var innerPacket = PacketRegistry.Create(InnerPacketId);
    innerPacket.Deserialize(reader);
    innerPacket.OnDispatched();       // throw → foreach 전체 중단
```

**BUG** 루프 본문에 항목별 오류 경계가 없다. 예외는 transport 경계(`RiptideServer.cs:199`)에서 **로그 한 줄로 삼켜진다** — 배치가 절단됐다는 표시가 없다.

**WHY** `MaxPackSize` 가 `PickupItemPacket`·`StorageItemPacket` 등에서 **500** 이다. 항목 #3 에서 NRE 가 나면(`PickupItemPacket.DisplayFX` 는 파괴 중인 객체에 `Def.GetUISprite` 를 호출한다) 나머지 497개 pickup 이 클라에서 **파괴되지 않는다**. 이 패킷들이 "삭제" 방향이므로 결과는 유령 아이템 누적이다.

**TRIGGER** 500개 배치 중 낡은 NetId 항목 1개. **#1 이 살아 있으면 낡은 NetId 는 상시 상태다.**

**FIX** 항목별 `try/catch` + `InnerPacketId`·인덱스 로그 + `continue`. 절단 카운터 노출. `HostBroadcastPacket.OnDispatched`(`:62-70`)와 `PacketHandler.Dispatch`(`:77`)에도 동일 적용.

---

### #9 레지스트리 별칭화: Unregister 가 소유자를 확인하지 않는다 — HIGH / High
**FILE** `Networking/NetworkIdentityRegistry.cs:35-40,43-56` + `Components/NetworkIdentity.cs:104-109` + `NetIdHelper.cs:25`

```csharp
public static void Unregister(int netId) { identities.Remove(netId); }   // 누구 것이든 지운다
public static void RegisterExisting(...) { if (!identities.ContainsKey(netId)) { … } }  // 중복이면 조용히 무시
```

**BUG** 객체 B 가 A 의 ID 와 충돌하면 B 는 `IsRegistered = true` 가 되지만 레지스트리는 여전히 A 를 가리킨다. **B 가 파괴되면 `Unregister(id)` 가 A 의 항목을 지운다.** A 는 살아 있는데 영구히 주소 불능이 된다.

**충돌은 규모에서 흔하다**: `GetDeterministicBuildingId`(`:25`)는 `cell ^ prefabIDHash ^ objectLayerHash` 이고 **breakoff 루프가 없다**. 주석의 "Range: 1,000,000,000+" 는 사실이 아니다 — hashcode XOR 은 범위 보장이 없어 건물 ID 가 엔티티 ID·`Guid` 파생 ID 와 같은 32비트 공간에서 충돌한다.

**SYMPTOM** 건물 하나가 "먹통"이 된다 — 이후 그 건물의 모든 `BuildingConfig`/`LogicState`/`AnimState` 트래픽이 조용히 드롭된다. **건물 수에 대해 2차로 증가.**

**FIX** `Unregister` 를 소유자 확인형으로: `if (identities.TryGetValue(netId, out var cur) && cur == entity) identities.Remove(netId);` `RegisterExisting` 은 `bool` 반환 + 충돌 로그. ID 공간을 상위 비트로 namespace 분리. `GetDeterministicBuildingId` 에도 breakoff 추가.

---

### #10 벌크 큐가 타입별 독립 시계를 가져 Dig→Cancel 을 Cancel→Dig 로 뒤집는다 — HIGH / High
**FILE** `Networking/Packets/Architecture/PacketSender.cs:37-38,85,111,206-210` + `Packets/Tools/DragToolPacket.cs:22-23`

**BUG** 큐가 **(connection, packetId) 별**이고 각각 자기 `PacketUpdateRunner` 시계를 갖는다. `CanDispatchNext` 는 이전 기록이 없으면 **즉시 true** 를 반환한다(`:37-38`).

**TRIGGER — 사용자가 물어본 바로 그 패턴 (호스트 A→B, 클라 B→A)**
1. 클라 A 가 셀 C 를 파고, 이어서 취소한다. 둘 다 즉시·순서대로 호스트 도착. 호스트 상태: **C 에 dig 없음**.
2. 클라 B 로 재방송: `DigPacket` 큐는 20 ms 전에 flush 했으므로 80 ms 더 막힌다. `CancelPacket` 큐는 기록이 없어 **다음 tick 에 즉시 flush**.
3. 클라 B 는 **Cancel → Dig** 순으로 받는다. cancel 은 아무것도 못 찾고, 그 다음 dig 가 놓인다.

**SYMPTOM** 클라 B 에는 호스트가 "없다"고 보는 셀에 영구 dig 명령이 남는다. **패킷 손실이 전혀 없어도 발생한다.** 게다가 `kvp.Value.Keys.ToList()`(`:85`) 의 **Dictionary 열거 순서**가 한 tick 안의 flush 순서를 정하므로 실행마다 달라진다.

**FIX** connection 당 `(packetId, payload)` **단일 FIFO** 로 통합해 삽입 순서로 drain. 헤더에 단조 `seq` 를 넣고 수신측이 순서 위반을 유예/거부. 임시 조치로 변경성 tool 패킷의 `IntervalMs => 0`.

---

## 3. 11–18위 (요약)

| # | Sev | 위치 | 내용 |
|---|---|---|---|
| 11 | HIGH | `RiptideClient.cs:130-152` | 로딩 중 끊김 가드가 **LAN 에만 없다**. `SteamworksClient.cs:240-244` 는 `LoadingWorld` 면 무시하는데 Riptide 는 `default:` 로 빠져 `OnReturnToMenu` → 메인 메뉴 강퇴. 수 분짜리 로드 중 어떤 이유든 끊기면 세이브 전송을 처음부터 다시. |
| 12 | HIGH | `RiptideServer.cs:267-282,295-313` | `_loadingClients` 가 `TimeoutSeconds`(30초) 후 조용히 만료 → 로드가 30초 넘는 콜로니는 재접속이 **신규 참가로 처리되어 세이브 재전송** → 무한 로드 루프. 또 `AddClientToList` 는 열거 순서로 **임의 항목**을 소비해 2명 동시 로딩 시 엉뚱한 클라를 재접속자로 표시. **콜로니 규모에 비례.** |
| 13 | HIGH | `SaveHelper.cs:62-66,415-419` | `ReadyStatus(Loading)` 를 보낸 **같은 프레임에** `Disconnect()`. Riptide 재전송은 `Update()` 틱이 필요한데 `_client` 가 null 이 된다 → **단 1회 미확인 전송**. 손실되면 호스트가 "player left" 로 처리해 `PauseSimOnPlayerLeft()` 로 콜로니를 멈춘다. |
| 14 | MEDIUM | `DebugTools/PacketTracker.cs:34-36,145` | 5만 슬롯 링 버퍼가 `IPacket` **객체 강참조**를 보관한다. 게이트 없이 모든 송수신에서 호출 → 세이브 전송 중 256 KB `PayloadBytes` 를 든 패킷들이 수백 MB 를 프로세스 끝까지 붙잡는다. 세션 종료 시 `Clear()` 없음. |
| 15 | MEDIUM | `Components/MainThreadExecutor.cs:14,36,52-62` | 락 없는 `List<Action>` 을 TCP 다운로드 스레드가 `Add`, 메인 코루틴이 `RemoveAt(0)` → 항목 유실/`IndexOutOfRangeException` → `Execute` 체인 사망 후 **이후 모든 marshalled 작업 소실**. 처리량은 **0.5초당 1건** 하드캡. `QueueEvent(bool, …)` 는 `bool` 을 값으로 캡처해 `WaitUntil` 이 상수를 기다린다(영구 대기). |
| 16 | MEDIUM | `WorldStateSyncer.cs:229-231` | `catch (System.Exception) { /* Silently ignore */ }` 가 staggered sync switch 전체를 감싼다. 어떤 sync 케이스가 지속적으로 던지면 **그 서브시스템이 영구히 복제되지 않는다, 진단 0.** 다른 모든 버그를 가리는 최악의 종류. |
| 17 | MEDIUM | `RiptideClient.cs:31,171` | `_incomingPackets` 는 static 이고 **어디서도 비우지 않는다**. 로드 직전 잔여 패킷이 재접속 후 **새로 로드된 월드에 적용**된다(`NetworkIdentityRegistry.Clear()` 로 ID 매핑이 리셋된 뒤 옛 ID 참조). `while(TryDequeue)` 에 프레임 예산도 없다. |
| 18 | MEDIUM | `SteamworksServer.cs:133-143` | Steam **서버** 수신 루프만 `try/catch` 가 없다(클라·Riptide 양쪽은 있다). throw 시 `messages[i..]` 가 **네이티브 leak** 되고 그 패킷들이 소실. Steam 전용. |

**그 외 확인된 소규모**: `DateTime.Now`(벽시계)로 전송 타임아웃 판정 → NTP/DST 보정이 오판 유발 (`SaveChunkAssembler`·`SaveFileTransferManager` 전반) · `SaveChunkAssembler.ReceiveChunk` 가 청크마다 O(N) 순회 + 30회 문자열 연결 + 오버레이 갱신 → 전송 전체로 **O(N²)** (LAN 최소 청크 1 KB 라 50 MB 세이브면 N≈51,200) · `RiptideServer` 정적/인스턴스 필드 혼용으로 Start 실패 후 stale `_server` 가 **재호스팅을 조용히 차단** · `RiptideClient.cs:113-120` 호스트 ID 하드코딩 `1` + `.Add` (Steam 은 인덱서) · `SpeedChangePacket` 에 권한 검사 없음 → 클라가 하드싱크 중인 호스트를 재개시킬 수 있다.

---

## 4. 권장 Top 3 수정 (설계만 — 승인된 범위가 Phase 1–3 이라 구현하지 않음)

세 개를 이렇게 고른 이유: **서로 독립적이고, diff 가 작고, 각각 사용자 증상 3종을 하나씩 직접 겨냥한다.**

### 수정 1 — `NetIdHelper.cs:66` 에서 float 항 제거 (→ 상태 불일치)
```
- hash = hash ^ go.GetProperName().GetHashCode() ^ primaryElement.ElementID.GetHashCode()
-        ^ primaryElement.Mass.GetHashCode() ^ primaryElement.Temperature.GetHashCode();
+ hash = hash ^ primaryElement.ElementID.GetHashCode();
```
**안전한 이유** ID 는 런타임 파생값이고 세이브에 저장되지 않으므로 기존 세이브에 영향이 없다. 양 피어가 같은 모드 버전을 쓰는 것은 이미 전제다(`ProtocolCompatibility`).
**회귀 위험** 엔트로피가 줄어 **충돌이 늘어난다**. `breakoff` 루프(`:71-74`)가 로컬 순서 의존이라 충돌 자체가 새 divergence 원인이 된다 → 이 수정은 **#9 의 소유자 확인형 `Unregister` 와 함께** 가야 한다. 단독 적용은 권하지 않는다.

### 수정 2 — 조각난 메시지를 Reliable 로 승격 (→ 파이프/가스 desync, LAN)
`RiptidePacketSender.SendPacket` 에서 `SendChunked` 호출 시 `sendType` 을 Reliable 로 강제 + `ConduitFlowSyncer.MAX_UPDATES_PER_PACKET` 을 LAN 에서 44 로.
**안전한 이유** 조각난 메시지에 unreliable 은 의미가 없다(조각 1개만 잃어도 전부 무효). Reliable 로 바꾸면 의미가 생긴다.
**회귀 위험** LAN 대역폭·재전송 증가. 하지만 조각화가 애초에 안 되게 44 로 낮추면 이 경로 자체를 거의 타지 않는다. `WorldUpdatePacket` 은 이미 바이트 예산이 있어 영향 없음.

### 수정 3 — 하드싱크당 세이브를 1회만 생성 (→ "Host Lost")
`GameServerHardSync` 가 `byte[]` 를 한 번 만들어 캐시하고 `SendSaveFileToAll` 에 주입. `:67` 의 `.Length` 전용 호출 삭제.
**안전한 이유** 같은 데이터를 여러 번 만드는 것을 한 번으로 줄이는 순수 절감. 의미 변화 없음 — 오히려 **모든 클라가 동일한 스냅샷**을 받게 되어 현재보다 정합적이다(지금은 클라마다 다른 시점의 세이브를 받는다!).
**회귀 위험** 낮음. 클라가 매우 늦게 참가하면 캐시가 낡을 수 있으니 하드싱크 스코프로만 캐시하고 일반 참가 요청에는 적용하지 않는다.

---

## 5. 반증하여 폐기한 주장 (신뢰성 기록)

| 주장 | 판정 |
|---|---|
| `InstantiationBatcher` 가 무제한 큐를 Unreliable 로 보낸다 | **폐기** — `KInstantiatePatch.Postfix:41-53` 에서 `InstantiationBatcher.Queue` 호출이 **주석 처리**되어 있다. 죽은 코드. (단 "생성이 복제되지 않는다"는 사실은 #1 의 전제로 유효) |
| `RiptideServer.Stop()` 이 `_client` null 로 NRE | **폐기** — 앞선 `if (!_server.IsRunning) return;`(`:214`)에 가려 도달 불가. 대신 stale `_server` 가 재호스팅을 막는 것이 실제 결함 |
| 클라가 청크 ACK 를 보내지 않는다 | **폐기** — `SecureTransferPacket.OnDispatched` → `SendChunkAck` 로 ACK 경로가 닫혀 있다 |
| TCP 전송이 `Read()==1 message` 를 가정한다 | **폐기** — `ReadExact`(`TcpFileTransferClient.cs:120-134`)와 서버의 8바이트 ID 읽기 모두 루프한다. **프레이밍은 이 레포에서 유일하게 제대로 된 부분.** 길이 프리픽스도 writer/reader 대칭 |
| 대용량 세이브 전송이 heartbeat 를 막는다 | **부분 폐기** — 전송 자체는 전용 백그라운드 스레드라 막지 않는다. 실제 메인 스레드 정지 원인은 **`GetWorldSave()` 의 동기 `SaveLoader.Save()`** 다 (→ #2) |
| `ChunkedPacket` 이 순서에 의존한다 | **폐기** — 재조립은 순서 독립(`:39-51`). 순서 문제는 그 위층 `SaveChunkAssembler` 에 있다 (→ #4) |

**#5(`ChunkedPacket` 송신자별 미분리)의 트리거 한계 솔직히**: 키가 `SequenceId` 뿐이라 호스트가 여러 클라의 청크를 한 dict 에 넣는 충돌은 **클라 2명 이상이 각자 1000 B 초과 non-bulk 패킷을 호스트로 보낼 때만** 성립한다. 호스트→클라 방향은 카운터가 하나라 충돌하지 않는다. **검증 가능한 예측: 클라 1명 대비 2명에서 불안정이 급증하면 이 경로가 실제로 발동한다.** 만료 없음·바운드 체크 없음은 클라 1명에서도 성립한다.

---

## 6. 재현 / 테스트 계획

| # | 목적 | 절차 | 기대 관찰 |
|---|---|---|---|
| R-1 | #2 (Host Lost) | 후기 콜로니(저장 5초+)에서 클라 2~3명 접속 후 하드싱크 | 호스트 수십 초 동결 → 클라 전원 타임아웃. 저장 로그가 N+1회 |
| R-2 | #2 반증 | 갓 시작한 콜로니로 동일 조작 | 정상 → **규모 의존성 입증** |
| R-3 | #3 (파이프, LAN) | LAN, 파이프 45셀 이상 활발한 기지에서 클라 파이프 내용 관찰 | 질량/온도 고착·오류, Steam 세션에서는 정상 |
| R-4 | #1 (유령 아이템) | 타일 대량 채굴 → 복제인간이 수거 → 클라 바닥 확인 | 클라에 광석이 남아 누적. 로그에 `[Registry] Lookup failed` 급증 |
| R-5 | #10 (순서) | 클라 A 가 dig 드래그 직후 같은 영역 cancel → 클라 B 확인 | B 에 dig 명령 잔존 (손실 없이 재현) |
| R-6 | #12 (로드 루프) | 로드가 30초 넘는 세이브로 LAN 참가 | 세이브 2회+ 다운로드 또는 참가 미완 |
| R-7 | #5 (하드싱크) | 하드싱크 중 클라 화면의 사이클 타이머 관찰 | 호스트는 멈췄는데 클라 시간이 흐른다. 하드싱크 후 호스트가 일시정지에 갇힘 |
| R-8 | #11 (LAN 강퇴) | 로딩 중 호스트 재시작 | LAN 은 메인 메뉴 강퇴, Steam 은 정상 재접속 |

**단위 테스트 후보** (`DebugTools/UnitTests/NetworkingTests.cs` 에 기존 하네스 있음):
- `SaveChunkAssembler`: 짧은 꼬리 청크를 **먼저** 넣고 나머지 순서대로 → `IsComplete()` true 여야 함 (**현재 실패**)
- `NetIdHelper`: 같은 prefab/cell 을 mass/temp 만 1 ULP 다르게 → 같은 ID 여야 함 (**현재 실패**)
- `ChunkedPacket`: 같은 `SequenceId` 로 다른 `TotalChunks` → 예외 없이 폐기되어야 함
- `MainThreadExecutor`: N 스레드 동시 `QueueEvent` → 유실 0

---

## 7. 진단 계측 제안 (host.log ↔ client.log 로 최초 divergence 찾기)

현재 헤더는 `int packetType` 4바이트뿐 → **손실·중복·재정렬을 사후 증명할 수 없다.** 이것이 다음 디버깅의 최우선 투자다.

1. **헤더 확장** (`SerializePacketForSending` / `HandleIncoming` 대칭): `packetType(4) | senderId(8) | seq(4) | sentTickMs(4)`. seq 는 (sender, receiver) 별 단조 카운터.
2. **수신측 갭 검출**: `seq != expected` → `WARN net.gap sender=… expected=… got=…`. 이 한 줄이 "패킷이 진짜 유실되는가"를 끝낸다.
3. **양쪽 동일 1줄 스키마** (사후 diff 가능):
   `ts_mono | role=host|client | tick | cycle | dir=tx|rx | type | seq | entityId | bytes | queueDepth | procMs`
4. **기존 자산 재사용**: `PacketTracker.cs`(876줄) + `Shared/Profiling/Profiler.cs`(684줄) 가 이미 있다. 새로 만들지 말고 **PacketTracker 에 seq/tick/cycle 컬럼 추가 + 파일 flush** — 단 #14 를 먼저 고쳐 객체 강참조를 없앤 뒤에.
5. **NetId 불일치 탐지 (#1 전용, 최고 가치)**: 호스트가 주기적으로 `(cell, prefabID, netId)` 요약 해시를 보내고 클라가 자기 값과 비교해 불일치 개수를 로그. **#1 이 실제로 발동 중인지 5분 안에 판정된다.**
6. **삼킨 예외 가시화**: `RiptideServer.cs:199`·`RiptideClient.cs:186`·`WorldStateSyncer.cs:229` 의 catch 에 **패킷/서브시스템별 누적 카운터** — "조용히 사라진 것의 수"를 노출.
7. **전송 계측**: 세이브 전송마다 `transferId, totalChunks, received, missing, resendRequests` 를 10초마다 1줄. #4 는 이 줄만 있으면 즉시 판별된다.

---

## 8. 남은 리스크 / 다음 단계

**미확인**
- **빌드·실행 검증 없음** — 게임 설치본이 없어 컴파일·실측을 못 했다. 전부 정적 추적이다.
- **Riptide 내부** — `Reliable` 이 순서를 보장하지 않는다는 전제로 #4-3 을 세웠다. 틀리면 그 트리거는 "손실 후 단독 재전송" 경로로 좁아지지만 버그 자체는 남는다.
- **fork 여부** — 업스트림 `main` 기준이다. 로컬 수정본이면 재검증 필요.
- **게임플레이 플로우 축 미완** — 건설/배달/에런드/자동화/연구/농업/크리터/전력의 종단 추적과 syncer 의 O(N) 특성은 별도 오딧이 진행 중이었고 이 문서에 반영되지 않았다. 특히 **크리터가 아예 동기화되지 않는지**는 확인되지 않았다.

**권장 다음 단계 (순서대로)**
1. **계측 먼저** (§7 의 1·2·5). 지금은 어떤 수정이 효과가 있었는지 측정할 수 없다.
2. **#2 를 먼저 고친다** — 가장 작은 diff 로 가장 눈에 보이는 증상("Host Lost")을 줄인다. 다른 버그를 조사할 수 있는 안정성을 먼저 확보.
3. **#1 + #9 를 함께** — 단독 적용 금지(§4 수정 1의 회귀 위험).
4. **#3** — LAN 전용 증상을 끝낸다.
5. **#16 의 catch-all 제거** — 이걸 남겨두면 위 수정들의 효과가 보이지 않는다.

---

## 9. 축 3 — 게임플레이 플로우 / 스케일링

### 권한 모델 (먼저 확정)
클라이언트는 `ChoreDriver`·`ChoreConsumer`·`MinionBrain`·`CreatureBrain`·`Sensors` 를 끄고 `Navigator.AdvancePath` 를 차단한다 (`MinionMultiplayerInitializer.cs:61-63`, `NavigatorPatch.cs:18-24`). `DuplicantClientController.cs` 는 파일 전체가 주석이다.
→ **이중 chore 선택은 없다.** divergence 는 독립 AI 가 아니라 **복제되지 않은 이벤트**와 **이중 복제된 이벤트**에서 온다. 사용자가 의심한 "두 시뮬레이션이 각자 판단" 시나리오는 복제인간·크리터 AI 에는 해당하지 않는다.

### G-1. 캔 광석이 클라에서 두 번 생성된다 — CRITICAL / High / **누적**
**FILE** `Patches/World/WorldDamagePatch.cs:45` (+ `Packets/Tools/Dig/DigCompletePacket.cs:66`, `Packets/World/WorldDamageSpawnResourcePacket.cs:80`)

```csharp
GameObject gameObject = element.substance.SpawnResource(vector, num, temperature, disease_idx, disease_count);  // :45 — 역할 검사 없음
...
if (MultiplayerSession.IsHost)   // :53 — 전송만 게이트된다
```

**BUG** `OnDigComplete` prefix 가 vanilla 를 **양쪽 역할 모두**에서 대체하고 광석을 무조건 생성한다. 게이트된 건 **패킷 전송뿐**이다. 그런데 클라는 dig 패킷에 의해 자기 sim 을 통해 이 경로로 **되돌려 보내진다**: `DigCompletePacket.cs:66` 의 `WorldDamage.Instance.DestroyCell(Cell)` → sim 콜백 → `OnDigComplete` → 로컬 생성. 그리고 호스트의 사본이 도착해 **존재 검사 없이 또 생성**한다.

**의도가 문서로 증명된다** — 저자 자신의 주석(`:69`): *"skipping spawn sync (**client will be short one item**)"*. 즉 설계 의도는 **패킷 전용**이고 클라의 로컬 생성은 버그다.

**WHY** 캔 타일마다 클라 질량이 호스트의 **2배**가 된다. 그리고 이를 교정할 것이 없다 — `ResourceSyncer` 는 스스로 비활성이다 (`Networking/Synchronization/ResourceSyncer.cs:16` `// Disabled - world inventory sync not working correctly / return;`). 클라는 오염된 월드에서 자기 `WorldInventory` 를 계산한다.

**SYMPTOM** 클라에 호스트 복제인간이 절대 줍지 않는 광석 더미가 쌓인다(로컬 중복은 호스트 측 대응물이 없다). 자원 총계가 영구 드리프트, 건설 재료 가용 표시가 플레이어 간 불일치.

**FIX** 전송이 아니라 **생성 자체를 게이트**한다. `OnDigCompletedUpdated` 앞부분에 `if (MultiplayerSession.IsClient) { Grid.Damage[cell] = 0f; return; }`. 이 패턴은 같은 레포의 물 처리에 **이미 올바르게** 있다 (`Patches/World/FallingWaterPatch.cs:53` `return false; // Client: Suppress local creation to avoid desync/duplication`).

### G-2. 전체상태 주기 패킷이 전부 1000B 를 넘고 Unreliable 로 나간다 — CRITICAL / High / **임계 후 완전 실패**
**FILE** `Components/PlantGrowthSyncer.cs:143-161` · `Components/WorldStateSyncer.cs:237-256` · `Transport/Riptide/RiptidePacketSender.cs:26,88`

§2 #3(`ConduitFlowSyncer`)은 이 문제의 **한 사례였다.** 실제로는 **전체상태 reconcile 패킷 전부**가 무제한·Unreliable 이다:

| 패킷 | 크기 | 1000B 초과 시점 |
|---|---|---|
| `PlantGrowthStatePacket` | ~40 B/식물, 야생 포함 전 맵 (`PlantTracker.cs:9` 필터 없음) | **식물 ~25개** |
| `DiggingStatePacket` | 4 B/셀, `Components.Diggables.Items` 전체 | dig 셀 ~250개 |
| `BuildingStatePacket` | cell + prefab 이름 **문자열** × 전 건물 | 건물 수십 개 |

**WHY** 임계를 넘으면 조각화되고, 조각 1개 손실이 스냅샷 전체를 죽인다. 그런데 이 스냅샷이 **해당 서브시스템의 유일한 교정 경로**다. Steam 도 unreliable 조각화는 all-or-nothing 이라 **LAN 전용이 아니다.**

**TRIGGER** 식물 25개 = **어떤 콜로니든 사이클 2**.

**SYMPTOM** 클라 식물 성숙도가 자유주행하며 절대 수렴하지 않음. dig 마커가 나타나거나 사라지지 않음. 오류 없이 **"동기화가 그냥 멈춘" 것처럼 보인다.**

**FIX** 모든 전체상태 패킷을 페이징한다 — `ConduitFlowSyncer.cs:189-201` 이 이미 올바른 패턴을 구현해 두었다. 식물은 viewport cull(`Cell` 을 이미 갖고 있다). reconcile 은 Reliable 로.

**부수 발견 (중요)**: staggered 사이클은 `SyncDigging`/`SyncChores`/`SyncResearchProgress` 만 돈다(`WorldStateSyncer.cs:220-226`). **`SyncPriorities`(:544)·`SyncDisinfectImpl`(:582)·`SyncResearch`(:437) 는 호출자가 없는 죽은 코드다** (직접 확인). → **해체·소독·우선순위·수확 표시에는 reconciler 가 아예 없다.** 이벤트 패킷이 하나 유실되거나 재정렬되면 **영구적**이다.

### G-3. 크리터가 전혀 복제되지 않는다 — CRITICAL / High
**FILE** `Patches/Critters/EntityTemplatesPatch.cs:33-36` · `Scripts/Creatures/CreatureMultiplayerInitializer.cs:69-80`

크리터의 네트워크 표면은 **위치 + 애니메이션 + 상태아이템뿐**이다. 산란·부화·성장·길들이기·목장·죽음 복제가 어디에도 없다.

**부재의 증거** (레포 전역 0 hit): `FertilityMonitor`, `IncubationMonitor`, `BabyMonitor`, `LayEgg`, `GameTags.Egg`, `EggConfig`, `Capturable`, `Groom`, `Butcher`, `Tame`, `DeathMonitor`, `CritterTracker`. `CreatureBrain` 은 딱 한 곳에 나오고 — **거기서 비활성화된다**. 범용 spawn 복제기도 죽었다(`KInstantiatePatch.cs:41-53` 주석 처리, `SpawnUtils.KNetInstantiate` 호출자 0). despawn 도 없다(`MoveablePatch.cs:35` 주석 처리된 `// Optional: PacketSender.SendToAll(new DespawnPacket ...)`).

**SYMPTOM** 클라의 크리터 개체수가 참가 시점 스냅샷에 **동결**된다. 출생은 보이지 않고, 죽음은 **불멸의 동결된 인형**을 남긴다. 호스트는 가득 찬 인공부화기를 보는데 클라는 빈 것을 본다.

**FIX** 최소안: `PlantTracker`/`PlantLifecyclePacket` 쌍(레포에서 이 패턴의 **유일한 올바른 구현**)을 미러링한 `CritterTracker` + `CritterLifecyclePacket`.

### G-4. 그 외 확정 (요약)
| Sev | 위치 | 내용 |
|---|---|---|
| HIGH | `BuildCompletePacket.cs:126-160` + `BuildingSyncer.cs:210-217` | ghost 존재 검사 안에 완성 로직 전체가 들어있고 `else` 가 없다. 클라 ghost 가 없으면(원소 상태 미수렴 시 `TryPlace` 가 null) 30초 fallback 이 **SandStone·293.15K·`Orientation.Neutral`** 로 잘못 건설한다. mass/temp 가 NetId 해시에 들어가므로(§2 #1) 그 건물의 NetId 가 호스트와 달라져 **이후 모든 패킷이 영구 오배송**. |
| HIGH | `BuildingConfigPacket.cs:45,127` | §2 #7 보다 나쁘다: `Serialize` 가 **매번 로컬 ID 로 `Sender` 를 다시 찍는다**. 호스트 relay 가 재직렬화하고 `SendToAllClients` 는 호스트만 제외 → **발신자가 자기 변경을 `Sender=host` 로 되받아** 적용하고, 수신 경로가 커서 아래 건물을 강제 재선택(`:170-171`). `SendToAllExcluding` 이 존재하는데 쓰이지 않는다. 게다가 ~22개 핸들러가 **자기가 패치한 메서드를 그대로 호출**해 전역 bool 하나가 유일한 재진입 방어다. |
| HIGH | `EntityPositionHandler.cs:75-79` vs `:113` | **Unix epoch ms 를 Unity 프로세스 상대 시계와 비교한다**: `Time.unscaledTime - (serverTimestamp/1000f) > 2f`. 좌변이 약 `-1.78e9` 라 조건이 **절대 참이 되지 않는다** → 호스트가 보지 않는 엔티티의 pull 복구 경로가 영구 사망. 호스트 viewport cull 과 겹쳐 클라가 호스트 미방문 지역을 보면 엔티티가 고정된다. |
| HIGH (perf) | `DebugConsole.cs:55-62` ← `NetIdHelper.cs:47`, `DragToolPacket.cs:152`, `HostBroadcastPacket.cs:62` | `DebugConsole.Log` 가 무조건 `UnityEngine.Debug.Log`(**스택트레이스 캡처**)를 부른다. **workable 등록마다**·**드래그 셀마다**·**relay 패킷마다** 1회. 30×30 dig 박스 = 한 프레임에 ~900회 스택트레이스 + ~900 breakoff 루프. → **드래그 릴리스 시 호스트 수백 ms~수 초 동결** = 네트워크 정지와 구별 불가, 클라 타임아웃. |
| HIGH (perf) | `DigToolPatch.cs:26` + `PrioritizablePatch.cs:39-42` | `DiggablePacket` 이 `IBulkablePacket` 이 **아니라** 셀당 별개 reliable 데이터그램. `SetMasterPriority` 마다 단일 원소 리스트로 `PrioritizeStatePacket` 1개. → 위 dig 드래그가 한 프레임에 **~1800 데이터그램**. |
| HIGH (perf) | `LogicStateSyncer.cs:238-300` | 변경 검사 **전에** 논리 건물 전체를 매초 재샘플링하며 **캐시 없는 `GetComponent` ~10회** + 새 Dictionary. 후기 자동화 500~2000개 = **초당 5,000~20,000 native 호출**, 변경이 없어도. |
| MED-HIGH | `WorldStateSyncer.cs:712-722` + `DuplicantChoreBroadcaster.cs:56-84` | 클라별 호스트 상태가 **연결 종료 시 정리되지 않는다**. `_clientViewports` 는 쓰기만 하고 liveness 검사 없이 순회 → 떠난 클라마다 1.5초당 전체 셀 스윕이 영구 추가. `SubscribedNetIds` 고아 항목은 **ONI 최고비용 쿼리(chore precondition 전체 스캔)를 500ms마다** 계속 돌린다. **세션을 넘어 누적.** |
| MEDIUM | `DiggablePatch.cs:8` | `Diggable.OnStopWork` 에 훅했는데 Klei 는 **중단 시에도** 이걸 발생시킨다. 클라는 `DestroyCell` 로 응답 → 중단된 채굴이 클라에서만 셀을 파괴. 같은 레포의 해체 패치는 `OnCompleteWork` 를 올바르게 쓴다(`DeconstructablePatch.cs:10`). |
| MEDIUM | `ResearchPatch.cs:16` | 연구가 **단방향·미교정**: `SetActiveResearch` postfix 가 `IsHost` 아니면 조기 return → 클라의 기술 선택은 전송되지 않음. `ResearchProgressPacket` 은 타입별 점수를 백분율 하나로 뭉개 클라에서 **모든** 연구 타입에 `cost * Progress` 로 재분배 → 호스트가 0인 고급 연구가 클라에선 부분 충전되어 보인다. |
| MEDIUM | `PickupItemPacket.cs:18,44` | 부분 수거를 표현할 수 없다 — `NetId` 만 싣고 클라는 `KDestroyGameObject`. 호스트 `TakeUnit` 은 잔량을 남기는데 분리된 객체엔 spawn 패킷이 없다 → **클라가 잔량 질량을 영구 손실**. |
| MEDIUM | `ComplexFabricatorSpawnProductPacket.cs:53` | 입력 저장이 복제되지 않는데 클라에서 `SpawnOrderProduct` 로 산출물을 만든다. 멱등 키도 없다(레시피 **인덱스**만) → **제작마다 무에서 질량 생성**. |
| MEDIUM | `TransportPacketSender.cs:12-27` + `PacketSender.cs:120-122` | `BulkSenderPacket` 이 큐 리스트를 **복사 없이 참조**하는데 직후 `pendingPackets.Clear()`. `EnablePacketQueue=true` 면 직렬화가 한 프레임 뒤라 **내부 패킷 0개로 전송**. 기본값 false 라 함정. |
| LOW-MED | `SliderPatches.cs:38-39` | `onReleaseHandle -= () => …; += () => …` — `-=` 가 **새 클로저**를 만들어 아무것도 제거하지 않는다 → 핸들러 누적, 릴리스 1회에 N개 중복 패킷. |
| LOW-MED | `RemoteProgressRegistry.cs:23-33` | `GetHashCode` 만 오버라이드하고 `Equals` 는 안 함 → Dictionary 가 `ObjectEqualityComparer` 로 폴백해 **조회마다 양쪽 박싱**. 클라 UI 갱신마다 workable 당 호출. |

### 반증 / 무혐의 (축 3)
- `WorldStateSyncer.IsCellInPlayerViewport` — **핫스팟 아니다**. dict 조회 + `Grid.CellToXY` + 4비교, 할당 없음(`:116-123`). 할당하는 형제 `IsCellVisibleToAnyClient`(`:104-111`)는 **호출자 0**.
- `Shared/Profiling/Profiler.cs` — release 에서 `Profiler.Scope()` 는 **no-op struct**. perf 문제 아님. (단 DEBUG 빌드는 호출마다 `Path.GetFileNameWithoutExtension` + 문자열 보간 → **DEBUG 빌드로는 성능 측정 불가**.)
- 건설 ghost 이중 생성 — **의도된 설계**(각 측이 자기 ghost 소유), `BuildCompletePacket` 이 통째로 교체한다. 문제는 G-4 의 fallback 이지 이중 실행이 아니다.
- 저장 질량 권한 — 호스트 단독이 `StoragePatches`·`PickupablePatches` 조기 return 으로 강제된다. **float 드리프트는 문제가 아니다** — 무에서 생성(G-1, 제작)과 잔량 손실(부분 수거)이 문제다.
- `FetchList`/배달 복제 — **부재**. 관련 심볼 전부 0 hit. 에런드는 UI 텍스트로만 전달되고 운반 아이템은 **1 kg 하드코딩** 라벨의 anim 프록시다(`DuplicantCarryItemPacket.cs:85,180`).

---

## 10. 최종 통합 마스터 순위 (3축 병합, severity × confidence)

| 순위 | Sev | 위치 | 한 줄 | LAN | 규모비례 |
|---|---|---|---|---|---|
| **1** | Crit | `WorldDamagePatch.cs:45` | 캔 광석이 클라에서 **2번 생성**, 교정기(`ResourceSyncer`) 비활성 | ✅ | ✅ |
| **2** | Crit | `PlantGrowthSyncer.cs:143` 외 | 전체상태 주기 패킷 전부 1000B 초과 + Unreliable → **식물 25개에서 동기화 사망** | ✅ | ✅ |
| **3** | Crit | `NetIdHelper.cs:66` | NetId 를 가변 `Mass`/`Temperature` float 에서 파생 → ID 영구 불일치 | ✅ | ✅ |
| **4** | Crit | `SaveHelper.cs:367` + `GameServerHardSync.cs:67` | 하드싱크당 전체 월드 **동기 저장 N+1회** → "Host Lost" | ✅ | ✅ |
| **5** | Crit | `EntityTemplatesPatch.cs:33` | **크리터 생명주기 미복제** (출생/죽음/알/길들이기 전부) | ✅ | ✅ |
| **6** | Crit | `SaveFileRequestPacket.cs:139` | transferId=파일명 → 두 스냅샷 혼입 → 참가 루프 | ✅ | ✅ |
| **7** | Crit | `GameServerHardSync.cs:71` | 하드싱크가 추측 타이머 종료 · 클라 안 멈춤 · 호스트 안 재개 | ✅ | ✅ |
| **8** | Crit | `NetworkIdentityRegistry.cs:35` | `Unregister` 소유자 미확인 → 살아있는 객체 주소불능 (**#1 이 매 타일마다 발동시킨다**) | ✅ | ✅ |
| **9** | High | `WorldStateSyncer.cs:220` | `SyncPriorities`·`SyncDisinfect`·`SyncResearch` **죽은 코드** → 해체/소독/우선순위에 reconciler 없음 | ✅ | — |
| **10** | High | `DebugConsole.cs:55` ← `NetIdHelper.cs:47` | 셀/패킷 단위 경로의 스택트레이스 로깅 → 드래그 릴리스 시 호스트 초단위 동결 | ✅ | ✅ |

11~30위: `BuildingConfigPacket` 에코/relay 루프 · 원소 그리드 shadow 선갱신 · `BulkSenderPacket` 절단 · `EntityPositionHandler` epoch 시계 버그 · `BuildCompletePacket` SandStone fallback · `PacketTracker` 수백MB 고정 · 벌크 순서 역전 · `LogicStateSyncer` GetComponent 폭풍 · 연결종료 시 클라 상태 미정리 · LAN 로딩중 강퇴 · 로드 루프 · `MainThreadExecutor` 레이스 · `DiggablePatch` OnStopWork · 연구 단방향 · 부분 수거 질량 손실 · 제작 질량 생성 · `_incomingPackets` 미청소 · `DateTime.Now` 타임아웃 · `SliderPatches` 클로저 누적 등.

### 수정 순서 권고 (재조정)
1. **G-1 (2줄)** — 가장 작은 diff, 가장 확실, 가장 큰 게임플레이 효과. `FallingWaterPatch.cs:53` 패턴 복사.
2. **#10 로깅 게이트 (1줄 + 삭제 2줄)** — 호스트 동결 제거. 이게 "네트워크 문제"로 오인되던 부분을 걷어낸다.
3. **#4 하드싱크 세이브 1회화** — "Host Lost" 직접 감소.
4. **#2 전체상태 패킷 페이징** — `ConduitFlowSyncer` 패턴 복사. 여러 서브시스템이 한 번에 살아난다.
5. **#3 + #8 함께** — NetId float 제거는 소유자 확인형 `Unregister` 없이 단독 적용 금지.
