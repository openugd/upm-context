using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace OpenUGD.Tests
{
    /// <summary>
    /// The three ways the README gives for objects only Unity can create — <c>Inject</c>, <c>AddInstance</c> and a
    /// factory that creates them the Unity way — with real engine objects.
    /// </summary>
    [TestFixture]
    [Category("RequiresUnity")]
    public class UnityObjectTests : ContextFixture
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void DestroyObjects()
        {
            for (var i = _objects.Count - 1; i >= 0; i--)
            {
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            }

            _objects.Clear();
        }

        private GameObject NewGameObject(string name)
        {
            var gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }

        [Test]
        public void InjectFillsTheMembersOfAComponentUnityCreated()
        {
            var label = NewGameObject("label").AddComponent<Label>();
            var builder = NewBuilder();
            builder.Services.Add<Gauge>().As<IGauge>();
            var context = builder.Build();

            context.Inject(label);

            Assert.AreSame(context.Resolve<IGauge>(), label.Gauge);
            Assert.IsNull(label.Localization, "An optional member nothing is registered for stays as it was.");
        }

        [Test]
        public void AScriptableObjectHandedToAddInstanceIsInjectedAndOutlivesTheContext()
        {
            var settings = ScriptableObject.CreateInstance<Settings>();
            _objects.Add(settings);
            var scope = NewDefinition("settings");
            var builder = Context.CreateBuilder(scope);
            builder.Services.AddInstance(settings);
            builder.Services.Add<Gauge>().As<IGauge>();
            var context = builder.Build();

            Assert.AreSame(settings, context.Resolve<Settings>());
            Assert.IsNotNull(settings.Gauge, "An AddInstance object's [Inject] members are filled during the build.");

            scope.Terminate();

            Assert.IsTrue(settings != null, "The context never destroys an object it was handed.");
        }

        [Test]
        public void AFactoryThatAddsAComponentIsInjectedAndBooted()
        {
            var host = NewGameObject("host");
            var builder = NewBuilder();
            builder.Services.Add<Gauge>().As<IGauge>();
            builder.Services.Add<Beacon>(c => host.AddComponent<Beacon>());
            var context = builder.Build();

            var beacon = context.Resolve<Beacon>();
            Assert.AreSame(host.GetComponent<Beacon>(), beacon);
            Assert.AreSame(context.Resolve<IGauge>(), beacon.Gauge);
            Assert.IsTrue(beacon.Awoken, "A component a factory returns enrols in the boot like any service.");
        }

        public interface IGauge
        {
        }

        public interface ILocalization
        {
        }

        public sealed class Gauge : IGauge
        {
        }

        public sealed class Label : MonoBehaviour
        {
            [Inject] public IGauge Gauge;
            [Inject(Optional = true)] public ILocalization Localization;
        }

        public sealed class Settings : ScriptableObject
        {
            [Inject] public IGauge Gauge;
        }

        public sealed class Beacon : MonoBehaviour, IAwakeService
        {
            [Inject] public IGauge Gauge;

            public bool Awoken { get; private set; }

            public Task AwakeAsync(CancellationToken cancellationToken)
            {
                Awoken = true;
                return Task.CompletedTask;
            }
        }
    }
}
