using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUGD
{
    internal sealed class Step
    {
        internal Type Implementation;
        internal Func<Context, object> Factory;
        internal ConstructorInfo Constructor;
        internal int[] ArgumentSlots;
        internal string Site;

        /// The registrations of this context that building this one acquired - constructor arguments and
        /// whatever a factory resolved - in the order acquired. Null when there were none.
        internal List<int> Needs;

        /// Registered with AddInstance: the slot is filled before the build and the object is the caller's.
        internal bool HandedOver;
    }

    internal sealed class BootStep
    {
        internal int Rank;
        internal string Name;
        internal Func<Context, CancellationToken, Task> Run;
    }

    internal sealed class ContextPlan
    {
        private static readonly BootStep[] NoSteps = new BootStep[0];

        internal Dictionary<Type, int> Map;
        internal object[] Instances;
        internal Step[] Steps;

        internal int ContextSlot = -1;
        internal int LifetimeSlot = -1;

        private BootStep[] _awake = NoSteps;
        private BootStep[] _initialize = NoSteps;

        // Build-time only. 0 = untouched, 1 = under construction, 2 = done. Needed for the one case the
        // pre-build cycle check cannot cover: a registration factory is opaque, so a cycle through one is
        // only visible while it runs.
        private byte[] _state;
        private List<int> _stack;
        private int _current = -1;

        // Build-time only: who holds each object. One object can fill several slots - a forwarding factory
        // Add<I>(c => c.Resolve<X>()), one object handed to AddInstance twice, a factory returning an
        // AddInstance object, a child re-registering what its parent holds - so ownership is decided per
        // object, by reference, never per slot. The first slot an object fills claims it; every later slot
        // holding it points at that claim, and the object is injected, booted and, if this context made it,
        // disposed once. An object an ancestor context holds, and this context's own Context and Lifetime,
        // are Foreign: never injected, booted or disposed here.
        private const int Foreign = -1;
        private Dictionary<object, int> _claims;
        private int[] _owner;
        private HashSet<object> _ancestors;
        private Context _parent;

        // Build-time only: per claiming slot, the claiming slots of the objects its [Inject] members hold.
        private List<int>[] _held;

        internal Context CreateContext(Lifetime.Definition definition, Context parent)
        {
            _state = new byte[Steps.Length];
            _stack = new List<int>();
            _claims = new Dictionary<object, int>(IdentityComparer.Instance);
            _owner = new int[Steps.Length];
            for (var i = 0; i < _owner.Length; i++) _owner[i] = Foreign;
            _held = new List<int>[Steps.Length];
            _parent = parent;

            var context = new Context(definition, parent, this);

            if (ContextSlot >= 0) Instances[ContextSlot] = context;
            if (LifetimeSlot >= 0) Instances[LifetimeSlot] = definition.Lifetime;

            return context;
        }

        internal void ConstructAll(Context context)
        {
            // Objects handed over with AddInstance are claimed first, in registration order, so a factory that
            // returns one later finds it claimed and never takes ownership of it.
            for (var i = 0; i < Steps.Length; i++)
            {
                if (Steps[i].HandedOver) Claim(context, i, Instances[i], false);
            }

            for (var i = 0; i < Steps.Length; i++) EnsureConstructed(context, i);
        }

        /// Resolution while the context is still being built: constructs on demand, and records the
        /// dependency so that a factory's otherwise invisible dependencies still rank it correctly.
        internal object Acquire(Context context, int slot)
        {
            var instance = Instances[slot] ?? EnsureConstructed(context, slot);

            if (_current >= 0 && _current != slot && slot < Steps.Length)
            {
                var step = Steps[_current];
                (step.Needs ?? (step.Needs = new List<int>())).Add(slot);
            }

            return instance;
        }

        private object EnsureConstructed(Context context, int slot)
        {
            var existing = Instances[slot];
            if (existing != null) return existing;
            if (slot >= Steps.Length) return null;

            var step = Steps[slot];
            if (_state[slot] == 1) throw FactoryCycle(slot);

            _state[slot] = 1;
            _stack.Add(slot);
            var previous = _current;
            _current = slot;

            object instance;
            if (step.Factory != null)
            {
                instance = step.Factory(context);
                if (instance == null)
                {
                    throw new ContextException(
                        "The factory registered for '" + Diagnostics.Display(step.Implementation) +
                        "' returned null." + Diagnostics.Where(step.Site));
                }

                if (!step.Implementation.IsInstanceOfType(instance))
                {
                    throw new ContextException(
                        "The factory registered for '" + Diagnostics.Display(step.Implementation) +
                        "' returned a '" + Diagnostics.Display(instance.GetType()) +
                        "', which is not assignable to it." + Diagnostics.Where(step.Site));
                }
            }
            else
            {
                var slots = step.ArgumentSlots;
                var arguments = slots.Length == 0 ? null : new object[slots.Length];
                for (var i = 0; i < slots.Length; i++) arguments[i] = Acquire(context, slots[i]);

                try
                {
                    instance = step.Constructor.Invoke(arguments);
                }
                catch (TargetInvocationException exception)
                {
                    throw new ContextException(
                        "The constructor of '" + Diagnostics.Display(step.Implementation) + "' threw." +
                        Diagnostics.Where(step.Site), exception.InnerException ?? exception);
                }
            }

            _current = previous;
            _stack.RemoveAt(_stack.Count - 1);
            _state[slot] = 2;
            Instances[slot] = instance;

            // A constructor always returns a new object, which nobody can hold yet; a factory may return
            // anything, including an object that already has an owner.
            Claim(context, slot, instance, step.Factory == null);

            return instance;
        }

        /// Records who holds <paramref name="instance"/>, now in <paramref name="slot"/>, and makes this
        /// context responsible for disposing it if - and only if - this slot is the first to hold it, this
        /// context produced it, and nobody else owns it.
        private void Claim(Context context, int slot, object instance, bool fresh)
        {
            int owner;
            if (!fresh && _claims.TryGetValue(instance, out owner))
            {
                _owner[slot] = owner;
                return;
            }

            owner = !fresh && IsForeign(context, instance) ? Foreign : slot;
            _claims[instance] = owner;
            _owner[slot] = owner;

            if (owner != slot || Steps[slot].HandedOver) return;

            // Registered here, one at a time, which is what makes a failed build atomic: terminating the
            // lifetime disposes exactly what was constructed before the failure, in reverse construction
            // order, because Lifetime runs its actions LIFO.
            var disposable = instance as IDisposable;
            if (disposable != null) context.Lifetime.AddAction(disposable.Dispose);
        }

        private bool IsForeign(Context context, object instance)
        {
            if (ReferenceEquals(instance, context) || ReferenceEquals(instance, context.Lifetime)) return true;
            if (_parent == null) return false;

            if (_ancestors == null)
            {
                // Every ancestor, not just the parent: a child copies only the contracts it does not shadow, so
                // an object a grandparent holds may be missing from the parent's table.
                _ancestors = new HashSet<object>(IdentityComparer.Instance);
                for (var ancestor = _parent; ancestor != null; ancestor = ancestor.Parent)
                {
                    var table = ancestor.Table;
                    for (var i = 0; i < table.Length; i++)
                    {
                        if (table[i] != null) _ancestors.Add(table[i]);
                    }
                }
            }

            return _ancestors.Contains(instance);
        }

        internal void InjectAll(Context context)
        {
            // After every constructor has run, so two services may hold each other through [Inject]
            // members. Everything here was validated before construction started; a factory that returned
            // a subtype with extra members is the one case that can still fail, and it fails loudly.
            for (var i = 0; i < Steps.Length; i++)
            {
                // Once per object, under the registration that claimed it; never an object of an ancestor,
                // which that ancestor injected from its own registrations.
                if (_owner[i] != i) continue;

                var instance = Instances[i];
                var type = instance.GetType();

                string error;
                var members = Activation.GetInjectMembers(type, out error);
                if (error != null) throw new ContextException(error + Diagnostics.Where(Steps[i].Site));

                for (var m = 0; m < members.Length; m++)
                {
                    var member = members[m];
                    object value;
                    if (!context.TryResolve(member.Contract, out value))
                    {
                        if (member.Optional) continue;

                        throw new ContextException(
                            Diagnostics.UnableToResolveMember(member, type, Steps[i].Site, context.Contracts));
                    }

                    member.SetValue(instance, value);

                    // A member is a boot dependency too: the holder should not boot before what it holds.
                    // Only objects of this context count - an inherited one booted with its own context.
                    int target;
                    if (!Map.TryGetValue(member.Contract, out target) || target >= Steps.Length) continue;

                    var held = _owner[target];
                    if (held != Foreign && held != i) (_held[i] ?? (_held[i] = new List<int>())).Add(held);
                }
            }
        }

        /// Ranks every object this context claimed for the boot, by the longest path of what it depends on:
        /// what building it acquired (constructor arguments, what a factory resolved) and what its [Inject]
        /// members hold. Members may be cyclic and constructors may not, so only a member edge can close a
        /// cycle; a member edge that does is not counted, since no order satisfies it. The objects of such a
        /// cycle share one rank - the first after everything any of them depends on outside it - except that
        /// a constructor or factory dependency inside the cycle is still ranked below what needs it.
        private int[] Rank()
        {
            var count = Steps.Length;
            var hard = new List<int>[count];
            for (var s = 0; s < count; s++)
            {
                var owner = _owner[s];
                var needs = Steps[s].Needs;
                if (owner == Foreign || needs == null) continue;

                // Every registration of one object contributes what it needed to that object's node.
                for (var n = 0; n < needs.Count; n++)
                {
                    var target = _owner[needs[n]];
                    if (target == Foreign || target == owner) continue;
                    (hard[owner] ?? (hard[owner] = new List<int>())).Add(target);
                }
            }

            List<List<int>> order;
            var component = StronglyConnected(hard, _held, out order);

            var rank = new int[count];
            var height = new int[count];
            var colour = new byte[count];
            for (var c = 0; c < order.Count; c++)
            {
                // Tarjan completes a component only after every component it reaches, so every rank read
                // below from outside this component is final.
                var members = order[c];
                var floor = 0;
                for (var m = 0; m < members.Count; m++)
                {
                    floor = Math.Max(floor, Floor(hard[members[m]], component, c, rank));
                    floor = Math.Max(floor, Floor(_held[members[m]], component, c, rank));
                }

                for (var m = 0; m < members.Count; m++)
                {
                    var node = members[m];
                    rank[node] = members.Count == 1 ? floor : floor + Height(node, hard, component, c, height, colour);
                }
            }

            return rank;
        }

        private static int Floor(List<int> edges, int[] component, int current, int[] rank)
        {
            var floor = 0;
            if (edges == null) return floor;

            for (var e = 0; e < edges.Count; e++)
            {
                var target = edges[e];
                if (component[target] != current) floor = Math.Max(floor, rank[target] + 1);
            }

            return floor;
        }

        /// The longest path along constructor and factory edges that stay inside one component. Those edges
        /// form a cycle only when one object is returned by several factories that also need each other's
        /// dependencies; the walk then cuts the cycle where it meets it.
        private static int Height(int node, List<int>[] hard, int[] component, int current, int[] height,
            byte[] colour)
        {
            if (colour[node] == 2) return height[node];

            colour[node] = 1;
            var best = 0;
            var edges = hard[node];
            if (edges != null)
            {
                for (var e = 0; e < edges.Count; e++)
                {
                    var target = edges[e];
                    if (component[target] != current || colour[target] == 1) continue;
                    best = Math.Max(best, Height(target, hard, component, current, height, colour) + 1);
                }
            }

            colour[node] = 2;
            height[node] = best;
            return best;
        }

        /// Tarjan's algorithm over both kinds of edge, without recursion. Returns each node's component and,
        /// through <paramref name="order"/>, the components in the order they completed: every component
        /// after all the components it reaches.
        private int[] StronglyConnected(List<int>[] hard, List<int>[] soft, out List<List<int>> order)
        {
            var count = Steps.Length;
            var index = new int[count];
            var low = new int[count];
            var component = new int[count];
            var onStack = new bool[count];
            for (var i = 0; i < count; i++)
            {
                index[i] = -1;
                component[i] = -1;
            }

            order = new List<List<int>>();
            var stack = new Stack<int>();
            var calls = new Stack<KeyValuePair<int, int>>(); // node, next edge
            var next = 0;

            for (var root = 0; root < count; root++)
            {
                if (_owner[root] != root || index[root] >= 0) continue;

                index[root] = low[root] = next++;
                stack.Push(root);
                onStack[root] = true;
                calls.Push(new KeyValuePair<int, int>(root, 0));

                while (calls.Count > 0)
                {
                    var call = calls.Pop();
                    var node = call.Key;
                    var edge = call.Value;
                    var hardCount = hard[node] == null ? 0 : hard[node].Count;
                    var total = hardCount + (soft[node] == null ? 0 : soft[node].Count);

                    if (edge < total)
                    {
                        calls.Push(new KeyValuePair<int, int>(node, edge + 1));
                        var target = edge < hardCount ? hard[node][edge] : soft[node][edge - hardCount];

                        if (index[target] < 0)
                        {
                            index[target] = low[target] = next++;
                            stack.Push(target);
                            onStack[target] = true;
                            calls.Push(new KeyValuePair<int, int>(target, 0));
                        }
                        else if (onStack[target])
                        {
                            low[node] = Math.Min(low[node], index[target]);
                        }

                        continue;
                    }

                    if (low[node] == index[node])
                    {
                        var members = new List<int>();
                        int member;
                        do
                        {
                            member = stack.Pop();
                            onStack[member] = false;
                            component[member] = order.Count;
                            members.Add(member);
                        } while (member != node);

                        // Registration order within the component, so a cycle reads the way it was written.
                        members.Sort();
                        order.Add(members);
                    }

                    if (calls.Count > 0)
                    {
                        var parent = calls.Peek().Key;
                        low[parent] = Math.Min(low[parent], low[node]);
                    }
                }
            }

            return component;
        }

        internal void CollectBootSteps(InitializerCollection initializers)
        {
            List<BootStep> awake = null;
            List<BootStep> initialize = null;

            // Explicit initializers first within their phase: they are infrastructure the services of that
            // phase may rely on, and they are not part of the dependency graph. Each gets a rank of its own,
            // below every service's and increasing in Add order, so they run one at a time in the order they
            // were added even under StartupMode.Parallel - a later step may rely on an earlier one, and
            // nothing in a lambda says otherwise.
            var entries = initializers.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                Add(ref awake, ref initialize, entry.Phase,
                    new BootStep { Rank = i - entries.Count, Name = entry.Name, Run = entry.Step });
            }

            // Enrolment is by what the instance actually is, not by the registered type, so a factory
            // returning a subtype is still enrolled. Once per object, under the registration that claimed it,
            // and never for an object an ancestor holds - that one booted with its own context.
            var ranks = Rank();
            for (var i = 0; i < Steps.Length; i++)
            {
                if (_owner[i] != i) continue;

                var instance = Instances[i];
                var rank = ranks[i];
                var name = Diagnostics.Display(instance.GetType());

                var awakeService = instance as IAwakeService;
                if (awakeService != null)
                {
                    Add(ref awake, ref initialize, BootPhase.Awake, new BootStep {
                        Rank = rank,
                        Name = name + ".AwakeAsync",
                        Run = (context, token) => awakeService.AwakeAsync(token)
                    });
                }

                var initializeService = instance as IInitializeService;
                if (initializeService != null)
                {
                    Add(ref awake, ref initialize, BootPhase.Initialize, new BootStep {
                        Rank = rank,
                        Name = name + ".InitializeAsync",
                        Run = (context, token) => initializeService.InitializeAsync(token)
                    });
                }
            }

            _awake = Sort(awake);
            _initialize = Sort(initialize);
        }

        internal async Task RunPhasesAsync(Context context, StartupMode mode, CancellationToken token)
        {
            await RunPhaseAsync(_awake, BootPhase.Awake, context, mode, token);
            await RunPhaseAsync(_initialize, BootPhase.Initialize, context, mode, token);
        }

        private static async Task RunPhaseAsync(BootStep[] steps, BootPhase phase, Context context,
            StartupMode mode, CancellationToken token)
        {
            var i = 0;
            while (i < steps.Length)
            {
                // One rank at a time. Everything within a rank is mutually independent by construction,
                // so running it concurrently cannot violate dependency order - that is what rank is for.
                var rank = steps[i].Rank;
                var end = i;
                while (end < steps.Length && steps[end].Rank == rank) end++;

                token.ThrowIfCancellationRequested();

                if (mode == StartupMode.Sequential || end - i == 1)
                {
                    for (var k = i; k < end; k++) await InvokeAsync(steps[k], phase, context, token);
                }
                else
                {
                    var tasks = new Task[end - i];
                    for (var k = i; k < end; k++) tasks[k - i] = InvokeAsync(steps[k], phase, context, token);

                    // Waits for every task in the rank even when one has already failed, so teardown never
                    // runs while a boot step is still touching the objects it is about to dispose. Awaiting
                    // WhenAll would rethrow only the first failure, so the rank is inspected as a whole.
                    try
                    {
                        await Task.WhenAll(tasks);
                    }
                    catch (Exception)
                    {
                        ThrowRankFailure(steps, i, tasks, phase, token);
                        throw;
                    }
                }

                i = end;
            }
        }

        /// Reports every step of a concurrently run rank that failed: one failure as itself, several as one
        /// ContextException naming each step. Failures win over cancellation, so a cancellation racing a
        /// real error never hides it. Returns only if nothing in the rank failed or was cancelled.
        private static void ThrowRankFailure(BootStep[] steps, int first, Task[] tasks, BootPhase phase,
            CancellationToken token)
        {
            List<Exception> failures = null;
            List<string> names = null;
            var cancelled = false;

            for (var t = 0; t < tasks.Length; t++)
            {
                var task = tasks[t];
                if (task.IsCanceled)
                {
                    cancelled = true;
                    continue;
                }

                if (!task.IsFaulted) continue;

                // InvokeAsync faults only with the ContextException naming its step, and lets nothing but the
                // build's own cancellation through - which an async method turns into a cancelled task.
                foreach (var exception in task.Exception.InnerExceptions)
                {
                    if (exception is OperationCanceledException)
                    {
                        cancelled = true;
                        continue;
                    }

                    (failures ?? (failures = new List<Exception>())).Add(exception);
                    (names ?? (names = new List<string>())).Add(steps[first + t].Name);
                }
            }

            if (failures == null)
            {
                if (!cancelled) return;

                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }

            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();

            var message = new StringBuilder();
            message.Append(failures.Count).Append(" boot steps threw during the ").Append(phase)
                .Append(" phase of Context.BuildAsync:");
            for (var f = 0; f < failures.Count; f++)
            {
                var original = failures[f].InnerException ?? failures[f];
                message.Append("\n      '").Append(names[f]).Append("': ")
                    .Append(original.GetType().Name).Append(": ").Append(original.Message);
            }

            message.Append("\n      The Context was not built and everything constructed so far has been disposed. ")
                .Append("The InnerException is an AggregateException holding one ContextException per step, ")
                .Append("in boot order, each with that step's original exception as its InnerException.");

            throw new ContextException(message.ToString(), new AggregateException(failures));
        }

        private static async Task InvokeAsync(BootStep step, BootPhase phase, Context context,
            CancellationToken token)
        {
            try
            {
                var task = step.Run(context, token);
                if (task != null) await task;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The build itself was cancelled - its scope ended or the caller's token fired - so this is not
                // the step's failure, and it propagates unwrapped.
                throw;
            }
            catch (OperationCanceledException exception)
            {
                // The build's token is not cancelled, so the step was cancelled by something of its own - a
                // timeout, a token it made, a cancelled task it awaited. That is the step failing.
                throw new ContextException(
                    "'" + step.Name + "' was cancelled during the " + phase + " phase of Context.BuildAsync, " +
                    "but not through the token the build passed it, so this is a failure of that step (a " +
                    "timeout or a cancellation of its own) and not a cancellation of the build. The Context " +
                    "was not built and everything constructed so far has been disposed. The original " +
                    "exception is the InnerException.", exception);
            }
            catch (Exception exception)
            {
                throw new ContextException(
                    "'" + step.Name + "' threw during the " + phase + " phase of Context.BuildAsync. The " +
                    "Context was not built and everything constructed so far has been disposed. The " +
                    "original exception is the InnerException.", exception);
            }
        }

        private static void Add(ref List<BootStep> awake, ref List<BootStep> initialize, BootPhase phase,
            BootStep step)
        {
            if (phase == BootPhase.Awake) (awake ?? (awake = new List<BootStep>())).Add(step);
            else (initialize ?? (initialize = new List<BootStep>())).Add(step);
        }

        private static BootStep[] Sort(List<BootStep> steps)
        {
            if (steps == null) return NoSteps;

            // Stable: the list is already in (explicit-first, then registration) order, and only equal
            // ranks can be reordered by an unstable sort, so the index is the tiebreaker.
            var indices = new int[steps.Count];
            for (var i = 0; i < indices.Length; i++) indices[i] = i;

            Array.Sort(indices, (a, b) => {
                var byRank = steps[a].Rank.CompareTo(steps[b].Rank);
                return byRank != 0 ? byRank : a.CompareTo(b);
            });

            var result = new BootStep[steps.Count];
            for (var i = 0; i < indices.Length; i++) result[i] = steps[indices[i]];
            return result;
        }

        private ContextException FactoryCycle(int slot)
        {
            var path = new List<Type>();
            var start = _stack.IndexOf(slot);
            for (var i = start; i < _stack.Count; i++) path.Add(Steps[_stack[i]].Implementation);
            path.Add(Steps[slot].Implementation);

            return new ContextException(
                "A circular dependency was detected for the service of type '" +
                Diagnostics.Display(Steps[slot].Implementation) + "': " + Diagnostics.Path(path) +
                ".\n      At least one step of this cycle goes through a registration factory, whose " +
                "dependencies are opaque until it runs, so it could not be caught before construction " +
                "started. Break it by taking one of these dependencies as an [Inject] member instead - " +
                "member injection happens after every service exists, so it is allowed to be cyclic.",
                path);
        }
    }

    /// Equality by reference, whatever the type says: two registrations hold "the same object" only if they
    /// hold the very same instance, however its Equals and GetHashCode are written.
    internal sealed class IdentityComparer : IEqualityComparer<object>
    {
        internal static readonly IdentityComparer Instance = new IdentityComparer();

        public new bool Equals(object x, object y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
