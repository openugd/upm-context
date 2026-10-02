using System;
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

        internal void SetValue(object target, object value)
        {
            if (_field != null) _field.SetValue(target, value);
            else _property.SetValue(target, value, null);
        }
    }

    internal static class Activation
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<Type, TypeMetadata> Cache = new Dictionary<Type, TypeMetadata>();
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

        internal static TypeMetadata GetMetadata(Type type)
        {
            lock (Gate)
            {
                TypeMetadata metadata;
                if (Cache.TryGetValue(type, out metadata)) return metadata;

                metadata = Read(type);
                Cache[type] = metadata;
                return metadata;
            }
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
            return null;
        }

        private static TypeMetadata Read(Type type)
        {
            var metadata = new TypeMetadata();
            List<string> errors = null;

            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                                                    BindingFlags.Instance);
            Array.Sort(constructors, (a, b) => b.GetParameters().Length.CompareTo(a.GetParameters().Length));

            metadata.Constructors = constructors;
            metadata.Parameters = new ParameterInfo[constructors.Length][];
            for (var i = 0; i < constructors.Length; i++)
            {
                metadata.Parameters[i] = constructors[i].GetParameters();
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
                    "Cannot instantiate '" + Diagnostics.Display(type) + "': " + reason + ".");
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
                if (TryBind(context, metadata.Parameters[index], args, used, out values, out missing))
                    return Invoke(type, metadata.Constructors[index], values);

                throw new ContextException(
                    "Cannot instantiate '" + Diagnostics.Display(type) +
                    "': its [Inject] constructor cannot be satisfied. " + missing);
            }

            if (metadata.PublicCount == 0)
            {
                throw new ContextException(
                    "Cannot instantiate '" + Diagnostics.Display(type) +
                    "': it has no public instance constructor, and none is marked [Inject]." +
                    Diagnostics.StrippingHint(metadata.Constructors.Length));
            }

            string firstFailure = null;
            for (var i = 0; i < metadata.Constructors.Length; i++)
            {
                if (!metadata.Constructors[i].IsPublic) continue;
                if (used != null) Array.Clear(used, 0, used.Length);

                object[] values;
                string missing;
                if (TryBind(context, metadata.Parameters[i], args, used, out values, out missing))
                    return Invoke(type, metadata.Constructors[i], values);

                if (firstFailure == null) firstFailure = missing;
            }

            throw new ContextException(
                "Cannot instantiate '" + Diagnostics.Display(type) +
                "': no public constructor could be satisfied from the supplied arguments and this context. " +
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
                throw new ContextException(
                    "The constructor of '" + Diagnostics.Display(type) + "' threw.",
                    exception.InnerException ?? exception);
            }
        }

        private static bool TryBind(Context context, ParameterInfo[] parameters, object[] args, bool[] used,
            out object[] values, out string missing)
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

                missing = "Unable to resolve service for type '" + Diagnostics.Display(contract) +
                          "' (constructor parameter '" + parameters[i].Name + "').";
                values = null;
                return false;
            }

            return true;
        }
    }
}
