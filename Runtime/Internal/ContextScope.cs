using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace OpenUGD
{
    /// <summary>
    /// The scope of one context from <c>CreateBuilder</c> to teardown, and the one place that decides when
    /// it ends.
    /// </summary>
    /// <remarks>
    /// The context's lifetime is <i>linked</i> to the lifetime it was created on rather than nested in it.
    /// Nesting would end it the moment that lifetime ended — synchronously, on whichever thread ended it —
    /// while a boot step could still be running and using the very services that ending disposes. Linked, an
    /// end that arrives during the build only cancels the build, and the scope ends when the build has
    /// unwound, after every step in flight has finished. Before and after the build an end is immediate, as
    /// it would be nested, and the boot token is always cancelled before anything is disposed. Like a
    /// nested definition, the scope hangs off nothing but the lifetimes it is linked to, so a context on a
    /// lifetime nobody holds is collected with it.
    /// </remarks>
    internal sealed class ContextScope
    {
        private const int Describing = 0;
        private const int Building = 1;
        private const int Built = 2;
        private const int Ended = 3;

        private readonly object _gate = new object();
        private readonly Lifetime.Definition _definition;

        // Guarded by _gate. _boot is created by BeginBuild and never disposed: its token is handed to the
        // boot steps, which may keep it.
        private int _phase = Describing;
        private bool _cancelled;
        private CancellationTokenSource _boot;

        internal ContextScope(Lifetime outer)
        {
            // The vacuous intersection: a definition attached to nothing, so the scope is reachable only
            // through its links - from the lifetimes it was created on - exactly as a nested definition
            // would be. Nothing but End and FailBuild ever terminates it.
            _definition = Lifetime.Intersection();
            Link(outer);
        }

        internal Lifetime Lifetime => _definition.Lifetime;

        /// Ends this scope when <paramref name="outer"/> ends - at once if it already has. A scope linked to
        /// several lifetimes ends with the first of them.
        internal void Link(Lifetime outer)
        {
            var link = outer.DefineNested(nameof(Context) + " link");
            link.Lifetime.AddAction(End);

            // Ending first detaches the link, so a long-lived outer lifetime keeps no entry for a context
            // that is gone.
            _definition.Lifetime.AddAction(link.Terminate);
        }

        /// Starts the build. The token returned is the one the boot steps receive; it is already cancelled
        /// if the scope ended before the build began.
        internal CancellationToken BeginBuild()
        {
            var boot = new CancellationTokenSource();
            bool ended;
            lock (_gate)
            {
                _boot = boot;
                ended = _phase == Ended;
                if (!ended) _phase = Building;
            }

            if (ended) boot.Cancel(); // nothing can be registered on it yet
            return boot.Token;
        }

        /// The caller's token was cancelled: abandon the build, if one is running. After the build this does
        /// nothing - the caller's token is a way to abandon a build, not to end a live context.
        internal void CancelBuild()
        {
            CancellationTokenSource boot;
            lock (_gate)
            {
                if (_phase != Building) return;

                _cancelled = true;
                boot = _boot;
            }

            boot.Cancel();
        }

        /// The last thing a build that got through every step does: the context goes live - unless its scope
        /// ended in the meantime, in which case this throws and the build fails like any cancelled one.
        internal void CompleteBuild(CancellationToken token)
        {
            lock (_gate)
            {
                if (_phase == Building && !_cancelled)
                {
                    _phase = Built;
                    return;
                }
            }

            throw new OperationCanceledException(
                "The Context's scope ended while it was being built, so the build was abandoned.", token);
        }

        /// Ends the scope of a build that failed or was cancelled, now that no boot step is running.
        internal void FailBuild()
        {
            CancellationTokenSource boot;
            lock (_gate)
            {
                _phase = Ended;
                boot = _boot;
            }

            Teardown(boot);
        }

        /// <summary>
        /// Ends the scope: at once, or - while the context is being built - by cancelling the build, whose
        /// unwinding then ends the scope once the steps in flight have finished. Idempotent and safe on any
        /// thread.
        /// </summary>
        internal void End()
        {
            CancellationTokenSource boot;
            bool building;
            lock (_gate)
            {
                if (_phase == Ended) return;

                boot = _boot;
                building = _phase == Building;
                if (building) _cancelled = true;
                else _phase = Ended;
            }

            if (building)
            {
                boot.Cancel();
                return;
            }

            Teardown(boot);
        }

        private void Teardown(CancellationTokenSource boot)
        {
            // Cancel first: work a boot step left running on its token, and every callback on that token,
            // hears of the end before anything they use is disposed.
            Exception cancelFailure = null;
            if (boot != null)
            {
                try
                {
                    boot.Cancel();
                }
                catch (Exception exception)
                {
                    cancelFailure = exception;
                }
            }

            try
            {
                _definition.Terminate();
            }
            catch (Exception teardown)
            {
                if (cancelFailure == null) throw;

                throw new AggregateException(
                    "Ending the Context failed twice: a callback on its boot token threw when the token was " +
                    "cancelled, and then disposing its services threw too.", cancelFailure, teardown);
            }

            if (cancelFailure != null) ExceptionDispatchInfo.Capture(cancelFailure).Throw();
        }
    }
}
