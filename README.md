# Context

A composition root for Unity — **a library, not a framework**. You describe your singletons on a builder;
`BuildAsync` validates the whole graph before it constructs anything, builds and boots it in dependency order,
and either hands you a finished `Context` or reports every problem at once and leaves nothing half-built.

Use it for the services of a game or a tool — saves, audio, networking, UI controllers — that depend on each
other and live as long as a scope: the game, a level, a window. There are no base classes, no scene
components and no assembly scanning, and the runtime has no `UnityEngine` reference, so the same composition
runs in a plain NUnit test, a console app or a headless server build.

## Install

### openupm-cli

```bash
openupm add com.openugd.context@2.0.0
```

### Scoped registry

Add the registry and the package to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.openugd"
      ]
    }
  ],
  "dependencies": {
    "com.openugd.context": "2.0.0"
  }
}
```

### Git URL

A git URL does not resolve the package's OpenUGD dependencies from OpenUPM, so list `com.openugd.lifetime`
as well:

```json
{
  "dependencies": {
    "com.openugd.lifetime": "https://github.com/openugd/upm-lifetime.git#2.0.0",
    "com.openugd.context": "https://github.com/openugd/upm-context.git#2.0.0"
  }
}
```

## Requirements

- Unity 6000.0 or newer.
- [`com.openugd.lifetime`](https://github.com/openugd/upm-lifetime) 2.0.0 or a later 2.x. Installing from
  OpenUPM brings it in.
- Nothing else. The runtime assembly is compiled with `noEngineReferences`; only the samples reference
  `UnityEngine`, and only the tests need the Unity Test Framework.

## Quick start

Services are plain classes. A MonoBehaviour builds the context when the scene starts and ends it when it is
destroyed:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using OpenUGD;
using UnityEngine;

public sealed class Clock { }

public interface ISave { }

public sealed class SaveService : ISave
{
    private readonly Clock _clock;
    public SaveService(Clock clock) => _clock = clock;   // never null: Clock was built first
}

// A service opts in to a boot phase only when it needs one.
public sealed class Profile : IAwakeService
{
    private readonly ISave _save;
    public Profile(ISave save) => _save = save;
    public Task AwakeAsync(CancellationToken cancellationToken) => Task.CompletedTask;   // load something here
}

public sealed class GameBoot : MonoBehaviour
{
    private Lifetime.Definition _scope;

    private async void Start()
    {
        _scope = Lifetime.Eternal.DefineNested("game");   // ended in OnDestroy: see "Play mode and domain reload"

        var builder = Context.CreateBuilder(_scope);
        builder.Services
            .Add<Clock>()                       // a singleton
            .Add<SaveService>().As<ISave>()     // resolvable as SaveService and as ISave
            .Add<Profile>();                    // awakened before BuildAsync returns

        try
        {
            var context = await builder.BuildAsync();
            Debug.Log("Ready: " + context.Resolve<Profile>());
        }
        catch (OperationCanceledException) when (_scope.IsTerminated)
        {
            // Destroyed during the boot: the build was abandoned and disposed what it had built.
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);   // every problem in the graph, with registration sites
        }
    }

    private void OnDestroy() => _scope?.Terminate();   // disposes every service, newest first
}
```

`As<T>()` *adds* a contract: `SaveService` stays resolvable as itself too. The *Basic Boot* sample is this
quick start grown into a full boot, with a settings asset and both boot phases.

## Concepts

### Registrations and contracts

Every registration is a singleton of its context, built once during the build and shared by every contract
it answers to.

```csharp
using OpenUGD;

public interface IStorage { }
public sealed class FileStorage : IStorage { }

public interface IClock { }
public sealed class SystemClock : IClock { }

public sealed class Telemetry
{
    public Telemetry(string endpoint) { }
}

public static class Registrations
{
    public static void Register(ServiceCollection services)
    {
        services.Add<FileStorage>().As<IStorage>();                 // the container constructs it
        services.AddInstance<IClock>(new SystemClock());            // an object you made: IClock only, never disposed here
        services.Add<Telemetry>(c => new Telemetry("telemetry"));   // a factory, called once during the build
        services.TryAdd<IClock, SystemClock>();                     // a default: skipped, IClock is taken
    }
}
```

- **One registration per contract.** Two registrations claiming the same contract are a build error, not a
  last-one-wins overwrite. Override in a child context instead, or offer a default with `TryAdd`.
- **Lookup is by exact type.** An interface the implementation merely implements does not resolve until it is
  added with `As<T>()`.
- **Constructors.** The container uses the constructor marked `[Inject]` if there is one, and otherwise the
  widest public constructor whose parameters can all be resolved. Two equally wide ones that both can be are
  an error, not a coin toss.
- **Disposal.** When the context ends, every `IDisposable` it constructed is disposed in reverse construction
  order, once, however many contracts or registrations hand it out. An object handed to `AddInstance` is never
  disposed by the context.
- **What every context supplies.** `Context` and `Lifetime` (the context's own) resolve without being
  registered, so a service that needs either takes it as a constructor parameter.

### Settings are objects

The container has no configuration system of its own. A setting is an object — a `ScriptableObject` edited in
the Inspector, or any plain object — registered with `AddInstance` and taken as an ordinary constructor
parameter:

```csharp
using OpenUGD;
using UnityEngine;

[CreateAssetMenu(menuName = "Game/Save Settings")]
public sealed class SaveSettings : ScriptableObject
{
    public int Slot;
    public float AutosaveSeconds = 60f;
}

public sealed class Autosave
{
    private readonly SaveSettings _settings;
    public Autosave(SaveSettings settings) => _settings = settings;
}

public sealed class SaveInstaller : MonoBehaviour
{
    [SerializeField] private SaveSettings _saveSettings;

    public void Register(ServiceCollection services)
    {
        services.AddInstance(_saveSettings);   // resolvable as SaveSettings
        services.Add<Autosave>();
    }
}
```

You already hold the object while you register, so a registration can branch on its values.
`AddInstance<ISaveSettings>(asset)` registers it under an interface instead of its own type. The context never
disposes or destroys an instance it was handed: the asset stays yours.

### The boot: Awake and Initialize

Once every service is constructed and injected, the build awaits `AwakeAsync` on every service that
implements `IAwakeService`, then `InitializeAsync` on every `IInitializeService`. Every Awake step finishes
before the first Initialize step starts. Services enrol by implementing the interface; nothing else is needed.

- **Order within a phase.** A service starts a phase only after everything it takes in its constructor,
  resolves in its factory, or holds through an `[Inject]` member has finished that phase — objects handed to
  `AddInstance` included. Services of the same dependency rank run one at a time, **in registration order**,
  under the default `StartupMode.Sequential`.
- **Cycles of `[Inject]` members.** Services that hold each other through `[Inject]` members cannot each boot
  after the other. Such a cycle shares one rank, after everything its members depend on outside it (a
  constructor dependency inside the cycle still boots first), so within the cycle registration order decides:
  register first the one that should boot first.
- **Parallel is opt-in.** `builder.Initializers.Mode = StartupMode.Parallel` boots the services of one rank
  concurrently. On Unity's main thread that interleaves them rather than using other threads, so it saves
  time only when steps await I/O, and the order is no longer deterministic.
- **Steps that are not services.** `builder.Initializers.Add(BootPhase.Initialize, (context, token) => ...,
  name: "warm up")` runs a lambda ahead of the services of its phase. Such steps run one at a time in the order
  they were added, in either mode.
- **The token.** Every step receives the same token. It is cancelled if the build is abandoned and, once the
  context is built, when the context ends, before anything is disposed — so work a step leaves running can stop
  on it. An `OperationCanceledException` from a token of the step's own is a failure of that step.
- **Failure.** A step that throws fails the build: everything constructed is disposed in reverse order, and
  the `ContextException` names the step and where it was registered.

### Building without `await`

Await `BuildAsync`; never block on it. `.Result`, `.Wait()` and `GetAwaiter().GetResult()` deadlock on
Unity's main thread as soon as one boot step really awaits, because the step's continuation is queued to the
very thread that is blocked waiting for it. From code that cannot await — a constructor, a property, `Awake`
— call `builder.Build()`. It runs the same build on the calling thread and returns the context when every
boot step completes synchronously. If a step returns an unfinished task, `Build` does not wait: it throws a
`ContextException` naming that step and its registration, and abandons the build, which is cancelled at once
and disposes what it constructed when that step finishes.

### Child contexts

A child context sees its parent's registrations, shadows whatever it registers again, and its own singletons
are disposed when its own scope ends while the parent's carry on. That is what replaces a `Scoped` service
lifetime — there isn't one, deliberately.

```csharp
using OpenUGD;

public sealed class WindowModel { }

public static class Windows
{
    public static Context Open(Context game, WindowModel model, out Lifetime.Definition windowScope)
    {
        windowScope = game.Lifetime.DefineNested("window");
        var builder = Context.CreateBuilder(windowScope, parent: game);
        builder.Services.AddInstance(model);
        return builder.Build();   // no await: nothing here boots asynchronously
    }
}
```

Terminating `windowScope` disposes only the window's own singletons. A singleton the parent registered is
always built and owned by the parent, even when a child asks for it first, so it never becomes captive in a
shorter scope. A child also **ends with its parent**, whatever scope it was given — before the parent's own
services are disposed — so it never hands out a parent's service after that service is gone. `CreateBuilder`
on a lifetime or a parent that has already ended does not throw: it gives a builder whose build throws
`OperationCanceledException` before constructing anything.

For a one-off object that is *not* registered and *not* owned by the container, use
`context.Instantiate<T>()`: constructor-injected, and yours to dispose.

### Play mode and domain reload

A context lives until the lifetime it was created on ends, and `Lifetime.Eternal` never ends. It is a static
field, so when *Enter Play Mode Options* skip the domain reload, everything nested in it — a root context and
every singleton in it — survives play-mode exit: still alive, still subscribed, still holding its resources in
the next play session. A root context must therefore end with the play session.

- **A scene object's `OnDestroy`.** Unity calls it when the scene unloads and when play mode is exited, so a
  context whose scope a MonoBehaviour ends there, as in the quick start, ends with the session.
- **`com.openugd.corelib`'s `PlaySession.Lifetime`** (namespace `OpenUGD.Core`) is the recommended root in a
  Unity project. It ends when the application quits or play mode is exited, and starts afresh with the next
  session. corelib's `ContextBehaviour` already roots its contexts in it.
- **Without corelib**, end the root on `Application.quitting`:

```csharp
using System.Threading.Tasks;
using OpenUGD;
using UnityEngine;

public static class GameRoot
{
    public static Task<Context> BuildAsync()
    {
        // Application.quitting is raised on player quit and, in the editor, on play-mode exit.
        var session = Lifetime.Eternal.DefineNested("play session");
        void End()
        {
            Application.quitting -= End;   // static events survive a skipped domain reload too
            session.Terminate();
        }

        Application.quitting += End;

        var builder = Context.CreateBuilder(session);
        // builder.Services.Add<...>();
        return builder.BuildAsync();
    }
}
```

This package has no engine reference, so it cannot end a root by itself.

### Several modules, one list

A registration can contribute its object to a list instead of claiming a contract:

```csharp
builder.Services.Add<FpsPanel>().AsElementOf<IDebugPanel>();
builder.Services.Add<MemoryPanel>().AsElementOf<IDebugPanel>();
builder.Services.Add<DebugMenu>();   // public DebugMenu(IReadOnlyList<IDebugPanel> panels)
```

The contributions resolve only as `IReadOnlyList<IDebugPanel>`; `IDebugPanel` itself stays unregistered, so a
single-instance contract is never made ambiguous. The list is in registration order, holds only this
context's contributions — a child's list does not repeat its parent's — and is empty, not an error, when
nothing contributes, so an `IReadOnlyList<T>` parameter of a reference type can always be satisfied. It is
built once, after every element, so `DebugMenu` is constructed and boots after every panel; a panel that takes
the list itself is a dependency cycle. One registration may be in several lists and behind contracts as well,
and is still one object.

### Objects the container did not build

Unity creates MonoBehaviours and ScriptableObjects, so the container never constructs one: `Add<T>()` of such
a type fails the build, which says what to do instead. Use one of these:

- **Inject it.** `context.Inject(component)` fills its `[Inject]` fields and properties from the context.
  Nothing is remembered or disposed; calling it twice injects twice.
- **Register it as it is.** `builder.Services.AddInstance(hud)` makes a scene object available to services.
- **Register a factory that creates it the Unity way**, for example `Add<Hud>(c => Object.Instantiate(prefab))`.

```csharp
using OpenUGD;
using UnityEngine;

public interface IScore
{
    int Value { get; }
}

public sealed class ScoreLabel : MonoBehaviour
{
    [Inject] private IScore _score;   // required: Inject throws if IScore is not registered

    private void Start() => Debug.Log("Score: " + _score.Value);
}

public static class SceneInjection
{
    // After the build, before the scene's components start.
    public static void InjectScene(Context context, GameObject root)
    {
        foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
        {
            context.Inject(behaviour);
        }
    }
}
```

The *MonoBehaviour Injection* sample builds a scene context that does this in `Awake`, with execution order
set so that the scene's components are injected before their `Start`.

### Optional dependencies

A missing binding is an error. That is the point of the container, and it is why the 0.1.x injector — which
returned `null` — is gone. But some collaborators genuinely have a defined behaviour when absent, and for those
there is `Optional`:

```csharp
using OpenUGD;

public interface ILocalization
{
    string Get(string key);
}

public class Greeting
{
    [Inject(Optional = true)] private ILocalization _localization;

    public string Render(string key) => _localization == null ? key : _localization.Get(key);
}
```

With a localization registered the text is translated; without one the key is shown. Neither the build nor
`Inject` fails, and the member keeps whatever it already held — so a field initializer works as a fallback:

```csharp
[Inject(Optional = true)] private IClock _clock = SystemClock.Instance;
```

Use it for a collaborator whose absence *means* something, not to silence a build error. Marking a dependency
the object cannot work without only moves the failure to a `NullReferenceException` somewhere unrelated. Two
guards keep the hole the size it claims to be:

- On a non-nullable value type it is an error. An unsatisfied `int` would be left at `0`, which no code can
  tell apart from an injected `0`.
- On a constructor it is an error, because it would read as "this constructor is optional", which is not a
  thing.

**Constructors do not need it.** The widest *satisfiable* constructor wins, so a second constructor already
says "this one is optional" — with the graph still fully validated:

```csharp
public Report(IClock clock) : this(clock, null) { }
public Report(IClock clock, ILocalization localization) { … }
```

With `ILocalization` registered the wide constructor is used; without it, the narrow one.

### When it goes wrong

That is the part this package exists for. A missing binding is caught at build time, in Microsoft's wording
so your search reflexes transfer, with the registration site and, where the container can find one, the fix.
Register a service only as itself while something asks for its interface:

```csharp
builder.Services.Add<FileStorage>();   // implements IStorage, but is registered only as itself
builder.Services.Add<SaveService>();   // public SaveService(IStorage storage)
```

and the build throws a `ContextException` with this message — captured by the package's tests, with the
call site's path and line replaced by a short one:

```
The Context could not be built. 1 problem was found while validating the service graph, before anything was constructed:
  - Unable to resolve service for type 'MyGame.IStorage' while attempting to activate 'MyGame.SaveService'.
      required by the constructor parameter 'storage'.
      registered at Assets/Scripts/GameBoot.cs:14
      'MyGame.FileStorage' is registered and does implement 'MyGame.IStorage', but was not registered as it. Add .As<IStorage>() to its registration.
```

Every problem in the graph is reported in that one message, not one per run, and nothing is constructed
until the whole graph validates. A dependency cycle names its real path (`A -> B -> C -> A`, also in
`ContextException.Path`), never a `StackOverflowException`. A constructor or factory that throws is
reported with its registration site and the chain of services that led to it, and a boot step that
throws with its name and where it was registered. Either way a failed build disposes what it had
constructed and leaves no half-initialised objects behind.

`ContextException` is not the only exception: a bad argument is an `ArgumentException`, a disposed context an
`ObjectDisposedException`, a second build an `InvalidOperationException`, a cancelled build an
`OperationCanceledException`, and a failed build whose teardown also fails an `AggregateException` whose first
inner exception is the original failure.

### Managed code stripping (IL2CPP)

The container calls constructors and fills `[Inject]` members by reflection, which Unity's linker cannot
follow by itself. The package tells the linker what to keep, so the ordinary ways of using it survive
Medium and High stripping with no extra work:

- **A type you register or instantiate by name keeps its constructors.** `Add<T>()`,
  `TryAdd<TContract, T>()`, `.Add<T>()` on a registration, `Instantiate<T>()`, `Add(typeof(T))` and
  `Instantiate(typeof(T))` ask the linker to keep `T`'s constructors, as long as `T` is written at that
  call — a type argument or a `typeof`.
- **`[Inject]` members are always kept**, together with the attribute the container looks for, so a class
  with an `[Inject]` member is kept even when nothing uses it.
- **A factory needs nothing.** `Add<T>(c => new T(c.Resolve<IDep>()))` calls the constructor in your
  code, where the linker sees it.

Two things break the chain:

- **A generic method of your own.** When `Add<T>()` gets a type parameter of yours instead of a concrete
  type, the linker cannot tell which types will pass through it, so it does not keep their constructors
  for the container. UnityLinker raises trim warning IL2091 naming your method, but the Editor may not
  surface it, so do not rely on the warning. Annotate that type parameter as the package annotates its
  own, or put `[Inject]` on the constructor of every type that goes through the method. Unity's class
  libraries do not include the annotation, so declare an `internal` copy of it once in your assembly; the
  linker recognises it by its full name.
- **A `Type` the linker cannot trace** — read from data, kept in a field, built from a string at run
  time. Put `[Inject]` on the constructor, or list the type in a `link.xml` under your project's
  `Assets` folder.

```csharp
using System.Diagnostics.CodeAnalysis;
using OpenUGD;

public static class GameInstallers
{
    // Passes Add<T>'s promise on: AddGameService<Shop>() keeps Shop's constructors, as Add<Shop>() would.
    public static Registration AddGameService<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors |
                                    DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(
        this ServiceCollection services) where T : class =>
        services.Add<T>();
}

// Once per assembly: Unity's class libraries lack this attribute, and the linker matches it by full name.
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.GenericParameter | AttributeTargets.Parameter |
                    AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.ReturnValue)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes) =>
            MemberTypes = memberTypes;

        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }

    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0003,
        NonPublicConstructors = 0x0004,
    }
}
```

**A class-level `[Preserve]` is not enough.** It keeps only the parameterless constructor, so a
constructor that takes dependencies is still removed. Put `[Inject]` on that constructor instead.

The package ships no `link.xml`, because Unity reads `link.xml` only from a project's `Assets` folder,
never from a package. If stripping leaves a type with no public constructor at all, the build reports
that it "has no public instance constructor" and names stripping as a possible cause. If stripping
removes only some of them, no error names stripping: the container uses the greediest public constructor
that is left and can be satisfied, which may not be the one you meant. So keep the constructors as
described above rather than wait for the error.

This was checked by running the UnityLinker of Unity 6000.0.41f1 and 6000.3.3f1 at Medium and High and
executing the stripped assemblies; no IL2CPP player has been built with it yet.

### Threads

Registration and the build are single-threaded setup code: `ServiceCollection` is not thread-safe. A built
context is: every service already exists, so resolving a registered contract with `TryResolve` or `Resolve`
is a dictionary lookup and an array read, with no lock and no allocation. `Instantiate` and `Inject` may also be called from any thread, but they
use reflection on every call (type metadata is cached), so keep them out of hot loops.

### What it does not do

- **No transient or scoped lifetimes.** Every registration is a singleton of its context; `Instantiate` gives
  a fresh, unowned object, and a child context gives a narrower scope.
- **No open generics, keyed services, implicit multi-registration (an `IEnumerable<T>` of every registration
  of a contract — `AsElementOf` is the explicit form), assembly scanning or decorators**, deliberately.
- **Activation uses reflection.** A source generator that resolves the graph at compile time is deferred. A
  factory registration is a reflection-free path that stays supported, so adding the generator later will not
  be a breaking change.
- **`AsElementOf` lists** are arrays created at run time and exposed as `IReadOnlyList<T>`; this has been run
  on Mono and CoreCLR, not yet in an IL2CPP player.

## API overview

All types are in the `OpenUGD` namespace.

| Type | Members | What it is |
| --- | --- | --- |
| `Context` | `CreateBuilder(lifetime, parent)`, `TryResolve(Type, out object)`, `Instantiate(Type, args)`, `Inject(object)`, `Dispose()`, `Lifetime`, `Parent` | The built container. Also an `IServiceProvider` and an `ILifetimeProvider`. |
| `ContextExtensions` | `Resolve<T>()`, `Resolve(Type)`, `TryResolve<T>(out T)`, `Instantiate<T>()`, `Instantiate<T>(params object[])` | Typed resolution and activation. `Resolve` throws `ContextException` when nothing is registered. |
| `ContextBuilder` | `Services`, `Initializers`, `BuildAsync(token)`, `Build()`, `Lifetime`, `Parent` | Describes one context and builds it, once. |
| `ServiceCollection` | `Add(Type, factory)`, `Contains(Type)`, enumeration of `Registration`s | The registrations. Not thread-safe; sealed by the build. |
| `ServiceCollectionExtensions` | `Add<T>()`, `Add<T>(Func<Context, T>)`, `AddInstance<T>(instance)`, `TryAdd<TContract, TImpl>()` | The typed ways to register. |
| `Registration` | `As(Type)`, `AsElementOf(Type)`, `Services`, `Implementation` | A handle to one registration. A struct: chaining allocates nothing. |
| `RegistrationExtensions` | `As<T>()`, `AsElementOf<T>()`, `Add<T>()` | `As` adds a contract, `AsElementOf` contributes to a list, `Add` starts the next registration. |
| `IAwakeService`, `IInitializeService` | `AwakeAsync(token)`, `InitializeAsync(token)` | Opt-in boot phases. Services enrol by implementing them. |
| `InitializerCollection` | `Add(phase, step, name)`, `Mode` | Boot steps that are not services, and the startup mode. |
| `BootPhase` | `Awake`, `Initialize` | The two phases, in order. |
| `StartupMode` | `Sequential` (default), `Parallel` | How the services of one dependency rank boot. |
| `ContextException` | `Path` | A failed build, resolve, activation or boot step. `Path` is the dependency chain, empty when there is none. |
| `InjectAttribute` | `Optional` | `[Inject]` on a field, property or constructor; `[Inject(Optional = true)]` on a member. |
| `ILifetimeProvider` | `Lifetime` | Anything that carries a scope; `Context` implements it. |

`OpenUGD.Internal.PreserveAttribute` is public only as the base of `InjectAttribute`, which makes the linker
keep `[Inject]` members; it cannot be applied.

## Samples

Import them from *Window > Package Manager > Context > Samples*. Each has a README with its expected output,
and EditMode tests that appear in the Test Runner once imported.

| Sample | Shows |
| --- | --- |
| Basic Boot | Registration, a `ScriptableObject` settings asset with `AddInstance`, Awake and Initialize services, `BuildAsync` awaited from a MonoBehaviour, and a scope that ends in `OnDestroy`. |
| Child Scopes | A root context for the game, a child per level and per window: inherit, shadow, dispose only your own, end with the parent. |
| Collections | `AsElementOf` and `IReadOnlyList<T>` for contributions from several modules: a tick loop, a debug menu, development-only cheats. |
| MonoBehaviour Injection | `Context.Inject` on scene components, a required `[Inject]` member and an `[Inject(Optional = true)]` one, and spawned objects. |

## Running the tests

The package's tests are an EditMode assembly, `com.openugd.context.tests`. List the package under
`testables` in `Packages/manifest.json`; your project needs `com.unity.test-framework`, which new projects
already have:

```json
{
  "dependencies": {
    "com.openugd.context": "2.0.0"
  },
  "testables": [
    "com.openugd.context"
  ]
}
```

Then open *Window > General > Test Runner* and run the EditMode tests. Tests in the category `RequiresUnity`
need the engine; the rest are plain .NET and use no engine API.

## Upgrading to 2.0

`com.openugd.context` had no release before 2.0.0. It replaces two published packages:

- the context layer of `com.openugd.corelib` 0.6.x — `ContextStartup`, `Service`, `IContext`,
  `ContextFactoryComponent` and the service builder. corelib 2.0 depends on this package instead; of that
  layer it keeps only the Unity side, `ContextBehaviour` and `PlaySession`;
- `com.openugd.dependency.injection` 0.1.x — `Injector` and its resolvers. It gets no further releases; its
  published versions stay on OpenUPM.

Both declare `OpenUGD.InjectAttribute`, so remove `com.openugd.dependency.injection` when you add this
package: code that sees both and uses `[Inject]` fails with `CS0433`.

### What behaves differently

- **The boot is awaitable and its failure is observable.** Building is the only way to get a context —
  `BuildAsync`, or `Build` for a boot that completes synchronously — so a failed startup can no longer be
  discarded. 0.6.x started the boot with `_ = Install(...)` in a constructor
  and lost every exception.
- **A missing binding is an error, not `null`.** `Injector.Resolve` returned `null`, which turned into a
  `NullReferenceException` later, in unrelated code. Where an absence is meaningful, say so once at the member
  with `[Inject(Optional = true)]`, or use `TryResolve`.
- **The whole graph is validated before anything is constructed**, and every problem is reported at once.
- **Services are constructed during the build**, not on first `Resolve`.
- **Cycles are reported with their path.** They were a `StackOverflowException`, which under IL2CPP is a crash
  with no managed stack.
- **A failed build is atomic.** Everything constructed is disposed in reverse order and no context escapes.
  There was no teardown or rollback of any kind.
- **The widest satisfiable constructor wins.** 0.1.x took the public constructor with the *fewest* parameters,
  so adding a convenience `public Foo() {}` silently disabled injection for that type. Check classes with more
  than one public constructor.
- **Boot order follows dependency rank, then registration order.** A service could be awakened before something
  it depends on. 0.6.x booted in parallel by default; 2.0 boots one step at a time unless you opt into
  `StartupMode.Parallel`.
- **An `[Inject]` member that cannot be resolved throws.** 0.1.x skipped it silently.
- **No transient or scoped registrations.** `Instantiate` gives a fresh object that you own; a child context on a
  shorter `Lifetime` gives a narrower scope. The container does not track disposable transients, which in
  containers that do is a well-known leak.
- **Settings are objects.** `ContextServiceBuilderOptions`, a `Dictionary<string, object>`, is gone; register a
  `ScriptableObject` or any plain object with `AddInstance`.

### From corelib 0.6.x

Before:

<!-- upm-tools: no-compile (the corelib 0.6.x and dependency.injection 0.1.x API, removed in 2.0) -->
```csharp
public class GameContext : ContextStartup<IContextServiceSetup, GameContext>, IContext
{
    public GameContext(Lifetime lifetime, Logger logger)
    {
        Lifetime = lifetime;
        Injector = new Injector();
        Injector.ToValue(this);
        _ = Install(this, this, IContextServiceBuilder.Default(logger, lifetime, Injector));   // failures are lost
    }

    public Injector Injector { get; }
    public Lifetime Lifetime { get; }
    public object Resolve(Type type) => Injector.Resolve(type);

    protected override void OnAwake(IContextServiceSetup setup)
    {
        setup.AddService<IClock>(() => new SystemClock());
        setup.AddService<ISaveService>(() => new SaveService());
    }

    protected override void OnConfigure(GameContext context) { }

    protected override void OnStart(GameContext context) => context.Resolve<ISaveService>().Continue();
}

public class SaveService : Service, ISaveService
{
    private IClock _clock;

    protected override Task OnAwake()
    {
        _clock = Resolve<IClock>();   // null when IClock is not registered
        return Task.CompletedTask;
    }

    public void Continue() { }
}
```

After:

```csharp
using System.Threading;
using System.Threading.Tasks;
using OpenUGD;

public interface IClock { }
public sealed class SystemClock : IClock { }

public interface ISaveService
{
    void Continue();
}

public sealed class SaveService : ISaveService, IAwakeService
{
    private readonly IClock _clock;

    public SaveService(IClock clock) => _clock = clock;   // the build fails if IClock is not registered

    public Task AwakeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Continue() { }
}

public static class Game
{
    public static async Task<Context> StartAsync(Lifetime lifetime)
    {
        var builder = Context.CreateBuilder(lifetime);
        builder.Services
            .Add<SystemClock>().As<IClock>()
            .Add<SaveService>().As<ISaveService>();

        var context = await builder.BuildAsync();     // throws if anything failed; nothing is left half-built
        context.Resolve<ISaveService>().Continue();   // what OnStart did
        return context;
    }
}
```

| corelib 0.6.x | 2.0 |
| --- | --- |
| `ContextStartup<TSetup, TContext>` and `Install(...)` | `Context.CreateBuilder(lifetime)`, then `await builder.BuildAsync()` |
| `OnAwake(setup)` with `setup.AddService(...)` | `builder.Services.Add<T>()` and friends, before the build |
| `OnConfigure(context)`, between the two phases | `builder.Initializers.Add(BootPhase.Initialize, (context, token) => ...)`: runs after every Awake step and before every `IInitializeService` |
| `OnStart(context)` | the code after `await builder.BuildAsync()` |
| `setup.AddService<IApi>(() => new Impl())` | `builder.Services.Add<Impl>().As<IApi>()`, or `Add<IApi>(c => new Impl(...))` for a factory |
| `Service` with `OnAwake()` / `OnInitialize()` | any class implementing `IAwakeService` / `IInitializeService` |
| `Service.Resolve<T>()` | a constructor parameter |
| `Service.Lifetime` | a `Lifetime` constructor parameter: the context's lifetime |
| `Service.Logger` | register your logger and take it as a constructor parameter |
| `Service.State` | none: a context that `BuildAsync` returned has booted every service |
| `ContextServiceBuilderOptions.InitializationStrategy` (default `Parallel`) | `builder.Initializers.Mode` (default `StartupMode.Sequential`) |
| `IContext`, the abstract `OpenUGD.Core.Context`, `IInjectorProvider` | the sealed `OpenUGD.Context`; take `Context` as a constructor parameter where it is needed |
| `IServicesObserverRegister`, `UnityDebugServiceObserver` | no counterpart; a failure is the `ContextException` from `BuildAsync` |
| `ContextFactoryComponent<T>` | corelib 2.0's `ContextBehaviour` (override `CreateContextAsync`), or a MonoBehaviour of your own as in the quick start |

### From dependency.injection 0.1.x

| 0.1.x (`Injector`) | 2.0 (`Context`) | What changes |
| --- | --- | --- |
| `new Injector()`, `new Injector(parent)` | `Context.CreateBuilder(lifetime)`, `Context.CreateBuilder(lifetime, parent)`, then `await builder.BuildAsync()` | Register first, build once, then resolve. A child sees its parent's registrations and shadows them with its own. |
| `ToValue(instance)`, `ToValue<TApi>(instance)` | `builder.Services.AddInstance<TApi>(instance)` | Reference types only: put primitive settings on a class or a `ScriptableObject`. Registered as `TApi`, inferred from the static type when omitted (0.1.x's `ToValue(instance)` used the run-time type). The instance's `[Inject]` members are filled during the build; it is never disposed. |
| `ToSingleton<T>()`, `ToSingleton<TApi, TImpl>()` | `builder.Services.Add<T>()`, `builder.Services.Add<TImpl>().As<TApi>()` | Constructed during the build, not on first `Resolve`. `TImpl` stays resolvable as itself; both contracts get the same instance. |
| `ToSingleton<T>(() => ...)` | `builder.Services.Add<T>(c => ...)` | Called once, during the build, with the context being built, so `c.Resolve<TDep>()` works inside it. |
| `ToFactory<T>()`, `ToFactory<TApi, TImpl>()` | `context.Instantiate<TImpl>()` | No transient registrations. Each call constructs a new object that is not registered, not cached and not disposed by the context. `Instantiate<T>(args)` supplies constructor arguments, matched by type. |
| `ToValue<T>(() => ...)` | `builder.Services.AddInstance<Func<T>>(() => ...)` | 0.1.x called the delegate on every `Resolve`. Every registration is now one instance, so register the delegate and call it. |
| `injector.Resolve<T>()` | `context.Resolve<T>()`, `context.TryResolve<T>(out var value)` | A missing registration throws `ContextException` instead of returning `null`. |
| `injector.Inject(obj)` | `context.Inject(obj)` | An unresolvable `[Inject]` member throws `ContextException`; mark the ones that may be absent `[Inject(Optional = true)]`. |
| `[Inject] Lazy<T>` | `[Inject] T` | `OpenUGD.Lazy<T>` is gone. Members are filled after every constructor has run, so two services can hold each other through `[Inject]` members. |
| a dependency on `IInjector`, `IResolve` or `IInject` | a constructor parameter of type `Context` (or `Lifetime`) | Both are available in every context without being registered. |
| `Register(type, resolver)`, `UnRegister(type)`, a custom `IResolver` | `builder.Services.Add(type, c => ...)` | No custom resolvers and no unregistering: a built context is fixed. Override a registration in a child context instead. |
| `[Inject]` on a method | — | A compile error now: the attribute applies to constructors, fields and properties. |

## Versioning

The OpenUGD packages share a major version and have independent minor and patch versions. Each 2.x package
works with the 2.x versions of its dependencies at or above the minimums declared in its `package.json`; for
this package that is `com.openugd.lifetime` 2.0.0. The changes in each version are in
[CHANGELOG.md](CHANGELOG.md).

## Licence

Apache-2.0 — see [LICENSE.md](LICENSE.md).
