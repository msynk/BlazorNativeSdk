// The sample's service worker. It does no offline caching: its only job is to give the page an active service worker
// registration, which lets INotificationService show action buttons (the 'actions' notification feature) and routes taps
// on notifications back into the app. With a window open, the tap goes to that window's circuit; with none, the worker
// opens the notification route with ?bns-notification-click=, and the new circuit publishes it as a cold-start
// activation. See docs/adapters/browser.md.
//
// It is registered from Components/App.razor with the app base path as its scope. The snippet ignores notifications that
// were not shown by BlazorNativeSdk, so your own notificationclick handlers can live next to it.
importScripts('./_content/BlazorNativeSdk.Browser/blazor-native-sdk-sw.js');

// Take over right away instead of waiting for every tab of a previous version to close, so the page that registered the
// worker can already show notifications with action buttons.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
