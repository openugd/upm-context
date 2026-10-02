using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// The contract with Unity's linker, checked by reflection so that level 1 catches a regression without
    /// running the linker. What these tests pin down is what the linker gate proved keeps a stripped player
    /// working: `[Inject]` is a Preserve attribute, and every entry point that hands a user type to the
    /// activator carries `[DynamicallyAccessedMembers]` for its constructors. The linker matches both by
    /// name, so these tests do too.
    /// </summary>
    [TestFixture]
    public class StrippingAnnotationTests
    {
        private const string Dam = "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute";

        // PublicParameterlessConstructor | PublicConstructors | NonPublicConstructors, the .NET values.
        private const int Constructors = 0x0001 | 0x0002 | 0x0004;

        [Test]
        public void InjectIsAPreserveAttributeToTheLinker()
        {
            var preserve = Bases(typeof(InjectAttribute)).FirstOrDefault(t => t.Name == "PreserveAttribute");

            Assert.IsNotNull(preserve,
                "UnityLinker keeps a member, and the attribute on it, only for an attribute named " +
                "PreserveAttribute or derived from one.");
            Assert.IsFalse(preserve.IsSealed, "InjectAttribute must be able to derive from it.");
        }

        [Test]
        public void ThePreserveBaseStaysOutOfTheWayOfUnitysOwn()
        {
            var preserve = typeof(InjectAttribute).BaseType;

            Assert.AreEqual("OpenUGD.Internal", preserve.Namespace,
                "In OpenUGD it would make [Preserve] ambiguous (CS0104) in every file that also imports " +
                "UnityEngine.Scripting.");
            Assert.IsEmpty(preserve.GetConstructors(BindingFlags.Public | BindingFlags.Instance),
                "It is a base class only: with no public constructor it cannot be applied as [Preserve].");
        }

        // Owner, method name, generic arity, parameter types. The type parameter checked is the last one.
        private static readonly object[] GenericEntryPoints =
        {
            new object[] { typeof(ServiceCollectionExtensions), "Add", 1,
                new[] { typeof(ServiceCollection), typeof(string), typeof(int) } },
            new object[] { typeof(ServiceCollectionExtensions), "TryAdd", 2,
                new[] { typeof(ServiceCollection), typeof(string), typeof(int) } },
            new object[] { typeof(RegistrationExtensions), "Add", 1,
                new[] { typeof(Registration), typeof(string), typeof(int) } },
            new object[] { typeof(ContextExtensions), "Instantiate", 1, new[] { typeof(Context) } },
            new object[] { typeof(ContextExtensions), "Instantiate", 1,
                new[] { typeof(Context), typeof(object[]) } },
        };

        [TestCaseSource(nameof(GenericEntryPoints))]
        public void EveryGenericEntryPointKeepsTheConstructorsOfItsTypeArgument(Type owner, string name,
            int arity, Type[] parameters)
        {
            var method = Method(owner, name, arity, parameters);
            var implementation = method.GetGenericArguments()[arity - 1];

            AssertKeepsConstructors(implementation.GetCustomAttributesData(), method + ", " + implementation);
        }

        [Test]
        public void ServiceCollectionAddKeepsTheConstructorsOfTheTypeItIsGiven()
        {
            var method = typeof(ServiceCollection).GetMethod(nameof(ServiceCollection.Add));

            AssertKeepsConstructors(method.GetParameters()[0].GetCustomAttributesData(), method.ToString());
        }

        [Test]
        public void ContextInstantiateKeepsTheConstructorsOfTheTypeItIsGiven()
        {
            var method = typeof(Context).GetMethod(nameof(Context.Instantiate));

            AssertKeepsConstructors(method.GetParameters()[0].GetCustomAttributesData(), method.ToString());
        }

        [Test]
        public void TheFactoryOverloadKeepsNothing()
        {
            // A factory calls the constructor in user code, where the linker sees it. Annotating this overload
            // would keep constructors no one calls, and forwarding its type to the annotated Add(Type) would
            // make the linker warn (IL2087), which is why it takes the internal, unannotated path.
            var method = typeof(ServiceCollectionExtensions).GetMethods()
                .Single(m => m.Name == "Add" && m.GetParameters().Length == 4);

            Assert.IsFalse(method.GetGenericArguments()[0].GetCustomAttributesData()
                .Any(a => a.AttributeType.FullName == Dam));
        }

        private static void AssertKeepsConstructors(System.Collections.Generic.IList<CustomAttributeData> data,
            string where)
        {
            var dam = data.SingleOrDefault(a => a.AttributeType.FullName == Dam);

            Assert.IsNotNull(dam, where + " has no [DynamicallyAccessedMembers].");
            var value = Convert.ToInt32(dam.ConstructorArguments[0].Value);
            Assert.AreEqual(Constructors, value & Constructors,
                where + " must keep public and non-public constructors; the container enumerates both.");
        }

        private static MethodInfo Method(Type owner, string name, int arity, Type[] parameters)
        {
            return owner.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m =>
                m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == arity &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters));
        }

        private static System.Collections.Generic.IEnumerable<Type> Bases(Type type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType) yield return t;
        }
    }
}
