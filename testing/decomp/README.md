# decomp — read the game's method bodies

`CLAUDE.md` has said "이름으로 추측하지 말고 본문을 본다" for a long time without saying how.
This is how. It decompiles a type or a single member out of the publicised assembly.

```powershell
dotnet build -c Release
$asm = '..\..\PublicisedAssembly\Assembly-CSharp_public.dll'
dotnet bin\Release\net8.0\decomp.dll $asm SuitLocker UnequipFrom     # one member
dotnet bin\Release\net8.0\decomp.dll $asm PlantablePlot              # whole type
dotnet bin\Release\net8.0\decomp.dll $asm Repairable __list__        # member names, when the name is unknown
```

Firstpass types live in the other assembly: `Assembly-CSharp-firstpass_public.dll` (`Util`, for one).

## Why it exists

Reflection load fails on the publicised assembly, and `ilspycmd` will not install against this
SDK - its NuGet package is missing `DotnetToolSettings.xml`. This is a twenty-line program over
`ICSharpCode.Decompiler` and it answers the questions that cost this project the most.

## What it settled, in one session

- `SuitLocker.UnequipFrom` opens `GetAssignable(...Suit)` then `assignable.Unassign()` with **no
  null check**. That is why replaying it on a client threw five times out of five. `EquipTo` has
  the opposite shape and returns quietly, which is why calling it blind looked like success.
- `PlantablePlot.SpawnOccupyingObject` returns a NEW object for a seed and the SAME object for
  anything else. That is the test for "was something planted", and it needs no species list -
  two earlier guesses (`Growing`, then `GameTags.Plant`) were both wrong.
- `SingleEntityReceptacle.ForceDeposit` is the sowing entry point, so the scenario never has to
  place a plant by hand.
- `BuildingHP.Repair` fires two triggers, one of them only when the building is whole again, and
  that second one is what ends the repair errand. The reflection write this replaced fired
  neither, so a client kept showing repair work the host had finished.
- `ColdBreatherConfig` never calls `ExtendEntityToBasicPlant`, so a Wheezewort carries no
  `GameTags.Plant` at all - which is why a fix written against that tag never fired.
- `Util.KInstantiate` does exactly what `InstantiationsPacket` already does by hand, and neither
  registers `Grid.Objects`; `OccupyArea.OnSpawn` does that on activation. This retired a written
  explanation for two reverted plant attempts.

## When to reach for a runtime probe instead

Read the body when the question is "what does this method do". Measure on the peer when the
question is "what is true here right now" - the two are different, and reading cannot answer the
second. Mono also inlines, so a caught exception's stack may name only the call site: a
`NullReferenceException` reported at `SuitEquipPacket.cs:116` was thrown inside `UnequipFrom`,
and only the body said so.
