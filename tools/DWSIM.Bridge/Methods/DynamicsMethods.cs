using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DWSIM.Automation.DynamicRunner;
using DWSIM.Automation.DynamicRunner.Setup;
using DWSIM.Bridge.Editors;
using DWSIM.Bridge.Rpc;
using DWSIM.DynamicsManager;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using Newtonsoft.Json.Linq;
using Converter = DWSIM.SharedClasses.SystemsOfUnits.Converter;
using DynEnums = DWSIM.Interfaces.Enums.Dynamics;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// Dynamic simulation, as the Avalonia dynamics manager, wizard and integrator panel run it:
    /// the mode switch, the readiness check with its suggested fixes, integrators with the
    /// variables they record, schedules, event sets, stored initial states, and integration runs.
    /// Times travel as seconds from the start of a run.
    /// </summary>
    public sealed class DynamicsMethods
    {
        private readonly SessionStore _sessions;
        private readonly INotifier _notifier;

        public DynamicsMethods(SessionStore sessions, INotifier notifier)
        {
            _sessions = sessions;
            _notifier = notifier;
        }

        private static readonly DateTime Zero = new DateTime();

        private static double Seconds(DateTime t) => (t - Zero).TotalSeconds;

        private static JToken Num(double v) => double.IsNaN(v) || double.IsInfinity(v) ? JValue.CreateNull() : new JValue(v);

        private static string Tag(IFlowsheet fs, string id)
            => fs.SimulationObjects.TryGetValue(id ?? "", out var o) ? o.GraphicObject?.Tag : null;

        private static string Label(IFlowsheet fs, string objectId, string property)
            => fs.SimulationObjects.TryGetValue(objectId ?? "", out var o) && !string.IsNullOrEmpty(property)
                ? o.GraphicObject?.Tag + " · " + VariablePicker.Caption(fs, o, property)
                : null;

        // ------------------------------------------------------------------ reading

        /// <summary>Everything the dynamics manager holds for this flowsheet.</summary>
        [RpcMethod("dynamics.get")]
        public JObject Get(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate) return State(session.Inner);
        }

        private static JObject State(IFlowsheet fs)
        {
            var m = fs.DynamicsManager;
            return new JObject
            {
                ["enabled"] = fs.DynamicMode,
                ["currentSchedule"] = string.IsNullOrEmpty(m.CurrentSchedule) ? null : m.CurrentSchedule,
                ["integrators"] = new JArray(m.IntegratorList.Values.Select(i => new JObject
                {
                    ["id"] = i.ID,
                    ["name"] = i.Description ?? "",
                    ["step"] = i.IntegrationStep.TotalSeconds,
                    ["duration"] = i.Duration.TotalSeconds,
                    ["method"] = i.IntegrationMethod.ToString(),
                    ["equilibriumEvery"] = i.CalculationRateEquilibrium,
                    ["pressureFlowEvery"] = i.CalculationRatePressureFlow,
                    ["controlEvery"] = i.CalculationRateControl,
                    ["variables"] = new JArray(i.MonitoredVariables.Select(v => new JObject
                    {
                        ["id"] = v.ID,
                        ["name"] = v.Description ?? "",
                        ["object"] = v.ObjectID,
                        ["property"] = v.PropertyID,
                        ["unit"] = string.IsNullOrEmpty(v.PropertyUnits) ? null : v.PropertyUnits,
                        ["label"] = Label(fs, v.ObjectID, v.PropertyID)
                    })),
                    ["samples"] = i.MonitoredVariableValues.Count
                })),
                ["schedules"] = new JArray(m.ScheduleList.Values.Select(s => new JObject
                {
                    ["id"] = s.ID,
                    ["name"] = s.Description ?? "",
                    ["integrator"] = string.IsNullOrEmpty(s.CurrentIntegrator) ? null : s.CurrentIntegrator,
                    ["useCurrentState"] = s.UseCurrentStateAsInitial,
                    ["initialState"] = string.IsNullOrEmpty(s.InitialFlowsheetStateID) ? null : s.InitialFlowsheetStateID,
                    ["resetContents"] = s.ResetContentsOfAllObjects,
                    ["eventSet"] = s.UsesEventList && !string.IsNullOrEmpty(s.CurrentEventList) ? s.CurrentEventList : null
                })),
                ["eventSets"] = new JArray(m.EventSetList.Values.Select(e => new JObject
                {
                    ["id"] = e.ID,
                    ["name"] = e.Description ?? "",
                    ["events"] = new JArray(e.Events.Values.OrderBy(x => x.TimeStamp).Select(x => new JObject
                    {
                        ["id"] = x.ID,
                        ["name"] = x.Description ?? "",
                        ["enabled"] = x.Enabled,
                        ["time"] = Seconds(x.TimeStamp),
                        ["kind"] = x.EventType.ToString(),
                        ["object"] = string.IsNullOrEmpty(x.SimulationObjectID) ? null : x.SimulationObjectID,
                        ["property"] = string.IsNullOrEmpty(x.SimulationObjectProperty) ? null : x.SimulationObjectProperty,
                        ["value"] = x.SimulationObjectPropertyValue ?? "",
                        ["unit"] = string.IsNullOrEmpty(x.SimulationObjectPropertyUnits) ? null : x.SimulationObjectPropertyUnits,
                        ["label"] = Label(fs, x.SimulationObjectID, x.SimulationObjectProperty),
                        ["script"] = string.IsNullOrEmpty(x.ScriptID) ? null : x.ScriptID,
                        ["transition"] = x.TransitionType.ToString()
                    }))
                })),
                ["states"] = new JArray(fs.StoredSolutions.Keys),
                ["scripts"] = new JArray(fs.Scripts.Values.Select(s => new JObject { ["value"] = s.ID, ["label"] = s.Title }))
            };
        }

        // ------------------------------------------------------------------ mode and readiness

        /// <summary>Turns dynamic mode on or off.</summary>
        [RpcMethod("dynamics.setMode")]
        public JObject SetMode(string flowsheetId, bool enabled)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                session.Record(enabled ? "Dynamic mode on" : "Dynamic mode off");
                session.Inner.DynamicMode = enabled;
                if (enabled) DynamicsSetupPlan.EnsureDynamicProperties(session.Inner);
                return State(session.Inner);
            }
        }

        private static string IssueKey(DynamicsIssue i) => i.Code + "|" + (i.ObjectId ?? "");

        /// <summary>
        /// What stands between the flowsheet and a dynamic run (the dynamics wizard's checks):
        /// holdup, hydraulics, boundary specifications, control and the integrator, each with a
        /// suggested value where one can be computed.
        /// </summary>
        [RpcMethod("dynamics.readiness")]
        public JArray Readiness(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var su = session.Inner.FlowsheetOptions.SelectedUnitSystem;
                return new JArray(DynamicsSetupPlan.Propose(session.Inner).Select(i =>
                {
                    string unit = null;
                    if (i.UnitType != UnitOfMeasure.none)
                    {
                        try { unit = su.GetCurrentUnits(i.UnitType); } catch { unit = null; }
                    }
                    JToken suggested = JValue.CreateNull();
                    if (i.SuggestedValue is double d)
                        suggested = Num(string.IsNullOrEmpty(unit) ? d : Converter.ConvertFromSI(unit, d));
                    else if (i.SuggestedValue != null)
                        suggested = Convert.ToString(i.SuggestedValue, CultureInfo.InvariantCulture);
                    return new JObject
                    {
                        ["key"] = IssueKey(i),
                        ["code"] = i.Code,
                        ["severity"] = i.Severity.ToString(),
                        ["category"] = i.Category.ToString(),
                        ["object"] = string.IsNullOrEmpty(i.ObjectId) ? null : i.ObjectId,
                        ["tag"] = string.IsNullOrEmpty(i.ObjectTag) ? null : i.ObjectTag,
                        ["message"] = i.Message,
                        ["fix"] = i.Fix,
                        ["canFix"] = i.CanAutoFix && i.Apply != null,
                        ["valueLabel"] = i.ValueLabel,
                        ["suggested"] = suggested,
                        ["unit"] = unit
                    };
                }));
            }
        }

        /// <summary>
        /// Applies the suggested fixes of the issues named by key (all that can be fixed when none
        /// are named), each with the value given for it in the flowsheet's unit, else its suggestion.
        /// One undo step. Returns the readiness that remains.
        /// </summary>
        [RpcMethod("dynamics.applyFixes")]
        public JObject ApplyFixes(string flowsheetId, string[] keys = null, JObject values = null)
        {
            var session = _sessions.Get(flowsheetId);
            var applied = new List<string>();
            var failed = new List<string>();
            lock (session.Gate)
            {
                var su = session.Inner.FlowsheetOptions.SelectedUnitSystem;
                var issues = DynamicsSetupPlan.Propose(session.Inner).Where(i => i.CanAutoFix && i.Apply != null).ToList();
                if (keys != null && keys.Length > 0) issues = issues.Where(i => keys.Contains(IssueKey(i))).ToList();
                if (issues.Count == 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Nothing to fix.");
                session.Record(issues.Count == 1 ? "Dynamics fix: " + issues[0].Code : $"Dynamics fixes ({issues.Count})");
                foreach (var issue in issues)
                {
                    object value = null;
                    var given = values?[IssueKey(issue)];
                    if (given != null && given.Type != JTokenType.Null)
                    {
                        if (given.Type == JTokenType.Float || given.Type == JTokenType.Integer)
                        {
                            var v = given.Value<double>();
                            string unit = null;
                            if (issue.UnitType != UnitOfMeasure.none) { try { unit = su.GetCurrentUnits(issue.UnitType); } catch { } }
                            value = string.IsNullOrEmpty(unit) ? v : Converter.ConvertToSI(unit, v);
                        }
                        else value = given.ToString();
                    }
                    try
                    {
                        if (DynamicsSetupPlan.Apply(issue, value)) applied.Add(IssueKey(issue));
                        else failed.Add(issue.ObjectTag + ": " + issue.Message);
                    }
                    catch (Exception ex) { failed.Add(issue.ObjectTag + ": " + (ex.InnerException ?? ex).Message); }
                }
            }
            return new JObject
            {
                ["applied"] = new JArray(applied),
                ["failed"] = new JArray(failed),
                ["remaining"] = Readiness(flowsheetId),
                ["state"] = Get(flowsheetId)
            };
        }

        // ------------------------------------------------------------------ editing

        private static void Unknown(string what, string id) => throw new RpcException(RpcErrorCodes.InvalidParams, "No " + what + " with id '" + id + "'.");

        /// <summary>Creates (no id) or replaces an integrator and the variables it records.</summary>
        [RpcMethod("dynamics.saveIntegrator")]
        public JObject SaveIntegrator(string flowsheetId, JObject integrator)
        {
            if (integrator == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the integrator.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var list = fs.DynamicsManager.IntegratorList;
                var id = integrator.Value<string>("id");
                IDynamicsIntegrator existing = null;
                if (!string.IsNullOrEmpty(id) && !list.TryGetValue(id, out existing)) Unknown("integrator", id);
                var step = integrator.Value<double?>("step") ?? 5;
                var duration = integrator.Value<double?>("duration") ?? 3600;
                if (!(step > 0)) throw new RpcException(RpcErrorCodes.InvalidParams, "The integration step must be greater than zero.");
                if (!(duration >= step)) throw new RpcException(RpcErrorCodes.InvalidParams, "The duration must be at least one step.");
                var name = (integrator.Value<string>("name") ?? "").Trim();
                session.Record((existing == null ? "Add integrator " : "Edit integrator ") + name);
                var i = existing ?? new Integrator { ID = Guid.NewGuid().ToString(), ShouldCalculateEquilibrium = true, ShouldCalculatePressureFlow = true, ShouldCalculateControl = true };
                i.Description = name.Length > 0 ? name : "Integrator " + (list.Count + (existing == null ? 1 : 0));
                i.IntegrationStep = TimeSpan.FromSeconds(step);
                i.Duration = TimeSpan.FromSeconds(duration);
                if (Enum.TryParse<DynEnums.IntegrationMethod>(integrator.Value<string>("method") ?? "", out var method)) i.IntegrationMethod = method;
                i.CalculationRateEquilibrium = Math.Max(1, integrator.Value<int?>("equilibriumEvery") ?? i.CalculationRateEquilibrium);
                i.CalculationRatePressureFlow = Math.Max(1, integrator.Value<int?>("pressureFlowEvery") ?? i.CalculationRatePressureFlow);
                i.CalculationRateControl = Math.Max(1, integrator.Value<int?>("controlEvery") ?? i.CalculationRateControl);
                if (integrator["variables"] is JArray vars)
                {
                    i.MonitoredVariables.Clear();
                    foreach (var v in vars)
                    {
                        var obj = v.Value<string>("object");
                        var prop = v.Value<string>("property");
                        if (!fs.SimulationObjects.TryGetValue(obj ?? "", out var o) || string.IsNullOrEmpty(prop)) continue;
                        var unit = v.Value<string>("unit");
                        if (string.IsNullOrEmpty(unit)) { try { unit = o.GetPropertyUnit(prop, fs.FlowsheetOptions.SelectedUnitSystem); } catch { unit = ""; } }
                        i.MonitoredVariables.Add(new MonitoredVariable
                        {
                            ID = string.IsNullOrEmpty(v.Value<string>("id")) ? Guid.NewGuid().ToString() : v.Value<string>("id"),
                            Description = string.IsNullOrWhiteSpace(v.Value<string>("name")) ? Label(fs, obj, prop) : v.Value<string>("name"),
                            ObjectID = obj,
                            PropertyID = prop,
                            PropertyUnits = unit ?? ""
                        });
                    }
                }
                if (existing == null) list.Add(i.ID, i);
                return State(fs);
            }
        }

        /// <summary>Creates (no id) or replaces a schedule: which integrator, which initial state, which events.</summary>
        [RpcMethod("dynamics.saveSchedule")]
        public JObject SaveSchedule(string flowsheetId, JObject schedule)
        {
            if (schedule == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the schedule.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var m = fs.DynamicsManager;
                var id = schedule.Value<string>("id");
                IDynamicsSchedule existing = null;
                if (!string.IsNullOrEmpty(id) && !m.ScheduleList.TryGetValue(id, out existing)) Unknown("schedule", id);
                var integrator = schedule.Value<string>("integrator");
                if (!string.IsNullOrEmpty(integrator) && !m.IntegratorList.ContainsKey(integrator)) Unknown("integrator", integrator);
                var eventSet = schedule.Value<string>("eventSet");
                if (!string.IsNullOrEmpty(eventSet) && !m.EventSetList.ContainsKey(eventSet)) Unknown("event set", eventSet);
                var state = schedule.Value<string>("initialState");
                var useCurrent = schedule.Value<bool?>("useCurrentState") ?? string.IsNullOrEmpty(state);
                if (!useCurrent && !fs.StoredSolutions.ContainsKey(state ?? ""))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Pick a stored state to start from, or start from the current one.");
                var name = (schedule.Value<string>("name") ?? "").Trim();
                session.Record((existing == null ? "Add schedule " : "Edit schedule ") + name);
                var s = existing ?? new Schedule { ID = Guid.NewGuid().ToString() };
                s.Description = name.Length > 0 ? name : "Schedule " + (m.ScheduleList.Count + (existing == null ? 1 : 0));
                s.CurrentIntegrator = integrator ?? m.IntegratorList.Keys.FirstOrDefault() ?? "";
                s.UseCurrentStateAsInitial = useCurrent;
                s.InitialFlowsheetStateID = useCurrent ? "" : state;
                s.ResetContentsOfAllObjects = schedule.Value<bool?>("resetContents") ?? false;
                s.UsesEventList = !string.IsNullOrEmpty(eventSet);
                s.CurrentEventList = eventSet ?? "";
                if (existing == null) m.ScheduleList.Add(s.ID, s);
                if (string.IsNullOrEmpty(m.CurrentSchedule) || !m.ScheduleList.ContainsKey(m.CurrentSchedule)) m.CurrentSchedule = s.ID;
                return State(fs);
            }
        }

        /// <summary>Creates (no id) or replaces an event set with its timed property changes and script runs.</summary>
        [RpcMethod("dynamics.saveEventSet")]
        public JObject SaveEventSet(string flowsheetId, JObject eventSet)
        {
            if (eventSet == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the event set.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var m = fs.DynamicsManager;
                var id = eventSet.Value<string>("id");
                IDynamicsEventSet existing = null;
                if (!string.IsNullOrEmpty(id) && !m.EventSetList.TryGetValue(id, out existing)) Unknown("event set", id);
                var events = new List<DynamicEvent>();
                foreach (var e in eventSet["events"] as JArray ?? new JArray())
                {
                    var kind = Enum.TryParse<DynEnums.DynamicsEventType>(e.Value<string>("kind") ?? "", out var k) ? k : DynEnums.DynamicsEventType.ChangeProperty;
                    var time = e.Value<double?>("time") ?? 0;
                    if (time < 0) throw new RpcException(RpcErrorCodes.InvalidParams, "An event cannot happen before the run starts.");
                    var obj = e.Value<string>("object");
                    var prop = e.Value<string>("property");
                    var script = e.Value<string>("script");
                    if (kind == DynEnums.DynamicsEventType.ChangeProperty && (!fs.SimulationObjects.ContainsKey(obj ?? "") || string.IsNullOrEmpty(prop)))
                        throw new RpcException(RpcErrorCodes.InvalidParams, "Pick the object and property each change event sets.");
                    if (kind == DynEnums.DynamicsEventType.RunScript && !fs.Scripts.ContainsKey(script ?? ""))
                        throw new RpcException(RpcErrorCodes.InvalidParams, "Pick the script each script event runs.");
                    events.Add(new DynamicEvent
                    {
                        ID = string.IsNullOrEmpty(e.Value<string>("id")) ? Guid.NewGuid().ToString() : e.Value<string>("id"),
                        Description = e.Value<string>("name") ?? "",
                        Enabled = e.Value<bool?>("enabled") ?? true,
                        TimeStamp = Zero.AddSeconds(time),
                        EventType = kind,
                        SimulationObjectID = obj ?? "",
                        SimulationObjectProperty = prop ?? "",
                        SimulationObjectPropertyValue = e.Value<string>("value") ?? "",
                        SimulationObjectPropertyUnits = e.Value<string>("unit") ?? "",
                        ScriptID = script ?? "",
                        TransitionType = Enum.TryParse<DynEnums.DynamicsEventTransitionType>(e.Value<string>("transition") ?? "", out var t) ? t : DynEnums.DynamicsEventTransitionType.StepChange,
                        TransitionReference = DynEnums.DynamicsEventTransitionReferenceType.PreviousEvent
                    });
                }
                var name = (eventSet.Value<string>("name") ?? "").Trim();
                session.Record((existing == null ? "Add event set " : "Edit event set ") + name);
                var set = existing ?? new EventSet { ID = Guid.NewGuid().ToString() };
                set.Description = name.Length > 0 ? name : "Events " + (m.EventSetList.Count + (existing == null ? 1 : 0));
                set.Events.Clear();
                foreach (var ev in events) set.Events[ev.ID] = ev;
                if (existing == null) m.EventSetList.Add(set.ID, set);
                return State(fs);
            }
        }

        /// <summary>Removes an integrator, schedule or event set; schedules that used it let go of it.</summary>
        [RpcMethod("dynamics.delete")]
        public JObject Delete(string flowsheetId, string kind, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var m = fs.DynamicsManager;
                switch (kind)
                {
                    case "integrator":
                        if (!m.IntegratorList.ContainsKey(id ?? "")) Unknown("integrator", id);
                        session.Record("Delete integrator " + m.IntegratorList[id].Description);
                        m.IntegratorList.Remove(id);
                        foreach (var s in m.ScheduleList.Values.Where(s => s.CurrentIntegrator == id)) s.CurrentIntegrator = "";
                        break;
                    case "schedule":
                        if (!m.ScheduleList.ContainsKey(id ?? "")) Unknown("schedule", id);
                        session.Record("Delete schedule " + m.ScheduleList[id].Description);
                        m.ScheduleList.Remove(id);
                        if (m.CurrentSchedule == id) m.CurrentSchedule = m.ScheduleList.Keys.FirstOrDefault() ?? "";
                        break;
                    case "eventSet":
                        if (!m.EventSetList.ContainsKey(id ?? "")) Unknown("event set", id);
                        session.Record("Delete event set " + m.EventSetList[id].Description);
                        m.EventSetList.Remove(id);
                        foreach (var s in m.ScheduleList.Values.Where(s => s.CurrentEventList == id)) { s.CurrentEventList = ""; s.UsesEventList = false; }
                        break;
                    default:
                        throw new RpcException(RpcErrorCodes.InvalidParams, "Delete an integrator, schedule or eventSet.");
                }
                return State(fs);
            }
        }

        /// <summary>Stores the flowsheet's process data as it is now, as a state a schedule can start from.</summary>
        [RpcMethod("dynamics.storeState")]
        public JObject StoreState(string flowsheetId, string name)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var key = (name ?? "").Trim();
                if (key.Length == 0) key = "State " + (fs.StoredSolutions.Count + 1);
                session.Record("Store state " + key);
                fs.StoredSolutions[key] = fs.GetProcessData();
                return State(fs);
            }
        }

        [RpcMethod("dynamics.deleteState")]
        public JObject DeleteState(string flowsheetId, string name)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                if (!fs.StoredSolutions.ContainsKey(name ?? "")) Unknown("state", name);
                session.Record("Delete state " + name);
                fs.StoredSolutions.Remove(name);
                foreach (var s in fs.DynamicsManager.ScheduleList.Values.Where(s => s.InitialFlowsheetStateID == name))
                {
                    s.InitialFlowsheetStateID = "";
                    s.UseCurrentStateAsInitial = true;
                }
                return State(fs);
            }
        }

        // ------------------------------------------------------------------ running

        /// <summary>
        /// Integrates a schedule (the current one when none is named) through the engine's
        /// IntegratorRunner, sending dynamics/progress as it goes, and returns the recorded
        /// variables as time series. Cancelling keeps the samples taken so far. The run moves
        /// the flowsheet, so it is one undo step.
        /// </summary>
        [RpcMethod("dynamics.run")]
        public JObject Run(string flowsheetId, string schedule = null, CancellationToken cancellation = default)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var m = fs.DynamicsManager;
                if (!fs.DynamicMode) throw new RpcException(RpcErrorCodes.InvalidParams, "Turn dynamic mode on first.");
                var sid = string.IsNullOrEmpty(schedule) ? m.CurrentSchedule : schedule;
                if (string.IsNullOrEmpty(sid) || !m.ScheduleList.TryGetValue(sid, out var sch))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Pick a schedule to run.");
                if (!m.IntegratorList.TryGetValue(sch.CurrentIntegrator ?? "", out var integrator))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "The schedule has no integrator; give it one.");
                m.CurrentSchedule = sid;
                session.Record("Run " + sch.Description);
                var lastNotice = DateTime.MinValue;
                var options = new IntegratorRunOptions
                {
                    Schedule = sid,
                    EnableHistorian = m.EnableHistorian,
                    CancellationToken = cancellation,
                    AbortRequested = () => cancellation.IsCancellationRequested,
                    OnProgress = p =>
                    {
                        // At most a few notices a second: a fast integrator steps far quicker than a view redraws.
                        if ((DateTime.UtcNow - lastNotice).TotalMilliseconds < 200 && p.CurrentSeconds < p.TotalSeconds) return;
                        lastNotice = DateTime.UtcNow;
                        _notifier.Notify("dynamics/progress", new JObject
                        {
                            ["flowsheetId"] = flowsheetId,
                            ["seconds"] = p.CurrentSeconds,
                            ["total"] = Num(p.TotalSeconds),
                            ["step"] = p.Step,
                            ["text"] = p.Status
                        });
                    }
                };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                IntegratorRunResult result = null;
                var errors = new List<string>();
                try { result = new IntegratorRunner(fs).Run(options); }
                catch (Exception ex) { errors.Add(Message(ex)); }
                if (result != null) errors.AddRange(result.Exceptions.Take(5).Select(Message));
                var json = Results(fs, integrator);
                json["cancelled"] = cancellation.IsCancellationRequested;
                json["errors"] = new JArray(errors.Distinct());
                json["steps"] = result?.Steps ?? 0;
                json["durationMs"] = clock.ElapsedMilliseconds;
                return json;
            }
        }

        private static string Message(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        /// <summary>The variables an integrator recorded on its last run, as time series.</summary>
        [RpcMethod("dynamics.results")]
        public JObject ResultsOf(string flowsheetId, string integrator)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                if (!session.Inner.DynamicsManager.IntegratorList.TryGetValue(integrator ?? "", out var i)) Unknown("integrator", integrator);
                return Results(session.Inner, i);
            }
        }

        private static JObject Results(IFlowsheet fs, IDynamicsIntegrator integrator)
        {
            var samples = integrator.MonitoredVariableValues.OrderBy(p => p.Key).ToList();
            var times = samples.Select(p => Seconds(new DateTime(p.Key))).ToList();
            var series = integrator.MonitoredVariables.Select(v =>
            {
                var values = samples.Select(s =>
                {
                    var hit = s.Value.FirstOrDefault(x => x.ID == v.ID);
                    return hit != null && double.TryParse(hit.PropertyValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
                }).ToList();
                return (v, values);
            }).ToList();
            var charts = new JArray();
            foreach (var group in series.GroupBy(s => s.v.PropertyUnits ?? ""))
            {
                var items = group.ToList();
                string Name(IDynamicsMonitoredVariable v) => string.IsNullOrWhiteSpace(v.Description) ? Label(fs, v.ObjectID, v.PropertyID) ?? v.PropertyID : v.Description;
                charts.Add(new JObject
                {
                    ["title"] = string.Join(", ", items.Select(s => Name(s.v))),
                    ["x"] = new JObject { ["label"] = "Time", ["unit"] = "s" },
                    ["y"] = new JObject { ["label"] = items.Count == 1 ? Name(items[0].v) : "Value", ["unit"] = group.Key.Length == 0 ? null : group.Key },
                    ["series"] = new JArray(items.Select(s =>
                    {
                        var pairs = times.Select((t, k) => (t, y: s.values[k])).Where(p => !double.IsNaN(p.y) && !double.IsInfinity(p.y)).ToList();
                        return new JObject { ["name"] = Name(s.v), ["kind"] = "line", ["x"] = new JArray(pairs.Select(p => p.t)), ["y"] = new JArray(pairs.Select(p => p.y)) };
                    }))
                });
            }
            return new JObject
            {
                ["integrator"] = integrator.ID,
                ["samples"] = samples.Count,
                ["charts"] = charts,
                ["table"] = new JObject
                {
                    ["columns"] = new JArray(new[] { "Time (s)" }.Concat(series.Select(s =>
                        (string.IsNullOrWhiteSpace(s.v.Description) ? s.v.PropertyID : s.v.Description) + (string.IsNullOrEmpty(s.v.PropertyUnits) ? "" : " (" + s.v.PropertyUnits + ")")))),
                    ["rows"] = new JArray(times.Select((t, k) => new JArray(new[] { Num(t) }.Concat(series.Select(s => Num(s.values[k]))))))
                }
            };
        }
    }
}
