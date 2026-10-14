import { expect, test } from "@playwright/test";

const password = process.env.Seed__DemoPassword;
const orgName = "Northwind Facilities";

test.beforeEach(() => {
  test.skip(!password, "Set Seed__DemoPassword and start the API, worker, and Vite app before e2e.");
});

test("requester signs in, creates a work order, and a technician claims it", async ({ page, browser }) => {
  await page.goto("/login");
  await page.getByLabel("Email").fill("requester.northwind@opsflow.example");
  await page.getByLabel("Password").fill(password!);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: orgName })).toBeVisible();

  await page.getByRole("link", { name: "New work order" }).click();
  await page.getByLabel("Title").fill("E2E badge reader");
  await page.getByLabel("Category").fill("Access");
  await page.getByLabel("Description").fill("Created by Playwright.");
  await page.getByRole("button", { name: "Create work order" }).click();
  await expect(page.getByRole("heading", { name: "E2E badge reader" })).toBeVisible();

  const tech = await browser.newPage();
  await tech.goto("/login");
  await tech.getByLabel("Email").fill("tech.a.northwind@opsflow.example");
  await tech.getByLabel("Password").fill(password!);
  await tech.getByRole("button", { name: "Sign in" }).click();
  await tech.getByRole("link", { name: "Work orders" }).click();
  await tech.getByRole("link", { name: /WO-/ }).first().click();
  await tech.getByRole("button", { name: "Claim" }).click();
  await expect(tech.getByText("Assigned")).toBeVisible();
});

test("requester cannot open audit", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Email").fill("requester.northwind@opsflow.example");
  await page.getByLabel("Password").fill(password!);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("link", { name: "Audit" })).toHaveCount(0);
  await page.goto("/audit");
  await expect(page.getByRole("alert")).toContainText("admin");
});
