using System;

namespace SelfContainedDeployment
{
    public sealed class TerminalTabEventArgs : EventArgs
    {
        public TerminalTabEventArgs(TerminalTabItem tab)
        {
            Tab = tab;
        }

        public TerminalTabItem Tab { get; }
    }
}
