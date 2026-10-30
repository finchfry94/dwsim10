// End-to-end smoke test of the frontend against the real engine, in a browser.
//
// Needs `pnpm dev:engine` and `pnpm dev` running, and Playwright with Chromium installed
// (`npm i -g playwright && npx playwright install chromium`). Run from app/:
//
//   NODE_PATH=$(npm root -g) node e2e/smoke.cjs [screenshot-dir]
//
// Opens the Cavett problem, solves it, colours streams by temperature and inspects a stream, its
// row in the stream table, the Excel export, its hydrate and critical-point tools, its phase
// envelope and the report;
// then sets a new simulation up in the wizard, builds feed -> heater -> product by drag and
// drop, specifies the feed, sets the heater to an 80 °C outlet in its editor, and checks that
// auto-solve brings the product to 80 °C; builds a reaction and runs a sensitivity analysis on
// the heater, runs a script, writes a spreadsheet formula and compares two scenarios. Then opens
// the extractive distillation sample, changes a column's reflux ratio in its editor and checks
// that the column re-solves, and cancels a solve. Last, opens the three-phase separator sample
// and integrates it in dynamic mode. Along the way it deletes and undoes, duplicates and checks
// the flowsheet from the command palette.

const { chromium } = require("playwright");
const assert = require("node:assert/strict");

const base = process.env.DWSIM_APP_URL ?? "http://localhost:1420/";
const shots = process.argv[2];

async function main() {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on("pageerror", (e) => errors.push(String(e)));
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  const shot = (name) => shots && page.screenshot({ path: `${shots}/${name}.png` });

  await page.goto(base);
  await page.getByText("Engine ready").waitFor({ timeout: 120_000 });

  // A reference flowsheet: open, solve, colour, inspect.
  await page.getByRole("button", { name: "Cavett Problem" }).click();
  await page.locator('[data-testid^="node-"]').first().waitFor({ timeout: 60_000 });
  assert.equal(await page.locator(".react-flow__node").count(), 42);
  assert.equal(await page.locator(".react-flow__edge").count(), 44);
  await page.getByTestId("solve").click();
  await page
    .getByText("The flowsheet was calculated successfully", { exact: false })
    .waitFor({ timeout: 180_000 });
  await page.selectOption("#color-mode", "temperature");
  await page.getByTestId("legend").waitFor();
  await page.locator('[data-testid="node-4"]').click();
  await page.getByTestId("composition").waitFor({ timeout: 20_000 });
  await shot("cavett-solved");

  // Results: the stream table, a phase envelope from a stream, and the report (of the selected
  // stream only, as it opens with the selection).
  await page.getByTestId("tab-streams").click();
  const streamTable = page.getByTestId("stream-table");
  await streamTable.locator("thead th").nth(20).waitFor();
  assert.equal(await streamTable.locator("thead th").count(), 21, "a column per stream");
  const download = page.waitForEvent("download");
  await page.getByTestId("export-excel").click();
  assert.match((await download).suggestedFilename(), /\.xlsx$/);

  // Engineering tools on the selected stream: hydrates, then its true critical point.
  await page.getByTestId("stream-tools").click();
  const tools = page.getByRole("dialog", { name: "Tools" });
  await tools.getByTestId("run-tool").click();
  await tools
    .getByTestId("tool-result")
    .getByText("No hydrates form", { exact: false })
    .waitFor({ timeout: 60_000 });
  await tools.getByTestId("tool-criticalPoint").click();
  await tools.getByTestId("run-tool").click();
  await tools.getByText(/Critical point at \d+(\.\d+)? K/).waitFor({ timeout: 60_000 });
  await shot("tools");
  await page.keyboard.press("Escape");
  await tools.waitFor({ state: "detached" });

  await page.getByTestId("phase-envelope").click();
  const analysis = page.getByRole("dialog", { name: "Analysis" });
  await analysis.getByTestId("xy-chart").waitFor({ timeout: 60_000 });
  assert.match(await analysis.getByTestId("critical-point").innerText(), /Critical point: \d+(\.\d+)? K/);
  await shot("phase-envelope");
  await page.keyboard.press("Escape");
  await analysis.waitFor({ state: "detached" });
  await page.getByTestId("report").click();
  await page
    .frameLocator('[data-testid="report-frame"]')
    .getByRole("heading", { name: "4 (Material Stream)", exact: true })
    .waitFor({ timeout: 60_000 })
    .catch(async (e) => {
      await shot("report-stuck");
      throw e;
    });
  await page.keyboard.press("Escape");
  await page.getByRole("dialog", { name: "Report" }).waitFor({ state: "detached" });
  await page.getByTestId("tab-log").click();

  // A new simulation built from scratch: setup wizard, feed -> heater -> product, solved.
  await page.getByRole("button", { name: "New", exact: true }).click();
  const setup = page.getByRole("dialog", { name: "Simulation setup" });
  await setup.waitFor();
  for (const compound of ["Water", "Ethanol"]) {
    await setup.getByLabel("Search compounds").fill(compound.toLowerCase());
    await setup.getByRole("button", { name: `Add ${compound}`, exact: true }).click();
    await setup.getByTestId("compound-selection").getByText(compound, { exact: true }).waitFor();
  }
  await setup.getByRole("button", { name: "Next" }).click();
  await setup.getByLabel("Filter property packages").fill("NRTL");
  await setup.getByRole("button", { name: "Add NRTL", exact: true }).click();
  await setup.getByTestId("package-selection").getByText("NRTL").waitFor();
  await setup.getByRole("button", { name: "Next" }).click();
  await setup.getByRole("button", { name: "Finish" }).click();
  await setup.waitFor({ state: "detached" });

  const drop = (type, x, y) =>
    page.dragAndDrop(`[data-testid="palette-${type}"]`, '[data-testid="canvas"]', {
      targetPosition: { x, y },
    });
  await drop("MaterialStream", 250, 300);
  await drop("Heater", 450, 300);
  await drop("MaterialStream", 650, 300);
  await drop("EnergyStream", 450, 450);
  await page.waitForFunction(() => document.querySelectorAll(".react-flow__node").length === 4);
  const tags = await page.locator(".node-label").allInnerTexts();
  const heater = tags.find((t) => t.startsWith("HT"));
  const energy = tags.find((t) => t.startsWith("E"));
  const port = (tag, id) =>
    page.locator(`.react-flow__node:has([data-testid="node-${tag}"]) [data-handleid="${id}"]`);
  let edges = 0;
  const connect = async (from, to) => {
    const a = await from.boundingBox();
    const b = await to.boundingBox();
    await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
    await page.mouse.down();
    await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 10 });
    await page.mouse.up();
    edges += 1;
    await page
      .waitForFunction((n) => document.querySelectorAll(".react-flow__edge").length >= n, edges, {
        timeout: 30_000,
      })
      .catch(async (e) => {
        await shot("connect-stuck");
        console.error(
          "connect-stuck",
          JSON.stringify(a),
          await page.evaluate(
            ([x, y]) => {
              const el = document.elementFromPoint(x, y);
              return el ? el.outerHTML.slice(0, 300) : null;
            },
            [a.x + a.width / 2, a.y + a.height / 2],
          ),
          JSON.stringify(b),
          (await page.locator(".log-line").allInnerTexts()).slice(-3),
        );
        throw e;
      });
  };
  await connect(port("1", "out-0"), port(heater, "in-0"));
  await connect(port(heater, "out-0"), port("2", "in-0"));
  await connect(port(energy, "out-0"), port(heater, "in-1"));

  await page.locator('[data-testid="node-1"]').click();
  const feed = page.getByTestId("stream-editor");
  await feed.getByLabel("Temperature", { exact: true }).fill("25");
  await feed.getByLabel("Temperature unit").selectOption("C");
  await feed.getByLabel("Pressure", { exact: true }).fill("1.01325");
  await feed.getByLabel("Pressure unit").selectOption("bar");
  await feed.getByLabel("Total flow", { exact: true }).fill("3600");
  await feed.getByLabel("Total flow unit").selectOption("kg/h");
  await feed.getByLabel("Water amount").fill("0.6");
  await feed.getByLabel("Ethanol amount").fill("0.4");
  await feed.getByTestId("apply-spec").click();
  await page.getByText("specification applied").waitFor();

  // The heater through its editor: outlet temperature mode, 80 °C. Auto-solve does the rest.
  await page.locator(`[data-testid="node-${heater}"]`).click();
  const editor = page.getByTestId("schema-editor");
  await editor.getByLabel("Calculation mode").selectOption("OutletTemperature");
  const outletT = editor.getByLabel("Outlet Temperature", { exact: true });
  await outletT.waitFor();
  await editor.getByLabel("Outlet Temperature unit").selectOption("C");
  await outletT.fill("80");
  await outletT.press("Enter");
  await editor.getByTestId("dof").filter({ hasText: "Fully specified" }).waitFor();

  await page.locator('[data-testid="node-2"]').click();
  const temperature = page.locator(".metric", { hasText: "Temperature" }).locator(".metric-value");
  await temperature.filter({ hasText: "80 °C" }).waitFor({ timeout: 60_000 });
  assert.equal(await temperature.innerText(), "80 °C");
  await page.locator(`[data-testid="node-${heater}"]`).click();
  await editor.getByTestId("dof").waitFor();
  await shot("new-flowsheet");

  // Editing comfort: delete the heater and undo it, duplicate it, check the flowsheet from the
  // command palette.
  const nodeCount = () => page.locator(".react-flow__node").count();
  const edgeCount = () => page.locator(".react-flow__edge").count();
  const heaterNode = page.locator(`[data-testid="node-${heater}"]`);
  await heaterNode.click();
  await page.getByTestId("selection-bar").waitFor();
  await page.keyboard.press("Delete");
  await page.waitForFunction(() => document.querySelectorAll(".react-flow__node").length === 3);
  assert.equal(await edgeCount(), 0);
  await page.getByTestId("undo").click();
  await heaterNode.waitFor({ timeout: 30_000 });
  await page.waitForFunction(() => document.querySelectorAll(".react-flow__edge").length === 3);
  assert.equal(await nodeCount(), 4);

  await heaterNode.click();
  await page.keyboard.press("Control+d");
  await page.waitForFunction(() => document.querySelectorAll(".react-flow__node").length === 5, null, {
    timeout: 30_000,
  });
  await page.getByText("Copied 1 object.").waitFor();
  await page.keyboard.press("Control+z");
  await page.waitForFunction(() => document.querySelectorAll(".react-flow__node").length === 4, null, {
    timeout: 30_000,
  });

  await page.keyboard.press("Control+k");
  const palette = page.getByRole("dialog", { name: "Command palette" });
  await palette.getByLabel("Search commands and objects").fill("check flow");
  await palette.getByRole("option", { name: /Check the flowsheet/ }).waitFor();
  await page.keyboard.press("Enter");
  await palette.waitFor({ state: "detached", timeout: 10_000 }).catch(async (e) => {
    await shot("palette-stuck");
    throw e;
  });
  await page.getByTestId("findings").waitFor();
  await shot("diagnostics");

  // Reactions: a conversion reaction can only be saved once Balance has balanced the masses;
  // saved, it joins the default set; deleted, it is gone.
  await page.getByTestId("reactions").click();
  const reactions = page.getByRole("dialog", { name: "Reactions" });
  await reactions.getByLabel("Add a reaction").selectOption("Conversion");
  const rx = reactions.getByTestId("reaction-editor");
  await rx.getByLabel("Name", { exact: true }).fill("Test reaction");
  await rx.getByLabel("Include Water").check();
  await rx.getByLabel("Include Ethanol").check();
  await rx.getByLabel("Coefficient of Water").fill("-1");
  await rx.getByLabel("Coefficient of Ethanol").fill("1");
  await rx.getByLabel("Water is the base component").check();
  assert.ok(await rx.getByTestId("save-reaction").isDisabled(), "unbalanced reactions cannot be saved");
  await rx.getByRole("button", { name: "Balance" }).click();
  await rx.getByTestId("stoichiometry").getByText("OK", { exact: true }).waitFor();
  await rx.getByTestId("save-reaction").click();
  await reactions
    .getByRole("button", { name: /^Default Set/ })
    .getByText("1 reaction")
    .waitFor({ timeout: 30_000 });
  await reactions.getByRole("button", { name: /^Test reaction/ }).click();
  await rx.getByRole("button", { name: "Delete" }).click();
  await reactions
    .getByRole("button", { name: /^Default Set/ })
    .getByText("0 reactions")
    .waitFor({ timeout: 30_000 });
  await page.keyboard.press("Escape");
  await reactions.waitFor({ state: "detached" });

  // Sensitivity: vary the heater's outlet temperature and watch the heat it adds.
  await page.getByTestId("studies").click();
  const studies = page.getByRole("dialog", { name: "Studies" });
  await studies.getByLabel("Independent variable object").selectOption({ label: heater });
  await studies.getByLabel("Independent variable property").selectOption("PROP_HT_2");
  const kelvin = (await studies.getByText(/^From, to/).innerText()).includes("(K)");
  await studies.getByLabel("From", { exact: true }).fill(kelvin ? "330" : "57");
  await studies.getByLabel("To", { exact: true }).fill(kelvin ? "360" : "87");
  await studies.getByLabel("Points", { exact: true }).fill("4");
  await studies.getByLabel("Dependent variable 1 object").selectOption({ label: heater });
  await studies.getByLabel("Dependent variable 1 property").selectOption("PROP_HT_3");
  await studies.getByTestId("run-study").click();
  const sensitivity = studies.getByTestId("sensitivity-result");
  await sensitivity.getByTestId("xy-chart").waitFor({ timeout: 120_000 });
  const duties = await sensitivity.locator("tbody tr td:nth-child(2)").allInnerTexts();
  assert.equal(duties.length, 4);
  const values = duties.map((d) => Number(d.replace(/,/g, "")));
  assert.ok(
    values.every((v, i) => i === 0 || v > values[i - 1]),
    `the duty rises: ${duties}`,
  );
  await shot("sensitivity");
  await page.keyboard.press("Escape");
  await studies.waitFor({ state: "detached" });

  // A script reads the heater's outlet temperature (80 °C) through the engine's Python.
  await page.getByTestId("scripts").click();
  const scripts = page.getByRole("dialog", { name: "Scripts" });
  await scripts
    .getByTestId("script-text")
    .fill(`print('%.1f' % ${heater.replace(/-/g, "_")}.GetPropertyValue('PROP_HT_2'))`);
  await scripts.getByTestId("run-script").click();
  const output = scripts.getByTestId("script-output");
  await output.waitFor({ timeout: 60_000 });
  assert.match(await output.innerText(), /353\.[12]/);
  await page.keyboard.press("Escape");
  await scripts.waitFor({ state: "detached" });

  // The spreadsheet: a formula the engine calculates.
  await page.getByTestId("tab-sheet").click();
  const sheet = page.getByTestId("spreadsheet");
  await sheet.locator('td[data-address="A1"]').click();
  await page.keyboard.type("=6*7");
  await page.keyboard.press("Enter");
  await sheet.locator('td[data-address="A1"]').filter({ hasText: "42" }).waitFor({ timeout: 30_000 });
  await page.getByTestId("tab-log").click();

  // Scenarios: capture, change the heater's outlet to 75 °C, capture again; the changed
  // specification heads the comparison.
  await page.getByTestId("studies").click();
  await studies.getByRole("tab", { name: "Scenarios" }).click();
  await studies.getByTestId("capture-A").click();
  await studies.getByText(/A, \d/).waitFor();
  await page.keyboard.press("Escape");
  await studies.waitFor({ state: "detached" });
  await page.locator(`[data-testid="node-${heater}"]`).click();
  await editor.getByLabel("Outlet Temperature unit").selectOption("C");
  await outletT.fill("75");
  await outletT.press("Enter");
  await page.locator('[data-testid="node-2"]').click();
  await temperature.filter({ hasText: "75 °C" }).waitFor({ timeout: 60_000 });
  await page.getByTestId("studies").click();
  await studies.getByRole("tab", { name: "Scenarios" }).click();
  await studies.getByTestId("capture-B").click();
  const comparison = studies.getByTestId("scenario-result");
  await comparison.waitFor({ timeout: 30_000 });
  assert.match(
    await comparison.locator("tbody tr").first().innerText(),
    new RegExp(`${heater}[\\s\\S]*Outlet Temperature`),
  );
  await shot("scenarios");
  await page.keyboard.press("Escape");
  await studies.waitFor({ state: "detached" });

  // A rigorous column: change the reflux ratio in its editor; auto-solve re-solves the column
  // and the condenser duty and stage profile follow.
  await page.goto(base);
  await page.getByText("Engine ready").waitFor({ timeout: 120_000 });
  await page.getByRole("button", { name: "Extractive Distillation" }).click();
  const column = page.locator('[data-testid^="node-"]', { hasText: "Methanol Column (1 atm)" });
  await column.waitFor({ timeout: 60_000 });
  await page.getByTestId("solve").click();
  await page
    .getByText("The flowsheet was calculated successfully", { exact: false })
    .waitFor({ timeout: 180_000 });
  await column.click();
  const columnEditor = page.getByTestId("schema-editor");
  await columnEditor.getByTestId("table-column:profile").waitFor({ timeout: 30_000 });
  await columnEditor.getByTestId("chart-column:profile").waitFor();
  const duty = columnEditor.locator(".schema-row", { hasText: "Condenser Duty" }).locator(".readonly");
  const dutyBefore = await duty.innerText();
  const condenser = columnEditor.getByTestId("section-condenser");
  assert.equal(await condenser.getByLabel("Specification", { exact: true }).inputValue(), "7");
  const reflux = condenser.getByLabel("Specification value", { exact: true });
  await reflux.fill("4");
  await reflux.press("Enter");
  for (let waited = 0; (await duty.innerText()) === dutyBefore; waited += 1000) {
    assert.ok(waited < 180_000, "the column did not re-solve");
    await page.waitForTimeout(1000);
  }
  assert.notEqual(await duty.innerText(), dutyBefore);
  assert.equal(await reflux.inputValue(), "4");
  await shot("column");

  // A solve can be cancelled: start one and stop it at once.
  await page.getByTestId("solve").click();
  await page.getByTestId("cancel-solve").click();
  await page.getByTestId("solve").waitFor({ timeout: 60_000 });
  await page.locator(".log-line").filter({ hasText: "Solve cancelled." }).waitFor({ timeout: 60_000 });

  // Dynamics: the three-phase separator sample made ready by the suggested fixes, its level
  // recorded, and a short run charted.
  page.once("dialog", (d) => d.accept()); // the column edits are unsaved
  await page.getByRole("button", { name: "Open", exact: true }).click();
  await page
    .getByPlaceholder("/path/to/flowsheet.dwxmz")
    .fill(require("node:path").resolve(__dirname, "../../tests/flowsheets/ThreePhaseSeparator.dwxml"));
  await page.keyboard.press("Enter");
  await page.locator('[data-testid="node-separator"]').waitFor({ timeout: 60_000 });
  await page.getByTestId("solve").click();
  await page.getByTestId("solve").waitFor({ timeout: 120_000 });
  await page.getByTestId("dynamics").click();
  const dynamics = page.getByRole("dialog", { name: "Dynamics" });
  await dynamics.getByLabel("Dynamic mode").click();
  await dynamics
    .getByTestId("dynamic-mode-badge")
    .filter({ hasText: "Dynamic mode" })
    .waitFor({ timeout: 30_000 });
  await dynamics.getByTestId("apply-all-fixes").click();
  await dynamics.getByText(/^Applied \d+ fix/).waitFor({ timeout: 30_000 });
  await dynamics.getByRole("tab", { name: "Integrators" }).click();
  await dynamics.getByRole("button", { name: "Record a variable" }).click();
  await dynamics.getByLabel("Recorded variable 1 object").selectOption({ label: "separator" });
  await dynamics.getByLabel("Recorded variable 1 property").selectOption("Liquid Level");
  await dynamics.getByLabel("Duration", { exact: true }).fill("60");
  await dynamics.getByTestId("save-integrator").click();
  await dynamics.getByRole("tab", { name: "Run" }).click();
  await dynamics.getByText(/recording 1 variable/).waitFor({ timeout: 30_000 });
  await dynamics.getByTestId("run-dynamics").click();
  await dynamics.getByTestId("dynamics-result").getByTestId("xy-chart").waitFor({ timeout: 120_000 });
  assert.match(await dynamics.getByTestId("dynamics-result").innerText(), /\d+ samples/);
  await shot("dynamics");
  await page.keyboard.press("Escape");
  await dynamics.waitFor({ state: "detached" });

  if (errors.length) console.error("console errors:", JSON.stringify(errors, null, 1));
  assert.deepEqual(errors, []);
  await browser.close();
  console.log("e2e smoke: ok");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
