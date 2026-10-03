using System;

namespace OpenUGD.Samples.ChildScopes
{
    /// <summary>
    /// A root context for the game, a child context per level, and a child context per window, each ending with
    /// its parent. Plain C#: <see cref="ChildScopesRunner"/> runs it in Unity, and <c>Run(Console.WriteLine)</c>
    /// runs it anywhere else.
    /// </summary>
    public static class ChildScopesSample
    {
        public static void Run(Action<string> write)
        {
            var log = new SampleLog(write);

            // In a Unity project the root lives on a scope that ends with the play session: an OnDestroy of the
            // entry point, or corelib's PlaySession.Lifetime. See the package README.
            using (var app = Lifetime.Eternal.DefineNested("app"))
            {
                var game = BuildGame(app, log);
                log.Write("game: built, music: " + game.Resolve<IMusic>().Track);

                // 1. A level is a child context on a scope nested in the game's. Ending it disposes the level's
                //    own services and nothing of the game's.
                var forestScope = game.Lifetime.DefineNested("Forest");
                var forest = BuildLevel(forestScope, game, "Forest");
                forest.Resolve<LevelSession>().Finish(120);
                forestScope.Terminate();
                log.Write("game: alive = " + game.Lifetime.IsAlive() + ", music: " + game.Resolve<IMusic>().Track +
                          ", best score " + game.Resolve<Profile>().BestScore);

                // 2. A window is a child of the level, on a scope that the UI owns rather than one nested in the
                //    level's. It still ends with the level, before the level's own services are disposed, so it
                //    never holds a LevelSession that has ended.
                var caveScope = game.Lifetime.DefineNested("Cave");
                var cave = BuildLevel(caveScope, game, "Cave");
                var ui = app.Lifetime.DefineNested("ui");
                var pause = BuildWindow(ui, cave);
                caveScope.Terminate();
                log.Write("window: alive = " + pause.Lifetime.IsAlive() + ", ui scope alive = " + ui.Lifetime.IsAlive());

                // 3. Ending the game ends every level still open, before the game's own services are disposed.
                var swampScope = game.Lifetime.DefineNested("Swamp");
                BuildLevel(swampScope, game, "Swamp");
                log.Write("-- quitting --");
            }
        }

        /// <summary>The root: services that live as long as the game.</summary>
        public static Context BuildGame(Lifetime lifetime, SampleLog log)
        {
            var builder = Context.CreateBuilder(lifetime);
            builder.Services.AddInstance(log);
            builder.Services
                .Add<Profile>()
                .Add<MenuMusic>().As<IMusic>();

            // Build, not BuildAsync: nothing in this sample boots asynchronously, so the build finishes on this
            // thread. With an asynchronous boot step, await BuildAsync instead.
            return builder.Build();
        }

        /// <summary>
        /// A child of the game for one level. It sees the game's services, <see cref="SampleLog"/> included, and its
        /// own <see cref="IMusic"/> shadows the game's.
        /// </summary>
        public static Context BuildLevel(Lifetime lifetime, Context game, string name)
        {
            var builder = Context.CreateBuilder(lifetime, parent: game);
            builder.Services.AddInstance(new LevelInfo(name));
            builder.Services
                .Add<BattleMusic>().As<IMusic>()
                .Add<LevelSession>();
            return builder.Build();
        }

        /// <summary>A child of a level for one window.</summary>
        public static Context BuildWindow(Lifetime lifetime, Context level)
        {
            var builder = Context.CreateBuilder(lifetime, parent: level);
            builder.Services.Add<PauseWindow>();
            return builder.Build();
        }
    }
}
