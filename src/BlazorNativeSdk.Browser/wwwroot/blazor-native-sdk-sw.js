// BlazorNativeSdk service-worker snippet: routes taps on notifications shown by INotificationService to the app.
//
// Usage, in your service worker (for example wwwroot/service-worker.js):
//   importScripts('./_content/BlazorNativeSdk.Browser/blazor-native-sdk-sw.js');
//
// Taps on notifications that were not created by BlazorNativeSdk are ignored, so the snippet can live next to your own
// notificationclick handlers. When a window of the app is open, it is focused and receives the tap as a message.
// Otherwise a new window is opened at the notification route (or the scope root) with the tap encoded in the
// 'bns-notification-click' query parameter, which the adapter publishes as a cold-start activation and then removes.

(() => {
  const MESSAGE_TYPE = 'bns-notification-click';
  const QUERY_PARAMETER = 'bns-notification-click';

  const encode = payload => {
    const bytes = new TextEncoder().encode(JSON.stringify(payload));
    let binary = '';
    for (const byte of bytes) {
      binary += String.fromCharCode(byte);
    }
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  };

  // Only same-origin routes inside the worker scope may be opened; anything else falls back to the scope root.
  const resolveUrl = route => {
    const scope = new URL(self.registration.scope);
    if (typeof route === 'string' && route.startsWith('/') && !route.startsWith('//')) {
      try {
        const candidate = new URL(route, scope);
        if (candidate.origin === scope.origin && candidate.pathname.startsWith(scope.pathname)) {
          return candidate;
        }
      } catch {
        // Invalid route.
      }
    }
    return scope;
  };

  self.addEventListener('notificationclick', event => {
    const notification = event.notification;
    const bns = notification.data && notification.data.bns;
    if (!bns) {
      return;
    }
    notification.close();

    const payload = {
      type: MESSAGE_TYPE,
      id: bns.id ?? null,
      action: event.action || null,
      route: bns.route ?? null,
      data: bns.data ?? {},
      reply: typeof event.reply === 'string' ? event.reply : null,
    };

    event.waitUntil((async () => {
      const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
      const scope = new URL(self.registration.scope);
      const sameScope = windows.filter(client => client.url.startsWith(scope.href));
      const client = sameScope.find(c => c.focused) ?? sameScope.find(c => c.visibilityState === 'visible') ?? sameScope[0];
      if (client) {
        try {
          await client.focus();
        } catch {
          // Focusing requires the notification click activation; ignore failures.
        }
        client.postMessage(payload);
        return;
      }

      const url = resolveUrl(payload.route);
      url.searchParams.set(QUERY_PARAMETER, encode(payload));
      await self.clients.openWindow(url.href);
    })());
  });
})();
