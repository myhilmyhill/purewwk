'use strict';
const CACHE_NAME = 'music-cache-v4';
const MAX_CACHE_SIZE = 500 * 1024 * 1024; // 500 MB limit
const DB_NAME = 'music-cache-db';
const STORE_NAME = 'entries';

function openDB() {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 2);
    req.onupgradeneeded = (e) => {
      const db = e.target.result;
      let store;
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        store = db.createObjectStore(STORE_NAME, { keyPath: 'url' });
      } else {
        store = req.transaction.objectStore(STORE_NAME);
      }
      if (!store.indexNames.contains('timestamp')) {
        store.createIndex('timestamp', 'timestamp', { unique: false });
      }
      if (!store.indexNames.contains('groupId')) {
        store.createIndex('groupId', 'groupId', { unique: false });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function addEntryToDB(url, size, groupId) {
  try {
    const db = await openDB();
    const now = Date.now();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readwrite');
      const store = tx.objectStore(STORE_NAME);
      
      store.put({ url, size, timestamp: now, groupId });

      // Update timestamp for all entries in the same group to prevent partial eviction
      if (groupId) {
        const idx = store.index('groupId');
        const req = idx.openCursor(IDBKeyRange.only(groupId));
        req.onsuccess = (e) => {
          const cursor = e.target.result;
          if (cursor) {
            if (cursor.value.url !== url && cursor.value.timestamp < now) {
              const updatedValue = { ...cursor.value, timestamp: now };
              cursor.update(updatedValue);
            }
            cursor.continue();
          }
        };
      }

      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  } catch (e) {
    console.error('DB Error:', e);
  }
}

async function enforceLimit() {
  let currentSize;
  try {
    if (navigator.storage && navigator.storage.estimate) {
      const estimate = await navigator.storage.estimate();
      currentSize = estimate.usage;
    } else {
      console.warn('StorageManager API not available.');
      return;
    }
  } catch (e) {
    console.error('Error getting storage estimate:', e);
    return;
  }

  if (currentSize <= MAX_CACHE_SIZE) return;

  try {
    const db = await openDB();
    const cache = await caches.open(CACHE_NAME);

    // Group entries by groupId, compute max timestamp and total size
    const groups = new Map();
    const entries = await new Promise((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readonly');
      const store = tx.objectStore(STORE_NAME);
      const req = store.getAll();
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });

    for (const entry of entries) {
      const gid = entry.groupId || entry.url; // fallback to url if no group
      if (!groups.has(gid)) {
        groups.set(gid, { entries: [], maxTimestamp: entry.timestamp, totalSize: 0 });
      }
      const g = groups.get(gid);
      g.entries.push(entry);
      g.maxTimestamp = Math.max(g.maxTimestamp, entry.timestamp);
      g.totalSize += entry.size;
    }

    // Sort groups by timestamp (oldest first)
    const sortedGroups = Array.from(groups.values()).sort((a, b) => a.maxTimestamp - b.maxTimestamp);

    for (const group of sortedGroups) {
      if (currentSize <= MAX_CACHE_SIZE) break;

      // Delete all entries in this group
      for (const entry of group.entries) {
        const deleted = await cache.delete(entry.url);
        const tx = db.transaction(STORE_NAME, 'readwrite');
        tx.objectStore(STORE_NAME).delete(entry.url);
        currentSize -= entry.size;
        await new Promise(r => tx.oncomplete = r);
      }
    }
  } catch (e) {
    console.error('Enforce limit error:', e);
  }
}

self.addEventListener('activate', event => {
  event.waitUntil(
    Promise.all([
      self.clients.claim(),
      caches.keys().then(cacheNames => {
        return Promise.all(
          cacheNames.map(cacheName => {
            if (cacheName !== CACHE_NAME) {
              return caches.delete(cacheName);
            }
          })
        );
      })
    ])
  );
});

self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET') return;

  // 2. Intercept Directory Listing to check for Cached items
  if (url.pathname === '/rest/getMusicDirectory.view') {
    event.respondWith(
      (async () => {
        try {
          const response = await fetch(event.request);
          if (!response.ok) return response;

          const clone = response.clone();
          const data = await clone.json();

          if (data['subsonic-response']?.directory?.child) {
            const cache = await caches.open(CACHE_NAME);
            let children = data['subsonic-response'].directory.child;
            // Handle single item or array
            const items = Array.isArray(children) ? children : [children];

            await Promise.all(items.map(async (item) => {
              if (!item.isDir) {
                const hlsUrl = `/rest/hls.m3u8?id=${encodeURIComponent(item.id)}`;
                const fullUrl = new URL(hlsUrl, self.location.origin).href;
                const match = await cache.match(fullUrl);
                item.cached = !!match;
              }
            }));

            const newHeaders = new Headers(response.headers);
            newHeaders.delete('content-length');
            newHeaders.delete('content-encoding');

            return new Response(JSON.stringify(data), {
              status: response.status,
              statusText: response.statusText,
              headers: newHeaders
            });
          }
          return response;
        } catch (e) {
          console.error('Directory cache check failed:', e);
          return fetch(event.request);
        }
      })()
    );
    return;
  }

  // 3. Media Segments and Playlist
  // Logic: If it looks like music/video, Cache First (or Network First for playlist)
  // Actually, we want to cache "downloaded music".
  // We treat everything that matches media types as candidates.

  event.respondWith(
    (async () => {
      // Try cache first for everything else to support offline if cached
      const cache = await caches.open(CACHE_NAME);
      const cachedResponse = await cache.match(event.request);

      if (cachedResponse) {
        // Return cached response immediately (Cache First)
        // We only cache complete playlists now, so this is safe.
        return cachedResponse;
      }

      // Not in cache, fetch it
      const networkResponse = await fetch(event.request);

      // Check if we should cache this response
      // Criteria: 200 OK, and correct content type
      if (networkResponse.status === 206) {
        return networkResponse;
      }

      if (networkResponse.ok) {
        // Special handling for playlists to ensure completeness
        if (url.pathname === '/rest/hls.m3u8') {
          const clonedRes = networkResponse.clone();
          const text = await clonedRes.text();
          const groupId = url.searchParams.get('id');

          if (text.includes('#EXT-X-ENDLIST')) {
            cache.put(event.request, new Response(text, {
              status: networkResponse.status,
              statusText: networkResponse.statusText,
              headers: networkResponse.headers
            }));
            addEntryToDB(event.request.url, text.length, groupId);
            enforceLimit();
          }
          // If incomplete, we do NOT cache it.
        } else if (url.pathname === '/hls') {
          // Logic for segments (TS, M4S, etc.)
          const clonedForCache = networkResponse.clone();
          let size = parseInt(networkResponse.headers.get('content-length'));

          const key = url.searchParams.get('key');
          let groupId = key;
          if (key) {
             // key is like id/variantKey/segment_XXX.ts
             const parts = key.split('/');
             if (parts.length >= 3) {
                groupId = parts.slice(0, -2).join('/');
             }
          }

          if (isNaN(size)) {
            const blobPromise = clonedForCache.blob();
            blobPromise.then(blob => {
              cache.put(event.request, new Response(blob, {
                status: networkResponse.status,
                statusText: networkResponse.statusText,
                headers: networkResponse.headers
              }));
              addEntryToDB(event.request.url, blob.size, groupId);
              enforceLimit();
            });
          } else {
            cache.put(event.request, clonedForCache);
            addEntryToDB(event.request.url, size, groupId);
            enforceLimit();
          }
        }
      }
      return networkResponse;
    })()
  );
});
