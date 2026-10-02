# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-07-29

First release. The version starts at 2.0.0 because the whole OpenUGD family moved to a synchronized
major version together — any 2.x package works with any other 2.x package.

This package replaces the context layer of `com.openugd.corelib` and the whole of
`com.openugd.dependency.injection`, which is being deprecated.

### Added

- `Context` — the built container: `TryResolve`, `Instantiate`, `Inject`, `Dispose`, plus `Lifetime`
  and `Parent`. Everything else is an extension method.
- `ContextBuilder` with `Services`, `Configuration`, `Initializers` and a single `BuildAsync`.
- `ServiceCollection.Add(Type, factory)` returning a `Registration` struct whose `As(contract)` adds a
  contract without replacing the self-registration, so `Add<A>().As<I1>().As<I2>()` leaves `A`, `I1`
  and `I2` all resolvable.
- Constructor injection as the primary mechanism, resolved at build time. `[Inject]` field and property
  injection remains for objects the container did not construct.
- `[Inject(Optional = true)]` for a collaborator whose absence has a defined behaviour. The member is
  injected when its contract is registered and left exactly as it was otherwise, so a field initializer
  serves as a fallback; neither the build nor `Inject` fails. It is rejected on a non-nullable value type
  (an unsatisfied `int` is indistinguishable from an injected `0`) and on a constructor (where it would
  read as "this constructor is optional"). Constructor parameters need no equivalent: a second, narrower
  constructor already expresses it, because the greediest *satisfiable* constructor wins.
- Two opt-in async boot phases, `IAwakeService` and `IInitializeService`. Services enrol automatically;
  `InitializerCollection` exists only for boot steps that are not services.
- `ConfigurationManager`, a `string → string` configuration readable during registration, with
  `AddJson`, `AddObject` and `AddDictionary` providers and `Get<T>` / `Bind` for typed access. It is
  registered into the container, so a service can take `IConfiguration` as a constructor parameter.
- Child contexts: `Context.CreateBuilder(lifetime, parent)`. A child sees the parent's registrations,
  shadows what it re-registers, and its singletons die with its own `Lifetime`. A parent-registered
  singleton is always built and cached in the parent, even when first requested through a child.
- `ContextException` carrying the dependency `Path`.
- `[Preserve]`, matched by Unity's linker by name, and a `link.xml`.

### Behaviour that differs from the layer this replaces

Read this section if you are migrating from `ContextStartup` / `Service` / `Injector`.

- **The boot is awaitable and its failure is observable.** `BuildAsync` is the only way to obtain a
  `Context`, so a startup failure can no longer be discarded — the old code did
  `_ = Install(...)` in a constructor and lost every exception.
- **A missing binding is an error, not `null`.** `Injector.Resolve` returned `null`, which six corelib
  call sites turned into a `NullReferenceException` a frame later, in unrelated code. Where the absence
  really is meaningful, `[Inject(Optional = true)]` says so at the member, once, instead of leaving every
  resolve in the codebase nullable.
- **The whole graph is validated before anything is constructed**, and every problem is reported at
  once rather than one per run.
- **Cycles are detected**, statically for constructor graphs and at construction time for cycles that
  pass through a registration factory, and reported with the real path. Previously a cycle was an
  uncatchable `StackOverflowException`, which under IL2CPP is a hard crash with no managed stack.
- **A failed build is atomic.** Everything already constructed is disposed in reverse order and no
  `Context` escapes. There was previously no teardown or rollback of any kind.
- **The greediest satisfiable constructor wins**, following .NET Core. The old `TypeProvider` seeded
  `maxParameters = int.MaxValue` and silently took the constructor with the *fewest* parameters, so
  adding a convenience `public Foo() {}` disabled injection for that type.
- **Boot order follows dependency rank, not registration order.** Previously a service could be
  awakened before something it depends on.
- **No `Transient` and no `Scoped` service lifetimes.** `Instantiate` covers "give me a fresh one" and
  hands ownership to the caller; a child context on a shorter `Lifetime` covers "a narrower scope".
  Note that .NET Core tracks disposable transients in the provider, which is a well-known leak source;
  this package deliberately does not.
- **`ContextServiceBuilderOptions`, a `Dictionary<string, object>` with one typed property, is gone.**
  Configuration is `string → string` with typed binding on top.

### Known limitations

- Activation uses reflection. A Roslyn source generator that resolves the graph at compile time was
  designed and deliberately deferred; a hand-written factory registration is a permanently supported,
  reflection-free path, so adding the generator later will not be a breaking change.
- Open generics, keyed services, multi-registration (`IEnumerable<T>` of every implementation),
  assembly scanning and decorators are not supported, deliberately.
