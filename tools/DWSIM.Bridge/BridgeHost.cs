using DWSIM.Bridge.Methods;
using DWSIM.Bridge.Rpc;

namespace DWSIM.Bridge
{
    /// <summary>Wires the method classes into a registry; shared by the process and the tests.</summary>
    public static class BridgeHost
    {
        public static void Register(RpcRegistry registry, SessionStore sessions, INotifier notifier)
        {
            registry.Register(new EngineMethods(registry));
            registry.Register(new FlowsheetMethods(sessions, notifier));
            registry.Register(new GraphMethods(sessions));
            registry.Register(new ObjectMethods(sessions));
            registry.Register(new SetupMethods(sessions));
            registry.Register(new EditorMethods(sessions));
            registry.Register(new SolveMethods(sessions, notifier));
            registry.Register(new AnalysisMethods(sessions));
            registry.Register(new ToolMethods(sessions));
            registry.Register(new ReactionMethods(sessions));
            registry.Register(new StudyMethods(sessions, notifier));
            registry.Register(new ScriptMethods(sessions));
            registry.Register(new SpreadsheetMethods(sessions));
            registry.Register(new ScenarioMethods(sessions));
            registry.Register(new DynamicsMethods(sessions, notifier));
        }

        /// <summary>
        /// Loads the compound databases and property packages once, up front, so the first real
        /// request does not pay for it. Creating a flowsheet is what triggers the load.
        /// </summary>
        public static void WarmUp()
        {
            DWSIM.Automation.FluentAPI.Flowsheet.RegisterAssemblyResolver();
            DWSIM.Automation.FluentAPI.Flowsheet.Create("warm-up");
        }
    }
}
