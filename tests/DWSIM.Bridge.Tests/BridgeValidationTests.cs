using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>
    /// The thermodynamics validation cases (tests/DWSIM.Validation.Tests/Thermodynamics), run
    /// through the engine sidecar as the desktop app runs it.
    /// </summary>
    /// <remarks>
    /// Each case sets the simulation up with the bridge's own methods (flowsheet.new,
    /// flowsheet.addCompounds, flowsheet.addPropertyPackage, graph.add, stream.specify), solves it
    /// with solve.run and reads the answer from stream.get, in a separate dwsim-bridge process. The
    /// references, tolerances and tolerance rules are those of the original case, whose name each
    /// test carries: a tolerance is relative to the reference unless the reference is zero.
    /// </remarks>
    [NonParallelizable]
    public class BridgeValidationTests
    {
        private BridgeProcess _bridge;

        [OneTimeSetUp]
        public void Start() => _bridge = new BridgeProcess();

        [OneTimeTearDown]
        public void Stop() => _bridge?.Dispose();

        // ------------------------------------------------------------------ the cases

        [Test]
        public void T01_SteamTables_Density_Water25C()
        {
            var s = Feed("Steam Tables (IAPWS-IF97)", new[] { "Water" }, temperature: 298.15, pressure: 101325.0, massFlow: 1.0, massFlows: new { Water = 1.0 });
            new Checks("Steam Tables - liquid H2O @ 25 °C, 1 atm")
                .Row("Density ρ (NIST 997.05)", 997.05, s.Density(0), 0.005)
                .Assert();
        }

        [Test]
        public void T02_SteamTables_SaturationT_1atm()
        {
            var s = Feed("Steam Tables (IAPWS-IF97)", new[] { "Water" }, pressure: 101325.0, vaporFraction: 0.5, massFlow: 1.0, massFlows: new { Water = 1.0 });
            new Checks("Steam Tables - water T_sat @ 1 atm")
                .Row("T_sat (NIST 373.124 K)", 373.124, s.T, 0.005)
                .Assert();
        }

        [TestCase("T03 NRTL", "NRTL", 0.01, 0.02)]
        [TestCase("T08 UNIQUAC", "UNIQUAC", 0.02, 0.05)]
        [TestCase("T10 UNIFAC", "UNIFAC", 0.03, 0.10)]
        public void T03_T08_T10_EthanolWaterAzeotrope(string name, string package, double tolT, double tolY)
        {
            var s = Feed(package, new[] { "Water", "Ethanol" }, pressure: 101325.0, vaporFraction: 0.5, molarFlow: 1.0,
                molarFlows: new Dictionary<string, double> { ["Ethanol"] = 0.894, ["Water"] = 0.106 });
            new Checks(name + " - ethanol/water azeotrope @ 1 atm, z_EtOH = 0.894")
                .Row("T_az (Gmehling 351.45 K)", 351.45, s.T, tolT)
                .Row("y_EtOH ≈ x_EtOH", s.X(1, "Ethanol"), s.X(2, "Ethanol"), tolY)
                .Assert();
        }

        [Test]
        public void T04_PR_MethaneIdealGasLimit()
        {
            var s = Feed("Peng-Robinson (PR)", new[] { "Methane" }, temperature: 500.0, pressure: 1e5, molarFlow: 1.0, molarFlows: new { Methane = 1.0 });
            new Checks("PR EOS - CH4 @ 500 K, 1 bar (ideal gas limit)")
                .Row("ρ vs ideal gas (PM/RT)", 1e5 * 0.01604 / (8.314 * 500.0), s.Density(0), 0.005)
                .Row("Vapor fraction (expected =1)", 1.0, s.Fraction(2), 0.001)
                .Assert();
        }

        [TestCase("T05 SRK - propane P_sat @ 298.15 K (NIST 9.49 bar)", "Soave-Redlich-Kwong (SRK)", "Propane", 298.15, 9.49e5)]
        [TestCase("T14 PRSV2-M - methanol P_sat @ 337.7 K", "Peng-Robinson-Stryjek-Vera 2 (PRSV2-M)", "Methanol", 337.7, 101325.0)]
        [TestCase("T19 Mod UNIFAC (NIST) - toluene P_sat @ 383.78 K", "Modified UNIFAC (NIST)", "Toluene", 383.78, 101325.0)]
        public void T05_T14_T19_VaporPressure(string name, string package, string compound, double temperature, double reference)
        {
            var s = Feed(package, new[] { compound }, temperature: temperature, vaporFraction: 0.5, molarFlow: 1.0,
                molarFlows: new Dictionary<string, double> { [compound] = 1.0 });
            new Checks(name).Row("P_sat", reference, s.P, 0.05).Assert();
        }

        [Test]
        public void T06_PR78_NHeptaneDensity()
        {
            var s = Feed("Peng-Robinson 1978 (PR78)", new[] { "N-heptane" }, temperature: 298.15, pressure: 101325.0, massFlow: 1.0,
                massFlows: new Dictionary<string, double> { ["N-heptane"] = 1.0 });
            new Checks("PR78 - liquid n-heptane @ 25 °C, 1 atm")
                .Row("ρ_liq (NIST 679.5)", 679.5, s.Density(0), 0.05)
                .Assert();
        }

        [Test]
        public void T07_LKP_MethaneCompressibility()
        {
            var s = Feed("Lee-Kesler-Plöcker", new[] { "Methane" }, temperature: 300.0, pressure: 1e5, molarFlow: 1.0, molarFlows: new { Methane = 1.0 });
            new Checks("LKP - CH4 gas @ 300 K, 1 bar (Z≈1)")
                .Row("ρ vs ideal gas", 1e5 * 0.01604 / (8.314 * 300.0), s.Density(0), 0.01)
                .Assert();
        }

        [TestCase("T09 Wilson - equimolar MeOH/H2O", "Wilson", "Methanol", 337.7)]
        [TestCase("T11 Mod UNIFAC (Dortmund) - equimolar Acetone/H2O", "Modified UNIFAC (Dortmund)", "Acetone", 329.2)]
        public void T09_T11_BubblePointOfAqueousSolvent(string name, string package, string solvent, double solventBoilingPoint)
        {
            var s = Feed(package, new[] { "Water", solvent }, pressure: 101325.0, vaporFraction: 0.001, molarFlow: 1.0,
                molarFlows: new Dictionary<string, double> { [solvent] = 0.5, ["Water"] = 0.5 });
            new Checks(name + " @ 1 atm, bubble point")
                .InRange("T_bubble between pure b.p.", solventBoilingPoint, 373.15, s.T)
                .InRange($"y_{solvent} > x_{solvent} (more volatile)", 0.50, 1.0, s.X(2, solvent))
                .Assert();
        }

        [Test]
        public void T12_Raoult_BenzeneToluene()
        {
            var s = Feed("Raoult's Law", new[] { "Benzene", "Toluene" }, pressure: 101325.0, vaporFraction: 0.001, molarFlow: 1.0,
                molarFlows: new { Benzene = 0.5, Toluene = 0.5 });
            new Checks("Raoult - equimolar Benzene/Toluene @ 1 atm")
                .Row("T_bubble (Smith VN ≈ 365 K)", 365.0, s.T, 0.02)
                .Row("y_Benzene (≈ 0.71)", 0.71, s.X(2, "Benzene"), 0.05)
                .Assert();
        }

        [Test]
        public void T13_Seawater_Density()
        {
            var s = Feed("Seawater IAPWS-08", new[] { "Water", "Salt" }, temperature: 298.15, pressure: 101325.0, massFlow: 1.0,
                massFlows: new { Salt = 0.035, Water = 0.965 });
            new Checks("Seawater (IAPWS-08) - S=35 g/kg, 25 °C, 1 atm")
                .Row("ρ (IAPWS-08 ≈ 1023.4)", 1023.4, s.Density(0), 0.01)
                .Assert();
        }

        [TestCase("T15 Grayson-Streed - C3/C4 50/50 @ 300 K, 5 bar", "Grayson-Streed", 300.0, 5e5, true)]
        [TestCase("T18 Chao-Seader - C3/n-C4 50/50 @ 320 K, 8 bar", "Chao-Seader", 320.0, 8e5, false)]
        public void T15_T18_PropaneButaneFlash(string name, string package, double temperature, double pressure, bool checkLiquid)
        {
            var s = Feed(package, new[] { "Propane", "N-butane" }, temperature: temperature, pressure: pressure, molarFlow: 1.0,
                molarFlows: new Dictionary<string, double> { ["Propane"] = 0.5, ["N-butane"] = 0.5 });
            var checks = new Checks(name).InRange("Vapor frac in (0,1) - two phases", 0.01, 0.99, s.Fraction(2));
            if (checkLiquid) checks.InRange("x_C4 > 0.5 (heavy liquid)", 0.5, 1.0, s.X(1, "N-butane"));
            checks.InRange("y_C3 > 0.5 (light vapor)", 0.5, 1.0, s.X(2, "Propane")).Assert();
        }

        [Test]
        public void T16_UNIFAC_LL_NHexaneWater()
        {
            var s = Feed("UNIFAC-LL", new[] { "Water", "N-hexane" }, temperature: 298.15, pressure: 101325.0, molarFlow: 1.0,
                molarFlows: new Dictionary<string, double> { ["Water"] = 0.5, ["N-hexane"] = 0.5 });
            new Checks("UNIFAC-LL - H2O/n-hexane 50/50 @ 25 °C (smoke)")
                .Row("T preserved", 298.15, s.T, 0.001)
                .InRange("Mass enthalpy finite", -1e6, 1e6, s.Enthalpy(0))
                .Assert();
        }

        [Test]
        public void T17_PR_vs_SRK_NaturalGas()
        {
            double DensityWith(string package) => Feed(package, new[] { "Methane", "Ethane", "Propane" }, temperature: 250.0, pressure: 50e5, molarFlow: 1.0,
                molarFlows: new { Methane = 0.90, Ethane = 0.07, Propane = 0.03 }).Density(0);
            var rhoPR = DensityWith("Peng-Robinson (PR)");
            var rhoSRK = DensityWith("Soave-Redlich-Kwong (SRK)");
            new Checks("PR vs SRK - natural gas (90/7/3) @ 250 K, 50 bar")
                .Row("ρ_SRK ≈ ρ_PR (± 5 %)", rhoPR, rhoSRK, 0.05)
                .InRange("ρ_PR > 0", 1.0, 1000.0, rhoPR)
                .Assert();
        }

        [Test]
        public void T20_PR_NaturalGas_VLE5Comp()
        {
            var s = Feed("Peng-Robinson (PR)", new[] { "Methane", "Ethane", "Propane", "N-butane", "N-pentane" }, temperature: 240.0, pressure: 40e5, molarFlow: 1.0,
                molarFlows: new Dictionary<string, double> { ["Methane"] = 0.75, ["Ethane"] = 0.10, ["Propane"] = 0.07, ["N-butane"] = 0.05, ["N-pentane"] = 0.03 });
            double vf = s.Fraction(2), lf = s.Fraction(3), xC5 = s.X(3, "N-pentane");
            new Checks("PR natural gas 5-comp VLE @ 240 K, 40 bar")
                .Row("Phase fractions sum to 1", 1.0, vf + lf, 0.002)
                .InRange("Vapour fraction", 0.71, 0.75, vf)
                .InRange("Vapour is methane-rich (y_C1)", 0.89, 0.91, s.X(2, "Methane"))
                .InRange("Liquid holds the C5 (x_C5)", 0.10, 0.12, xC5)
                .InRange("C5 K-value well below 1 (heavy stays liquid)", 0.0, 0.05, s.X(2, "N-pentane") / xC5)
                .Assert();
        }

        [Test]
        public void T21_SRK_AcidGas_VLE4Comp()
        {
            var s = Feed("Soave-Redlich-Kwong (SRK)", new[] { "Carbon dioxide", "Methane", "Ethane", "Propane" }, temperature: 250.0, pressure: 40e5, molarFlow: 1.0,
                molarFlows: new Dictionary<string, double> { ["Carbon dioxide"] = 0.30, ["Methane"] = 0.40, ["Ethane"] = 0.20, ["Propane"] = 0.10 });
            double vf = s.Fraction(2), lf = s.Fraction(3);
            new Checks("SRK CO2/C1/C2/C3 VLE @ 250 K, 40 bar")
                .Row("Phase fractions sum to 1", 1.0, vf + lf, 0.002)
                .InRange("Vapour fraction", 0.40, 0.46, vf)
                .InRange("Vapour is methane-rich (y_C1)", 0.60, 0.65, s.X(2, "Methane"))
                .InRange("Propane concentrates in liquid (x_C3)", 0.14, 0.18, s.X(3, "Propane"))
                .Assert();
        }

        [TestCase("T22 PR water/methane/n-hexane/n-octane VLLE @ 350 K, 3 bar", 350.0, 3e5,
            new[] { "Water", "Methane", "N-hexane", "N-octane" }, new[] { 0.40, 0.20, 0.20, 0.20 }, 0.28, 0.34, 0.28, 0.38, 0.01)]
        [TestCase("T23 PR water/n-pentane/n-octane VLLE @ 370 K, 2 bar", 370.0, 2e5,
            new[] { "Water", "N-pentane", "N-octane" }, new[] { 0.50, 0.25, 0.25 }, 0.55, 0.63, 0.13, 0.20, 0.02)]
        public void T22_T23_PR_WaterHydrocarbon_VLLE(string name, double temperature, double pressure, string[] compounds, double[] flows,
            double vfLow, double vfHigh, double minLiqLow, double minLiqHigh, double orgWaterHigh)
        {
            var s = Feed("Peng-Robinson (PR)", compounds, temperature: temperature, pressure: pressure, molarFlow: 1.0, molarFlows: Zip(compounds, flows));
            double vf = s.Fraction(2), l1 = s.Fraction(3), l2 = s.Fraction(4);
            double w1 = s.X(3, "Water"), w2 = s.X(4, "Water");
            new Checks(name)
                .Row("Phase fractions sum to 1", 1.0, vf + l1 + l2, 0.005)
                .InRange("A vapour phase is present", vfLow, vfHigh, vf)
                .InRange("Two liquid phases are present (smaller > 1%)", minLiqLow, minLiqHigh, Math.Min(l1, l2))
                .InRange("The aqueous liquid is nearly pure water", 0.99, 1.0, Math.Max(w1, w2))
                .InRange("The organic liquid rejects water", 0.0, orgWaterHigh, Math.Min(w1, w2))
                .Assert();
        }

        [TestCase("T24 NRTL water/benzene/acetone LLE @ 298 K, 2 bar", "NRTL", "Benzene", 0.45, 0.55, 0.02)]
        [TestCase("T25 UNIQUAC water/toluene/acetone LLE @ 298 K, 2 bar", "UNIQUAC", "Toluene", 0.42, 0.55, 0.03)]
        public void T24_T25_WaterAromaticAcetone_LLE(string name, string package, string aromatic, double minLow, double minHigh, double orgWaterHigh)
        {
            var s = Feed(package, new[] { "Water", aromatic, "Acetone" }, temperature: 298.15, pressure: 2e5, molarFlow: 1.0,
                molarFlows: new Dictionary<string, double> { ["Water"] = 0.50, [aromatic] = 0.40, ["Acetone"] = 0.10 });
            double vf = s.Fraction(2), l1 = s.Fraction(3), l2 = s.Fraction(4);
            double w1 = s.X(3, "Water"), w2 = s.X(4, "Water");
            new Checks(name)
                .Row("No vapour phase", 0.0, vf, 0.001)
                .Row("Two liquid phases sum to 1", 1.0, l1 + l2, 0.005)
                .InRange("Both liquid phases present", minLow, minHigh, Math.Min(l1, l2))
                .InRange("Aqueous phase is water-rich", 0.94, 1.0, Math.Max(w1, w2))
                .InRange("Organic phase rejects water", 0.0, orgWaterHigh, Math.Min(w1, w2))
                .Assert();
        }

        [Test]
        public void T26_NRTL_XyleneCrystallization_SLE()
        {
            var s = Feed("NRTL", new[] { "P-xylene", "M-xylene", "O-xylene" }, temperature: 255.0, pressure: 101325.0, molarFlow: 1.0,
                molarFlows: new Dictionary<string, double> { ["P-xylene"] = 0.50, ["M-xylene"] = 0.30, ["O-xylene"] = 0.20 },
                flashSettings: new JObject { ["HandleSolidsInDefaultEqCalcMode"] = true });
            double lf = s.Fraction(3), sf = s.Fraction(7);
            new Checks("NRTL p/m/o-xylene SLE @ 255 K, 1 atm")
                .Row("Liquid + solid fractions sum to 1", 1.0, lf + sf, 0.005)
                .InRange("A solid phase forms", 0.12, 0.17, sf)
                .InRange("The solid is essentially pure p-xylene", 0.98, 1.0, s.X(7, "P-xylene"))
                .InRange("The liquid is depleted in p-xylene (< feed 0.5)", 0.39, 0.44, s.X(3, "P-xylene"))
                .Assert();
        }

        // The liquid-liquid split cases share LiquidLiquidSplitCase in the original suite.
        [TestCase("T27 NRTL water/1-butanol", "NRTL", "1-butanol", 0.75, 0.25, 298.15, 200000.0, 0.3, 0.975, 0.998, 0.45, 0.65)]
        [TestCase("T28 UNIQUAC water/1-butanol", "UNIQUAC", "1-butanol", 0.75, 0.25, 298.15, 200000.0, 0.3, 0.975, 0.995, 0.33, 0.56)]
        [TestCase("T29 UNIFAC water/1-butanol", "UNIFAC", "1-butanol", 0.75, 0.25, 298.15, 200000.0, 0.2, 0.96, 0.995, 0.35, 0.65)]
        [TestCase("T30 NRTL water/ethyl acetate", "NRTL", "Ethyl acetate", 0.5, 0.5, 298.15, 200000.0, 0.3, 0.975, 0.995, 0.08, 0.22,
            Ignore = "Ignored in the original suite: the NRTL water/ethyl acetate parameters put this feed outside the two-liquid region")]
        [TestCase("T31 NRTL water/MEK", "NRTL", "Methyl ethyl ketone", 0.6, 0.4, 298.15, 200000.0, 0.2, 0.88, 0.95, 0.25, 0.45,
            Ignore = "Ignored in the original suite: two water/2-butanone NRTL parameter sets in opposite orientations")]
        [TestCase("T32 UNIQUAC water/n-butyl acetate", "UNIQUAC", "N-butyl acetate", 0.5, 0.5, 298.15, 200000.0, 0.3, 0.995, 1.0, 0.03, 0.2,
            Ignore = "Ignored in the original suite: water/ester feeds go to the vapour-liquid flash")]
        [TestCase("T34 PR water/n-decane", "Peng-Robinson (PR)", "N-decane", 0.5, 0.5, 373.15, 1000000.0, 0.4, 0.99, 1.0, 0.0, 0.05)]
        public void T27_T34_LiquidLiquidSplit(string name, string package, string organic, double water, double organicFlow,
            double temperature, double pressure, double minPhase, double aqLow, double aqHigh, double orgLow, double orgHigh)
        {
            var s = Feed(package, new[] { "Water", organic }, temperature: temperature, pressure: pressure, molarFlow: water + organicFlow,
                molarFlows: new Dictionary<string, double> { ["Water"] = water, [organic] = organicFlow });
            double vf = s.Fraction(2), l1 = s.Fraction(3), l2 = s.Fraction(4);
            double w1 = s.X(3, "Water"), w2 = s.X(4, "Water");
            new Checks(name)
                .Row("No vapour phase", 0.0, vf, 0.001)
                .Row("Two liquid phases sum to 1", 1.0, l1 + l2, 0.005)
                .InRange("Both liquid phases present", minPhase, 1.0 - minPhase, Math.Min(l1, l2))
                .InRange("Water in the aqueous liquid", aqLow, aqHigh, Math.Max(w1, w2))
                .InRange("Water in the organic liquid", orgLow, orgHigh, Math.Min(w1, w2))
                .Assert();
        }

        // ------------------------------------------------------------------ unit operations
        // tests/DWSIM.Validation.Tests/UnitOpsBasic U01-U08 and UnitOpsAdvanced A04, built and
        // specified through the editors (object.setMode, object.setField) as the app does.

        [Test]
        public void U01_Heater_WaterDuty()
        {
            using var sim = new Sim(_bridge, "Steam Tables (IAPWS-IF97)", "Water");
            var feed = sim.Feed("in", 298.15, 101325.0, massFlow: 1.0);
            var product = sim.Add("MaterialStream", "out");
            var heater = sim.Add("Heater", "HX-1");
            sim.Chain(feed, heater, product);
            sim.Energy(heater, "in-1");
            sim.Mode(heater, "OutletTemperature");
            sim.Set(heater, "Outlet Temperature", 353.15, "K");
            sim.Set(heater, "Pressure Drop", 0.0, "Pa");
            sim.Set(heater, "Efficiency", 100.0);
            sim.Solve();
            new Checks("Heater - H2O 1 kg/s, 25 → 80 °C")
                .Row("T_out", 353.15, sim.Stream(product).Value<double>("temperature"), 0.001)
                .InRange("Heat duty (NIST 230±5 kW)", 225.0, 235.0, sim.Result(heater, "Heat Added", "kW"))
                .Assert();
        }

        [Test]
        public void U02_Cooler_WaterDuty()
        {
            using var sim = new Sim(_bridge, "Steam Tables (IAPWS-IF97)", "Water");
            var feed = sim.Feed("in", 353.15, 101325.0, massFlow: 1.0);
            var product = sim.Add("MaterialStream", "out");
            var cooler = sim.Add("Cooler", "CL-1");
            sim.Chain(feed, cooler, product);
            sim.Energy(cooler, "energy", outgoing: true);
            sim.Mode(cooler, "OutletTemperature");
            sim.Set(cooler, "Outlet Temperature", 298.15, "K");
            sim.Set(cooler, "Pressure Drop", 0.0, "Pa");
            sim.Set(cooler, "Efficiency", 100.0);
            sim.Solve();
            new Checks("Cooler - H2O 1 kg/s, 80 → 25 °C")
                .Row("T_out", 298.15, sim.Stream(product).Value<double>("temperature"), 0.001)
                .InRange("Heat removed (NIST 230±5 kW)", 225.0, 235.0, Math.Abs(sim.Result(cooler, "Heat Removed", "kW")))
                .Assert();
        }

        [Test]
        public void U03_Pump_WaterPower()
        {
            using var sim = new Sim(_bridge, "Steam Tables (IAPWS-IF97)", "Water");
            var feed = sim.Feed("in", 298.15, 101325.0, massFlow: 1.0);
            var product = sim.Add("MaterialStream", "out");
            var pump = sim.Add("Pump", "P-1");
            sim.Chain(feed, pump, product);
            sim.Energy(pump, "in-1");
            sim.Mode(pump, "Delta_P");
            sim.Set(pump, "Pressure Increase", 10.0, "bar");
            sim.Set(pump, "Efficiency", 75.0);
            sim.Solve();
            double expected = 1.0 / 997.0 * 10e5 / 0.75 / 1000.0;
            new Checks("Pump - H2O 1 kg/s, ΔP=10 bar, η=75 %")
                .Row("ΔP", 10e5, sim.Result(pump, "Pressure Increase", "Pa"), 0.001)
                .Row("Power (V̇·ΔP/η)", expected, sim.Result(pump, "Power Required", "kW"), 0.05)
                .Assert();
        }

        [Test]
        public void U04_HeatExchanger_DutyBalance()
        {
            using var sim = new Sim(_bridge, "Steam Tables (IAPWS-IF97)", "Water");
            var hotIn = sim.Feed("hotIn", 373.15, 101325.0, massFlow: 1.0);
            var coldIn = sim.Feed("coldIn", 298.15, 101325.0, massFlow: 1.0);
            var hotOut = sim.Add("MaterialStream", "hotOut");
            var coldOut = sim.Add("MaterialStream", "coldOut");
            var hx = sim.Add("HeatExchanger", "HX-1");
            sim.Connect(hotIn, "out-0", hx, "in-0");
            sim.Connect(coldIn, "out-0", hx, "in-1");
            sim.Connect(hx, "out-0", hotOut, "in-0");
            sim.Connect(hx, "out-1", coldOut, "in-0");
            sim.Mode(hx, "CalcBothTemp_UA");
            // UA = 5000 W/K, as U × A
            sim.Set(hx, "Heat Exchange Area (A)", 1.0, "m2");
            sim.Set(hx, "Global Heat Transfer Coefficient (U)", 5000.0, "W/[m2.K]");
            sim.Solve();
            double q = sim.Result(hx, "Heat Load", "kW");
            double qHot = sim.Stream(hotIn).Value<double>("massFlow") * (sim.Enthalpy(hotIn) - sim.Enthalpy(hotOut));
            double qCold = sim.Stream(coldIn).Value<double>("massFlow") * (sim.Enthalpy(coldOut) - sim.Enthalpy(coldIn));
            new Checks("Counter-current HX - H2O 1 kg/s @ 100→? vs 25→?")
                .Row("q_hot vs Q", q, qHot, 0.01)
                .Row("q_cold vs Q", q, qCold, 0.01)
                .Row("Balance (q_hot - q_cold)/Q", 0.0, (qHot - qCold) / Math.Max(Math.Abs(q), 1e-9), 0.01)
                .InRange("Q > 0 (transfer occurred)", 1.0, 1e6, q)
                .Assert();
        }

        [Test]
        public void U05_Compressor_Isentropic()
        {
            using var sim = new Sim(_bridge, "Peng-Robinson (PR)", "Nitrogen");
            var feed = sim.Feed("in", 300.0, 1e5, molarFlow: 1.0);
            var product = sim.Add("MaterialStream", "out");
            var compressor = sim.Add("Compressor", "C-1");
            sim.Chain(feed, compressor, product);
            var work = sim.Energy(compressor, "in-1");
            sim.Set(compressor, "choice:ProcessPath", "Adiabatic");
            sim.Mode(compressor, "OutletPressure");
            sim.Set(compressor, "Pressure", 5e5, "Pa");
            sim.Set(compressor, "Adiabatic Efficiency", 100.0);
            sim.Solve();
            var outlet = sim.Stream(product);
            new Checks("Isentropic compressor - N2 1→5 bar, T_in=300 K")
                .Row("T_out (γ=1.4 ideal ≈ 475 K)", 300.0 * Math.Pow(5.0, 0.4 / 1.4), outlet.Value<double>("temperature"), 0.03)
                .InRange("P_out", 4.99e5, 5.01e5, outlet.Value<double>("pressure"))
                .InRange("Work > 0", 0.1, 1e6, sim.Stream(work).Value<double>("power_kW"))
                .Assert();
        }

        [Test]
        public void U06_Expander_Isentropic()
        {
            using var sim = new Sim(_bridge, "Peng-Robinson (PR)", "Nitrogen");
            var feed = sim.Feed("in", 500.0, 10e5, molarFlow: 1.0);
            var product = sim.Add("MaterialStream", "out");
            var expander = sim.Add("Expander", "E-1");
            sim.Chain(feed, expander, product);
            var work = sim.Energy(expander, "energy", outgoing: true);
            sim.Set(expander, "choice:ProcessPath", "Adiabatic");
            sim.Mode(expander, "OutletPressure");
            sim.Set(expander, "Pressure", 1e5, "Pa");
            sim.Set(expander, "Adiabatic Efficiency", 100.0);
            sim.Solve();
            new Checks("Isentropic expander - N2 10→1 bar, T_in=500 K")
                .Row("T_out (γ=1.4 ideal ≈ 259 K)", 500.0 * Math.Pow(0.1, 0.4 / 1.4), sim.Stream(product).Value<double>("temperature"), 0.03)
                .InRange("Generated work > 0", 0.1, 1e6, sim.Stream(work).Value<double>("power_kW"))
                .Assert();
        }

        [Test]
        public void U07_Valve_JouleThomson()
        {
            using var sim = new Sim(_bridge, "Peng-Robinson (PR)", "Carbon dioxide");
            var feed = sim.Feed("in", 300.0, 100e5, molarFlow: 1.0);
            var product = sim.Add("MaterialStream", "out");
            var valve = sim.Add("Valve", "V-1");
            sim.Chain(feed, valve, product);
            sim.Mode(valve, "OutletPressure");
            sim.Set(valve, "Outlet Pressure", 10e5, "Pa");
            sim.Solve();
            var outlet = sim.Stream(product);
            new Checks("Isenthalpic valve - CO2 100→10 bar, T_in=300 K (JT effect)")
                .Row("h_in ≈ h_out", sim.Enthalpy(feed), sim.Enthalpy(product), 0.001)
                .InRange("T_out < T_in (μ_JT > 0)", 200.0, 299.99, outlet.Value<double>("temperature"))
                .InRange("P_out", 9.99e5, 10.01e5, outlet.Value<double>("pressure"))
                .Assert();
        }

        [Test]
        public void U08_Splitter_Ratios()
        {
            using var sim = new Sim(_bridge, "Steam Tables (IAPWS-IF97)", "Water");
            var feed = sim.Feed("in", 350.0, 101325.0, massFlow: 100.0);
            var out1 = sim.Add("MaterialStream", "o1");
            var out2 = sim.Add("MaterialStream", "o2");
            var splitter = sim.Add("Splitter", "SP-1");
            sim.Connect(feed, "out-0", splitter, "in-0");
            sim.Connect(splitter, "out-0", out1, "in-0");
            sim.Connect(splitter, "out-1", out2, "in-0");
            sim.Mode(splitter, "SplitRatios");
            sim.Set(splitter, "split:0", 0.70);
            sim.Set(splitter, "split:1", 0.30);
            sim.Set(splitter, "split:2", 0.0);
            sim.Solve();
            new Checks("Splitter - H2O 100 kg/s, ratio 70/30")
                .Row("m_out1", 70.0, sim.Stream(out1).Value<double>("massFlow"), 0.001)
                .Row("m_out2", 30.0, sim.Stream(out2).Value<double>("massFlow"), 0.001)
                .Row("T_out1 = T_in", 350.0, sim.Stream(out1).Value<double>("temperature"), 0.001)
                .Row("T_out2 = T_in", 350.0, sim.Stream(out2).Value<double>("temperature"), 0.001)
                .Assert();
        }

        [Test]
        public void A04_Mixer_EnergyBalance()
        {
            using var sim = new Sim(_bridge, "Steam Tables (IAPWS-IF97)", "Water");
            var inlet1 = sim.Feed("inlet1", 300.0, 101325.0, massFlow: 100.0);
            var inlet2 = sim.Feed("inlet2", 348.0, 101325.0, massFlow: 50.0);
            var outlet = sim.Add("MaterialStream", "outlet");
            var mixer = sim.Add("Mixer", "MIX-1");
            sim.Connect(inlet1, "out-0", mixer, "in-0");
            sim.Connect(inlet2, "out-0", mixer, "in-1");
            sim.Connect(mixer, "out-0", outlet, "in-0");
            sim.Solve();
            double m1 = sim.Stream(inlet1).Value<double>("massFlow"), m2 = sim.Stream(inlet2).Value<double>("massFlow");
            double m3 = sim.Stream(outlet).Value<double>("massFlow");
            new Checks("Adiabatic mixer - H2O 100 kg/s@300K + 50 kg/s@348K")
                .Row("Mass balance m1+m2=m3", m1 + m2, m3, 0.0001)
                .Row("Energy balance ΣmH_in=ΣmH_out", m1 * sim.Enthalpy(inlet1) + m2 * sim.Enthalpy(inlet2), m3 * sim.Enthalpy(outlet), 0.001)
                .InRange("T_out within [300, 348] K", 300.0, 348.0, sim.Stream(outlet).Value<double>("temperature"))
                .Assert();
        }

        /// <summary>
        /// A flowsheet in the bridge, in SI units, built as the editors build one: objects added,
        /// connected port to port, modes and fields set by the labels the editor shows.
        /// </summary>
        private sealed class Sim : IDisposable
        {
            private readonly BridgeProcess _b;
            private readonly string _id;
            private string _compound;

            public Sim(BridgeProcess bridge, string package, string compound)
            {
                _b = bridge;
                _compound = compound;
                _id = _b.Call("flowsheet.new", new { name = "validation" }).Value<string>("id");
                _b.Call("flowsheet.addCompounds", new { flowsheetId = _id, names = new[] { compound } });
                _b.Call("flowsheet.addPropertyPackage", new { flowsheetId = _id, name = package });
                _b.Call("flowsheet.setUnitSystem", new { flowsheetId = _id, name = "SI" });
            }

            public string Add(string type, string tag)
                => _b.Call("graph.add", new { flowsheetId = _id, type, x = 0, y = 0, tag }).Value<string>("id");

            public string Feed(string tag, double temperature, double pressure, double? massFlow = null, double? molarFlow = null)
            {
                var id = Add("MaterialStream", tag);
                _b.Call("stream.specify", new
                {
                    flowsheetId = _id, id, temperature, pressure, massFlow, molarFlow,
                    moleFractions = new Dictionary<string, double> { [_compound] = 1.0 }
                });
                return id;
            }

            public void Connect(string source, string sourcePort, string target, string targetPort)
                => _b.Call("graph.connect", new { flowsheetId = _id, sourceId = source, sourcePort, targetId = target, targetPort });

            public void Chain(string feed, string unit, string product)
            {
                Connect(feed, "out-0", unit, "in-0");
                Connect(unit, "out-0", product, "in-0");
            }

            /// <summary>An energy stream on a unit's energy port, into it or out of it.</summary>
            public string Energy(string unit, string port, bool outgoing = false)
            {
                var e = Add("EnergyStream", "E-" + unit.Substring(0, Math.Min(6, unit.Length)));
                if (outgoing) Connect(unit, port, e, "in-0");
                else Connect(e, "out-0", unit, port);
                return e;
            }

            public void Mode(string unit, string value) => _b.Call("object.setMode", new { flowsheetId = _id, id = unit, value });

            private JToken FieldOf(JToken schema, string labelOrId)
                => schema["sections"].SelectMany(s => s["fields"]).FirstOrDefault(f =>
                       f.Value<string>("id") == labelOrId || f.Value<string>("label") == labelOrId)
                   ?? throw new AssertionException($"no field '{labelOrId}' in {schema.Value<string>("type")}: " +
                       string.Join(", ", schema["sections"].SelectMany(s => s["fields"]).Select(f => f.Value<string>("label"))));

            /// <summary>Sets a field found by its label (or id), as a user would in the editor.</summary>
            public void Set(string unit, string labelOrId, object value, string units = null)
            {
                var schema = _b.Call("object.schema", new { flowsheetId = _id, id = unit });
                var field = FieldOf(schema, labelOrId);
                _b.Call("object.setField", new { flowsheetId = _id, id = unit, field = field.Value<string>("id"), value, unit = units });
            }

            /// <summary>A field's value after solving, converted to <paramref name="unit"/>.</summary>
            public double Result(string obj, string label, string unit)
            {
                var field = FieldOf(_b.Call("object.schema", new { flowsheetId = _id, id = obj }), label);
                var value = field.Value<double>("value");
                var from = field.Value<string>("unit");
                return from == null || from == unit ? value : _b.Call("units.convert", new { value, from, to = unit }).Value<double>();
            }

            public JToken Stream(string id) => _b.Call("stream.get", new { flowsheetId = _id, id });

            /// <summary>The stream's mass enthalpy, kJ/kg.</summary>
            public double Enthalpy(string id) => Stream(id)["phases"].First(p => p.Value<int>("index") == 0).Value<double>("enthalpy");

            public void Solve()
            {
                var result = _b.Call("solve.run", new { flowsheetId = _id });
                Assert.That(result.Value<bool>("ok"), Is.True, "solve failed: " + string.Join(" | ", result["errors"]));
            }

            public void Dispose() => _b.Call("flowsheet.close", new { flowsheetId = _id });
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>
        /// One feed stream in a new flowsheet, set up and solved entirely through the bridge, and
        /// its state read back with stream.get.
        /// </summary>
        private StreamView Feed(string package, string[] compounds,
            double? temperature = null, double? pressure = null, double? vaporFraction = null,
            double? molarFlow = null, double? massFlow = null, object molarFlows = null, object massFlows = null,
            JObject flashSettings = null)
        {
            var id = _bridge.Call("flowsheet.new", new { name = "validation" }).Value<string>("id");
            try
            {
                _bridge.Call("flowsheet.addCompounds", new { flowsheetId = id, names = compounds });
                _bridge.Call("flowsheet.addPropertyPackage", new { flowsheetId = id, name = package, flashSettings });
                var stream = _bridge.Call("graph.add", new { flowsheetId = id, type = "MaterialStream", x = 100, y = 100, tag = "feed" });
                _bridge.Call("stream.specify", new
                {
                    flowsheetId = id,
                    id = stream.Value<string>("id"),
                    temperature,
                    pressure,
                    vaporFraction,
                    molarFlow,
                    massFlow,
                    compoundMolarFlows = molarFlows,
                    compoundMassFlows = massFlows
                });
                var solve = _bridge.Call("solve.run", new { flowsheetId = id });
                Assert.That(solve.Value<bool>("ok"), Is.True, "solve failed: " + string.Join(" | ", solve["errors"]));
                return new StreamView(_bridge.Call("stream.get", new { flowsheetId = id, id = stream.Value<string>("id") }));
            }
            finally
            {
                _bridge.Call("flowsheet.close", new { flowsheetId = id });
            }
        }

        private static Dictionary<string, double> Zip(string[] names, double[] values)
            => names.Zip(values, (n, v) => (n, v)).ToDictionary(p => p.n, p => p.v);

        /// <summary>A stream.get answer, read the way the original cases read the engine's phases.</summary>
        private sealed class StreamView
        {
            private readonly JToken _s;

            public StreamView(JToken s) { _s = s; }

            public double T => _s.Value<double>("temperature");

            public double P => _s.Value<double>("pressure");

            private JToken Phase(int index) => _s["phases"].FirstOrDefault(p => p.Value<int>("index") == index);

            /// <summary>Molar fraction of a phase; a phase the stream does not have is 0, as in the engine.</summary>
            public double Fraction(int index) => Phase(index)?.Value<double?>("moleFraction") ?? 0.0;

            public double Density(int index) => Phase(index)?.Value<double?>("density") ?? 0.0;

            public double Enthalpy(int index) => Phase(index)?.Value<double?>("enthalpy") ?? double.NaN;

            public double X(int index, string compound)
                => Phase(index)?["compounds"].FirstOrDefault(c => c.Value<string>("name") == compound)?.Value<double?>("moleFraction") ?? 0.0;
        }

        /// <summary>The original suite's ResultTable rules: relative tolerance, or an inclusive range.</summary>
        private sealed class Checks
        {
            private readonly string _title;
            private readonly StringBuilder _table = new StringBuilder();
            private readonly List<string> _failures = new List<string>();

            public Checks(string title)
            {
                _title = title;
                _table.AppendLine(title);
            }

            public Checks Row(string name, double expected, double actual, double relTol)
            {
                var denom = Math.Abs(expected) > 1e-12 ? Math.Abs(expected) : 1.0;
                var err = (actual - expected) / denom;
                var pass = Math.Abs(err) <= relTol;
                _table.AppendLine($"  {name,-48} expected {expected,12:G6}  got {actual,12:G6}  err {err * 100,7:0.00}%  tol {relTol * 100:0.##}%  {(pass ? "PASS" : "FAIL")}");
                if (!pass) _failures.Add($"{name}: expected {expected:G6}, got {actual:G6} ({err * 100:+0.00;-0.00}%, tol {relTol * 100:0.##}%)");
                return this;
            }

            public Checks InRange(string name, double low, double high, double actual)
            {
                var pass = actual >= low && actual <= high;
                _table.AppendLine($"  {name,-48} range [{low:G4}, {high:G4}]  got {actual,12:G6}  {(pass ? "PASS" : "FAIL")}");
                if (!pass) _failures.Add($"{name}: {actual:G6} outside [{low:G4}, {high:G4}]");
                return this;
            }

            public void Assert()
            {
                TestContext.WriteLine(_table.ToString());
                NUnit.Framework.Assert.That(_failures, Is.Empty, _title + "\n" + string.Join("\n", _failures));
            }
        }
    }
}
