using System;
using System.Collections.Generic;

namespace OpenUGD.Samples.Collections
{
    /// <summary>
    /// Several modules contributing to one list. Plain C#: <see cref="CollectionsRunner"/> runs it in Unity, and
    /// <c>Run(Console.WriteLine)</c> runs it anywhere else.
    /// </summary>
    public static class CollectionsSample
    {
        /// <summary>
        /// The composition root: install the modules, then register what consumes their lists. The order of the
        /// modules is the order of each list.
        /// </summary>
        public static void Compose(ServiceCollection services, bool developmentBuild)
        {
            AudioModule.Install(services);
            NetworkModule.Install(services);
            if (developmentBuild) CheatsModule.Install(services);

            services
                .Add<TickLoop>()     // takes IReadOnlyList<ITickable>
                .Add<DebugMenu>();   // takes IReadOnlyList<IDebugPanel> and IReadOnlyList<ICheat>
        }

        public static void Run(Action<string> write)
        {
            using (var app = Lifetime.Eternal.DefineNested("collections"))
            {
                // 1. The same composition as a release build and as a development build. Without the cheats
                //    module, IReadOnlyList<ICheat> is empty, not missing, and DebugMenu builds either way.
                Context game = null;
                foreach (var developmentBuild in new[] { false, true })
                {
                    var builder = Context.CreateBuilder(app);
                    Compose(builder.Services, developmentBuild);
                    game = builder.Build();

                    var loop = game.Resolve<TickLoop>();
                    for (var frame = 0; frame < 3; frame++) loop.Tick(0.5f);

                    write((developmentBuild ? "development" : "release") + " build: " + loop.Count + " tickables");
                    write(game.Resolve<DebugMenu>().Render());
                }

                // 2. A list can be resolved directly too. One registration in two lists and behind a contract
                //    is still one object.
                var panels = game.Resolve<IReadOnlyList<IDebugPanel>>();
                write("one Connection behind IConnection and in both lists: " +
                      (ReferenceEquals(game.Resolve<IConnection>(), panels[1]) &&
                       ReferenceEquals(panels[1], game.Resolve<IReadOnlyList<ITickable>>()[1])));

                // 3. A list holds its own context's contributions only. A level that contributes a tickable of
                //    its own runs its own loop over it, and the game's loop keeps ticking the game's.
                var levelScope = game.Lifetime.DefineNested("level");
                var level = Context.CreateBuilder(levelScope, parent: game);
                level.Services.Add<LevelTimer>().AsElementOf<ITickable>();
                level.Services.Add<TickLoop>();
                var levelContext = level.Build();
                write("level loop: " + levelContext.Resolve<TickLoop>().Count + " tickable, game loop: " +
                      game.Resolve<TickLoop>().Count + " tickables");
            }
        }
    }
}
