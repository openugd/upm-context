using System;
using UnityEngine;

namespace OpenUGD.Samples.BasicBoot
{
    /// <summary>
    /// The entry point: builds the game's context when the scene starts and ends it when this object is destroyed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameBoot : MonoBehaviour
    {
        [Tooltip("Create one with Assets > Create > OpenUGD Samples > Basic Boot > Game Settings. Defaults are used without one.")]
        [SerializeField] private GameSettings _settings;

        private Lifetime.Definition _scope;

        /// <summary>The built context, or <c>null</c> until the boot has finished.</summary>
        public Context Context { get; private set; }

        private void Awake()
        {
            // The context's scope. OnDestroy ends it, and Unity calls OnDestroy when the scene unloads and when play
            // mode is exited, so nothing built here survives into the next play session, with or without a domain
            // reload. With com.openugd.corelib, nest it in PlaySession.Lifetime instead of Lifetime.Eternal, or
            // derive from ContextBehaviour, which does both.
            _scope = Lifetime.Eternal.DefineNested(name);
        }

        private async void Start()
        {
            var builder = Context.CreateBuilder(_scope);
            GameInstaller.Register(builder.Services, _settings != null ? _settings : DefaultSettings(builder.Lifetime));

            Debug.Log("GameBoot: building", this);
            try
            {
                Context = await builder.BuildAsync();
            }
            catch (OperationCanceledException) when (_scope.IsTerminated)
            {
                // Destroyed during the boot: the build was abandoned and what it constructed is disposed.
                return;
            }
            catch (Exception exception)
            {
                // A failed build has already disposed everything it constructed. Report it: an async void method
                // has no caller to rethrow to.
                Debug.LogException(exception, this);
                return;
            }

            Context.Lifetime.AddAction(() => Debug.Log("GameBoot: context ended"));
            Debug.Log("GameBoot: ready", this);

            Context.Resolve<Wallet>().Earn(25);
        }

        private void OnDestroy() => _scope?.Terminate();

        private static GameSettings DefaultSettings(Lifetime lifetime)
        {
            Debug.LogWarning("GameBoot: no GameSettings assigned, so defaults are used.");

            // Created here, so destroyed here: when the context's scope ends.
            var settings = ScriptableObject.CreateInstance<GameSettings>();
            lifetime.AddAction(() => Destroy(settings));
            return settings;
        }
    }
}
