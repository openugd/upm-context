using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OpenUGD.Samples.BasicBoot
{
    public interface ISaveStore
    {
        int Coins { get; set; }
    }

    /// <summary>
    /// Loads the save during the Awake phase. Everything in the Initialize phase may assume it is loaded, because
    /// every Awake step finishes before the first Initialize step starts.
    /// </summary>
    public sealed class MemorySaveStore : ISaveStore, IAwakeService
    {
        private readonly GameSettings _settings;
        private int _coins;
        private bool _loaded;

        public MemorySaveStore(GameSettings settings) => _settings = settings;

        public int Coins
        {
            get
            {
                if (!_loaded) throw new InvalidOperationException("The save has not been loaded yet.");
                return _coins;
            }
            set => _coins = value;
        }

        public async Task AwakeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("SaveStore: loading");

            // Stands in for reading a save file or a cloud save. The token is cancelled if the boot is abandoned,
            // for instance because GameBoot was destroyed first.
            await Task.Delay(TimeSpan.FromSeconds(0.25), cancellationToken);

            _coins = _settings.StartingCoins;
            _loaded = true;
            Debug.Log("SaveStore: loaded " + _coins + " coins");
        }
    }

    /// <summary>The player's coins. Reads the loaded save in the Initialize phase.</summary>
    public sealed class Wallet : IInitializeService
    {
        private readonly ISaveStore _store;
        private readonly GameSettings _settings;

        public Wallet(ISaveStore store, GameSettings settings)
        {
            _store = store;
            _settings = settings;
        }

        public int Coins { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Coins = _store.Coins;
            Debug.Log("Wallet: " + _settings.PlayerName + " has " + Coins + " coins");
            return Task.CompletedTask;
        }

        public void Earn(int amount)
        {
            Coins += amount;
            Debug.Log("Wallet: earned " + amount + ", now " + Coins);
        }
    }

    /// <summary>
    /// Saves on a timer while the context lives, and once more when it ends. The timer runs on the boot token, which
    /// the context cancels when it ends, before it disposes anything.
    /// </summary>
    public sealed class Autosave : IInitializeService, IDisposable
    {
        private readonly Wallet _wallet;
        private readonly ISaveStore _store;
        private readonly GameSettings _settings;

        public Autosave(Wallet wallet, ISaveStore store, GameSettings settings)
        {
            _wallet = wallet;
            _store = store;
            _settings = settings;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            // Started here and left running after the boot; returning its task would make the boot wait for it.
            // RunAsync handles every exception itself, so nothing is lost by not awaiting it.
            _ = RunAsync(cancellationToken);
            Debug.Log("Autosave: every " + _settings.AutosaveSeconds + " s");
            return Task.CompletedTask;
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                var interval = TimeSpan.FromSeconds(Math.Max(1f, _settings.AutosaveSeconds));
                while (true)
                {
                    await Task.Delay(interval, cancellationToken);
                    Save("autosave");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The context ended.
            }
            catch (Exception exception)
            {
                // Nothing awaits this task, so report what would otherwise go unobserved.
                Debug.LogException(exception);
            }
        }

        // Disposed in reverse construction order: before Wallet and the store, which it uses here.
        public void Dispose() => Save("final save");

        private void Save(string reason)
        {
            _store.Coins = _wallet.Coins;
            Debug.Log("Autosave: " + reason + ", " + _wallet.Coins + " coins");
        }
    }
}
