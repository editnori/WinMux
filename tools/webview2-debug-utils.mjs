import fs from "node:fs/promises";
import { chromium } from "playwright";

function unique(values) {
  return [...new Set(values.filter(Boolean))];
}

async function getWslHostIp() {
  try {
    const resolv = await fs.readFile("/etc/resolv.conf", "utf8");
    const line = resolv
      .split(/\r?\n/)
      .find((entry) => entry.trim().startsWith("nameserver "));

    return line?.trim().split(/\s+/)[1];
  }
  catch {
    return null;
  }
}

export async function getCandidateDebugUrls(port = 9222) {
  const envUrl = process.env.WEBVIEW2_DEBUG_URL;
  const wslHostIp = await getWslHostIp();

  return unique([
    envUrl,
    `http://127.0.0.1:${port}`,
    wslHostIp ? `http://${wslHostIp}:${port}` : null,
  ]);
}

export async function connectToWebView2({ port = 9222, attempts = 20, delayMs = 500 } = {}) {
  const urls = await getCandidateDebugUrls(port);
  let lastError;

  for (let attempt = 0; attempt < attempts; attempt++) {
    for (const debugUrl of urls) {
      try {
        const browser = await chromium.connectOverCDP(debugUrl);
        return { browser, debugUrl };
      }
      catch (error) {
        lastError = error;
      }
    }

    await new Promise((resolve) => setTimeout(resolve, delayMs));
  }

  throw new Error(
    `Could not connect to WebView2 CDP. Tried ${urls.join(", ")}. Last error: ${lastError}`,
  );
}
