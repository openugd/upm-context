using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace OpenUGD
{
    internal readonly struct Member
    {
        private readonly FieldInfo _field;
        private readonly PropertyInfo _property;

        internal readonly Type Contract;
        internal readonly Type DeclaringType;
        internal readonly string Name;
        internal readonly string Kind;

        /// Injected only when the contract is registered; left untouched otherwise.
        internal readonly bool Optional;

        internal Member(FieldInfo field, PropertyInfo property, bool optional = false)
        {
            _field = field;
            _property = property;
            Contract = field != null ? field.FieldType : property.PropertyType;
            DeclaringType = field != null ? field.DeclaringType : property.DeclaringType;
            Name = field != null ? field.Name : property.Name;
            Kind = field != null ? "field" : "property";
            Optional = optional;
        }

        /// Assigns the member, reporting a property setter that throws the way a throwing constructor is
        /// reported: which member of which type, where it was registered, and the original as the inner
        /// exception.
        internal void SetValue(object target, object value, string site)
        {
            if (_field != null)
            {
                _field.SetValue(target, value);
                return;
            }

            try
            {
                _property.SetValue(target, value, null);
            }
            catch (TargetInvocationException exception)
            {
                var original = exception.InnerException ?? exception;
                throw new ContextException(
                    "The setter of the [Inject] property '" + Diagnostics.Display(DeclaringType) + "." + Name +
                    "' threw " + Diagnostics.Describe(original) + Diagnostics.Where(site), original);
            }
        }
    }

    internal static class Activation
    {
        private static readonly ConcurrentDictionary<Type, TypeMetadata> Cache =
            new ConcurrentDictionary<Type, TypeMetadata>();
        private static readonly Func<Type, TypeMetadata> ReadMetadata = Read;
        private static readonly object[] NoArguments = new object[0];
        private static readonly Member[] NoMembers = new Member[0];

        internal sealed class TypeMetadata
        {
            internal ConstructorInfo[] Constructors;
            internal ParameterInfo[][] Parameters;
            internal int MarkedIndex = -1;
            internal int MarkedCount;
            internal int PublicCount;
            internal Member[] Members;
            internal string MemberError;
        }

        /// A hit takes no lock. A miss reads the type outside any lock - reflection is slow and may run code
        /// of the type's own, such as an attribute's constructor - so it never holds up a lookup of another
        /// type; two threads missing the same type at once may both read it, and both get the one that was
        /// stored first. The metadata never changes once read.
        internal static TypeMetadata GetMetadata(Type type)
        {
            TypeMetadata metadata;
            return Cache.TryGetValue(type, out metadata) ? metadata : Cache.GetOrAdd(type, ReadMetadata);
        }

        internal static Member[] GetInjectMembers(Type type, out string error)
        {
            var metadata = GetMetadata(type);
            error = metadata.MemberError;
            return metadata.Members;
        }

        internal static string DescribeIfNotActivatable(Type type)
        {
            if (type.IsInterface) return "it is an interface";
            if (type.IsAbstract) return "it is abstract";
            if (type.IsValueType) return "it is a value type";
            if (type.IsArray) return "it is an array type";
            if (type.IsGenericTypeDefinition) return "it is an open generic type";
            if (type.ContainsGenericParameters) return "it has unbound generic parameters";
            if (IsEngineObject(type))
            {
                return "it derives from UnityEngine.Object - a MonoBehaviour, a ScriptableObject or another " +
                       "engine object - which only Unity can create; one made by calling its constructor has no " +
                       "native object behind it";
            }

            return null;
        }

        /// Whether <paramref name="type"/> is a UnityEngine.Object, told by the name of a base type: this
        /// assembly has no engine reference, so the type itself cannot be named.
        internal static bool IsEngineObject(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.FullName == "UnityEngine.Object") return true;
            }

            return false;
        }

        private static TypeMetadata Read(Type type)
        {
            var metadata = new TypeMetadata();
            List<string> errors = null;

            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                                                    BindingFlags.Instance);
            var parameters = new ParameterInfo[constructors.Length][];
            for (var i = 0; i < constructors.Length; i++) parameters[i] = constructors[i].GetParameters();

            // Widest first, and stable: equally wide constructors keep the order reflection returned them in,
            // so which of them is tried and named first never varies from one run to the next.
            for (var i = 1; i < constructors.Length; i++)
            {
                var constructor = constructors[i];
                var signature = parameters[i];
                var j = i - 1;
                for (; j >= 0 && parameters[j].Length < signature.Length; j--)
                {
                    constructors[j + 1] = constructors[j];
                    parameters[j + 1] = parameters[j];
                }

                constructors[j + 1] = constructor;
                parameters[j + 1] = signature;
            }

            metadata.Constructors = constructors;
            metadata.Parameters = parameters;
            for (var i = 0; i < constructors.Length; i++)
            {
                if (constructors[i].IsPublic) metadata.PublicCount++;

                var marker = GetInject(constructors[i], false);
                if (marker == null) continue;

                if (marker.Optional)
                {
                    // Silently ignoring it would read as "this constructor is optional", which is not a
                    // thing. Optional applies to a member, never to the choice of constructor.
                    (errors ?? (errors = new List<string>())).Add(
                        "The constructor of '" + Diagnostics.Display(type) +
                        "' is marked [Inject(Optional = true)], which has no meaning on a constructor. " +
                        "Optional applies to an injected field or property. To make one dependency " +
                        "optional, declare a second constructor without it: the greediest satisfiable " +
                        "constructor wins, so the wider one is used when the dependency is registered.");
                }

                metadata.MarkedCount++;
                metadata.MarkedIndex = i;
            }

            ReadMembers(type, metadata, ref errors);

            metadata.MemberError = errors == null ? null : string.Join("\n      ", errors.ToArray());
            return metadata;
        }

        private static InjectAttribute GetInject(MemberInfo member, bool inherit) =>
            (InjectAttribute)Attribute.GetCustomAttribute(member, typeof(InjectAttribute), inherit);

        private static void ReadMembers(Type type, TypeMetadata metadata, ref List<string> errors)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;

            List<List<Member>> levels = null;
            HashSet<string> properties = null;

            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                List<Member> level = null;

                var fields = current.GetFields(flags);
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    var attribute = GetInject(field, true);
                    if (attribute == null) continue;

                    if (field.IsInitOnly || field.IsLiteral)
                    {
                        (errors ?? (errors = new List<string>())).Add(
                            "'" + Diagnostics.Display(current) + "." + field.Name +
                            "' is marked [Inject] but is readonly or const, so it can never be assigned.");
                        continue;
                    }

                    var unusable = DescribeIfNotOptionable(attribute, field.FieldType);
                    if (unusable != null)
                    {
                        (errors ?? (errors = new List<string>())).Add(
                            "'" + Diagnostics.Display(current) + "." + field.Name + "' " + unusable);
                        continue;
                    }

                    (level ?? (level = new List<Member>())).Add(new Member(field, null, attribute.Optional));
                }

                var members = current.GetProperties(flags);
                for (var i = 0; i < members.Length; i++)
                {
                    var property = members[i];
                    var attribute = GetInject(property, true);
                    if (attribute == null) continue;

                    // Collected most-derived first, so an override or a `new` declaration wins and the
                    // shadowed one is not injected twice.
                    if (!(properties ?? (properties = new HashSet<string>())).Add(property.Name)) continue;

                    if (property.GetIndexParameters().Length != 0)
                    {
                        (errors ?? (errors = new List<string>())).Add(
                            "'" + Diagnostics.Display(current) + "." + property.Name +
                            "' is marked [Inject] but is an indexer.");
                        continue;
                    }

                    if (property.GetSetMethod(true) == null)
                    {
                        (errors ?? (errors = new List<string>())).Add(
                            "'" + Diagnostics.Display(current) + "." + property.Name +
                            "' is marked [Inject] but has no setter, so it can never be assigned. Add a " +
                            "setter (it may be private), or use a field.");
                        continue;
                    }

                    var unusable = DescribeIfNotOptionable(attribute, property.PropertyType);
                    if (unusable != null)
                    {
                        (errors ?? (errors = new List<string>())).Add(
                            "'" + Diagnostics.Display(current) + "." + property.Name + "' " + unusable);
                        continue;
                    }

                    (level ?? (level = new List<Member>())).Add(new Member(null, property, attribute.Optional));
                }

                if (level != null) (levels ?? (levels = new List<List<Member>>())).Add(level);
            }

            List<Member> ordered = null;
            if (levels != null)
            {
                // Reversed, so a base class's members are assigned before a derived class's - the same
                // order the constructors ran in.
                for (var i = levels.Count - 1; i >= 0; i--)
                {
                    (ordered ?? (ordered = new List<Member>())).AddRange(levels[i]);
                }
            }

            metadata.Members = ordered == null ? NoMembers : ordered.ToArray();
        }

        /// An unsatisfied optional member is left at whatever it held, which for a non-nullable value type is
        /// `0` or `false` - indistinguishable from an injected value, and therefore a silent bug rather than
        /// a defined fallback.
        private static string DescribeIfNotOptionable(InjectAttribute attribute, Type contract)
        {
            if (!attribute.Optional) return null;
            if (!contract.IsValueType || Nullable.GetUnderlyingType(contract) != null) return null;

            return "is marked [Inject(Optional = true)] but its type '" + Diagnostics.Display(contract) +
                   "' is a non-nullable value type, so an unsatisfied member would be left at its " +
                   "default value and no code could tell that apart from an injected one. Use a " +
                   "reference type, or '" + Diagnostics.Display(contract) + "?'.";
        }

        internal static object Instantiate(Context context, Type type, object[] args)
        {
            var reason = DescribeIfNotActivatable(type);
            if (reason != null)
            {
                throw new ContextException(
                    "Cannot instantiate '" + Diagnostics.Display(type) + "': " + reason + "." +
                    (IsEngineObject(type)
                        ? "\n      Create it the Unity way - AddComponent, Object.Instantiate or " +
                          "ScriptableObject.CreateInstance - and pass it to Context.Inject to fill its [Inject] members."
                        : string.Empty));
            }

            var metadata = GetMetadata(type);
            if (metadata.MarkedCount > 1)
            {
                throw new ContextException(
                    "'" + Diagnostics.Display(type) + "' has " + metadata.MarkedCount +
                    " constructors marked [Inject]. Exactly one may be marked.");
            }

            var used = args == null || args.Length == 0 ? null : new bool[args.Length];

            if (metadata.MarkedCount == 1)
            {
                var index = metadata.MarkedIndex;
                object[] values;
                string missing;
                if (TryBind(context, type, metadata.Parameters[index], args, used, out values, out missing))
                    return Invoke(type, metadata.Constructors[index], values);

                throw new ContextException(
                    "Cannot instantiate '" + Diagnostics.Display(type) +
                    "': its [Inject] constructor cannot be satisfied.\n      " + missing);
            }

            if (metadata.PublicCount == 0)
            {
                throw new ContextException(
                    "Cannot instantiate '" + Diagnostics.Display(type) +
                    "': it has no public instance constructor, and none is marked [Inject]." +
                    Diagnostics.StrippingHint(metadata.Constructors.Length));
            }

            // The build's rule: the widest satisfiable public constructor, and two equally wide ones that can
            // both be satisfied are an error rather than a choice made by the order reflection lists them in.
            var chosen = -1;
            object[] chosenValues = null;
            string firstFailure = null;
            for (var i = 0; i < metadata.Constructors.Length; i++)
            {
                if (!metadata.Constructors[i].IsPublic) continue;
                if (chosen >= 0 && metadata.Parameters[i].Length != metadata.Parameters[chosen].Length) break;
                if (used != null) Array.Clear(used, 0, used.Length);

                object[] values;
                string missing;
                if (!TryBind(context, type, metadata.Parameters[i], args, used, out values, out missing))
                {
                    if (firstFailure == null) firstFailure = missing;
                    continue;
                }

                if (chosen < 0)
                {
                    chosen = i;
                    chosenValues = values;
                    continue;
                }

                throw new ContextException(
                    "Cannot instantiate '" + Diagnostics.Display(type) + "': it has two public constructors of " +
                    metadata.Parameters[i].Length + " parameters that can both be satisfied, so the choice is " +
                    "ambiguous: (" + Diagnostics.Signature(metadata.Parameters[chosen]) + ") and (" +
                    Diagnostics.Signature(metadata.Parameters[i]) + ")." +
                    "\n      Mark the one you mean with [Inject], or construct it yourself.");
            }

            if (chosen >= 0) return Invoke(type, metadata.Constructors[chosen], chosenValues);

            throw new ContextException(
                "Cannot instantiate '" + Diagnostics.Display(type) +
                "': no public constructor could be satisfied from the supplied arguments and this context.\n      " +
                firstFailure);
        }

        private static object Invoke(Type type, ConstructorInfo constructor, object[] values)
        {
            try
            {
                return constructor.Invoke(values);
            }
            catch (TargetInvocationException exception)
            {
                // The build's wording: what was thrown, on the first line, and the original as the inner one.
                var original = exception.InnerException ?? exception;
                throw new ContextException(
                    "The constructor of '" + Diagnostics.Display(type) + "' threw " + Diagnostics.Describe(original),
                    original);
            }
        }

        private static bool TryBind(Context context, Type type, ParameterInfo[] parameters, object[] args,
            bool[] used, out object[] values, out string missing)
        {
            missing = null;
            if (parameters.Length == 0)
            {
                values = NoArguments;
                return true;
            }

            values = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var contract = parameters[i].ParameterType;

                var matched = false;
                if (args != null)
                {
                    for (var a = 0; a < args.Length; a++)
                    {
                        if (used[a] || args[a] == null || !contract.IsInstanceOfType(args[a])) continue;

                        used[a] = true;
                        values[i] = args[a];
                        matched = true;
                        break;
                    }
                }

                if (matched) continue;

                object service;
                if (context.TryResolve(contract, out service))
                {
                    values[i] = service;
                    continue;
                }

                // The build's wording and suggestions, so a missing .As<> reads the same here as there.
                missing = Diagnostics.UnableToResolve(contract, type,
                    "the constructor parameter '" + parameters[i].Name + "'", null, context.Contracts);
                values = null;
                return false;
            }

            return true;
        }
    }
}
