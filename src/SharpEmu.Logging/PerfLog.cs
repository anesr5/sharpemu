// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Logging;

/// <summary>
/// Lightweight performance counters sampled to a CSV file once per second.
/// Hot paths guard on <see cref="Enabled"/> so the counters cost nothing
/// when --perf-logs is not passed.
/// </summary>
public static class PerfLog
{
    public static bool Enabled { get; private set; }

    public static long FramesPresented;
    public static long FlipsSubmitted;
    public static long DcbSubmits;
    public static long DrawsTranslated;
    public static long ComputeDispatches;
    public static long GuestWorkExecuted;
    public static long SpirvCacheHits;
    public static long SpirvCacheMisses;
    public static long PipelinesCreated;
    public static long GuestTextureBytesRead;
    public static long PresentFenceWaitTicks;
    public static long ShaderEvalTicks;
    public static long FlipGpuCache;
    public static long FlipDrawFallback;
    public static long FlipSoftware;
    public static long FlipGuestDraw;
    public static long FlipUnhandled;
    public static long PresentSkipsNotReady;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static Thread? _sampler;
    private static bool _stopRequested;

    public static void Start(string path)
    {
        lock (Gate)
        {
            if (_writer is not null)
            {
                return;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _writer.WriteLine(
                "time_s,fps,frames_total,flips_per_s,dcb_submits_per_s,draws_per_s," +
                "dispatches_per_s,guest_work_per_s,spirv_hits_per_s,spirv_misses_per_s," +
                "pipelines_created_total,texture_mb_read_per_s,fence_wait_ms_per_s," +
                "shader_eval_ms_per_s,gc0_total,gc1_total,gc2_total,managed_mb," +
                "alloc_mb_per_s,working_set_mb,cpu_percent," +
                "flip_gpu_cache_per_s,flip_draw_fallback_per_s,flip_software_per_s," +
                "flip_guest_draw_per_s,flip_unhandled_per_s,present_skips_per_s");
            _writer.Flush();
            _stopRequested = false;
            Enabled = true;
            _sampler = new Thread(SampleLoop)
            {
                IsBackground = true,
                Name = "SharpEmu PerfLog",
            };
            _sampler.Start();
        }
    }

    public static void Stop()
    {
        Thread? sampler;
        lock (Gate)
        {
            if (_writer is null)
            {
                return;
            }

            Enabled = false;
            _stopRequested = true;
            sampler = _sampler;
            _sampler = null;
        }

        sampler?.Join(TimeSpan.FromSeconds(3));
        lock (Gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void SampleLoop()
    {
        var process = Process.GetCurrentProcess();
        var stopwatch = Stopwatch.StartNew();
        var lastElapsed = TimeSpan.Zero;
        var lastCpu = process.TotalProcessorTime;
        var lastFrames = 0L;
        var lastFlips = 0L;
        var lastDcb = 0L;
        var lastDraws = 0L;
        var lastDispatches = 0L;
        var lastGuestWork = 0L;
        var lastSpirvHits = 0L;
        var lastSpirvMisses = 0L;
        var lastTextureBytes = 0L;
        var lastFenceTicks = 0L;
        var lastEvalTicks = 0L;
        var lastAllocated = 0L;
        var lastFlipGpuCache = 0L;
        var lastFlipDrawFallback = 0L;
        var lastFlipSoftware = 0L;
        var lastFlipGuestDraw = 0L;
        var lastFlipUnhandled = 0L;
        var lastPresentSkips = 0L;
        while (!_stopRequested)
        {
            Thread.Sleep(1000);
            var elapsed = stopwatch.Elapsed;
            var intervalSeconds = (elapsed - lastElapsed).TotalSeconds;
            if (intervalSeconds <= 0)
            {
                continue;
            }

            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var frames = Interlocked.Read(ref FramesPresented);
            var flips = Interlocked.Read(ref FlipsSubmitted);
            var dcb = Interlocked.Read(ref DcbSubmits);
            var draws = Interlocked.Read(ref DrawsTranslated);
            var dispatches = Interlocked.Read(ref ComputeDispatches);
            var guestWork = Interlocked.Read(ref GuestWorkExecuted);
            var spirvHits = Interlocked.Read(ref SpirvCacheHits);
            var spirvMisses = Interlocked.Read(ref SpirvCacheMisses);
            var textureBytes = Interlocked.Read(ref GuestTextureBytesRead);
            var fenceTicks = Interlocked.Read(ref PresentFenceWaitTicks);
            var evalTicks = Interlocked.Read(ref ShaderEvalTicks);
            var allocated = GC.GetTotalAllocatedBytes(precise: false);
            var flipGpuCache = Interlocked.Read(ref FlipGpuCache);
            var flipDrawFallback = Interlocked.Read(ref FlipDrawFallback);
            var flipSoftware = Interlocked.Read(ref FlipSoftware);
            var flipGuestDraw = Interlocked.Read(ref FlipGuestDraw);
            var flipUnhandled = Interlocked.Read(ref FlipUnhandled);
            var presentSkips = Interlocked.Read(ref PresentSkipsNotReady);
            var cpuPercent =
                (cpu - lastCpu).TotalSeconds / intervalSeconds / Environment.ProcessorCount * 100.0;

            var line = string.Create(CultureInfo.InvariantCulture,
                $"{elapsed.TotalSeconds:F1}," +
                $"{(frames - lastFrames) / intervalSeconds:F1}," +
                $"{frames}," +
                $"{(flips - lastFlips) / intervalSeconds:F1}," +
                $"{(dcb - lastDcb) / intervalSeconds:F1}," +
                $"{(draws - lastDraws) / intervalSeconds:F1}," +
                $"{(dispatches - lastDispatches) / intervalSeconds:F1}," +
                $"{(guestWork - lastGuestWork) / intervalSeconds:F1}," +
                $"{(spirvHits - lastSpirvHits) / intervalSeconds:F1}," +
                $"{(spirvMisses - lastSpirvMisses) / intervalSeconds:F1}," +
                $"{Interlocked.Read(ref PipelinesCreated)}," +
                $"{(textureBytes - lastTextureBytes) / intervalSeconds / (1024.0 * 1024.0):F2}," +
                $"{(fenceTicks - lastFenceTicks) * 1000.0 / Stopwatch.Frequency / intervalSeconds:F2}," +
                $"{(evalTicks - lastEvalTicks) * 1000.0 / Stopwatch.Frequency / intervalSeconds:F2}," +
                $"{GC.CollectionCount(0)}," +
                $"{GC.CollectionCount(1)}," +
                $"{GC.CollectionCount(2)}," +
                $"{GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0):F1}," +
                $"{(allocated - lastAllocated) / intervalSeconds / (1024.0 * 1024.0):F1}," +
                $"{process.WorkingSet64 / (1024.0 * 1024.0):F1}," +
                $"{cpuPercent:F1}," +
                $"{(flipGpuCache - lastFlipGpuCache) / intervalSeconds:F1}," +
                $"{(flipDrawFallback - lastFlipDrawFallback) / intervalSeconds:F1}," +
                $"{(flipSoftware - lastFlipSoftware) / intervalSeconds:F1}," +
                $"{(flipGuestDraw - lastFlipGuestDraw) / intervalSeconds:F1}," +
                $"{(flipUnhandled - lastFlipUnhandled) / intervalSeconds:F1}," +
                $"{(presentSkips - lastPresentSkips) / intervalSeconds:F1}");
            lock (Gate)
            {
                if (_writer is null)
                {
                    return;
                }

                _writer.WriteLine(line);
                _writer.Flush();
            }

            lastElapsed = elapsed;
            lastCpu = cpu;
            lastFrames = frames;
            lastFlips = flips;
            lastDcb = dcb;
            lastDraws = draws;
            lastDispatches = dispatches;
            lastGuestWork = guestWork;
            lastSpirvHits = spirvHits;
            lastSpirvMisses = spirvMisses;
            lastTextureBytes = textureBytes;
            lastFenceTicks = fenceTicks;
            lastEvalTicks = evalTicks;
            lastAllocated = allocated;
            lastFlipGpuCache = flipGpuCache;
            lastFlipDrawFallback = flipDrawFallback;
            lastFlipSoftware = flipSoftware;
            lastFlipGuestDraw = flipGuestDraw;
            lastFlipUnhandled = flipUnhandled;
            lastPresentSkips = presentSkips;
        }
    }
}
