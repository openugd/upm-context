using UnityEngine;

namespace OpenUGD.Samples.BasicBoot
{
    /// <summary>
    /// The game's settings: an asset edited in the Inspector, handed to the context with <c>AddInstance</c> and taken
    /// by services as an ordinary constructor parameter. The context never disposes or destroys it.
    /// </summary>
    [CreateAssetMenu(menuName = "OpenUGD Samples/Basic Boot/Game Settings", fileName = "GameSettings")]
    public sealed class GameSettings : ScriptableObject
    {
        public string PlayerName = "Player";

        [Min(0)] public int StartingCoins = 100;

        [Min(1f)] public float AutosaveSeconds = 30f;
    }
}
