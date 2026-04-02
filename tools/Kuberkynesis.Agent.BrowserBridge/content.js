(function () {
  const pageProbeType = "kuberkynesis-agent-bridge-probe";
  const pageProbeResponseType = "kuberkynesis-agent-bridge-probe-response";
  const pageRequestType = "kuberkynesis-agent-bridge-request";
  const pageResponseType = "kuberkynesis-agent-bridge-response";
  const pageWebSocketConnectType = "kuberkynesis-agent-bridge-ws-connect";
  const pageWebSocketDisconnectType = "kuberkynesis-agent-bridge-ws-disconnect";
  const pageWebSocketEventType = "kuberkynesis-agent-bridge-ws-event";
  const pageReadyType = "kuberkynesis-agent-bridge-ready";
  const pageSource = "kuberkynesis-ui";
  const bridgeSource = "kuberkynesis-agent-bridge";

  window.addEventListener("message", (event) => {
    if (event.source !== window || !event.data || event.data.source !== pageSource || event.data.type !== pageRequestType) {
      return;
    }

    const requestId = typeof event.data.requestId === "string" ? event.data.requestId : "";
    if (event.data.type === pageProbeType) {
      if (!requestId) {
        return;
      }

      window.postMessage(
        {
          source: bridgeSource,
          type: pageProbeResponseType,
          requestId
        },
        window.location.origin
      );
      return;
    }

    if (event.data.type === pageRequestType) {
      if (!requestId) {
        return;
      }

      chrome.runtime.sendMessage(
        {
          type: "kuberkynesis-agent-bridge-fetch",
          request: event.data.request
        },
        (response) => {
          const runtimeError = chrome.runtime.lastError;

          window.postMessage(
            {
              source: bridgeSource,
              type: pageResponseType,
              requestId,
              response: runtimeError
                ? {
                    ok: false,
                    error: runtimeError.message
                  }
                : response
            },
            window.location.origin
          );
        });
      return;
    }

    if (event.data.type === pageWebSocketConnectType) {
      chrome.runtime.sendMessage(
        {
          type: "kuberkynesis-agent-bridge-ws-connect",
          connectionId: event.data.connectionId,
          url: event.data.url
        },
        () => {
          const runtimeError = chrome.runtime.lastError;
          if (!runtimeError) {
            return;
          }

          window.postMessage(
            {
              source: bridgeSource,
              type: pageWebSocketEventType,
              connectionId: event.data.connectionId,
              eventType: "closed",
              code: 1006,
              reason: runtimeError.message
            },
            window.location.origin
          );
        });
      return;
    }

    if (event.data.type === pageWebSocketDisconnectType) {
      chrome.runtime.sendMessage(
        {
          type: "kuberkynesis-agent-bridge-ws-disconnect",
          connectionId: event.data.connectionId
        },
        () => {
          chrome.runtime.lastError;
        });
      return;
    }
  });

  chrome.runtime.onMessage.addListener((message) => {
    if (!message || message.type !== "kuberkynesis-agent-bridge-ws-event") {
      return;
    }

    window.postMessage(
      {
        source: bridgeSource,
        type: pageWebSocketEventType,
        connectionId: message.connectionId,
        eventType: message.eventType,
        data: message.data,
        code: message.code,
        reason: message.reason
      },
      window.location.origin
    );
  });

  window.postMessage(
    {
      source: bridgeSource,
      type: pageReadyType
    },
    window.location.origin
  );
})();
