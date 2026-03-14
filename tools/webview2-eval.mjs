import { connectToWebView2 } from "./webview2-debug-utils.mjs";

const port = Number(process.env.WEBVIEW2_DEBUG_PORT ?? "9222");
const expression = process.argv.slice(2).join(" ").trim();

if (!expression) {
  throw new Error("Usage: node ./tools/webview2-eval.mjs \"document.title\"");
}

const { browser, debugUrl } = await connectToWebView2({ port });
const page = browser
  .contexts()
  .flatMap((context) => context.pages())
  .find((candidate) => candidate.url().includes("terminal-host.html"))
  ?? browser.contexts().flatMap((context) => context.pages())[0];

if (!page) {
  throw new Error(`No WebView2 pages found at ${debugUrl}`);
}

const result = await page.evaluate((source) => {
  // Intentionally evals local debug expressions against the live renderer.
  return globalThis.eval(source);
}, expression);

console.log(JSON.stringify({
  debugUrl,
  title: await page.title(),
  url: page.url(),
  expression,
  result,
}, null, 2));

await browser.close();
