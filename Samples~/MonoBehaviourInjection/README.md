# MonoBehaviour Injection

Unity creates scene components, so the context cannot construct them. It can fill their `[Inject]` fields and
properties instead: `context.Inject(component)` assigns every `[Inject]` member from the context's services.
A member marked `[Inject(Optional = true)]` is assigned only when its contract is registered, and is left as
it was otherwise.

## How to set it up

No scene is shipped; the setup takes a minute.

1. Create an empty GameObject and add `SceneContext`.
2. Create another GameObject and add `ScoreLabel` and `PointsTicker`.
3. Press Play.

The Console shows `Score: 0`, then `Score: 10`, `Score: 20` and `Score: 30`, one a second. Untick
*Register Localization* on `SceneContext` and press Play again: the label shows `ui.score: 0` and so on, and
nothing fails, because the label's `ILocalization` is optional.

## What to look at

### `SceneContext.cs`

The scene's composition root.

- `[DefaultExecutionOrder(-1000)]` makes its `Awake` run before the other components of the scene.
- `Awake` builds the context with `Build()`. Nothing here boots asynchronously, so the context is ready before
  any other component starts. (With asynchronous boot steps you would `await BuildAsync()`, and the scene's
  components would start before they are injected.)
- It then injects every component of its scene, inactive ones included, root by root. A component whose
  required member cannot be resolved is reported to the Console and the other roots are still injected.
- `Spawn` injects what it instantiates. Nothing else injects an object created after `Awake`.
- `OnDestroy` ends the context. Unity calls it when the scene unloads and when play mode is exited, so the
  context never outlives its scene, with or without a domain reload.

### `ScoreLabel.cs`

`[Inject] private IScoreService _score;` is required: if no `IScoreService` were registered, `Inject` would
throw a `ContextException` naming `ScoreLabel._score`. `[Inject(Optional = true)] private ILocalization
_localization;` is optional: without a registration it stays `null` and the label shows the key.

It uses its injected members from `Start`, not `Awake`: `SceneContext` injects in its own `Awake`, and that is
the guarantee to build on.

### `ContextGameObjectExtensions.cs`

`InjectGameObject` calls `Context.Inject` on every `MonoBehaviour` of a GameObject and its children. A
component without `[Inject]` members is left alone. The container reads each type's `[Inject]` members by
reflection once and caches them.

## Rules this sample follows

- **Never `Add<SomeMonoBehaviour>()`.** Only Unity can create a component or a `ScriptableObject`, and the
  build refuses to construct one. To make a scene object available to services, register it as it is:
  `builder.Services.AddInstance(hud)`. To have the context create one, register a factory that does it the
  Unity way: `Add<Hud>(c => Object.Instantiate(hudPrefab))`.
- **`Inject` does not take ownership.** The context neither remembers nor disposes an injected component;
  Unity destroys it.
- **Inject again and it assigns again.** Injection is not tracked, so call it once per object.

`Tests/` holds EditMode checks of the injection. They compile only in a project with the Unity Test
Framework; delete the folder if you do not want them in your Test Runner.
