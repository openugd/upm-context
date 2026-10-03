# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-07-29

First release. The version starts at 2.0.0 because the whole OpenUGD family moved to a synchronized
major version together. Minor and patch versions are independent: a 2.x package works with the 2.x
versions of its dependencies at or above the minimums in its `package.json`.

This package replaces the context layer of `com.openugd.corelib` and the whole of
`com.openugd.dependency.injection`, which is being deprecated.

### Added

- `Context` — the built container: `TryResolve`, `Instantiate`, `Inject`, `Dispose`, plus `Lifetime`
  and `Parent`. Everything else is an extension method.
- `ContextBuilder` with `Services`, `Initializers` and a single `BuildAsync`.
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
  `InitializerCollection` exists only for boot steps that are not services. Within a phase the boot runs
  by dependency rank and, within a rank, one step at a time in registration order
  (`StartupMode.Sequential`, the default). `StartupMode.Parallel` boots the services of a rank
  concurrently and is opt-in.
- Child contexts: `Context.CreateBuilder(lifetime, parent)`. A child sees the parent's registrations,
  shadows what it re-registers, and its singletons die with its own `Lifetime`, which ends no later than
  the parent's. A parent-registered singleton is always built and cached in the parent, even when first
  requested through a child.
- `ContextException` carrying the dependency `Path`.
- Support for IL2CPP managed code stripping with no `link.xml`. `[Inject]` derives from a linker
  `Preserve` attribute, so every `[Inject]` member survives with the attribute the container reads. The
  entry points that hand a user type to the activator (`Add<T>()`, `TryAdd`, `Registration.Add<T>()`,
  both `Instantiate<T>`, `ServiceCollection.Add(Type)` and `Context.Instantiate(Type)`) carry
  `[DynamicallyAccessedMembers]`, so the constructors of every type written at those calls survive Medium
  and High stripping. Checked by running the UnityLinker of 6000.0.41f1 and 6000.3.3f1 at both levels
  and executing the stripped assemblies; no IL2CPP player was built. The "no public instance constructor"
  error names stripping as a possible cause.

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
  pass through a registration factory or a constructor that resolves from its `Context`, and reported
  with the real path. Previously a cycle was an
  uncatchable `StackOverflowException`, which under IL2CPP is a hard crash with no managed stack.
- **A failed build is atomic.** Everything already constructed is disposed in reverse order and no
  `Context` escapes. There was previously no teardown or rollback of any kind.
- **The greediest satisfiable constructor wins**, following .NET Core. The old `TypeProvider` seeded
  `maxParameters = int.MaxValue` and silently took the constructor with the *fewest* parameters, so
  adding a convenience `public Foo() {}` disabled injection for that type.
- **Boot order follows dependency rank, then registration order.** Previously a service could be
  awakened before something it depends on. Within a rank, steps run one at a time in registration order
  unless you opt into `StartupMode.Parallel`.
- **No `Transient` and no `Scoped` service lifetimes.** `Instantiate` covers "give me a fresh one" and
  hands ownership to the caller; a child context on a shorter `Lifetime` covers "a narrower scope".
  Note that .NET Core tracks disposable transients in the provider, which is a well-known leak source;
  this package deliberately does not.
- **`ContextServiceBuilderOptions`, a `Dictionary<string, object>` with one typed property, is gone.**
  A setting is an object — a `ScriptableObject` or any plain object — registered with `AddInstance` and
  taken as an ordinary constructor parameter. The container has no configuration system of its own.

### Changed during the 2.0.0 cycle

Code written against an unreleased snapshot of this package, as the rest of the OpenUGD family was, needs
these changes.

- **Breaking: `OpenUGD.PreserveAttribute` is gone.** Its replacement, `OpenUGD.Internal.PreserveAttribute`,
  exists only as the abstract base of `[Inject]`; its constructor is protected, so it cannot be applied.
  Migration: a registered type needs nothing (see *Added*); otherwise put `[Inject]` on the constructor
  the container calls, or use `UnityEngine.Scripting.Preserve` for anything else.
- **Breaking: `StartupMode.Sequential` is the default**, permanently; it was `Parallel`. Migration: set
  `builder.Initializers.Mode = StartupMode.Parallel` where same-rank boot steps should overlap.
- **Breaking: `StartupMode` is renumbered** to `Sequential = 0`, `Parallel = 1`, so `default(StartupMode)`
  is the default mode. Migration: code that names the members needs nothing; a value stored as a number
  (a serialized field, a saved setting) now means the other mode, so set it again after upgrading.
- **Breaking: `Context.Dispose` rethrows a single failure as itself.** When exactly one service throws
  while the context is disposed, that exception is rethrown with its original stack trace instead of
  being wrapped in an `AggregateException`; two or more still arrive as one `AggregateException`. This
  follows `com.openugd.lifetime` 2.0.0. The same applies to the teardown exception that a failed
  `BuildAsync` puts second in its `AggregateException`. Migration: catch the exception your service
  throws (or `Exception`) rather than only `AggregateException`.
- **Breaking: `Context.CreateBuilder` on a terminated lifetime or a disposed parent no longer throws.** It
  returns a builder that is born terminated — the rule `Lifetime.DefineNested` follows in
  `com.openugd.lifetime` 2.0.0 — whose `BuildAsync` throws `OperationCanceledException` without
  constructing anything, as it does when the scope ends just after `CreateBuilder`. Migration: code that
  caught `InvalidOperationException` from `CreateBuilder` catches `OperationCanceledException` from
  `BuildAsync`, which it has to handle anyway for a scope that ends during the build.

### Fixed

Defects in unreleased snapshots of this package, found by the 2026-09 audit and fixed before release.

- **A boot step's own cancellation is a failure of that step, not a cancellation of the build** (audit
  CX-12). Every `OperationCanceledException` a step threw used to propagate unwrapped, so a timeout inside
  `AwakeAsync` — a `TaskCanceledException` from an HTTP call, a `Task.Delay` on a token of its own — was
  reported as if the caller had cancelled the build, with no step named. Now only a cancellation while the
  build's own token is cancelled propagates unwrapped; any other is wrapped in a `ContextException` that
  names the step and the phase and keeps the original as its `InnerException`.
- **Every boot step that fails in a `StartupMode.Parallel` rank is reported** (audit CX-11). The rank was
  awaited with `Task.WhenAll`, whose `await` rethrows only the first failure, so the others were lost. One
  failure is still its own `ContextException`; several arrive as one `ContextException` that names every
  failed step with its exception's type and message, and whose `InnerException` is an
  `AggregateException` of the per-step `ContextException`s in boot order.
- **Steps added to `InitializerCollection` run one at a time, in the order they were added, in both
  startup modes** (audit CX-10). They all shared one rank, so under `StartupMode.Parallel` they ran
  concurrently although a later step may well rely on an earlier one and nothing in a lambda says so.
  Each now has a rank of its own, still ahead of every service of its phase.
- **An object that answers to several registrations is injected, booted and disposed at most once, and a
  child never disposes, injects or boots an object its parent holds** (audit CX-1). Ownership was decided
  per registration, so the .NET-idiomatic forwarding factory `Add<IFoo>(c => c.Resolve<Foo>())` disposed
  `Foo` twice and ran its boot phases twice; one object handed to `AddInstance` under two contracts was
  injected and booted twice; a factory returning an `AddInstance` object made the context dispose an
  object it had been told it did not own; and a child that registered its parent's object again
  re-injected it with the child's own services, booted it again, and disposed it when the child ended.
  Ownership now goes by the object, compared by reference: the first registration that holds an object
  claims it, an `AddInstance` object is never disposed, and an object any ancestor context holds is
  never injected, booted or disposed by a descendant.
- **A service no longer boots before a collaborator it holds through an `[Inject]` member** (audit CX-9).
  Boot rank counted only constructor parameters and what a factory resolved, so a member edge — and every
  edge of an `AddInstance` object, which has no constructor — was invisible, and under
  `StartupMode.Parallel` the holder raced the collaborator whatever the registration order. Rank now
  counts member edges as well, for `AddInstance` objects too. Members may be cyclic, and an edge that
  closes a cycle cannot be honoured, so it is not counted: the services of such a cycle share one rank,
  after everything any of them depends on outside it, and boot in registration order within it; a
  constructor or factory dependency inside the cycle still boots first.
- **Ending a context's scope while a boot step is running cancels the build first and disposes only after
  every step in flight has finished** (audit CX-2). The context's lifetime was nested in the lifetime
  given to `CreateBuilder`, and the build's cancellation was registered on it before any service, so
  ending that lifetime mid-boot disposed every service — and ran every action the services had
  registered on the context's `Lifetime` — synchronously, while `AwakeAsync` was still using them, and
  only then cancelled the token. The context's lifetime is now linked to that lifetime instead of nested
  in it: an end that arrives during the build, or `Context.Dispose` called from a factory or a boot step,
  only cancels the build's token, and the build ends the scope once it has unwound. Before and after the
  build an end is immediate, as before, and teardown keeps its reverse construction order with the
  services' own lifetime actions interleaved.
- **The token handed to boot steps behaves the same whether or not a token was passed to `BuildAsync`**
  (audit CX-13). Without one, steps got a token derived from the context's lifetime, which stayed live
  for the context's whole life and was cancelled when it ended — after its services had been disposed;
  with one, they got a linked token whose source was disposed when the build returned, so it was never
  cancelled at all afterwards. Now every step gets the same token, cancelled when the build is abandoned
  (the caller's token is cancelled, or the scope ends during the build) and, once the context is live,
  when the context ends, before anything is disposed — so work a step leaves running can stop on it. The
  caller's token is listened to only while the build runs.
- **A child given a lifetime of its own still ends with its parent** (audit CX-3). The child's scope was
  nested in `lifetime ?? parent.Lifetime`, so a child built on a lifetime not nested in the parent's
  outlived a disposed parent and went on handing out its disposed services. A child's scope is now also
  linked to the parent's lifetime, and ends — before the parent's services are disposed — with whichever
  of the two ends first. `BuildAsync` rechecks: a parent disposed after `CreateBuilder` cancels the build
  before anything is constructed, and one disposed during the boot cancels it like any end of the scope.

- **A registration factory that throws is reported like a constructor that throws** (audit CX-8). What a
  factory threw used to leave the build unwrapped, with no registration site and nothing to say which
  service it was building. It is now a `ContextException` naming the registration, its file and line,
  and the original's type and message, with the original as its `InnerException`.
- **A failure during construction carries the chain that led to it** (audit CX-14). A constructor or a
  factory that throws, and a factory that returns `null` or the wrong type, now report the services being
  constructed at that moment (`while constructing A -> B -> C`), and `ContextException.Path` holds the
  same chain. A failure that passes through a factory or a constructor on its way out is reported once,
  for the service that failed, not wrapped again at each level. A factory that catches such a failure and
  carries on no longer corrupts the build's bookkeeping, which used to crash it with an
  `ArgumentOutOfRangeException` or report a cycle that was not there.
- **A cycle found during construction names what hid it** (audit CX-14). Its message always blamed a
  registration factory, and a cycle through a constructor that resolved from its `Context` was not
  reported as a cycle at all: it arrived wrapped as "the constructor threw", with an empty `Path`. It is
  now reported as a cycle with its path, and the message names each link the validation could not see —
  a factory that resolves a service, or a constructor that resolves one from its `Context` — with its
  registration site.

- **A boot step that fails is named with the file and line it was registered at** (audit CX-14). The
  message named the step and the phase only. A service's step now carries the site of the registration
  that owns the service, and a step added to `InitializerCollection` the site of its `Add` call, which
  records it the way `ServiceCollection.Add` does (two compiler-supplied parameters after `name`). Each
  step listed in a `StartupMode.Parallel` rank failure carries its site too, and the step's exception
  type and message are on the first line.

- **`Instantiate` reports a missing dependency the way the build does** (audit CX-14). It said only
  "Unable to resolve service for type 'X' (constructor parameter 'x')". It now uses the build's wording,
  names the type it was activating and the parameter, and adds the build's suggestion: a registered type
  that implements the contract but was not registered as it, or the nearest registered name.

- **`Instantiate` chooses a constructor by the build's rule** (audit CX-23). The build rejects two
  equally wide public constructors that can both be satisfied as ambiguous; `Instantiate` silently took
  whichever reflection happened to list first. It now throws the same ambiguity error, naming both
  signatures. Constructors are also sorted stably, widest first with ties in the order reflection lists
  them, so which one is tried and named first no longer varies between runs (`Array.Sort` is unstable and
  did reorder them on .NET's runtime once a type had more than 16 constructors).

- **A `MonoBehaviour`, `ScriptableObject` or other `UnityEngine.Object` is never constructed by
  reflection** (audit CX-27). `Add<T>()` and `Instantiate<T>()` called its constructor, which in Unity
  only logs a warning and yields an object with no native counterpart. Both now refuse such a type —
  recognised by the full name of a base type, since the package has no engine reference — and say what to
  do instead: register the object Unity made with `AddInstance`, or a factory that creates it the Unity
  way; for `Instantiate`, create it and pass it to `Context.Inject`. (The other half of CX-27, engine
  objects' properties leaking into configuration, left with the configuration system.)

- **A suggestion never asks for an `.As<>` that cannot be added** (audit CX-28). A missing
  `ILifetimeProvider` or `IServiceProvider` was answered with "add .As<…>() to its registration" for
  `Context`, which every context supplies itself and nobody registers, and a missing `object` with
  ".As<Object>()" on whatever happened to be registered first. `Context` and `Lifetime` are no longer
  proposed for `.As<>` — the message says to take them directly instead — and `object` gets no `.As<>`
  suggestion at all.

### Known limitations

- Activation uses reflection. A Roslyn source generator that resolves the graph at compile time was
  designed and deliberately deferred; a hand-written factory registration is a permanently supported,
  reflection-free path, so adding the generator later will not be a breaking change.
- Open generics, keyed services, multi-registration (`IEnumerable<T>` of every implementation),
  assembly scanning and decorators are not supported, deliberately.
