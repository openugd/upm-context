using UnityEngine;

namespace OpenUGD.Samples.MonoBehaviourInjection
{
    public static class ContextGameObjectExtensions
    {
        /// <summary>
        /// Calls <see cref="Context.Inject"/> on every <see cref="MonoBehaviour"/> of <paramref name="gameObject"/> and
        /// its children, inactive ones included. A component without <c>[Inject]</c> members is left as it is.
        /// </summary>
        /// <exception cref="ContextException">A component has a required <c>[Inject]</c> member whose contract is
        /// not registered. Components before it in the hierarchy have been injected; the rest have not.</exception>
        public static void InjectGameObject(this Context context, GameObject gameObject)
        {
            foreach (var behaviour in gameObject.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
            {
                if (behaviour != null) context.Inject(behaviour);
            }
        }
    }
}
