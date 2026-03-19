using EasyWindowsTerminalControl;
using Microsoft.UI.Xaml.Automation.Peers;

namespace SelfContainedDeployment.Terminal
{
    internal class SafeEasyTerminalControl : EasyTerminalControl
    {
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new SafeEasyTerminalAutomationPeer(this);
        }

        private sealed class SafeEasyTerminalAutomationPeer : FrameworkElementAutomationPeer
        {
            public SafeEasyTerminalAutomationPeer(SafeEasyTerminalControl owner)
                : base(owner)
            {
            }

            protected override string GetClassNameCore()
            {
                return nameof(SafeEasyTerminalControl);
            }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return AutomationControlType.Pane;
            }
        }
    }
}
