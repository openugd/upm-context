# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-10-03

Version 2.0.0, the first release of this package, versioned with the synchronized OpenUGD 2.x family. It
replaces the context layer of `com.openugd.corelib` 0.6.x and `com.openugd.dependency.injection` 0.1.x; the
README's "Upgrading to 2.0" section is the migration guide.

### Added

- `Context`, the built container: `TryResolve`, `Instantiate`, `Inject`, `Dispose`, `Lifetime`, `Parent`, with
  typed `Resolve`, `TryResolve` and `Instantiate` in `ContextExtensions`.
- `ContextBuilder` with `Services`, `Initializers`, `BuildAsync`, and `Build` for a boot that completes
  synchronously.
- Registration: `Add<T>()`, `Add<T>(factory)`, `AddInstance<T>(instance)`, `TryAdd<TContract, TImpl>()`; a
  `Registration` handle whose `As<T>()` adds a contract without replacing the self-registration.
- `Registration.AsElementOf<T>()`: several registrations contribute to one `IReadOnlyList<T>`, in registration
  order, local to their context, empty when nothing contributes.
- Constructor injection, `[Inject]` fields and properties, and `[Inject(Optional = true)]`.
- Two opt-in boot phases, `IAwakeService` and `IInitializeService`; `InitializerCollection` for steps that are
  not services; `StartupMode.Parallel` as an opt-in.
- Child contexts: `Context.CreateBuilder(lifetime, parent)`. A child inherits, shadows, and ends with its
  parent.
- `ContextException` with the dependency `Path`. A failed build lists every problem with its registration site.
- IL2CPP stripping support without `link.xml`: `[Inject]` derives from a linker `Preserve` attribute, and the
  registration entry points carry `[DynamicallyAccessedMembers]`. Checked with the UnityLinker of 6000.0.41f1
  and 6000.3.3f1 at Medium and High, and in IL2CPP WebGL players built with 6000.0.41f1 at Medium and High.
- Samples: Basic Boot, Child Scopes, Collections, MonoBehaviour Injection, each with EditMode tests.
- An EditMode test assembly, `com.openugd.context.tests`.

### Changed

Compared with the layer this package replaces. The README's "Upgrading to 2.0" has the details and before and
after code.

- **The boot is awaited.** Building (`BuildAsync` or `Build`) is the only way to get a context, so a failed
  startup surfaces. Affects you if you started the boot with `_ = Install(...)`.
- **A missing binding is an error, not `null`.** Affects you if code tested a resolved service for `null`: use
  `TryResolve` or `[Inject(Optional = true)]`.
- **An unresolvable `[Inject]` member throws** instead of being skipped.
- **The widest satisfiable constructor wins**, not the narrowest. Affects you if a class has more than one
  public constructor.
- **Services are constructed during the build**, not on first resolve, and the whole graph is validated first.
- **Boot order is dependency rank, then registration order, one step at a time.** Affects you if you relied on
  the old parallel default: opt in with `StartupMode.Parallel`.
- **A failed build disposes everything it constructed**, in reverse order.

### Removed

Not carried over from the layer this package replaces.

- Transient registrations (`ToFactory`): use `Instantiate<T>()`, or a child context for a narrower scope.
- `ContextStartup`, the `Service` base class, `IContext` and the service builder: register plain classes on a
  `ContextBuilder`.
- Custom resolvers, `UnRegister` and `OpenUGD.Lazy<T>`.
- `ContextServiceBuilderOptions`: register a settings object with `AddInstance`.
- `[Inject]` on methods: now a compile error. 0.1.x accepted it there but never called the method.

### For code written against a pre-release snapshot

2.0.0 is the first release. Code written against an unreleased snapshot of this package needs these changes:

- **Breaking: the configuration system moved out** to
  [`com.openugd.configuration`](https://github.com/openugd/upm-configuration) (0.x, not part of the 2.0
  release), with `ContextBuilder.Configuration` and the automatic `IConfiguration` registration. Register a
  settings object with `AddInstance`, or call that package's `builder.AddConfiguration()`.
- **Breaking: `IContextInitializer` and `BootPhase.Configure` are gone.** Take `Context` as a constructor
  parameter, or add an `InitializerCollection` step. `BootPhase.Initialize` keeps its value, 2.
- **Breaking: `OpenUGD.PreserveAttribute` is gone**; `OpenUGD.Internal.PreserveAttribute` is only the base of
  `[Inject]` and cannot be applied. Put `[Inject]` on the constructor, or use
  `UnityEngine.Scripting.Preserve`. The package's `link.xml` is gone too: Unity never read it.
- **Breaking: `StartupMode.Sequential` is the default**, permanently, and the enum is renumbered
  (`Sequential = 0`, `Parallel = 1`). Affects you if you stored the mode as a number.
- **Breaking: `Context.Dispose` rethrows a single failure as itself**; two or more arrive as one
  `AggregateException`, as in `com.openugd.lifetime` 2.0.0. Affects you if you caught only
  `AggregateException`.
- **Breaking: `Context.CreateBuilder` on a terminated lifetime or a disposed parent no longer throws.** The
  builder is born terminated and `BuildAsync` throws `OperationCanceledException`. Affects you if you caught
  `InvalidOperationException` from `CreateBuilder`.
- **The minimum Unity version is 6000.0.**

Those snapshots also had defects that 2.0.0 fixes: among others, a boot step's own cancellation passed for a
cancelled build, a `StartupMode.Parallel` rank reported only its first failure, an object answering to several
registrations could be injected, booted or disposed more than once, and a child given a lifetime of its own did
not end with its parent. The README describes how 2.0.0 behaves.

[2.0.0]: https://github.com/openugd/upm-context/releases/tag/2.0.0
