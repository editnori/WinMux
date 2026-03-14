import { connectToWebView2 } from "./webview2-debug-utils.mjs";

const port = Number(process.env.WEBVIEW2_DEBUG_PORT ?? "9222");
const { browser, debugUrl } = await connectToWebView2({ port });

const contexts = browser.contexts();
const pages = contexts.flatMap((context, contextIndex) =>
  context.pages().map((page, pageIndex) => ({
    contextIndex,
    pageIndex,
    title: page.title(),
    url: page.url(),
  })),
);

const resolvedPages = [];
for (const entry of pages) {
  resolvedPages.push({
    contextIndex: entry.contextIndex,
    pageIndex: entry.pageIndex,
    title: await entry.title,
    url: entry.url,
  });
}

console.log(JSON.stringify({
  debugUrl,
  contextCount: contexts.length,
  pageCount: resolvedPages.length,
  pages: resolvedPages,
}, null, 2));

await browser.close();
