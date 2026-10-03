using System;
using System.Collections.Generic;
using System.Reflection;
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

        private BootStep[] _awake = NoSteps;
        private BootStep[] _initialize = NoSteps;

        // Build-time only. 0 = untouched, 1 = under construction, 2 = done. Needed for the one case the
        // pre-build cycle check cannot cover: a registration factory is opaque, so a cycle through one is
        // only visible while it runs.
        private byte[] _state;
        private List<int> _stack;
        private int _current = -1;

        internal Context CreateContext(Lifetime.Definition definition, Context parent)
        {
            _state = new byte[Steps.Length];
            _stack = new List<int>();

            var context = new Context(definition, parent, this);

            if (ContextSlot >= 0) Instances[ContextSlot] = context;
            if (LifetimeSlot >= 0) Instances[LifetimeSlot] = definition.Lifetime;

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
            // returning a subtype is still enrolled.
            for (var i = 0; i < Steps.Length; i++)
            {
                var instance = Instances[i];
                var rank = Steps[i].Rank;
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
}
