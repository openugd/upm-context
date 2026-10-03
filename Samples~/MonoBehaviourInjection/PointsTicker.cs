using UnityEngine;

namespace OpenUGD.Samples.MonoBehaviourInjection
{
    /// <summary>Adds points to the injected <see cref="IScoreService"/> at a fixed interval, a few times.</summary>
    public sealed class PointsTicker : MonoBehaviour
    {
        [Inject] private IScoreService _score;

        [SerializeField, Min(0.1f)] private float _intervalSeconds = 1f;
        [SerializeField] private int _points = 10;
        [SerializeField, Min(0)] private int _times = 3;

        private float _untilNext;

        private void Start() => _untilNext = _intervalSeconds;

        private void Update()
        {
            if (_score == null || _times <= 0) return;

            _untilNext -= Time.deltaTime;
            if (_untilNext > 0f) return;

            _untilNext = _intervalSeconds;
            _times--;
            _score.Add(_points);
        }
    }
}
