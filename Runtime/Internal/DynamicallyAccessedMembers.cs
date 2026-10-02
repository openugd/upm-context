// A private copy of the trimming annotation that .NET 5 added to its class libraries. Unity's class libraries
// (netstandard 2.1 and unityaot) do not have it, but Unity's linker is built on the .NET linker and honours
// it, matching the attribute by its full name in any assembly. It goes on every parameter through which a
// user type reaches a constructor the container calls by reflection — Add<TImpl>(), TryAdd, Instantiate<T>,
// ServiceCollection.Add(Type), Context.Instantiate(Type) — so the linker keeps the constructors of exactly
// the types that appear at those call sites.
//
// Internal: other assemblies cannot see it, so it never collides with a copy of their own. Left out when the
// class libraries already have the real one.
#if !NET5_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(
        AttributeTargets.Field | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter |
        AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Method |
        AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct,
        Inherited = false)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes)
        {
            MemberTypes = memberTypes;
        }

        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }

    // The values are the .NET ones: the linker reads the number, not the name.
    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0002 | PublicParameterlessConstructor,
        NonPublicConstructors = 0x0004,
        PublicMethods = 0x0008,
        NonPublicMethods = 0x0010,
        PublicFields = 0x0020,
        NonPublicFields = 0x0040,
        PublicNestedTypes = 0x0080,
        NonPublicNestedTypes = 0x0100,
        PublicProperties = 0x0200,
        NonPublicProperties = 0x0400,
        PublicEvents = 0x0800,
        NonPublicEvents = 0x1000,
        Interfaces = 0x2000,
        All = ~None
    }
}
#endif

namespace OpenUGD
{
    using System.Diagnostics.CodeAnalysis;

    internal static class Trimming
    {
        /// What every entry point that hands a user type to the activator asks the linker to keep: the
        /// constructors, public and not, because the container enumerates both to find an [Inject] one.
        /// Members need no annotation: an [Inject] member is kept by the attribute itself.
        internal const DynamicallyAccessedMemberTypes Constructors =
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors;
    }
}
