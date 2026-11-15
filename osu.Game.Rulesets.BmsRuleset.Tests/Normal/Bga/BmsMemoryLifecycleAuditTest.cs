#nullable enable

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using NUnit.Framework;
using osu.Game.Rulesets.BmsRuleset.Media.FFmpeg;
using osu.Game.Rulesets.BmsRuleset.Media.Video.Supplemental;
using osu.Game.Rulesets.BmsRuleset.UI.HudComponents;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Game.Rulesets.BmsRuleset.Tests.Normal.Bga;

[TestFixture]
public class BmsMemoryLifecycleAuditTest
{
    [Test]
    public void ObserveReturnedUploadArrayBeforeDeferredConsumption()
    {
        var pool = new ReusablePixelPool();
        using var frame = new BmsSupplementalVideoFrame(pool, pool.Rent(1), 1, 1, 0);
        var upload = frame.CreateUpload();
        Assert.That(upload.Data[0].R, Is.Zero);

        // The renderer retains the upload object when SetData queues it. Model that delay
        // without creating a real GPU device or relying on a racy rent from the shared pool.
        upload.Dispose();
        var next = pool.Rent(1);
        next[0] = new Rgba32(255, 0, 0, 255);
        TestContext.WriteLine($"AUDIT upload reused={ReferenceEquals(next, pool.Buffer)} deferredRed={upload.Data[0].R}");
        Assert.That(upload.Data[0].R, Is.EqualTo(255));
    }

    [Test]
    public void ObservePreloadedBytesAndStreamOwnership()
    {
        var root = Path.Combine(TestContext.CurrentContext.TestDirectory, "bga_fixtures");
        using var store = new BmsBgaDisplay.BmsBgaResourceStore(root, null);
        string[] paths = ["h264.mp4", "mpeg1.mpg", "mpeg4.avi", "wmv2.wmv"];
        store.Preload(paths);
        var retained = paths.Select(path => store.Get(path)!).ToArray();
        Assert.That(retained.Select(bytes => bytes.Length), Is.All.Positive);

        using var stream = store.GetStream(paths[0])!;
        var marker = retained[0][0];
        retained[0][0] ^= 0xff;
        try
        {
            Assert.That(stream.ReadByte(), Is.EqualTo(retained[0][0]));
            Assert.That(store.Get(paths[0]), Is.SameAs(retained[0]));
            store.Dispose();
            stream.Position = 0;
            Assert.That(stream.ReadByte(), Is.EqualTo(retained[0][0]));
        }
        finally
        {
            retained[0][0] = marker;
        }

        TestContext.WriteLine($"AUDIT store files={paths.Length} compressedBytes={retained.Sum(bytes => (long)bytes.Length)} streamSharesBytes=true");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void ObserveVideoWorkerLifecycle(int workerCount)
    {
        if (workerCount > 0 && !BmsSupplementalFFmpegFuncs.TryCreate(out _, out var error))
            Assert.Ignore(error ?? "Supplemental FFmpeg backend unavailable.");

        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "bga_fixtures", "mpeg1.mpg");
        using var process = Process.GetCurrentProcess();
        record("start", 0);
        var maxQueued = 0;
        var maxStartMs = 0d;
        var maxDisposeMs = 0d;

        for (var cycle = 1; cycle <= 25; cycle++)
        {
            // Each source gets separate compressed bytes, as it would on a chart transition.
            var sources = Enumerable.Range(0, workerCount)
                                    .Select(_ => new BmsSupplementalVideoFrameSource(File.ReadAllBytes(path))).ToArray();
            try
            {
                var timer = Stopwatch.StartNew();
                foreach (var source in sources)
                {
                    source.SetTargetTime(10);
                    source.Start();
                }

                foreach (var source in sources)
                    Assert.That(SpinWait.SpinUntil(() => BmsVideoTestAccess.GetQueuedFrameCount(source) > 0, TimeSpan.FromSeconds(3)), Is.True);

                maxStartMs = Math.Max(maxStartMs, timer.Elapsed.TotalMilliseconds);
                // This fixture ends before the fixed target. Once the first frame is queued,
                // stale frames may be discarded. Rewind the target without consuming the queue.
                foreach (var source in sources)
                    source.SetTargetTime(0);

                if (sources.Length > 0)
                    Thread.Sleep(15);

                foreach (var source in sources)
                    maxQueued = Math.Max(maxQueued, BmsVideoTestAccess.GetQueuedFrameCount(source));

                if (cycle is 1 or 25)
                    record("active", cycle);
            }
            finally
            {
                var timer = Stopwatch.StartNew();
                foreach (var source in sources)
                    source.Dispose();
                maxDisposeMs = Math.Max(maxDisposeMs, timer.Elapsed.TotalMilliseconds);
            }

            foreach (var source in sources)
            {
                Assert.That(BmsVideoTestAccess.IsWorkerCompleted(source), Is.True);
                Assert.That(BmsVideoTestAccess.GetQueuedFrameCount(source), Is.Zero);
            }

            if (cycle is 1 or 5 or 10 or 15 or 20 or 25)
                record("disposed", cycle);
        }

        TestContext.WriteLine(FormattableString.Invariant($"AUDIT workers={workerCount} cycles=25 maxQueuedSample={maxQueued} maxStartMs={maxStartMs:F3} maxDisposeMs={maxDisposeMs:F3}"));

        void record(string phase, int cycle)
        {
            process.Refresh();
            TestContext.WriteLine($"AUDIT workers={workerCount} cycle={cycle} phase={phase} private={process.PrivateMemorySize64} working={process.WorkingSet64} managedEstimate={GC.GetTotalMemory(false)} gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} allocated={GC.GetTotalAllocatedBytes()}");
        }
    }

    [Test]
    public void ObserveQueueCanExceedNominalFrameCount()
    {
        if (!BmsSupplementalFFmpegFuncs.TryCreate(out _, out var error))
            Assert.Ignore(error ?? "Supplemental FFmpeg backend unavailable.");

        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "bga_fixtures", "mpeg1.mpg");
        using var source = new BmsSupplementalVideoFrameSource(File.ReadAllBytes(path));
        source.SetTargetTime(0.15);
        source.Start();
        Assert.That(SpinWait.SpinUntil(() => BmsVideoTestAccess.GetQueuedFrameCount(source) > 3, TimeSpan.FromSeconds(3)), Is.True);
        TestContext.WriteLine($"AUDIT queue targetSeconds=0.15 count={BmsVideoTestAccess.GetQueuedFrameCount(source)}");
    }

    [Test]
    public void ObserveCompressedVideoReleasedAcrossChartResourceCycles()
    {
        if (!BmsSupplementalFFmpegFuncs.TryCreate(out _, out var error))
            Assert.Ignore(error ?? "Supplemental FFmpeg backend unavailable.");

        var directory = findOutlawDirectory();
        var previousInputs = new WeakReference<byte[]>[25];
        using var process = Process.GetCurrentProcess();
        for (var index = 0; index < previousInputs.Length; index++)
        {
            previousInputs[index] = runResourceCycle(directory);
            if (index is 0 or 4 or 9 or 14 or 19 or 24)
            {
                process.Refresh();
                TestContext.WriteLine($"AUDIT realMpegCycle={index + 1} private={process.PrivateMemorySize64} working={process.WorkingSet64} managedEstimate={GC.GetTotalMemory(false)} gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
            }
        }

        // Collect only at the separate reachability checkpoint, after recording the natural run.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var stillReachable = previousInputs.Count(input => input.TryGetTarget(out _));
        TestContext.WriteLine($"AUDIT reachability cycles={previousInputs.Length} liveCompressedArraysAfterGc={stillReachable}");
        Assert.That(stillReachable, Is.Zero);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<byte[]> runResourceCycle(string directory)
    {
        using var store = new BmsBgaDisplay.BmsBgaResourceStore(directory, null);
        var bytes = store.Get("bga.mpg")!;
        using var source = new BmsSupplementalVideoFrameSource(bytes);
        source.Start();
        Assert.That(SpinWait.SpinUntil(() => BmsVideoTestAccess.GetQueuedFrameCount(source) > 0, TimeSpan.FromSeconds(3)), Is.True);
        source.Dispose();
        Assert.That(BmsVideoTestAccess.IsWorkerCompleted(source), Is.True);
        Assert.That(BmsVideoTestAccess.GetQueuedFrameCount(source), Is.Zero);
        return new WeakReference<byte[]>(bytes);
    }

    private static string findOutlawDirectory([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "../../bms_test_songs/103_outlaw_ogg"));

    private sealed class ReusablePixelPool : ArrayPool<Rgba32>
    {
        internal Rgba32[] Buffer { get; } = new Rgba32[1];
        private bool available = true;

        public override Rgba32[] Rent(int minimumLength)
        {
            if (!available || minimumLength > Buffer.Length)
                throw new InvalidOperationException("The diagnostic buffer is already leased or is too small.");

            available = false;
            return Buffer;
        }

        public override void Return(Rgba32[] array, bool clearArray = false)
        {
            if (available || !ReferenceEquals(array, Buffer))
                throw new InvalidOperationException("Unexpected buffer return.");

            available = true;
            if (clearArray)
                Array.Clear(array);
        }
    }
}
