using System.Collections.Generic;
using NUnit.Framework;

namespace OpenUGD.Samples.ChildScopes.Tests
{
    /// <summary>The sample prints what its README says it prints. Plain C#: no engine call.</summary>
    [TestFixture]
    public class ChildScopesSampleTests
    {
        [Test]
        public void TheSamplePrintsWhatItsReadmeShows()
        {
            var lines = new List<string>();

            ChildScopesSample.Run(lines.Add);

            CollectionAssert.AreEqual(new[] {
                "game: built, music: menu theme",
                "LevelSession(Forest): started for Ada, music: battle theme",
                "LevelSession(Forest): finished with 120",
                "LevelSession(Forest): ended",
                "BattleMusic(Forest): stopped",
                "game: alive = True, music: menu theme, best score 120",
                "LevelSession(Cave): started for Ada, music: battle theme",
                "PauseWindow: opened over Cave",
                "PauseWindow: closed",
                "LevelSession(Cave): ended",
                "BattleMusic(Cave): stopped",
                "window: alive = False, ui scope alive = True",
                "LevelSession(Swamp): started for Ada, music: battle theme",
                "-- quitting --",
                "LevelSession(Swamp): ended",
                "BattleMusic(Swamp): stopped",
                "Profile: saved, best score 120",
            }, lines);
        }

        [Test]
        public void AWindowOnAScopeOfItsOwnStillEndsWithItsLevel()
        {
            var log = new SampleLog(_ => { });
            using (var app = Lifetime.Eternal.DefineNested("app"))
            {
                var game = ChildScopesSample.BuildGame(app, log);
                var levelScope = game.Lifetime.DefineNested("level");
                var level = ChildScopesSample.BuildLevel(levelScope, game, "Level");
                var ui = app.Lifetime.DefineNested("ui");
                var window = ChildScopesSample.BuildWindow(ui, level);

                levelScope.Terminate();

                Assert.IsTrue(window.Lifetime.IsTerminated, "The window ends with the level it was built on.");
                Assert.IsFalse(ui.Lifetime.IsTerminated, "Its own scope is left to whoever owns it.");
                Assert.IsTrue(game.Lifetime.IsAlive(), "Ending a level ends nothing of the game's.");
            }
        }
    }
}
