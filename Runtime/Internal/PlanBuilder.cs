using System;
using System.Collections.Generic;
using System.Reflection;

namespace OpenUGD
{
    internal static class PlanBuilder
    {
        internal static ContextPlan Build(ContextBuilder builder)
        {
            var compilation = new Compilation(builder);

            compilation.ClaimContracts();
            compilation.CollectInherited();
            compilation.PlanActivations();
            compilation.DetectCycles();
            compilation.ThrowIfFailed();

            return compilation.Emit();
        }

        private sealed class Compilation
        {
            private const byte White = 0;
            private const byte Grey = 1;
            private const byte Black = 2;

            private readonly Context _parent;
            private readonly ServiceCollection.Entry[] _entries;
            private readonly Node[] _nodes;
            private readonly List<string> _errors = new List<string>();

            private readonly Dictionary<Type, int> _owners = new Dictionary<Type, int>();
            private readonly HashSet<Type> _resolvable = new HashSet<Type>();
            private readonly List<Type> _automatic = new List<Type>();
            private readonly List<KeyValuePair<Type, object>> _inherited =
                new List<KeyValuePair<Type, object>>();

            // IReadOnlyList<T> -> the entries contributing to it, in registration order; empty for a list a
            // constructor or member asks for and nothing contributes to.
            private readonly Dictionary<Type, List<int>> _lists = new Dictionary<Type, List<int>>();

            private List<Type> _cycle;

            internal Compilation(ContextBuilder builder)
            {
                _parent = builder.Parent;
                _entries = builder.Services.Snapshot();
                _nodes = new Node[_entries.Length];
                for (var i = 0; i < _nodes.Length; i++) _nodes[i] = new Node();
            }

            private sealed class Node
            {
                internal ConstructorInfo Constructor;
                internal Type[] Parameters = Type.EmptyTypes;
                internal byte Colour;
            }

            // ------------------------------------------------------------------ contracts

            internal void ClaimContracts()
            {
                for (var i = 0; i < _entries.Length; i++)
                {
                    var entry = _entries[i];
                    var contracts = entry.Contracts;

                    for (var c = 0; c < contracts.Count; c++)
                    {
                        var contract = contracts[c];
                        if (!contract.IsAssignableFrom(entry.Implementation))
                        {
                            _errors.Add(
                                "'" + Diagnostics.Display(entry.Implementation) +
                                "' cannot be registered as '" + Diagnostics.Display(contract) +
                                "': it does not implement or inherit it." + Diagnostics.Where(entry.Site));
                            continue;
                        }

                        Claim(contract, i);
                    }
                }

                // The two services every graph may assume exist - unless the caller registered their
                // own, which wins.
                Automatic(typeof(Context));
                Automatic(typeof(Lifetime));

                for (var i = 0; i < _entries.Length; i++)
                {
                    var elements = _entries[i].Elements;
                    for (var e = 0; elements != null && e < elements.Count; e++) Contribute(i, elements[e]);
                }
            }

            private void Contribute(int index, Type element)
            {
                var entry = _entries[index];
                var list = element.IsValueType ? null : CollectionContract.Of(element);
                var problem = list == null ? "it is a value type, and a collection holds reference types"
                    : !element.IsAssignableFrom(entry.Implementation) ? "it does not implement or inherit it"
                    : _owners.ContainsKey(list) ? "'" + Diagnostics.Display(list) + "' is also registered as an " +
                                                  "ordinary contract by '" +
                                                  Diagnostics.Display(_entries[_owners[list]].Implementation) + "'"
                    : null;

                if (problem != null)
                {
                    _errors.Add("'" + Diagnostics.Display(entry.Implementation) + "' cannot be an element of '" +
                                Diagnostics.Display(element) + "': " + problem + "." + Diagnostics.Where(entry.Site));
                    return;
                }

                List<int> contributors;
                if (!_lists.TryGetValue(list, out contributors)) _lists[list] = contributors = new List<int>();
                contributors.Add(index);
                _resolvable.Add(list);
            }

            /// Whether a dependency on <paramref name="contract"/> can be met. An IReadOnlyList of a reference
            /// type always can: nothing claimed it, so it is this context's collection, empty if need be.
            private bool Resolvable(Type contract)
            {
                if (_resolvable.Contains(contract)) return true;
                if (CollectionContract.ElementOf(contract) == null) return false;

                _lists[contract] = new List<int>();
                _resolvable.Add(contract);
                return true;
            }

            private void Claim(Type contract, int index)
            {
                int existing;
                if (!_owners.TryGetValue(contract, out existing))
                {
                    _owners[contract] = index;
                    _resolvable.Add(contract);
                    return;
                }

                if (existing == index) return;

                _errors.Add(
                    "'" + Diagnostics.Display(contract) + "' is registered twice: by '" +
                    Diagnostics.Display(_entries[existing].Implementation) + "'" +
                    Diagnostics.Where(_entries[existing].Site) + " and by '" +
                    Diagnostics.Display(_entries[index].Implementation) + "'" +
                    Diagnostics.Where(_entries[index].Site) +
                    "\n      One registration per contract. Use TryAdd to supply a default only when " +
                    "nothing else does, or a child Context to override one.");
            }

            private void Automatic(Type contract)
            {
                if (_resolvable.Contains(contract)) return;

                _automatic.Add(contract);
                _resolvable.Add(contract);
            }

            internal void CollectInherited()
            {
                if (_parent == null) return;

                foreach (var contract in _parent.Contracts)
                {
                    if (_resolvable.Contains(contract)) continue; // shadowed locally
                    if (_parent.IsCollection(contract)) continue; // a collection is local to its context

                    object instance;
                    if (!_parent.TryResolve(contract, out instance)) continue;

                    // C7: the instance stays the parent's - built there, cached there, disposed with the
                    // parent's lifetime. The child copies the reference, so a parent singleton can never
                    // become captive in a shorter scope, and an inherited resolve costs exactly what a
                    // local one costs.
                    _inherited.Add(new KeyValuePair<Type, object>(contract, instance));
                    _resolvable.Add(contract);
                }
            }

            // ------------------------------------------------------------------ activation

            internal void PlanActivations()
            {
                for (var i = 0; i < _entries.Length; i++)
                {
                    var entry = _entries[i];

                    if (entry.Instance == null && entry.Factory == null) SelectConstructor(entry, _nodes[i]);

                    var type = entry.Instance != null ? entry.Instance.GetType() : entry.Implementation;

                    string memberError;
                    var members = Activation.GetInjectMembers(type, out memberError);
                    if (memberError != null)
                    {
                        _errors.Add(memberError + Diagnostics.Where(entry.Site));
                        continue;
                    }

                    for (var m = 0; m < members.Length; m++)
                    {
                        var member = members[m];
                        if (member.Optional || Resolvable(member.Contract)) continue;

                        _errors.Add(Diagnostics.UnableToResolveMember(member, type, entry.Site, Known()));
                    }
                }
            }

            private void SelectConstructor(ServiceCollection.Entry entry, Node node)
            {
                var type = entry.Implementation;

                var reason = Activation.DescribeIfNotActivatable(type);
                if (reason != null)
                {
                    _errors.Add(
                        "'" + Diagnostics.Display(type) + "' cannot be activated because " + reason + "." +
                        Diagnostics.Where(entry.Site) +
                        (Activation.IsEngineObject(type)
                            ? "\n      Register the object Unity made with AddInstance, or a factory that makes it " +
                              "the Unity way: Add<T>(c => gameObject.AddComponent<T>()), " +
                              "Add<T>(c => ScriptableObject.CreateInstance<T>())."
                            : "\n      Register a concrete type, or a factory: Add<T>(c => new T(...))."));
                    return;
                }

                var metadata = Activation.GetMetadata(type);

                if (metadata.MarkedCount > 1)
                {
                    _errors.Add(
                        "'" + Diagnostics.Display(type) + "' has " + metadata.MarkedCount +
                        " constructors marked [Inject]. Exactly one may be marked." +
                        Diagnostics.Where(entry.Site));
                    return;
                }

                if (metadata.MarkedCount == 1)
                {
                    var marked = metadata.MarkedIndex;
                    Accept(entry, node, metadata.Constructors[marked], metadata.Parameters[marked], true);
                    return;
                }

                if (metadata.PublicCount == 0)
                {
                    _errors.Add(
                        "'" + Diagnostics.Display(type) +
                        "' has no public instance constructor, and none is marked [Inject]." +
                        Diagnostics.Where(entry.Site) + Diagnostics.StrippingHint(metadata.Constructors.Length));
                    return;
                }

                if (metadata.PublicCount == 1)
                {
                    for (var i = 0; i < metadata.Constructors.Length; i++)
                    {
                        if (!metadata.Constructors[i].IsPublic) continue;

                        Accept(entry, node, metadata.Constructors[i], metadata.Parameters[i], true);
                        return;
                    }
                }

                // The greediest satisfiable constructor, .NET Core's rule - and the deliberate opposite of
                // the 1.x injector, which took the FEWEST parameters, so adding `public Foo() {}` silently
                // disabled injection for the whole type.
                var chosen = -1;
                for (var i = 0; i < metadata.Constructors.Length; i++) // sorted widest-first
                {
                    if (!metadata.Constructors[i].IsPublic) continue;
                    if (!AllResolvable(metadata.Parameters[i])) continue;

                    if (chosen < 0)
                    {
                        chosen = i;
                        continue;
                    }

                    if (metadata.Parameters[i].Length != metadata.Parameters[chosen].Length) break;

                    _errors.Add(
                        "'" + Diagnostics.Display(type) + "' has two public constructors of " +
                        metadata.Parameters[i].Length +
                        " parameters that can both be satisfied, so the choice is ambiguous: (" +
                        Diagnostics.Signature(metadata.Parameters[chosen]) + ") and (" +
                        Diagnostics.Signature(metadata.Parameters[i]) + ")." + Diagnostics.Where(entry.Site) +
                        "\n      Mark the one you mean with [Inject], or register a factory.");
                    return;
                }

                if (chosen >= 0)
                {
                    Accept(entry, node, metadata.Constructors[chosen], metadata.Parameters[chosen], false);
                    return;
                }

                // Nothing is satisfiable. Report against the widest public constructor: it is the one the
                // author meant to be used, and its missing bindings are the informative ones.
                _errors.Add(
                    "'" + Diagnostics.Display(type) +
                    "' has no public constructor whose parameters are all registered." +
                    Diagnostics.Where(entry.Site));

                for (var i = 0; i < metadata.Constructors.Length; i++)
                {
                    if (!metadata.Constructors[i].IsPublic) continue;

                    Accept(entry, node, metadata.Constructors[i], metadata.Parameters[i], true);
                    return;
                }
            }

            private void Accept(ServiceCollection.Entry entry, Node node, ConstructorInfo constructor,
                ParameterInfo[] parameters, bool reportMissing)
            {
                node.Constructor = constructor;
                node.Parameters = new Type[parameters.Length];

                for (var i = 0; i < parameters.Length; i++)
                {
                    var contract = parameters[i].ParameterType;
                    node.Parameters[i] = contract;
                    if (!reportMissing || Resolvable(contract)) continue;

                    _errors.Add(Diagnostics.UnableToResolve(contract, entry.Implementation,
                        "the constructor parameter '" + parameters[i].Name + "'", entry.Site, Known()));
                }
            }

            private bool AllResolvable(ParameterInfo[] parameters)
            {
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (!Resolvable(parameters[i].ParameterType)) return false;
                }

                return true;
            }

            private IEnumerable<Type> Known()
            {
                foreach (var contract in _resolvable) yield return contract;
            }

            // ------------------------------------------------------------------ cycles

            internal void DetectCycles()
            {
                var stack = new List<int>();
                for (var i = 0; i < _nodes.Length; i++)
                {
                    if (_cycle != null) return; // one cycle is enough; the rest are usually the same one
                    Visit(i, stack);
                }
            }

            private void Visit(int index, List<int> stack)
            {
                var node = _nodes[index];
                if (node.Colour == Black) return;

                if (node.Colour == Grey)
                {
                    var path = new List<Type>();
                    var start = stack.IndexOf(index);
                    for (var i = start; i < stack.Count; i++) path.Add(_entries[stack[i]].Implementation);
                    path.Add(_entries[index].Implementation);
                    _cycle = path;

                    _errors.Add(
                        "A circular dependency was detected for the service of type '" +
                        Diagnostics.Display(_entries[index].Implementation) + "': " +
                        Diagnostics.Path(path) +
                        ".\n      Break it by taking one of these dependencies as an [Inject] member " +
                        "instead - member injection happens after every service is constructed, so it is " +
                        "allowed to be cyclic - or by extracting the part both sides need into a third " +
                        "service.");
                    return;
                }

                node.Colour = Grey;
                stack.Add(index);

                for (var p = 0; p < node.Parameters.Length; p++)
                {
                    // A list depends on every element in it, so its consumer does too.
                    List<int> elements;
                    int dependency;
                    if (_lists.TryGetValue(node.Parameters[p], out elements))
                    {
                        for (var e = 0; e < elements.Count && _cycle == null; e++) Visit(elements[e], stack);
                    }
                    else if (_owners.TryGetValue(node.Parameters[p], out dependency))
                    {
                        Visit(dependency, stack);
                    }

                    if (_cycle != null) return;
                }

                stack.RemoveAt(stack.Count - 1);
                node.Colour = Black;
            }

            // ------------------------------------------------------------------ result

            internal void ThrowIfFailed()
            {
                if (_errors.Count == 0) return;

                var message = Diagnostics.Report(_errors);
                throw _cycle != null ? new ContextException(message, _cycle) : new ContextException(message);
            }

            internal ContextPlan Emit()
            {
                var count = _entries.Length;
                var steps = count + _lists.Count;
                var total = steps + _automatic.Count + _inherited.Count;

                var plan = new ContextPlan {
                    Map = new Dictionary<Type, int>(total),
                    Instances = new object[total],
                    Steps = new Step[steps],
                    Collections = new HashSet<Type>(_lists.Keys)
                };

                // Slot == registration index. Construction order is decided by the recursion in
                // ContextPlan, which visits a dependency before whatever needs it, so no topological
                // sort is needed here and a boot log reads in the order the registrations were written.
                foreach (var pair in _owners) plan.Map[pair.Key] = pair.Value;

                // Each list is a step after the registrations, built by gathering its elements - which
                // records each as something the list needs, so the list, and whatever takes it, ranks after
                // all of them.
                var next = count;
                foreach (var pair in _lists)
                {
                    var element = CollectionContract.ElementOf(pair.Key);
                    var slots = pair.Value.ToArray();
                    plan.Map[pair.Key] = next;
                    plan.Steps[next++] = new Step {
                        Implementation = pair.Key,
                        Factory = context => plan.Gather(context, element, slots),
                        Collection = true,
                        ArgumentSlots = new int[0]
                    };
                }

                for (var i = 0; i < _automatic.Count; i++)
                {
                    var contract = _automatic[i];
                    plan.Map[contract] = next;

                    if (contract == typeof(Context)) plan.ContextSlot = next;
                    else plan.LifetimeSlot = next;

                    next++;
                }

                for (var i = 0; i < _inherited.Count; i++)
                {
                    plan.Map[_inherited[i].Key] = next;
                    plan.Instances[next] = _inherited[i].Value;
                    next++;
                }

                for (var i = 0; i < count; i++)
                {
                    var entry = _entries[i];
                    var node = _nodes[i];

                    var step = new Step {
                        Implementation = entry.Implementation,
                        Factory = entry.Factory,
                        Constructor = node.Constructor,
                        Site = entry.Site,
                        HandedOver = entry.Instance != null,
                        ArgumentSlots = new int[entry.Factory == null && entry.Instance == null
                            ? node.Parameters.Length
                            : 0]
                    };

                    for (var p = 0; p < step.ArgumentSlots.Length; p++)
                    {
                        step.ArgumentSlots[p] = plan.Map[node.Parameters[p]];
                    }

                    plan.Steps[i] = step;

                    // An instance the caller handed over is never constructed and never disposed here;
                    // pre-filling its slot is all there is to do.
                    if (entry.Instance != null) plan.Instances[i] = entry.Instance;
                }

                return plan;
            }
        }
    }
}
