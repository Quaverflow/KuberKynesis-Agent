const defaultAgentBaseUrl = "http://127.0.0.1:46321/";
const requestTimeoutMs = 30000;
const webSocketConnections = new Map();

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message.type !== "string") {
    return false;
  }

  switch (message.type) {
    case "kuberkynesis-agent-bridge-probe":
      sendResponse({ ok: true });
      return false;
    case "kuberkynesis-agent-bridge-fetch":
      handleFetchMessage(message)
        .then(sendResponse)
        .catch((error) => {
          sendResponse({
            ok: false,
            error: error instanceof Error ? error.message : "The bridge request failed."
          });
        });

      return true;
    case "kuberkynesis-agent-bridge-ws-connect":
      handleWebSocketConnectMessage(message, sender)
        .then(sendResponse)
        .catch((error) => {
          sendResponse({
            ok: false,
            error: error instanceof Error ? error.message : "The bridge websocket connect failed."
          });
        });

      return true;
    case "kuberkynesis-agent-bridge-ws-disconnect":
      handleWebSocketDisconnectMessage(message);
      sendResponse({ ok: true });
      return false;
    default:
      return false;
  }
});

async function handleWebSocketConnectMessage(message, sender) {
  const connectionId = typeof message.connectionId === "string" ? message.connectionId.trim() : "";
  const url = typeof message.url === "string" ? message.url.trim() : "";

  if (!connectionId) {
    throw new Error("The bridge websocket connection id is required.");
  }

  if (!url) {
    throw new Error("The bridge websocket url is required.");
  }

  if (typeof sender.tab?.id !== "number") {
    throw new Error("The bridge websocket connection requires a browser tab.");
  }

  const targetUrl = normalizeWebSocketUrl(url);
  const socket = new WebSocket(targetUrl);
  const tabId = sender.tab.id;

  closeConnection(connectionId, false);
  webSocketConnections.set(connectionId, { socket, tabId });

  socket.onopen = () => {
    postWebSocketEvent(tabId, {
      connectionId,
      eventType: "opened"
    });
  };

  socket.onmessage = event => {
    postWebSocketEvent(tabId, {
      connectionId,
      eventType: "message",
      data: typeof event.data === "string" ? event.data : ""
    });
  };

  socket.onerror = () => {
    postWebSocketEvent(tabId, {
      connectionId,
      eventType: "errored"
    });
  };

  socket.onclose = event => {
    webSocketConnections.delete(connectionId);
    postWebSocketEvent(tabId, {
      connectionId,
      eventType: "closed",
      code: event.code,
      reason: event.reason || ""
    });
  };

  return { ok: true };
}

function handleWebSocketDisconnectMessage(message) {
  const connectionId = typeof message.connectionId === "string" ? message.connectionId.trim() : "";
  if (!connectionId) {
    return;
  }

  closeConnection(connectionId, true);
}

function closeConnection(connectionId, sendClientCloseReason) {
  const entry = webSocketConnections.get(connectionId);
  if (!entry) {
    return;
  }

  webSocketConnections.delete(connectionId);

  try {
    entry.socket.close(1000, sendClientCloseReason ? "Client disconnect" : undefined);
  } catch {
  }
}

function postWebSocketEvent(tabId, payload) {
  chrome.tabs.sendMessage(tabId, {
    type: "kuberkynesis-agent-bridge-ws-event",
    ...payload
  }).catch(() => {});
}

function normalizeWebSocketUrl(url) {
  const targetUrl = new URL(url);

  if (targetUrl.protocol !== "ws:" && targetUrl.protocol !== "wss:") {
    throw new Error("Only websocket agent endpoints are supported.");
  }

  if (targetUrl.hostname !== "127.0.0.1" && targetUrl.hostname !== "localhost") {
    throw new Error("The bridge only supports localhost websocket endpoints.");
  }

  return targetUrl.toString();
}

async function handleFetchMessage(message) {
  const request = message.request ?? {};
  const method = typeof request.method === "string" ? request.method.toUpperCase() : "GET";
  const requestUrl = typeof request.url === "string" ? request.url.trim() : "";

  if (!requestUrl) {
    throw new Error("The bridge request url is required.");
  }

  const targetUrl = buildTargetUrl(requestUrl);
  const headers = sanitizeHeaders(request.headers);
  const controller = new AbortController();
  const timeoutHandle = setTimeout(() => controller.abort(), requestTimeoutMs);

  try {
    const response = await fetch(targetUrl, {
      method,
      headers,
      body: method === "GET" || method === "HEAD" ? undefined : request.body ?? undefined,
      signal: controller.signal
    });

    const responseHeaders = {};
    for (const [key, value] of response.headers.entries()) {
      responseHeaders[key] = value;
    }

    return {
      ok: response.ok,
      status: response.status,
      statusText: response.statusText,
      headers: responseHeaders,
      body: await response.text()
    };
  }
  finally {
    clearTimeout(timeoutHandle);
  }
}

function buildTargetUrl(requestUrl) {
  const resolvedRequestUrl = requestUrl || defaultAgentBaseUrl;
  const targetUrl = new URL(resolvedRequestUrl, ensureTrailingSlash(defaultAgentBaseUrl));

  if (targetUrl.protocol !== "http:") {
    throw new Error("Only local HTTP agent endpoints are supported.");
  }

  if (targetUrl.hostname !== "127.0.0.1" && targetUrl.hostname !== "localhost") {
    throw new Error("The bridge only supports localhost agent endpoints.");
  }

  return targetUrl.toString();
}

function ensureTrailingSlash(value) {
  return value.endsWith("/") ? value : `${value}/`;
}

function sanitizeHeaders(headers) {
  const result = {};

  if (!headers || typeof headers !== "object") {
    return result;
  }

  for (const [key, value] of Object.entries(headers)) {
    if (typeof key !== "string" || typeof value !== "string") {
      continue;
    }

    result[key] = value;
  }

  return result;
}
