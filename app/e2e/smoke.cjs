// End-to-end smoke test of the frontend against the real engine, in a browser.
//
// Needs `pnpm dev:engine` and `pnpm dev` running, and Playwright with Chromium installed
// (`npm i -g playwright && npx playwright install chromium`). Run from app/:
//
//   NODE_PATH=$(npm root -g) node e2e/smoke.cjs [screenshot-dir]
//
// Opens the Cavett problem, solves it, colours streams by temperature and inspects a stream;
// then sets a new simulation up in the wizard, builds feed -> heater -> product by drag and
// drop, specifies the feed, sets the heater to an 80 °C outlet in its editor, and checks that
// auto-solve brings the product to 80 °C. Last, opens the extractive distillation sample, changes
// a column's reflux ratio in its editor and checks that the column re-solves.

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
    await page.waitForFunction((n) => document.querySelectorAll(".react-flow__edge").length >= n, edges, {
      timeout: 10_000,
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

  if (errors.length) console.error("console errors:", JSON.stringify(errors, null, 1));
  assert.deepEqual(errors, []);
  await browser.close();
  console.log("e2e smoke: ok");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
