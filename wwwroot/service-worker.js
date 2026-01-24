const CACHE_NAME = 'music-cache-v1';
const MAX_CACHE_SIZE = 500 * 1024 * 1024; // 500 MB limit
const DB_NAME = 'music-cache-db';
const STORE_NAME = 'entries';

// Precache the app shell
const PRECACHE_URLS = [
    '/',
    '/index.html',
    '/index.css',
    'https://cdn.jsdelivr.net/npm/hls.js@latest' // External CDN might be tricky with CORS, but hls.js usually allows it.
];

// IndexedDB Helper Functions
function openDB() {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, 1);
        req.onupgradeneeded = (e) => {
            const db = e.target.result;
            if (!db.objectStoreNames.contains(STORE_NAME)) {
                const store = db.createObjectStore(STORE_NAME, { keyPath: 'url' });
                store.createIndex('timestamp', 'timestamp', { unique: false });
            }
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

async function addEntryToDB(url, size) {
    try {
        const db = await openDB();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readwrite');
            const store = tx.objectStore(STORE_NAME);
            store.put({ url, size, timestamp: Date.now() });
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
    } catch (e) {
        console.error('DB Error:', e);
    }
}

async function getTotalSize() {
    try {
        const db = await openDB();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readonly');
            const store = tx.objectStore(STORE_NAME);
            let total = 0;
            const req = store.openCursor();
            req.onsuccess = (e) => {
                const cursor = e.target.result;
                if (cursor) {
                    total += cursor.value.size || 0;
                    cursor.continue();
                } else {
                    resolve(total);
                }
            };
            req.onerror = () => reject(tx.error);
        });
    } catch (e) {
        return 0;
    }
}

async function enforceLimit() {
    let currentSize = await getTotalSize();
    if (currentSize <= MAX_CACHE_SIZE) return;

    try {
        const db = await openDB();
        const cache = await caches.open(CACHE_NAME);

        // Get all entries sorted by timestamp
        const entries = await new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readonly');
            const store = tx.objectStore(STORE_NAME);
            const idx = store.index('timestamp'); // Oldest first
            const req = idx.getAll();
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });

        for (const entry of entries) {
            if (currentSize <= MAX_CACHE_SIZE) break;

            // Delete from cache
            const deleted = await cache.delete(entry.url);
            // Delete from DB
            if (deleted) {
                const tx = db.transaction(STORE_NAME, 'readwrite');
                tx.objectStore(STORE_NAME).delete(entry.url);
                currentSize -= entry.size;
                await new Promise(r => tx.oncomplete = r);
            } else {
                // Even if not in cache (stale DB entry), remove from DB
                const tx = db.transaction(STORE_NAME, 'readwrite');
                tx.objectStore(STORE_NAME).delete(entry.url);
                currentSize -= entry.size; // Assume it's gone
                await new Promise(r => tx.oncomplete = r);
            }
        }
    } catch (e) {
        console.error('Enforce limit error:', e);
    }
}

self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then(cache => cache.addAll(PRECACHE_URLS))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener('activate', event => {
    event.waitUntil(
        Promise.all([
            self.clients.claim(),
            // Clean up other caches if logic changes, simplified here
        ])
    );
});

self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);

    // Skip non-GET
    if (event.request.method !== 'GET') return;

    // Determine strategy
    // 1. App Shell -> Stale While Revalidate
    if (PRECACHE_URLS.includes(url.pathname) || PRECACHE_URLS.includes(url.href)) {
        event.respondWith(
            caches.open(CACHE_NAME).then(async cache => {
                const cachedResponse = await cache.match(event.request);
                const fetchPromise = fetch(event.request).then(networkResponse => {
                    cache.put(event.request, networkResponse.clone());
                    return networkResponse;
                });
                return cachedResponse || fetchPromise;
            })
        );
        return;
    }

    // 2. Media Segments and Playlist
    // Logic: If it looks like music/video, Cache First (or Network First for playlist)
    // Actually, we want to cache "downloaded music".
    // We treat everything that matches media types as candidates.

    event.respondWith(
        (async () => {
            // Try cache first for everything else to support offline if cached
            const cache = await caches.open(CACHE_NAME);
            const cachedResponse = await cache.match(event.request);

            if (cachedResponse) {
                // If it's a playlist (.m3u8), we might want to check network for updates? 
                // But for "downloaded music", usually we want the cached version.
                // However, if the user plays a new file, we need to fetch it.
                // Simple Cache-First is dangerous for dynamic content but fine for static segments.
                // Assuming .ts segments are immutable.
                // .m3u8 might change.
                if (url.pathname.endsWith('.m3u8')) {
                    // Network First for playlists
                    try {
                        const networkResponse = await fetch(event.request);
                        if (networkResponse.ok) {
                            const clonedRes = networkResponse.clone();
                            const blob = await clonedRes.blob(); // Read body to get size? Or clone again?
                            // Putting in cache consumes the body from the put() argument. 
                            // We need to be careful with streams.

                            // Let's just put it.
                            cache.put(event.request, networkResponse.clone());
                            // Update DB size (approximate for playlist)
                            addEntryToDB(event.request.url, parseInt(networkResponse.headers.get('content-length') || '1000'));
                            enforceLimit();

                            return networkResponse;
                        }
                    } catch (e) {
                        return cachedResponse; // Fallback to cache
                    }
                }

                // For other cached stuff (segments), return cache
                return cachedResponse;
            }

            // Not in cache, fetch it
            try {
                const networkResponse = await fetch(event.request);

                // Check if we should cache this response
                // Criteria: 200 OK, and correct content type
                if (networkResponse.ok) {
                    const contentType = networkResponse.headers.get('content-type') || '';
                    const shouldCache =
                        url.pathname.endsWith('.ts') ||
                        url.pathname.endsWith('.m4s') ||
                        contentType.includes('audio/') ||
                        contentType.includes('video/') ||
                        contentType.includes('application/vnd.apple.mpegurl') ||
                        contentType.includes('application/x-mpegURL');

                    if (shouldCache) {
                        const clonedForCache = networkResponse.clone();
                        // We can't easily get the size of a stream without reading it.
                        // Content-Length header is often present for static files.
                        let size = parseInt(networkResponse.headers.get('content-length'));

                        // If Content-Length is missing, we might need to read the blob
                        if (isNaN(size)) {
                            // If we convert to blob to read size, we consume the stream.
                            // We can clone -> blob -> put.
                            // But we need to return the original networkResponse to the browser.
                            const blobPromise = clonedForCache.blob();
                            blobPromise.then(blob => {
                                cache.put(event.request, new Response(blob, {
                                    status: networkResponse.status,
                                    statusText: networkResponse.statusText,
                                    headers: networkResponse.headers
                                }));
                                addEntryToDB(event.request.url, blob.size);
                                enforceLimit();
                            });
                        } else {
                            cache.put(event.request, clonedForCache);
                            addEntryToDB(event.request.url, size);
                            enforceLimit();
                        }
                    }
                }
                return networkResponse;
            } catch (error) {
                // If offline and not in cache
                // Maybe return a fallback placeholder if appropriate?
                throw error;
            }
        })()
    );
});
