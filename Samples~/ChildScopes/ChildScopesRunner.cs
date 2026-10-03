using UnityEngine;

namespace OpenUGD.Samples.ChildScopes
{
    /// <summary>Runs <see cref="ChildScopesSample"/> once and writes its output to the Console.</summary>
    public sealed class ChildScopesRunner : MonoBehaviour
    {
        private void Start() => ChildScopesSample.Run(line => Debug.Log(line, this));
    }
}
