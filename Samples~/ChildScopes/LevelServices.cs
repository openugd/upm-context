using System;

namespace OpenUGD.Samples.ChildScopes
{
    /// <summary>Writes the sample's output. Registered with <c>AddInstance</c>, so the context never disposes it.</summary>
    public sealed class SampleLog
    {
        private readonly Action<string> _write;

        public SampleLog(Action<string> write) => _write = write ?? throw new ArgumentNullException(nameof(write));

        public void Write(string line) => _write(line);
    }

    public interface IMusic
    {
        string Track { get; }
    }

    // ----- Game scope: one of each per root context -----

    /// <summary>Game-wide state. Every level reads and updates the same instance, which lives in the root.</summary>
    public sealed class Profile : IDisposable
    {
        private readonly SampleLog _log;

        public Profile(SampleLog log) => _log = log;

        public string PlayerName => "Ada";

        public int BestScore { get; private set; }

        public void Record(int score) => BestScore = Math.Max(BestScore, score);

        public void Dispose() => _log.Write("Profile: saved, best score " + BestScore);
    }

    /// <summary>The game's music. A level registers its own <see cref="IMusic"/>, which shadows this one inside the level.</summary>
    public sealed class MenuMusic : IMusic
    {
        public string Track => "menu theme";
    }

    // ----- Level scope: one of each per level context -----

    /// <summary>Which level a level context is for. Handed over with <c>AddInstance</c>.</summary>
    public sealed class LevelInfo
    {
        public LevelInfo(string name) => Name = name;

        public string Name { get; }
    }

    /// <summary>Shadows the game's <see cref="IMusic"/> inside one level, and stops when that level ends.</summary>
    public sealed class BattleMusic : IMusic, IDisposable
    {
        private readonly LevelInfo _level;
        private readonly SampleLog _log;

        public BattleMusic(LevelInfo level, SampleLog log)
        {
            _level = level;
            _log = log;
        }

        public string Track => "battle theme";

        public void Dispose() => _log.Write("BattleMusic(" + _level.Name + "): stopped");
    }

    /// <summary>
    /// One level's session. It takes <see cref="Profile"/> from the game and <see cref="IMusic"/> from its own level:
    /// the child resolves both, and its own registration wins.
    /// </summary>
    public sealed class LevelSession : IDisposable
    {
        private readonly LevelInfo _level;
        private readonly Profile _profile;
        private readonly SampleLog _log;

        public LevelSession(LevelInfo level, Profile profile, IMusic music, SampleLog log)
        {
            _level = level;
            _profile = profile;
            _log = log;
            log.Write("LevelSession(" + level.Name + "): started for " + profile.PlayerName + ", music: " + music.Track);
        }

        public string LevelName => _level.Name;

        public void Finish(int score)
        {
            _profile.Record(score);
            _log.Write("LevelSession(" + _level.Name + "): finished with " + score);
        }

        public void Dispose() => _log.Write("LevelSession(" + _level.Name + "): ended");
    }

    // ----- Window scope: one of each per window context -----

    /// <summary>A window opened over a level. It takes the level's <see cref="LevelSession"/> from its parent context.</summary>
    public sealed class PauseWindow : IDisposable
    {
        private readonly SampleLog _log;

        public PauseWindow(LevelSession session, SampleLog log)
        {
            _log = log;
            log.Write("PauseWindow: opened over " + session.LevelName);
        }

        public void Dispose() => _log.Write("PauseWindow: closed");
    }
}
