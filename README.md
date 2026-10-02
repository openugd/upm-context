# Context

A composition root for Unity — **a library, not a framework**.

You describe singletons on a builder. `BuildAsync` validates the whole graph *before* constructing
anything, reports every problem at once with the file and line of each registration, constructs in
dependency order, runs an `Awake → Initialize` boot ordered by dependency rank, and then
either hands you a fully built `Context` or disposes everything it made and rethrows.

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
var windowScope = Lifetime.Define(context.Lifetime, "window");
var wb = Context.CreateBuilder(windowScope, parent: context);
wb.Services.AddInstance(model);
var windowContext = await wb.BuildAsync();   // completes synchronously: nothing here is async

windowScope.Terminate();                     // only the window's own singletons are disposed
```

For a one-off object that is *not* registered and *not* owned by the container, use
`context.Instantiate<T>()` — constructor-injected, and yours to dispose.

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
| `ContextBuilder` | `Services`, `Initializers`, `BuildAsync`. |
| `ServiceCollection` | `Add(Type, factory)`, `Contains`. Everything else is an extension. |
| `Registration` | What `Add` returns. `As(Type)` adds a contract. A struct — no allocation. |
| `InitializerCollection` | Boot steps that are not services. `Mode` picks Parallel or Sequential. |
| `IAwakeService`, `IInitializeService` | Opt-in async boot phases. Services enrol automatically. |
| `ContextException` | The one exception the container throws. `Path` carries the dependency chain. |
| `[Inject]` | Field/property injection, for objects the container did not construct. |
| `[Inject(Optional = true)]` | Same, but injected only if registered. The member is left alone otherwise. |

Core types carry at most three methods; the ergonomics live in extension methods, so you can add your
own without touching the package.

## When it goes wrong

That is the part this package exists for. A missing binding is caught at build time, in Microsoft's
wording so your search reflexes transfer, plus the registration site and a closest-match suggestion:

```
Unable to resolve service for type 'IStorage' while attempting to activate 'SaveService'.
  SaveService was registered at ProjectBoot.cs:14.
  Did you mean 'IStorageService', which is registered?
```

A dependency cycle is a `ContextException` naming the real path (`A → B → C → A`), never a
`StackOverflowException`. Every problem in the graph is reported at once, not one per run. And nothing
is constructed until the whole graph validates — a failed build leaves no half-initialised objects
behind.

## Requirements

Unity 6000.0 or newer. Depends on `com.openugd.lifetime` 2.0.0.

Services are activated by reflection, so managed-code stripping can remove the constructors the
container calls. The package ships no `link.xml`: Unity reads `link.xml` only from a project's `Assets`
folder, never from a package. If you strip managed code:

- register the service with a hand-written factory — `Add<T>(c => new T(c.Resolve<IDep>()))` — which
  uses no reflection and is always safe;
- or put `[OpenUGD.Preserve]` on the constructor the container calls. On the class it keeps only a
  parameterless constructor;
- or list your service types in a `link.xml` under your project's `Assets` folder.

## Licence

Apache-2.0 — see [LICENSE.md](LICENSE.md).
