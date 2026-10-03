using UnityEngine;

namespace OpenUGD.Samples.MonoBehaviourInjection
{
    /// <summary>
    /// A scene component with injected members. Unity creates it, so the context cannot construct it; the context
    /// fills its <c>[Inject]</c> members instead, through <see cref="Context.Inject"/>.
    /// </summary>
    public sealed class ScoreLabel : MonoBehaviour
    {
        public const string Key = "ui.score";

        // Required: Context.Inject throws when no IScoreService is registered.
        [Inject] private IScoreService _score;

        // Optional: left as it is (null here) when no ILocalization is registered; Inject does not fail.
        [Inject(Optional = true)] private ILocalization _localization;

        public string Render() => (_localization == null ? Key : _localization.Get(Key)) + ": " + _score.Score;

        // Injected members are used from Start on: SceneContext injects the scene's components in its Awake, which
        // runs first. Awake of this component may run before that.
        private void Start()
        {
            if (_score == null)
            {
                Debug.LogError("ScoreLabel was not injected. Put a SceneContext in the scene, or inject it with " +
                               "Context.Inject after creating it.", this);
                enabled = false;
                return;
            }

            _score.Changed += OnChanged;
            Debug.Log(Render(), this);
        }

        private void OnDestroy()
        {
            if (_score != null) _score.Changed -= OnChanged;
        }

        private void OnChanged(int score) => Debug.Log(Render(), this);
    }
}
