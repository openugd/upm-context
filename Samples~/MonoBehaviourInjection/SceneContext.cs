using System;
using UnityEngine;

namespace OpenUGD.Samples.MonoBehaviourInjection
{
    /// <summary>
    /// Builds a context for its scene in <c>Awake</c>, injects every component of the scene, and ends the context in
    /// <c>OnDestroy</c>. Runs before the scene's other components, so they are injected before their <c>Start</c>.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class SceneContext : MonoBehaviour
    {
        [Tooltip("Register an ILocalization. Without one, ScoreLabel shows the raw key and nothing fails.")]
        [SerializeField] private bool _registerLocalization = true;

        private Lifetime.Definition _scope;

        /// <summary>The scene's context, or <c>null</c> before <c>Awake</c> and after a failed build.</summary>
        public Context Context { get; private set; }

        private void Awake()
        {
            // Ends in OnDestroy, which Unity calls when the scene unloads and when play mode is exited.
            _scope = Lifetime.Eternal.DefineNested(name);

            var builder = Context.CreateBuilder(_scope);
            builder.Services.Add<ScoreService>().As<IScoreService>();
            if (_registerLocalization) builder.Services.Add<SampleLocalization>().As<ILocalization>();

            // Never builder.Services.Add<ScoreLabel>(): only Unity can create a MonoBehaviour, and the build refuses
            // to. A scene object that a service needs is registered as it is, with AddInstance(component).

            try
            {
                // Build, not BuildAsync: no service here boots asynchronously, so the context is ready now, before
                // any other component of the scene has started.
                Context = builder.Build();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                return;
            }

            InjectScene();
        }

        /// <summary>
        /// Instantiates <paramref name="prefab"/> and injects it. Objects created after <c>Awake</c> are not injected
        /// by anything else.
        /// </summary>
        public GameObject Spawn(GameObject prefab, Transform parent = null)
        {
            if (Context == null)
            {
                throw new InvalidOperationException(
                    "The SceneContext has no context: it has not awoken yet, or its build failed.");
            }

            var instance = Instantiate(prefab, parent);
            Context.InjectGameObject(instance);
            return instance;
        }

        private void InjectScene()
        {
            foreach (var root in gameObject.scene.GetRootGameObjects())
            {
                try
                {
                    Context.InjectGameObject(root);
                }
                catch (ContextException exception)
                {
                    // One misconfigured object is reported; the scene's other roots are still injected.
                    Debug.LogException(exception, root);
                }
            }
        }

        private void OnDestroy() => _scope?.Terminate();
    }
}
