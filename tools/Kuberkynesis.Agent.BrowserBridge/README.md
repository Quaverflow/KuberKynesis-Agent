# Kuberkynesis Agent Bridge Extension

This unpacked Chrome/Edge extension is a local-testing bridge for the hosted Kuberkynesis UI. It lets pages such as `https://kuberkynesis.com/` forward HTTP requests to a local Kuberkynesis agent without the page itself calling `127.0.0.1` directly.

## Load locally

1. Open `chrome://extensions` or `edge://extensions`.
2. Enable `Developer mode`.
3. Click `Load unpacked`.
4. Select this folder: `tools/Kuberkynesis.Agent.BrowserBridge`

## What it does today

- injects a small page bridge into:
  - `https://kuberkynesis.com/*`
  - `https://www.kuberkynesis.com/*`
  - `https://kuberkynesis.pages.dev/*`
  - `http://localhost:5173/*`
- allows fetch proxying only to:
  - `http://127.0.0.1/*`
  - `http://localhost/*`

## Test from the browser console

```js
window.addEventListener("message", (event) => {
  if (event.data?.source === "kuberkynesis-agent-bridge") {
    console.log("bridge event", event.data);
  }
});

window.postMessage({
  source: "kuberkynesis-ui",
  type: "kuberkynesis-agent-bridge-request",
  requestId: "hello-test",
  request: {
    baseUrl: "http://127.0.0.1:46321/",
    path: "v1/hello",
    method: "GET"
  }
}, window.location.origin);
```

If the local agent is running and reachable, the response event should include the `v1/hello` payload.
