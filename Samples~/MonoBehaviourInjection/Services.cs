using System;
using System.Collections.Generic;

namespace OpenUGD.Samples.MonoBehaviourInjection
{
    public interface IScoreService
    {
        int Score { get; }

        /// <summary>Raised with the new score after every change.</summary>
        event Action<int> Changed;

        void Add(int points);
    }

    /// <summary>A plain service: constructed and owned by the context, injected into scene components.</summary>
    public sealed class ScoreService : IScoreService
    {
        public int Score { get; private set; }

        public event Action<int> Changed;

        public void Add(int points)
        {
            Score += points;
            Changed?.Invoke(Score);
        }
    }

    /// <summary>
    /// An optional collaborator: <see cref="ScoreLabel"/> shows translated text when one is registered and the raw key
    /// when none is.
    /// </summary>
    public interface ILocalization
    {
        string Get(string key);
    }

    public sealed class SampleLocalization : ILocalization
    {
        private readonly Dictionary<string, string> _texts = new Dictionary<string, string> {
            { "ui.score", "Score" },
        };

        public string Get(string key) => _texts.TryGetValue(key, out var text) ? text : key;
    }
}
