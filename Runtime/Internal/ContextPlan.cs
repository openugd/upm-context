using System;
using System.Collections.Generic;
using System.Reflection;
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
        internal int Rank;
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
        internal int ConfigurationSlot = -1;

        private BootStep[] _awake = NoSteps;
        private BootStep[] _configure = NoSteps;
        private BootStep[] _initialize = NoSteps;

        // Build-time only. 0 = untouched, 1 = under construction, 2 = done. Needed for the one case the
        // pre-build cycle check cannot cover: a registration factory is opaque, so a cycle through one is
        // only visible while it runs.
        private byte[] _state;
        private List<int> _stack;
        private int _current = -1;

        internal Context CreateContext(Lifetime.Definition definition, Context parent,
            ConfigurationManager configuration)
        {
            _state = new byte[Steps.Length];
            _stack = new List<int>();

            var context = new Context(definition, parent, this);

            if (ContextSlot >= 0) Instances[ContextSlot] = context;
            if (LifetimeSlot >= 0) Instances[LifetimeSlot] = definition.Lifetime;
            if (ConfigurationSlot >= 0) Instances[ConfigurationSlot] = configuration;

            return context;
        }

        internal void ConstructAll(Context context)
        {
            for (var i = 0; i < Steps.Length; i++) EnsureConstructed(context, i);
        }

        /// Resolution while the context is still being built: constructs on demand, and records the
        /// dependency so that a factory's otherwise invisible dependencies still rank it correctly.
        internal object Acquire(Context context, int slot)
        {
            var instance = Instances[slot] ?? EnsureConstructed(context, slot);

            if (_current >= 0 && _current != slot && slot < Steps.Length)
            {
                // Rank = longest dependency path. Every dependency is fully constructed - and therefore
                // has its final rank - before this line runs, so one pass over the actual construction
                // order computes the true longest path, factories included.
                var rank = Steps[slot].Rank + 1;
                if (Steps[_current].Rank < rank) Steps[_current].Rank = rank;
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

            // Registered here, one at a time, which is what makes a failed build atomic: terminating the
            // lifetime disposes exactly what was constructed before the failure, in reverse construction
            // order, because Lifetime runs its actions LIFO.
            var disposable = instance as IDisposable;
            if (disposable != null) context.Lifetime.AddAction(disposable.Dispose);

            return instance;
        }

        internal void InjectAll(Context context)
        {
            // After every constructor has run, so two services may hold each other through [Inject]
            // members. Everything here was validated before construction started; a factory that returned
            // a subtype with extra members is the one case that can still fail, and it fails loudly.
            for (var i = 0; i < Steps.Length; i++)
            {
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
                }
            }
        }

        internal void CollectBootSteps(InitializerCollection initializers)
        {
            List<BootStep> awake = null;
            List<BootStep> configure = null;
            List<BootStep> initialize = null;

            // Explicit initializers first within their phase (rank -1): they are infrastructure the
            // services of that phase may rely on, and they are not part of the dependency graph.
            var entries = initializers.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                Add(ref awake, ref configure, ref initialize, entry.Phase,
                    new BootStep { Rank = -1, Name = entry.Name, Run = entry.Step });
            }

            // Enrolment is by what the instance actually is, not by the registered type, so a factory
            // returning a subtype is still enrolled.
            for (var i = 0; i < Steps.Length; i++)
            {
                var instance = Instances[i];
                var rank = Steps[i].Rank;
                var name = Diagnostics.Display(instance.GetType());

                var awakeService = instance as IAwakeService;
                if (awakeService != null)
                {
                    Add(ref awake, ref configure, ref initialize, BootPhase.Awake, new BootStep {
                        Rank = rank,
                        Name = name + ".AwakeAsync",
                        Run = (context, token) => awakeService.AwakeAsync(token)
                    });
                }

                var initializeService = instance as IInitializeService;
                if (initializeService != null)
                {
                    Add(ref awake, ref configure, ref initialize, BootPhase.Initialize, new BootStep {
                        Rank = rank,
                        Name = name + ".InitializeAsync",
                        Run = (context, token) => initializeService.InitializeAsync(token)
                    });
                }

                var contextInitializer = instance as IContextInitializer;
                if (contextInitializer != null)
                {
                    var phase = contextInitializer.Phase;
                    Add(ref awake, ref configure, ref initialize, phase, new BootStep {
                        Rank = rank,
                        Name = name + ".InitializeAsync (" + phase + ")",
                        Run = contextInitializer.InitializeAsync
                    });
                }
            }

            _awake = Sort(awake);
            _configure = Sort(configure);
            _initialize = Sort(initialize);
        }

        internal async Task RunPhasesAsync(Context context, StartupMode mode, CancellationToken token)
        {
            await RunPhaseAsync(_awake, BootPhase.Awake, context, mode, token);
            await RunPhaseAsync(_configure, BootPhase.Configure, context, mode, token);
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
                    // runs while a boot step is still touching the objects it is about to dispose.
                    await Task.WhenAll(tasks);
                }

                i = end;
            }
        }

        private static async Task InvokeAsync(BootStep step, BootPhase phase, Context context,
            CancellationToken token)
        {
            try
            {
                var task = step.Run(context, token);
                if (task != null) await task;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ContextException(
                    "'" + step.Name + "' threw during the " + phase + " phase of Context.BuildAsync. The " +
                    "Context was not built and everything constructed so far has been disposed. The " +
                    "original exception is the InnerException.", exception);
            }
        }

        private static void Add(ref List<BootStep> awake, ref List<BootStep> configure,
            ref List<BootStep> initialize, BootPhase phase, BootStep step)
        {
            switch (phase)
            {
                case BootPhase.Awake:
                    (awake ?? (awake = new List<BootStep>())).Add(step);
                    break;
                case BootPhase.Configure:
                    (configure ?? (configure = new List<BootStep>())).Add(step);
                    break;
                default:
                    (initialize ?? (initialize = new List<BootStep>())).Add(step);
                    break;
            }
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
}
