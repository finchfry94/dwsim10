// End-to-end smoke test of the frontend against the real engine, in a browser.
//
// Needs `pnpm dev:engine` and `pnpm dev` running, and Playwright with Chromium installed
// (`npm i -g playwright && npx playwright install chromium`). Run from app/:
//
//   NODE_PATH=$(npm root -g) node e2e/smoke.cjs [screenshot-dir]
//
// Opens the Cavett problem, solves it, colours streams by temperature and inspects a stream;
// then builds a stream-into-heater flowsheet by drag and drop and renames the heater.

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
  await page.getByText("The flowsheet was calculated successfully", { exact: false }).waitFor({ timeout: 180_000 });
  await page.selectOption("#color-mode", "temperature");
  await page.getByTestId("legend").waitFor();
  await page.locator('[data-testid="node-4"]').click();
  await page.getByTestId("composition").waitFor({ timeout: 20_000 });
  await shot("cavett-solved");

  // A new flowsheet built by hand.
  await page.getByRole("button", { name: "New", exact: true }).click();
  await page.getByText("New Simulation").first().waitFor();
  await page.dragAndDrop('[data-testid="palette-MaterialStream"]', '[data-testid="canvas"]', { targetPosition: { x: 300, y: 300 } });
  await page.dragAndDrop('[data-testid="palette-Heater"]', '[data-testid="canvas"]', { targetPosition: { x: 520, y: 300 } });
  await page.waitForFunction(() => document.querySelectorAll(".react-flow__node").length === 2);
  const from = await page.locator('.react-flow__node:has([data-testid="node-1"]) [data-handleid="out-0"]').boundingBox();
  const to = await page.locator('.react-flow__node:has([data-testid^="node-HT"]) [data-handleid="in-0"]').boundingBox();
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 10 });
  await page.mouse.up();
  await page.waitForFunction(() => document.querySelectorAll(".react-flow__edge").length === 1, null, { timeout: 10_000 });
  await page.locator('[data-testid^="node-HT"]').click();
  await page.getByLabel("Name").fill("Feed heater");
  await page.getByLabel("Name").press("Enter");
  await page.getByTestId("node-Feed heater").waitFor();
  await shot("new-flowsheet");

  assert.deepEqual(errors, []);
  await browser.close();
  console.log("e2e smoke: ok");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
