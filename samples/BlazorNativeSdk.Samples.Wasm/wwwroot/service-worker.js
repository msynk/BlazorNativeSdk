// The sample's service worker. It does no offline caching: its only job is to give the page an active service worker
// registration, which lets INotificationService show action buttons (the 'actions' notification feature) and routes taps
// on notifications back into the app, including taps after the page was closed. See docs/adapters/browser.md.
//
// It is registered from index.html with the app base path as its scope. The snippet ignores notifications that were not
// shown by BlazorNativeSdk, so your own notificationclick handlers can live next to it.
importScripts('./_content/BlazorNativeSdk.Browser/blazor-native-sdk-sw.js');

// Take over right away instead of waiting for every tab of a previous version to close, so the page that registered the
// worker can already show notifications with action buttons.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
