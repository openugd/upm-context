# Basic Boot

A game's composition root from start to end: register services, hand over a settings asset, boot them in two
phases, and end everything when the scene does.

## How to set it up

1. Create an empty GameObject and add `GameBoot`.
2. Optionally create a settings asset with *Assets > Create > OpenUGD Samples > Basic Boot > Game Settings* and
   assign it to *Settings* on `GameBoot`. Without one, defaults are used and a warning says so.
3. Press Play, then exit play mode.

## Expected output

With no settings asset assigned (with one, its values appear instead and there is no warning):

```
GameBoot: no GameSettings assigned, so defaults are used.
GameBoot: building
SaveStore: loading
SaveStore: loaded 100 coins
Wallet: Player has 100 coins
Autosave: every 30 s
GameBoot: ready
Wallet: earned 25, now 125
```

and on exiting play mode:

```
GameBoot: context ended
Autosave: final save, 125 coins
```

Stay in play mode for 30 seconds and `Autosave: autosave, 125 coins` appears between the two.

## What to look at

### `GameInstaller.cs` — registration

```csharp
services.AddInstance(settings);
services
    .Add<MemorySaveStore>().As<ISaveStore>()
    .Add<Wallet>()
    .Add<Autosave>();
```

`Add<T>()` registers a singleton the context constructs; `.As<ISaveStore>()` adds a contract, so the store
resolves as itself and as `ISaveStore`. Registration order does not decide construction order: `Autosave`
takes `Wallet` and `ISaveStore` in its constructor, so both are built first wherever they are registered. The
registrations live outside the MonoBehaviour so that `Tests/` builds the same graph.

### `GameSettings.cs` — settings are an object

A `ScriptableObject` edited in the Inspector, registered with `AddInstance` and taken by services as a plain
constructor parameter. The context never disposes or destroys an object it was handed: the asset stays yours.
`GameBoot` destroys the default instance it creates itself, when the context's scope ends.

### `Services.cs` — the two boot phases

- `MemorySaveStore` implements `IAwakeService`. Its `AwakeAsync` awaits a delay that stands in for reading a
  save file.
- `Wallet` implements `IInitializeService`. Every Awake step finishes before the first Initialize step starts,
  so `Wallet` can read the store in `InitializeAsync`. Within a phase, a service starts only after the services
  it depends on have finished that phase, which is why `Wallet` logs before `Autosave`.
- `Autosave` starts a timer in `InitializeAsync` on the token it is given. That token is cancelled when the
  context ends, before anything is disposed, so the timer stops on it. `Dispose` saves once more: services are
  disposed in reverse construction order, so `Autosave` goes before the `Wallet` and the store it writes to.

### `GameBoot.cs` — the entry point and the end of the scope

- `Awake` creates the context's scope and `OnDestroy` ends it. Unity calls `OnDestroy` when the scene unloads
  and when play mode is exited, so nothing built here survives into the next play session, even with domain
  reload disabled.
- `Start` is `async void`: it awaits `BuildAsync`, never blocks on it. Blocking (`.Result`, `.Wait()`) would
  deadlock on Unity's main thread as soon as the boot really awaits, as `MemorySaveStore` does.
- A build cancelled because `GameBoot` was destroyed mid-boot is not an error; any other failure is logged.
  Either way the build has already disposed everything it constructed. A build that fails validation reports
  every problem at once, with the file and line of each registration.

With `com.openugd.corelib`, nest the scope in `PlaySession.Lifetime` instead of `Lifetime.Eternal`, or derive
the entry point from `ContextBehaviour`, which roots its scope in the play session and ends it in
`OnDestroy`.

`Tests/` builds the graph in EditMode with a real `GameSettings` asset. It compiles only in a project with the
Unity Test Framework; delete the folder if you do not want it in your Test Runner.
