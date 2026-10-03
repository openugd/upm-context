using System.Globalization;

namespace OpenUGD.Samples.Collections
{
    // Three modules that know nothing of each other, or of TickLoop and DebugMenu. Each one contributes to the
    // lists with AsElementOf; none of them claims ITickable, IDebugPanel or ICheat as a contract.

    // ----- Audio -----

    public sealed class AudioMixer : ITickable
    {
        public float SecondsPlayed { get; private set; }

        public void Tick(float deltaTime) => SecondsPlayed += deltaTime;
    }

    public sealed class AudioPanel : IDebugPanel
    {
        private readonly AudioMixer _mixer;

        public AudioPanel(AudioMixer mixer) => _mixer = mixer;

        public string Title => "Audio";

        public string Describe() => _mixer.SecondsPlayed.ToString("0.00", CultureInfo.InvariantCulture) + " s played";
    }

    public static class AudioModule
    {
        public static void Install(ServiceCollection services)
        {
            services.Add<AudioMixer>().AsElementOf<ITickable>();
            services.Add<AudioPanel>().AsElementOf<IDebugPanel>();
        }
    }

    // ----- Network -----

    public interface IConnection
    {
        int PacketsSent { get; }
    }

    /// <summary>One object in two lists and behind a contract: constructed once, shared by all three.</summary>
    public sealed class Connection : IConnection, ITickable, IDebugPanel
    {
        public int PacketsSent { get; private set; }

        public void Tick(float deltaTime) => PacketsSent++;

        public string Title => "Network";

        public string Describe() => PacketsSent + " packets sent";
    }

    public static class NetworkModule
    {
        public static void Install(ServiceCollection services)
        {
            services.Add<Connection>()
                .As<IConnection>()
                .AsElementOf<ITickable>()
                .AsElementOf<IDebugPanel>();
        }
    }

    // ----- Cheats: development builds only -----

    public sealed class GodMode : ICheat
    {
        public string Name => "god mode";
    }

    public sealed class AllLevels : ICheat
    {
        public string Name => "unlock all levels";
    }

    public static class CheatsModule
    {
        public static void Install(ServiceCollection services)
        {
            services.Add<GodMode>().AsElementOf<ICheat>();
            services.Add<AllLevels>().AsElementOf<ICheat>();
        }
    }

    // ----- A level: a child context with a tickable of its own -----

    public sealed class LevelTimer : ITickable
    {
        public float Elapsed { get; private set; }

        public void Tick(float deltaTime) => Elapsed += deltaTime;
    }
}
