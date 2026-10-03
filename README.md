# Context

A composition root for Unity — **a library, not a framework**.

You describe singletons on a builder. `BuildAsync` validates the whole graph *before* constructing
anything, reports every problem at once with the file and line of each registration, constructs in
dependency order, runs an `Awake → Initialize` boot ordered by dependency rank and then by registration
order, and either hands you a fully built `Context` or disposes everything it made and rethrows.

Scopes are [`Lifetime`](https://github.com/openugd/upm-lifetime)s. There is no `UnityEngine` reference,
so the whole thing runs in a plain NUnit test, a console app, or a headless server build.

## Install

```
openupm add com.openugd.context
```

Or add a scoped registry to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": ["com.openugd"]
    }
  ],
  "dependencies": {
    "com.openugd.context": "2.0.0"
  }
}
```

Or by git URL: `https://github.com/openugd/upm-context.git`

## Quick start

```csharp
using System.Threading.Tasks;
using OpenUGD;

// Plain classes. No base class, no marker interface, no attributes.
public sealed class Clock { }

public interface ISave { }
public sealed class SaveService : ISave
{
    private readonly Clock _clock;
    public SaveService(Clock clock) => _clock = clock;   // non-null here, not "later"
}

// Opt in to a boot phase only if you need one.
public sealed class Profile : IAwakeService
{
    private readonly ISave _save;
    public Profile(ISave save) => _save = save;
    public Task AwakeAsync(System.Threading.CancellationToken ct) => LoadAsync(ct);
    private Task LoadAsync(System.Threading.CancellationToken ct) => Task.CompletedTask;
}
```

```csharp
var scope   = Lifetime.Eternal.DefineNested("app");
var builder = Context.CreateBuilder(scope);

builder.Services
    .Add<Clock>()                       // plain singleton
    .Add<SaveService>().As<ISave>()     // also resolvable as ISave
    .Add<Profile>();                    // async-initialised

var context = await builder.BuildAsync();

context.Resolve<Profile>();
```

`As<T>()` *adds* a contract — `SaveService` stays resolvable as itself too. Chain as many as you like:
`.Add<UIWindowService>().As<IUIWindowService>().As<IUIWindowsProvider>()`.

## Settings

The container has no configuration system of its own. A setting is an object: a `ScriptableObject`
you edit in the Inspector, or any plain object, registered with `AddInstance` and taken as an ordinary
constructor parameter.

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
```

```csharp
[SerializeField] private SaveSettings _saveSettings;   // on the MonoBehaviour that builds the context

builder.Services.AddInstance(_saveSettings);            // resolvable as SaveSettings
builder.Services.Add<Autosave>();
```

You already hold the object while you register, so a registration can branch on its values.
`AddInstance<ISaveSettings>(asset)` registers it under an interface instead of its own type. The context
never disposes an instance it was handed: the asset stays yours.

## Scopes

A child context sees its parent's registrations, shadows whatever it re-registers, and its own
singletons die with its own `Lifetime` while the parent's survive. That is what replaces a
`Scoped` service lifetime — there isn't one, deliberately.

```csharp
var windowScope = context.Lifetime.DefineNested("window");
var wb = Context.CreateBuilder(windowScope, parent: context);
wb.Services.AddInstance(model);
var windowContext = wb.Build();              // no await: nothing here boots asynchronously

windowScope.Terminate();                     // only the window's own singletons are disposed
```

A child also ends with its parent, whatever lifetime it was given, so it never hands out the parent's
services after they are gone.

For a one-off object that is *not* registered and *not* owned by the container, use
`context.Instantiate<T>()` — constructor-injected, and yours to dispose.

## Boot order

`BuildAsync` awaits `AwakeAsync` on every service that implements `IAwakeService`, then
`InitializeAsync` on every `IInitializeService`. Within each phase a service starts only after everything
it takes in its constructor, resolves in its factory, or holds through an `[Inject]` member has finished
that phase — objects handed to `AddInstance` included. Services of the same dependency rank run one at a
time, **in registration order**.

Services that hold each other through `[Inject]` members cannot each boot after the other. Such a cycle
shares one rank, after everything its members depend on outside it (a constructor dependency inside the
cycle still boots first), so within the cycle registration order decides under the default mode:
register first the one that should boot first.

`builder.Initializers.Mode = StartupMode.Parallel` boots the services of one rank concurrently instead.
On Unity's main thread that interleaves them rather than using other threads, so it only saves time when
steps await I/O, and the order is no longer deterministic. Steps added to `builder.Initializers` run
first, one at a time in the order they were added, in either mode.

## Building without `await`

Await `BuildAsync`; never block on it. `.Result`, `.Wait()` and `GetAwaiter().GetResult()` deadlock on
Unity's main thread as soon as one boot step really awaits, because the step's continuation is queued to
the very thread that is blocked waiting for it. From code that cannot await — a constructor, a property,
`Awake` — call `builder.Build()`. It runs the same build on the calling thread and returns the context
when every boot step completes synchronously. If a step returns an unfinished task, `Build` does not wait:
it throws a `ContextException` naming that step and its registration, and abandons the build, which is
cancelled at once and disposes what it constructed when that step finishes.

## Optional dependencies

A missing binding is an error. That is the point of the container, and it is why the 1.x injector —
which returned `null` — is gone. But some collaborators genuinely have a defined behaviour when
absent, and for those there is `Optional`:

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

With a localization registered the text is translated; without one the key is shown. Neither the
build nor `Inject` fails, and the member keeps whatever it already held — so a field initializer
works as a fallback:

```csharp
[Inject(Optional = true)] private IClock _clock = SystemClock.Instance;
```

Use it for a collaborator whose absence *means* something, not to silence a build error. Marking a
dependency the object cannot work without only moves the failure to a `NullReferenceException`
somewhere unrelated — the exact 1.x behaviour this package exists to remove. Two guards keep the
hole the size it claims to be:

- On a non-nullable value type it is an error. An unsatisfied `int` would be left at `0`, which no
  code can tell apart from an injected `0`.
- On a constructor it is an error, because it would read as "this constructor is optional", which is
  not a thing.

**Constructors do not need it.** The greediest *satisfiable* constructor wins, so a second
constructor already says "this one is optional" — with the graph still fully validated:

```csharp
public Report(IClock clock) : this(clock, null) { }
public Report(IClock clock, ILocalization localization) { … }
```

With `ILocalization` registered the wide constructor is used; without it, the narrow one. Nothing is
silent and nothing is reflective about it.

## API

| Type | What it is |
| --- | --- |
| `Context` | The built container. `TryResolve`, `Instantiate`, `Inject`, `Dispose`. |
| `ContextBuilder` | `Services`, `Initializers`, `BuildAsync`, and `Build` for a boot that completes synchronously. |
| `ServiceCollection` | `Add(Type, factory)`, `Contains`. Everything else is an extension. |
| `Registration` | What `Add` returns. `As(Type)` adds a contract. A struct — no allocation. |
| `InitializerCollection` | Boot steps that are not services. `Mode` is Sequential by default; Parallel is opt-in. |
| `IAwakeService`, `IInitializeService` | Opt-in async boot phases. Services enrol automatically. |
| `ContextException` | What a failed build, resolve or activation throws. `Path` carries the dependency chain. Bad arguments, a disposed context and cancellation throw the standard exceptions. |
| `[Inject]` | Field/property injection, for objects the container did not construct. |
| `[Inject(Optional = true)]` | Same, but injected only if registered. The member is left alone otherwise. |

The core types stay small — `Context` does its work through `TryResolve`, `Instantiate` and `Inject` —
and the ergonomics (`Resolve<T>`, `Add<T>`, `As<T>`, `AddInstance`, `TryAdd`) are extension methods, so
you can add your own without touching the package.

## When it goes wrong

That is the part this package exists for. A missing binding is caught at build time, in Microsoft's
wording so your search reflexes transfer, with the registration site and, where the container can find
one, the fix. Register a service only as itself while something asks for its interface:

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

## Requirements

Unity 6000.0 or newer. Depends on `com.openugd.lifetime` 2.0.0.

## Managed code stripping (IL2CPP)

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

## Licence

Apache-2.0 — see [LICENSE.md](LICENSE.md).
