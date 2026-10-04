using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// The README shows output the package really produces: the message is captured here, and
    /// the README has to show it verbatim.
    /// </summary>
    [TestFixture]
    public class ReadmeExampleTests : ContextFixture
    {
        /// The site the README shows in place of this file's path, which differs on every machine.
        private const string ShownSite = "Assets/Scripts/GameBoot.cs:14";

        private const string MissingBinding =
            "The Context could not be built. 1 problem was found while validating the service graph, before anything was constructed:\n" +
            "  - Unable to resolve service for type 'MyGame.IStorage' while attempting to activate 'MyGame.SaveService'.\n" +
            "      required by the constructor parameter 'storage'.\n" +
            "      registered at Assets/Scripts/GameBoot.cs:14\n" +
            "      'MyGame.FileStorage' is registered and does implement 'MyGame.IStorage', but was not registered as it. Add .As<IStorage>() to its registration.";

        private static string ThisFile([CallerFilePath] string file = null) => file;

        private static int Line([CallerLineNumber] int line = 0) => line;

        [Test]
        public void TheMissingBindingExampleIsWhatTheBuildReports()
        {
            var builder = NewBuilder();
            builder.Services.Add<MyGame.FileStorage>();
            builder.Services.Add<MyGame.SaveService>(); var site = ThisFile() + ":" + Line();

            var error = FailToBuild<ContextException>(builder);

            Assert.AreEqual(MissingBinding, error.Message.Replace(site, ShownSite));
        }

        [Test]
        public void TheReadmeShowsThatMessageVerbatim()
        {
            var readme = Path.Combine(Path.GetDirectoryName(ThisFile()) ?? ".", "..", "..", "README.md");
            if (!File.Exists(readme)) Assert.Ignore("README.md is not next to the test sources here: " + readme);

            StringAssert.Contains(MissingBinding, File.ReadAllText(readme).Replace("\r\n", "\n"),
                "Update the README's \"When it goes wrong\" block to the message this test captures.");
        }
    }
}

namespace MyGame
{
    public interface IStorage { }

    public sealed class FileStorage : IStorage { }

    public sealed class SaveService
    {
        public SaveService(IStorage storage) { }
    }
}
