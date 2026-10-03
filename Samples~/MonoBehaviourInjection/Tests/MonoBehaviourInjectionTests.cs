using NUnit.Framework;
using UnityEngine;

namespace OpenUGD.Samples.MonoBehaviourInjection.Tests
{
    /// <summary>
    /// EditMode checks of injecting scene components. Unity does not call Awake or Start on these components in Edit
    /// Mode, so the tests build the context themselves and inject with <see cref="ContextGameObjectExtensions"/>.
    /// </summary>
    [TestFixture]
    [Category("RequiresUnity")]
    public class MonoBehaviourInjectionTests
    {
        private Lifetime.Definition _scope;
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _scope = Lifetime.Eternal.DefineNested("test");
            _root = new GameObject("root");
        }

        [TearDown]
        public void TearDown()
        {
            _scope.Terminate();
            Object.DestroyImmediate(_root);
        }

        private Context Build(bool localization)
        {
            var builder = Context.CreateBuilder(_scope);
            builder.Services.Add<ScoreService>().As<IScoreService>();
            if (localization) builder.Services.Add<SampleLocalization>().As<ILocalization>();
            return builder.Build();
        }

        [Test]
        public void WithLocalizationRegistered_TheLabelIsTranslated()
        {
            var label = _root.AddComponent<ScoreLabel>();
            var context = Build(localization: true);

            context.InjectGameObject(_root);
            context.Resolve<IScoreService>().Add(10);

            Assert.AreEqual("Score: 10", label.Render());
        }

        [Test]
        public void WithoutLocalization_InjectStillSucceedsAndTheLabelShowsTheKey()
        {
            var label = _root.AddComponent<ScoreLabel>();
            var context = Build(localization: false);

            context.InjectGameObject(_root);

            Assert.AreEqual(ScoreLabel.Key + ": 0", label.Render());
        }

        [Test]
        public void InactiveChildrenAreInjectedToo()
        {
            var child = new GameObject("inactive child");
            child.transform.SetParent(_root.transform);
            child.SetActive(false);
            var label = child.AddComponent<ScoreLabel>();
            var context = Build(localization: true);

            context.InjectGameObject(_root);

            Assert.AreEqual("Score: 0", label.Render());
        }

        [Test]
        public void AMissingRequiredServiceFailsTheInjectionAndNamesTheMember()
        {
            _root.AddComponent<ScoreLabel>();
            var context = Context.CreateBuilder(_scope).Build();

            var error = Assert.Throws<ContextException>(() => context.InjectGameObject(_root));

            StringAssert.Contains("_score", error.Message);
            StringAssert.Contains(typeof(IScoreService).Name, error.Message);
        }

        [Test]
        public void TheBuildRefusesToConstructAMonoBehaviour()
        {
            var builder = Context.CreateBuilder(_scope);
            builder.Services.Add<ScoreLabel>();

            var error = Assert.Throws<ContextException>(() => builder.Build());

            StringAssert.Contains("AddInstance", error.Message);
        }
    }
}
