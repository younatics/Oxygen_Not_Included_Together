# 실패 지점 전수 목록

각 항목은 **코드에서 확인한 것**만 적는다. 추측은 "가설" 로 명시한다.
상태: `OPEN` 미수정 · `TEST` 실패 테스트로 인코딩됨 · `FIXED` 수정+검증 완료

---

## T. 조각화 / 전송 계층 — 가장 위험한 구역

`RiptidePacketSender.MAX_PAYLOAD_BYTES = 1000`. 이걸 넘으면 `SendChunked` 가 자동으로 쪼갠다.
그 경로 전체에 방어가 거의 없다.

### T8 · ConduitFlowSyncer 가 Riptide 한도를 넘게 설계돼 있다 `OPEN`
```
ConduitFlowSyncer.cs:38   // 50 * 22 = 1100 bytes, fits Steam P2P unreliable MTU (~1200 B)
ConduitFlowSyncer.cs:39   MAX_UPDATES_PER_PACKET = 50
ConduitFlowSyncer.cs:191  SendToAllClients(packet, PacketSendMode.Unreliable)
```
주석이 **Steam 기준으로 설계했다고 명시**한다. Riptide 한도는 1000B.
→ 파이프가 50칸 이상 흐르면 주기 패킷이 **LAN 에서만** 매번 조각난다.

**이것이 아래 T1–T7 을 전부 활성화하는 방아쇠다.**

⚠️ **Steam 이 안전하다는 뜻이 아니다.** Steam 은 조각화를 *자기 안에서* 한다 —
unreliable 메시지가 ~1200B 를 넘으면 내부에서 나누고 **조각 하나만 잃어도 메시지 전체를 버린다.**
실패 모드는 같고 임계값만 다르다. 1100B 는 그 경계 바로 밑이라 헤더 오버헤드가 얹히면 넘어간다.
→ 크기 테스트는 **두 트랜스포트 모두**에 대해 돌려야 한다. 아래 ST 절 참고.

### T5 · 조각이 호출자의 신뢰성을 그대로 물려받는다 `OPEN`
`RiptidePacketSender.cs:66-89` — `SendChunked(connection, bytes, sendType)`.
Unreliable 로 보낸 큰 패킷은 **모든 조각이 Unreliable** 이 된다.
조각 하나만 잃어도 전체 페이로드가 영구 소실되고, 재전송도 NAK 도 없다.
조각화는 신뢰 전송을 전제로만 성립한다.

### T1 · 재조립 버퍼가 발신자를 구분하지 않는다 `OPEN`
```csharp
private static Dictionary<int, byte[][]> _pendingChunks;   // key = SequenceId 만
private static int _nextSequenceId = 0;                    // 피어마다 0 부터 시작
```
호스트는 **여러 클라이언트**에게서 조각을 받는다. 클라 A 와 B 가 각자 sequence 0 을 쓰면
같은 버퍼에 섞인다 → A 의 조각 0 + B 의 조각 1 이 "완성" 으로 판정되어
**서로 다른 두 메시지가 하나로 접합된 쓰레기**가 `PacketHandler.HandleIncoming` 으로 들어간다.
관측된 `Failed to handle packet` ×11 (클라 전용)과 정합한다.

### T3 · 와이어에서 온 인덱스를 검증하지 않는다 `OPEN`
```csharp
chunks = new byte[TotalChunks][];   // 처음 도착한 조각의 TotalChunks 로 크기 결정
chunks[ChunkIndex] = ChunkData;     // 경계 검사 없음
```
T1 로 sequence 가 겹치면 `ChunkIndex >= TotalChunks` 가 되어 `IndexOutOfRangeException`.
삼켜지는 dispatch 예외로 나타난다.

### T2 · 크기 상한이 없다 `OPEN`
`Deserialize` 가 `TotalChunks` 와 `len` 을 그대로 믿는다 →
`new byte[TotalChunks][]`, `reader.ReadBytes(len)` 로 거대 할당 가능.
**같은 레포의 다른 패킷은 이미 방어하고 있다** (`WorldDataPacket.MaxChunkCount = 16384`,
`InstantiationsPacket.MaxCompressedBytes = 16MB`). 패턴이 있는데 여기만 빠졌다.

### T4 · 재조립 타임아웃/축출이 없다 `OPEN`
조각 하나가 유실되면 `_pendingChunks[seq]` 가 **영원히** 남는다.
긴 세션에서 무한 증가하고, 그 페이로드는 절대 완성되지 않는다.

### T6 · 부분 전송을 성공으로 보고한다 `OPEN`
`SendChunked` 는 `SendRaw` 의 실패를 무시하고 무조건 `true` 를 반환한다.
3번 조각 전송이 실패해도 호출자는 성공으로 안다.

### T7 · 세션 경계에서 상태가 초기화되지 않는다 `OPEN`
`_nextSequenceId` 와 `_pendingChunks` 는 static 이고 재접속/세션 종료 시 정리되지 않는다.
이전 세션의 미완성 엔트리가 새 세션의 sequence 와 충돌한다.

---

## ST. Steam 트랜스포트 — 다른 방식으로 깨진다

Riptide 는 한도를 넘으면 **쪼갠다**. Steam 은 **그냥 보낸다.** 둘 다 문제고 증상만 다르다.

### ST1 · 전송 실패가 보이지 않는다 `OPEN`
`SteamworksPacketSender.cs:36-40`
```csharp
bool sent = result == EResult.k_EResultOK;
if (!sent)
{
    // DebugConsole.LogError($"[Sockets] Failed to send ...", false);   ← 주석 처리됨
}
```
실패하면 `false` 를 반환할 뿐 **아무 기록도 남지 않는다.** 호출부 대부분이 반환값을 안 본다
(`PacketSender.SendToAllClients(...)` 는 void 계열). 즉 Steam 세션에서 패킷이 조용히 사라진다.
LAN 쪽 `Failed to handle packet` 같은 흔적조차 없어서 **사후 추적이 불가능하다.**

### ST2 · 크기 검사가 전혀 없다 `OPEN`
Riptide 는 `bytes.Length > MAX_PAYLOAD_BYTES` 를 검사한다. Steam 송신부에는 그 분기가 없다.
`SendMessageToConnection` 에 통째로 넘긴다.
- unreliable + ~1200B 초과 → Steam 내부 조각화 → 조각 유실 시 메시지 전체 폐기
- `k_cbMaxSteamNetworkingSocketsMessageSizeSend` = **512 KB** 초과 → `k_EResultLimitExceeded`

### ST3 · 패킷 상한이 Steam 의 메시지 상한보다 크다 `OPEN`
```
WorldDataPacket.MaxCompressedBytes      = 32 MB
InstantiationsPacket.MaxCompressedBytes = 16 MB
Steam 메시지 상한                        = 512 KB
```
큰 월드 상태는 Steam 에서 **조용히 실패**하고 Riptide 에서는 조각난다.
**같은 패킷이 트랜스포트별로 완전히 다르게 동작한다** — 어느 쪽도 성공하지 않는다.

### ST4 · 두 송신부가 서로 다른 계약을 구현한다 `OPEN` (설계)
Riptide 는 초과분을 처리하고 Steam 은 안 한다. `TransportPacketSender` 추상화가
"보낼 수 있는 최대 크기" 를 계약으로 노출하지 않아서, 호출부가 어느 쪽에 맞춰야 하는지 알 수 없다.
`ConduitFlowSyncer` 가 Steam 숫자에 맞춘 것도 이 때문이다.
→ 송신부가 `MaxUnfragmentedPayloadBytes` 를 노출하고 syncer 가 그걸 읽어야 한다.

---

## N. NetId

### N1 · workable 해시에 셀이 없고 breakoff 가 도착 순서에 의존 `FIXED` (c7f9c690)
2-PC 실측: 비교 가능 키 87개 중 33개(37.9%) 불일치, 클라 lookup 실패 1292.

### N2 · `Unregister` 가 소유자를 확인하지 않음 `FIXED` (c7f9c690)

### N3 · entity 경로가 아직 가변 float 을 해시한다 `OPEN`
`GetDeterministicEntityId:66` 이 `Mass`·`Temperature` 를 섞는다. 둘 다 객체가 사는 동안 변한다.
workable 경로만 고쳤으므로 순수 entity 경로는 그대로다.

### N4 · `NetId` 가 세이브에 직렬화된다 `OPEN` (마이그레이션 문제)
`NetworkIdentity.cs:11` `[Serialize] public int NetId`, `:39` `if (NetId == 0)` 일 때만 해시 호출.
→ **수정 이전 세이브는 옛 id 를 그대로 복원하고 새 해시를 절대 안 부른다.**
기존 콜로니는 고쳐지지 않는다. 마이그레이션 정책이 필요하다.

### N5 · `RegisterExisting` 가 충돌 시 조용히 건너뛴다 `OPEN`
```csharp
if (!identities.ContainsKey(netId)) identities[netId] = entity;   // else 는 주석 처리됨
```
진 쪽은 `NetId` 를 들고 있지만 레지스트리에는 없다 → 그 객체로 가는 모든 패킷이 lookup 실패.

---

## S. 세션 장부

### S1 · 트랜스포트가 아는 피어를 세션이 모른다 `OPEN`
테스트가 잡음: `Transport client 2 is missing from ConnectedPlayers` (클라 전용).
호스트 쪽 `No connection found for SteamID` 와 같은 구멍의 양면으로 보인다. **가설**

### S3 · 재접속이 `127.0.0.1:7777` 로 간다 `OPEN`
`RiptideClient.CleanupRiptide()` 가 끊길 때마다 `MultiplayerSession.ServerIp/ServerPort` 를
덮어쓰고 `ReconnectToSession()` 이 그 값을 읽는다. 재접속은 구조적으로 성공 불가.
기본 LAN 포트가 8080인데 7777로 되돌리는 것도 별개 불일치.

### S4 · 로딩 중 끊김에 LAN 만 가드가 없다 `OPEN`
Steam 경로(`SteamworksClient.cs:240-244`)에는 `LoadingWorld` 가드가 있고 Riptide 에는 없다.

### S5 · 타임아웃이 양쪽 비대칭 `OPEN` (설정 문제 가능)
실측: host `TimeoutTime = 120000ms`, client `30000ms`.
`RiptideServer.cs:63` 은 `Host.TimeoutSeconds`, `:85` 는 `HostTimeoutSeconds` 를 읽는다 —
**서로 다른 설정 속성**이다. 의도된 것인지 확인 필요.

---

## H. 패킷 헤더

### H1 · 헤더에 sequence·tick·sender 가 없다 `OPEN`
`int packetType` 4바이트뿐. 그래서 손실·중복·재정렬을 **사후에 증명할 수 없다.**
T1(발신자 미구분)의 근본 원인이기도 하다.

---

## 테스트 계획 — 공격적으로

기존 43개는 전부 **작은 고정 입력 1개**의 정상 경로 왕복이다. 아래를 추가한다.

| 축 | 내용 |
|---|---|
| 크기 | 각 주기 syncer 의 **최악 패킷을 자기 상한치로** 직렬화 → 활성 트랜스포트 한도와 비교 |
| 손실 | 조각 k 를 버리고 재조립 시도 → 완성되지 않아야 하고, 버퍼가 **누수되지 않아야** 한다 |
| 재정렬 | 조각을 역순·무작위 순으로 투입 → 동일 페이로드가 나와야 한다 |
| 중복 | 같은 조각을 2번 투입 → 페이로드가 깨지지 않아야 한다 |
| 교차 발신자 | 두 발신자가 같은 sequence 로 동시에 → 서로를 오염시키지 않아야 한다 |
| 악성 입력 | `TotalChunks = int.MaxValue`, `ChunkIndex = -1`, `len` 과 실제 길이 불일치 → 예외 없이 거부 |
| 규모 | 파이프 50·100·200칸, 식물 25·30·60개에서 패킷 수·크기·조각 수 측정 |
| 세션 경계 | 재접속 후 static 상태가 깨끗한가 |

**T 축은 세션도 2대도 필요 없다.** 전부 순수 로직이라 한 박스에서 수 ms 안에 판정 난다.
