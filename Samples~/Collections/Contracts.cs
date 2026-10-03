using System;
using System.Collections.Generic;
using System.Text;

namespace OpenUGD.Samples.Collections
{
    /// <summary>Something that wants a call every frame. Modules contribute them; <see cref="TickLoop"/> calls them.</summary>
    public interface ITickable
    {
        void Tick(float deltaTime);
    }

    /// <summary>One section of the debug menu. Modules contribute them; <see cref="DebugMenu"/> shows them.</summary>
    public interface IDebugPanel
    {
        string Title { get; }

        string Describe();
    }

    /// <summary>A cheat. Only the development build installs any.</summary>
    public interface ICheat
    {
        string Name { get; }
    }

    /// <summary>
    /// Calls every <see cref="ITickable"/> registered in its context, in registration order. It knows no module:
    /// the list is everything any registration contributed with <c>AsElementOf&lt;ITickable&gt;()</c>.
    /// </summary>
    public sealed class TickLoop
    {
        private readonly IReadOnlyList<ITickable> _tickables;

        public TickLoop(IReadOnlyList<ITickable> tickables) => _tickables = tickables;

        public int Count => _tickables.Count;

        public void Tick(float deltaTime)
        {
            for (var i = 0; i < _tickables.Count; i++) _tickables[i].Tick(deltaTime);
        }
    }

    /// <summary>
    /// Shows every <see cref="IDebugPanel"/> and lists every <see cref="ICheat"/>. Without a cheats module the cheat
    /// list is empty rather than missing, so the menu builds either way.
    /// </summary>
    public sealed class DebugMenu
    {
        private readonly IReadOnlyList<IDebugPanel> _panels;
        private readonly IReadOnlyList<ICheat> _cheats;

        public DebugMenu(IReadOnlyList<IDebugPanel> panels, IReadOnlyList<ICheat> cheats)
        {
            _panels = panels;
            _cheats = cheats;
        }

        public string Render()
        {
            var text = new StringBuilder("Debug menu:");
            for (var i = 0; i < _panels.Count; i++)
            {
                text.Append("\n  [").Append(_panels[i].Title).Append("] ").Append(_panels[i].Describe());
            }

            text.Append("\n  cheats: ");
            if (_cheats.Count == 0) text.Append("none");
            for (var i = 0; i < _cheats.Count; i++) text.Append(i == 0 ? "" : ", ").Append(_cheats[i].Name);
            return text.ToString();
        }
    }
}
