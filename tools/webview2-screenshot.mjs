import path from "node:path";
import { connectToWebView2, resolvePreferredPage } from "./webview2-debug-utils.mjs";

const port = Number(process.env.WEBVIEW2_DEBUG_PORT ?? "9222");
const outputPath = process.argv[2]
  ? path.resolve(process.argv[2])
  : path.resolve("webview2-debug-shot.png");

const { browser, debugUrl } = await connectToWebView2({ port });
const page = resolvePreferredPage(browser);

if (!page) {
  throw new Error(`No WebView2 pages found at ${debugUrl}`);
}

await page.screenshot({
  path: outputPath,
  type: "png",
});

console.log(JSON.stringify({
  debugUrl,
  title: await page.title(),
  url: page.url(),
  outputPath,
}, null, 2));

await browser.close();
