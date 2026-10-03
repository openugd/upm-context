# Child Scopes

There is no `Scoped` service lifetime in this package. A narrower scope is a **child context**: it resolves
everything its parent can, shadows whatever it registers again, and its own singletons are disposed when its
own scope ends, while the parent's carry on. A child also ends with its parent, whatever scope it was given,
so it never hands out a parent's service after that service has been disposed.

This sample builds one root context for the game, a child per level, and a child of a level per window.

## How to run it

Add `ChildScopesRunner` to any GameObject and press Play; the output goes to the Console. The sample itself
is plain C#: `ChildScopesSample.Run(Console.WriteLine)` runs it outside Unity too.

## What to look at

`ChildScopesSample.cs` holds the three builders and the script; `LevelServices.cs` holds the services.

| Context | Registers | Built on |
| --- | --- | --- |
| game | `SampleLog`, `Profile`, `MenuMusic` as `IMusic` | the app's scope |
| level | `LevelInfo`, `BattleMusic` as `IMusic`, `LevelSession` | a scope nested in the game's |
| window | `PauseWindow` | a scope the UI owns, with the level as its parent |

- **A child sees its parent's services.** `LevelSession` takes `Profile` and `SampleLog`, which only the game
  registers. Every level gets the same `Profile` instance: it is built and owned by the game.
- **A child shadows what it registers again.** Inside a level, `IMusic` is the level's `BattleMusic`; the game
  still resolves its `MenuMusic`.
- **Ending a child ends only the child.** `forestScope.Terminate()` disposes the level's `LevelSession` and
  `BattleMusic`, newest first. The game, and its `Profile`, carry on.
- **A child ends with its parent.** The window is built on `ui`, a scope that is not nested in the level's.
  Ending the level still closes the window first, before the level's own services are disposed, and leaves
  `ui` alone.
- **Ending the root ends everything under it.** At the end the app scope ends: the open Swamp level is
  disposed, then the game's `Profile`.

All three builders call `Build()` rather than `await BuildAsync()`, because no service here has an
asynchronous boot step. See the *Basic Boot* sample for the asynchronous boot.

## Expected output

```
game: built, music: menu theme
LevelSession(Forest): started for Ada, music: battle theme
LevelSession(Forest): finished with 120
LevelSession(Forest): ended
BattleMusic(Forest): stopped
game: alive = True, music: menu theme, best score 120
LevelSession(Cave): started for Ada, music: battle theme
PauseWindow: opened over Cave
PauseWindow: closed
LevelSession(Cave): ended
BattleMusic(Cave): stopped
window: alive = False, ui scope alive = True
LevelSession(Swamp): started for Ada, music: battle theme
-- quitting --
LevelSession(Swamp): ended
BattleMusic(Swamp): stopped
Profile: saved, best score 120
```

`Tests/` checks this output in EditMode. It compiles only in a project with the Unity Test Framework; delete
the folder if you do not want it in your Test Runner.

## In a game

- **A level** is a child built when the level is loaded, on a scope that ends when it is unloaded — for
  example a scope a level-scene component terminates in its `OnDestroy`.
- **A window** is a child built when the window opens, on a scope that ends when it closes. Give it the
  context of whatever it is shown over as its parent, so it closes with that too.
- **The root** must end with the play session, or with domain reload disabled it survives into the next one.
  See "Play mode and domain reload" in the package README.
