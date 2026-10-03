using System;
using UnityEngine;

namespace OpenUGD.Samples.Collections
{
    /// <summary>
    /// Builds the sample's composition, ticks <see cref="TickLoop"/> every frame and writes the debug menu to the
    /// Console every few seconds. Press Play with it on any GameObject.
    /// </summary>
    public sealed class CollectionsRunner : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float _reportEverySeconds = 2f;

        private Lifetime.Definition _scope;
        private TickLoop _loop;
        private DebugMenu _menu;
        private float _untilReport;

        private void Awake()
        {
            // Ends in OnDestroy, which Unity also calls when play mode is exited.
            _scope = Lifetime.Eternal.DefineNested(name);
            try
            {
                var builder = Context.CreateBuilder(_scope);

                // Debug.isDebugBuild is true in the Editor and in a Development Build, so a release player gets
                // no cheats and an empty IReadOnlyList<ICheat>.
                CollectionsSample.Compose(builder.Services, developmentBuild: Debug.isDebugBuild);

                // Nothing here boots asynchronously, so Build finishes in Awake.
                var context = builder.Build();
                _loop = context.Resolve<TickLoop>();
                _menu = context.Resolve<DebugMenu>();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                enabled = false;
            }
        }

        private void Update()
        {
            _loop.Tick(Time.deltaTime);

            _untilReport -= Time.deltaTime;
            if (_untilReport > 0f) return;

            _untilReport = _reportEverySeconds;
            Debug.Log(_menu.Render(), this);
        }

        private void OnDestroy() => _scope?.Terminate();
    }
}
