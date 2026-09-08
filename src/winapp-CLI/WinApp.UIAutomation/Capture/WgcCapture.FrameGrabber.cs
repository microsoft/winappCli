// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Foundation;
using D3D = Windows.Win32.Graphics.Direct3D11;
using D3DCommon = Windows.Win32.Graphics.Direct3D;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal static partial class WgcCapture
{
    internal static Func<HWND, ILogger, int, IFrameGrabber> s_startGrabber =
        (hwnd, logger, fps) => StartGrabber(hwnd, logger, fps);

    /// <summary>
    /// Opens a persistent Windows Graphics Capture session for a window and keeps
    /// the most recently arrived frame available on demand. Used by the recorder to
    /// sample frames at a fixed cadence without re-initializing D3D per frame.
    /// </summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): tests cover recorder orchestration through the start-grabber
    /// seam. This method itself performs D3D11CreateDevice and WGC FramePool/session creation for a
    /// real HWND, which requires native GPU/WinRT resources unavailable in deterministic headless runs.
    /// </remarks>
    public static FrameGrabber StartGrabber(HWND hwnd, ILogger logger, int fps = 0)
    {
        if (!s_isSupported())
        {
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }

        PInvoke.D3D11CreateDevice(
            pAdapter: null,
            D3DCommon.D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            Software: default,
            D3D.D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            pFeatureLevels: default,
            SDKVersion: D3D11_SDK_VERSION,
            out var device,
            out _,
            out var context).ThrowOnFailure();

        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? uiTarget = null;
        try
        {
            var winrtDevice = CreateDirect3DDevice(device);
            var item = CreateItemForWindow(hwnd);
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                item.Size);
            uiTarget = pool.CreateCaptureSession(item);
            uiTarget.IsCursorCaptureEnabled = false;

            return new FrameGrabber(device, context, pool, uiTarget, item, logger, fps);
        }
        catch
        {
            // If we fail after allocating the frame pool/session but before FrameGrabber takes
            // ownership, dispose them here so the heavy WGC/GPU resources are not leaked.
            uiTarget?.Dispose();
            pool?.Dispose();
            (context as IDisposable)?.Dispose();
            (device as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Holds a live capture session and caches the latest CPU-readable frame. Thread-safe:
    /// frames arrive on WGC's free-threaded pool while the recorder samples via
    /// <see cref="TryGetLatest"/>.
    /// </summary>
    internal sealed class FrameGrabber : IFrameGrabber
    {
        private readonly D3D.ID3D11Device _device;
        private readonly D3D.ID3D11DeviceContext _context;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly GraphicsCaptureItem _item;
        private readonly ILogger _logger;
        private readonly Lock _callbackLock = new();
        private readonly Lock _lock = new();
        private byte[]? _latestPixels;
        private int _latestWidth;
        private int _latestHeight;
        private long _version;
        private bool _disposed;
        // Track the pool's current creation size so we can detect window resizes.
        private global::Windows.Graphics.SizeInt32 _poolSize;
        // Set to true when the captured item closes mid-recording.
        private volatile bool _isClosed;

        // Throttle: track when we last did the expensive GPU→CPU copy so arrivals faster than
        // the target FPS are discarded without copying. A residual TOCTOU race means at most
        // ~2 copies can occur in the same sampling interval (two threads both pass the check
        // before either updates _lastSampleMs), but this is harmless — the second copy simply
        // overwrites the cached frame with an identical (or slightly newer) one.
        private long _lastSampleMs;
        private readonly int _minIntervalMs; // 0 = no throttle

        /// <remarks>
        /// Coverage ceiling (issue #630): constructing this type subscribes to a live WGC frame pool
        /// and starts native capture. Tests use the <see cref="IFrameGrabber"/> seam for orchestration;
        /// this constructor remains bound to real D3D/WGC objects.
        /// </remarks>
        internal FrameGrabber(
            D3D.ID3D11Device device,
            D3D.ID3D11DeviceContext context,
            Direct3D11CaptureFramePool pool,
            GraphicsCaptureSession uiTarget,
            GraphicsCaptureItem item,
            ILogger logger,
            int fps = 0)
        {
            _device = device;
            _context = context;
            _pool = pool;
            _session = uiTarget;
            _item = item;
            _logger = logger;
            _poolSize = item.Size;
            // fps == 0 disables throttling (used when fps is unknown or caller handles timing).
            _minIntervalMs = fps > 0 ? 1000 / fps : 0;
            _lastSampleMs = 0; // treat as "very old" so the first frame is always captured
            _pool.FrameArrived += OnFrameArrived;
            _item.Closed += OnItemClosed;
            _session.StartCapture();
        }

        /// <summary>
        /// Fires when the captured window or item is closed/destroyed mid-recording.
        /// Sets <see cref="IsClosed"/> so the recording loop can stop gracefully.
        /// </summary>
        /// <remarks>
        /// Coverage ceiling (issue #630): WGC raises this only when a real captured item closes during
        /// a live session. Recorder close-drain behavior is covered through the frame-grabber seam.
        /// </remarks>
        private void OnItemClosed(GraphicsCaptureItem sender, object args)
        {
            _isClosed = true;
            _logger.LogDebug("WGC capture item closed mid-recording.");
        }

        /// <summary>
        /// Returns <see langword="true"/> when the captured window/item has been closed.
        /// The recording loop should stop and finalize any frames already captured
        /// rather than padding with stale data to the duration deadline.
        /// </summary>
        public bool IsClosed => _isClosed;

        /// <remarks>
        /// Coverage ceiling (issue #630): this callback depends on a live WGC frame pool and GPU
        /// frame resources. Pure resize/throttle helpers and recorder consumers are tested; native
        /// frame arrival/copy COM fault arms cannot be triggered safely.
        /// </remarks>
        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            lock (_callbackLock)
            {
                if (_disposed)
                {
                    return;
                }

                Direct3D11CaptureFrame? frame = null;
                try
                {
                    frame = sender.TryGetNextFrame();
                    if (frame is null)
                    {
                        return;
                    }

                    // Detect window resize: if the frame's reported content size differs from the
                    // pool's creation size, recreate the pool at the new size so subsequent frames
                    // match. The ENCODER output dimensions remain fixed; ProcessFrame letterboxes/scales.
                    var contentSize = frame.ContentSize;
                    if (ShouldRecreateFramePool(_poolSize, contentSize))
                    {
                        _logger.LogDebug("WGC: window resized {Old}→{New}; recreating frame pool",
                            $"{_poolSize.Width}x{_poolSize.Height}", $"{contentSize.Width}x{contentSize.Height}");
                        // M10: dispose the triggering frame BEFORE recreating the pool so no frame
                        // from the old pool is alive during recreation. Set frame=null to prevent
                        // double-dispose in the outer finally.
                        try
                        {
                            DisposeFrameBeforeRecreate(
                                () =>
                                {
                                    frame.Dispose();
                                    frame = null;
                                },
                                () =>
                                {
                                    var winrtDevice = CreateDirect3DDevice(_device);
                                    _pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, numberOfBuffers: 2, contentSize);
                                });
                            _poolSize = contentSize;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "WGC frame pool Recreate failed; continuing with old size");
                        }
                        return; // Skip copying the first frame at the new size — wait for next arrival
                    }

                    // Throttle BEFORE the expensive GPU→CPU readback: skip arrivals that are faster
                    // than the target sampling interval. At 4K this can prevent gigabytes/sec of
                    // unnecessary memory allocation and copy when the display refresh rate exceeds fps.
                    if (_minIntervalMs > 0)
                    {
                        var nowMs = Environment.TickCount64;
                        if (nowMs - Interlocked.Read(ref _lastSampleMs) < _minIntervalMs)
                        {
                            return; // too soon — discard without copying
                        }
                        Interlocked.Exchange(ref _lastSampleMs, nowMs);
                    }

                    var (pixels, width, height) = CopyFrame(_device, _context, frame);
                    lock (_lock)
                    {
                        _latestPixels = pixels;
                        _latestWidth = width;
                        _latestHeight = height;
                        _version++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WGC frame copy failed during recording");
                }
                finally
                {
                    frame?.Dispose();
                }
            }
        }

        internal static bool ShouldRecreateFramePool(global::Windows.Graphics.SizeInt32 poolSize, global::Windows.Graphics.SizeInt32 contentSize)
            => contentSize.Width > 0 && contentSize.Height > 0
                && (contentSize.Width != poolSize.Width || contentSize.Height != poolSize.Height);

        internal static void DisposeFrameBeforeRecreate(Action disposeFrame, Action recreatePool)
        {
            disposeFrame();
            recreatePool();
        }

        /// <summary>Returns the most recently captured frame, or <see langword="null"/> if none has arrived yet.</summary>
        /// <remarks>
        /// Coverage ceiling (issue #630): the real cache is populated only by live WGC callbacks; the
        /// recorder paths that consume cached frames are covered with a deterministic fake grabber.
        /// </remarks>
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
        {
            lock (_lock)
            {
                if (_latestPixels is null)
                {
                    return null;
                }
                return (_latestPixels, _latestWidth, _latestHeight, _version);
            }
        }

        /// <summary>Waits (up to <paramref name="timeout"/>) for the first frame to arrive.</summary>
        /// <remarks>
        /// Coverage ceiling (issue #630): waiting for the first frame requires the native WGC callback
        /// to populate this instance; recorder timeout behavior is covered through the grabber seam.
        /// </remarks>
        public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (TryGetLatest() is not null)
                {
                    return true;
                }
                await Task.Delay(30, ct).ConfigureAwait(false);
            }
            return TryGetLatest() is not null;
        }

        /// <remarks>
        /// Coverage ceiling (issue #630): disposal releases real WGC/D3D COM resources and unsubscribes
        /// native events. Tests verify consumers dispose fake grabbers; native resource release itself
        /// requires a live capture session.
        /// </remarks>
        public void Dispose()
        {
            lock (_callbackLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                // Drain any in-flight free-threaded callback before releasing D3D resources; after
                // handlers are removed, pool disposal cannot re-enter this callback path.
                _pool.FrameArrived -= OnFrameArrived;
                _item.Closed -= OnItemClosed;
                _session.Dispose();
                _pool.Dispose();
                (_context as IDisposable)?.Dispose();
                (_device as IDisposable)?.Dispose();
            }
        }
    }
}
