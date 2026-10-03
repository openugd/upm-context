namespace OpenUGD.Samples.BasicBoot
{
    /// <summary>
    /// The registrations, kept out of the MonoBehaviour so that a test can build the same graph. Registration order
    /// does not decide what is constructed first; dependencies do.
    /// </summary>
    public static class GameInstaller
    {
        public static void Register(ServiceCollection services, GameSettings settings)
        {
            // A setting is an object: resolvable as GameSettings, never disposed or destroyed by the context.
            services.AddInstance(settings);

            services
                .Add<MemorySaveStore>().As<ISaveStore>()   // also resolvable as itself
                .Add<Wallet>()
                .Add<Autosave>();
        }
    }
}
