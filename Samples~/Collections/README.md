# Collections

Several modules contribute to one thing — tickables, debug panels, cheats — without knowing each other or the
code that uses them. Each contribution is registered with `AsElementOf<T>()`, and whatever needs all of them
takes an `IReadOnlyList<T>`:

```csharp
services.Add<AudioMixer>().AsElementOf<ITickable>();
services.Add<TickLoop>();   // public TickLoop(IReadOnlyList<ITickable> tickables)
```

## How to run it

Add `CollectionsRunner` to any GameObject and press Play. It ticks `TickLoop` every frame and writes the debug
menu to the Console every two seconds. `CollectionsSample.Run(Console.WriteLine)` runs the scripted version
below, in Unity or anywhere else.

## What to look at

- `Modules.cs` — three modules, `AudioModule`, `NetworkModule` and `CheatsModule`. Each `Install` method
  contributes with `AsElementOf`. None of them registers `ITickable`, `IDebugPanel` or `ICheat` as a contract,
  so none of them can collide with another.
- `Contracts.cs` — the consumers. `TickLoop` takes `IReadOnlyList<ITickable>`; `DebugMenu` takes
  `IReadOnlyList<IDebugPanel>` and `IReadOnlyList<ICheat>`.
- `CollectionsSample.Compose` — the composition root. The order the modules are installed in is the order of
  every list. The cheats module is installed only in a development build.

What the lists guarantee:

- **Registration order.** `AudioMixer` ticks before `Connection` because `AudioModule` was installed first.
- **Empty, not missing.** A release build installs no cheats, and `DebugMenu` still builds: its
  `IReadOnlyList<ICheat>` is empty.
- **One object, however many lists.** `Connection` is registered once, `.As<IConnection>()` and in two lists.
  All three hand out the same instance, constructed once.
- **Built after its elements.** A list is built after everything in it, so `TickLoop` and `DebugMenu` are
  constructed after every contribution, and a consumer with a boot phase boots after them too. An element that
  takes its own list is a dependency cycle, reported by the build.
- **Local to its context.** A child context's list holds the child's contributions only, not its parent's. The
  level in the script registers a `LevelTimer` and a `TickLoop` of its own; the game's loop keeps ticking the
  game's two tickables.
- **The element type stays unregistered.** Resolving `ITickable` itself fails: contributing to a list claims
  no contract.

## Expected output

```
release build: 2 tickables
Debug menu:
  [Audio] 1.50 s played
  [Network] 3 packets sent
  cheats: none
development build: 2 tickables
Debug menu:
  [Audio] 1.50 s played
  [Network] 3 packets sent
  cheats: god mode, unlock all levels
one Connection behind IConnection and in both lists: True
level loop: 1 tickable, game loop: 2 tickables
```

`Tests/` checks this output and the rules above in EditMode. It compiles only in a project with the Unity
Test Framework; delete the folder if you do not want it in your Test Runner.
