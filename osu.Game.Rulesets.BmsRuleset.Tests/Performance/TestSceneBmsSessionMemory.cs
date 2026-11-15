#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Video;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Rulesets.BmsRuleset.IO.Import;
using osu.Game.Rulesets.BmsRuleset.Media.Video.Supplemental;
using osu.Game.Rulesets.BmsRuleset.UI.HudComponents;
using osu.Game.Rulesets.BmsRuleset.UI.SongSelect;
using osu.Game.Screens.Play;
using osu.Game.Tests.Visual;

namespace osu.Game.Rulesets.BmsRuleset.Tests.Performance;

[HeadlessTest]
public partial class TestSceneBmsSessionMemory : OsuGameTestScene
{
    // The framework sets this only after Drawable disposal. Parent removal alone leaves async cleanup pending.
    private static readonly PropertyInfo is_disposed =
        typeof(Drawable).GetProperty("IsDisposed", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Resolved]
    private GameHost host { get; set; } = null!;

    private StreamWriter? report;

    [TearDown]
    public void CloseReport() => report?.Dispose();

    [Test]
    public void TestAlternateTwelveMapsWithBga()
    {
        var manifestPath = Environment.GetEnvironmentVariable("BMS_SESSION_MEMORY_MANIFEST");
        var reportPath = Environment.GetEnvironmentVariable("BMS_SESSION_MEMORY_REPORT");
        if (string.IsNullOrEmpty(manifestPath) || string.IsNullOrEmpty(reportPath))
            Assert.Ignore("Set BMS_SESSION_MEMORY_MANIFEST and BMS_SESSION_MEMORY_REPORT for the session diagnostic.");

        var corpus = File.ReadAllLines(manifestPath!).Select(line => line.Split('\t'))
                         .Select(parts => (Id: parts[0], Path: parts[1], Hash: parts[2])).ToArray();
        Assert.That(corpus, Has.Length.EqualTo(12));
        Assert.That(corpus.Select(chart => chart.Hash).Distinct().Count(), Is.EqualTo(12));

        report = new StreamWriter(File.Open(reportPath!, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var elapsed = Stopwatch.StartNew();
        Task import = null!;
        MemorySongSelect selector = null!;
        BeatmapInfo[] maps = null!;
        var allowInactive = Environment.GetEnvironmentVariable("BMS_SESSION_MEMORY_ALLOW_INACTIVE") == "1";
        var activeWindowRequired = typeof(PlayerLoader).GetProperty("WindowShouldBeActiveForGameplayStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PlayerLoader? loader = null;
        Player? player = null;
        MasterGameplayClockContainer? clock = null;
        var oldPlayers = new List<WeakReference<Player>>();
        var oldDisplays = new List<WeakReference<BmsBgaDisplay>>();
        double firstFrameTime = 0;
        long disposedAt = 0;
        var relevantChartTime = 0.0;

        AddStep("import local corpus into the isolated osu! game", () =>
        {
            import = new BmsFileImporter(Game.Realm, Game.Storage).Import(corpus.Select(chart => chart.Path).ToArray());
        });
        AddUntilStep("import completed", () => import.IsCompleted);
        AddStep("find charts by their content hashes", () =>
        {
            import.GetAwaiter().GetResult();
            var bms = Game.Realm.Run(realm => realm.All<BeatmapInfo>().AsEnumerable()
                                                     .Where(map => map.Ruleset.ShortName == "bms")
                                                     .Select(map => map.Detach()).ToArray());
            maps = corpus.Select(chart => bms.Single(map => string.Equals(map.Hash, chart.Hash, StringComparison.OrdinalIgnoreCase))).ToArray();
            Game.Ruleset.Value = maps[0].Ruleset;
            Game.Audio.Volume.Value = 0;
            report!.WriteLine(JsonSerializer.Serialize(new { Renderer = host.Renderer.GetType().Name, Backend = host.ResolvedRenderer.ToString(), ProcessId = Environment.ProcessId, AllowInactive = allowInactive, Protocol = "accelerated-early-exit" }));
        });
        PushAndConfirm(() => selector = new MemorySongSelect());

        for (var index = 0; index < corpus.Length; index++)
        {
            var cycle = index;
            var id = corpus[cycle].Id;
            AddStep($"{id}: select chart", () =>
            {
                Game.Beatmap.Value = Game.BeatmapManager.GetWorkingBeatmap(maps[cycle]);
                Game.SelectedMods.Value = [maps[cycle].Ruleset.CreateInstance().GetAutoplayMod()!];
                record(cycle, id, "before");
            });
            AddUntilStep($"{id}: correct chart selected", () => Game.Beatmap.Value.BeatmapInfo.Hash == maps[cycle].Hash);
            AddStep($"{id}: start song", () =>
            {
                // The production loader requires an active window. A diagnostic runs unattended.
                if (!allowInactive && !host.IsActive.Value && host.Window is { } window)
                    host.InputThread.Scheduler.Add(() => window.Raise());
                selector.StartSelected();
            });
            AddUntilStep($"{id}: enter loader", () => (loader = Game.ScreenStack.CurrentScreen as PlayerLoader) != null);
            AddStep($"{id}: sample while loading", () =>
            {
                // The loader otherwise waits for focus even if rendering and replay can run in the background.
                if (allowInactive)
                    activeWindowRequired.SetValue(loader, false);
                record(cycle, id, "loading");
            });
            AddUntilStep($"{id}: enter player", () => (player = Game.ScreenStack.CurrentScreen as Player) is { IsLoaded: true, LoadedBeatmapSuccessfully: true, Alpha: 1 });
            AddStep($"{id}: begin playback", () =>
            {
                Assert.That(player!.GameplayState.Beatmap.BeatmapInfo.Hash, Is.EqualTo(maps[cycle].Hash));
                clock = player!.ChildrenOfType<MasterGameplayClockContainer>().Single();
                record(cycle, id, "gameplay-start");
                if (clock.CurrentTime < 0)
                    clock.Seek(0);
            });
            AddUntilStep($"{id}: BGA has decoded a frame", () => videoHasFrame(player!));
            AddStep($"{id}: sample after first valid frame", () =>
            {
                firstFrameTime = clock!.CurrentTime;
                record(cycle, id, "first-bga");
                // Exercise advancing video and audio before exiting, while capping wall time per chart.
                clock.UserPlaybackRate.MaxValue = 4;
                clock.UserPlaybackRate.Value = 4;
            });
            AddUntilStep($"{id}: six chart seconds elapsed with BGA", () => clock!.CurrentTime >= firstFrameTime + 6000);
            AddStep($"{id}: exit gameplay", () =>
            {
                record(cycle, id, "active");
                oldPlayers.Add(new WeakReference<Player>(player!));
                oldDisplays.AddRange(player!.ChildrenOfType<BmsBgaDisplay>().Select(display => new WeakReference<BmsBgaDisplay>(display)));
                relevantChartTime = clock!.CurrentTime;
                record(cycle, id, "exit");
                selector.MakeCurrent();
            });
            AddUntilStep($"{id}: selector resumed", () => selector.IsCurrentScreen());
            AddStep($"{id}: sample returned selector", () => record(cycle, id, "selector"));
            AddUntilStep($"{id}: previous player and loader disposed", () => disposed(player!) && disposed(loader!));
            AddStep($"{id}: sample after disposal", () =>
            {
                record(cycle, id, "disposed");
                (player, loader, clock) = (null, null, null);
                disposedAt = elapsed.ElapsedMilliseconds;
            });
            AddUntilStep($"{id}: 750 ms after disposal", () => elapsed.ElapsedMilliseconds - disposedAt >= 750);
            AddStep($"{id}: sample stabilized selector", () => record(cycle, id, "stable"));
        }

        if (Environment.GetEnvironmentVariable("BMS_SESSION_MEMORY_REACHABILITY") == "1")
        {
            Task collect = null!;
            AddStep("start separate reachability probe", () =>
            {
                record(corpus.Length - 1, corpus[^1].Id, "reachability-before-gc");
                collect = Task.Run(() =>
                {
                    GC.Collect(2, GCCollectionMode.Forced, true, false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, true, false);
                });
            });
            AddUntilStep("controlled collections completed", () => collect.IsCompleted);
            AddStep("record reachable objects after collections", () =>
            {
                collect.GetAwaiter().GetResult();
                record(corpus.Length - 1, corpus[^1].Id, "reachability-after-gc");
            });
            if (Environment.GetEnvironmentVariable("BMS_SESSION_MEMORY_HOLD") == "1")
                AddWaitStep("hold the isolated process for a heap capture", 150);
        }

        AddStep("close diagnostic report", CloseReport);
        return;

        void record(int cycle, string id, string phase)
        {
            using var process = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            report.WriteLine(JsonSerializer.Serialize(new
            {
                Cycle = cycle + 1,
                Chart = id,
                Phase = phase,
                ElapsedMs = elapsed.Elapsed.TotalMilliseconds,
                ChartMs = clock?.CurrentTime ?? (phase == "stable" ? relevantChartTime : (double?)null),
                ActualPlaybackRate = clock?.Rate,
                WindowActive = host.IsActive.Value,
                process.PrivateMemorySize64,
                process.WorkingSet64,
                process.PeakWorkingSet64,
                ThreadCount = process.Threads.Count,
                ManagedEstimateBytes = GC.GetTotalMemory(false),
                GcHeapBytesAtLastCollection = gc.HeapSizeBytes,
                GcCommittedBytesAtLastCollection = gc.TotalCommittedBytes,
                Collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
                EarlierPlayersReachable = oldPlayers.Count(p => p.TryGetTarget(out _)),
                EarlierDisplaysReachable = oldDisplays.Count(d => d.TryGetTarget(out _)),
            }));
        }
    }

    private static bool disposed(Drawable drawable) => is_disposed.GetValue(drawable) is true;

    private static bool videoHasFrame(Player player) =>
        player.ChildrenOfType<BmsBgaDisplay>().Any(display =>
            display.ChildrenOfType<BmsSupplementalVideoDrawable>().Any(video => video.ChildrenOfType<Sprite>().Any(sprite => sprite.Texture != null))
            || display.ChildrenOfType<Video>().Any(video => video.FramesProcessed > 0));

    internal partial class DesktopRun(string output) : OsuGameBase
    {
        private readonly Stopwatch timeout = Stopwatch.StartNew();
        private bool finished;
        public int ExitCode { get; private set; } = 1;

        public override void SetHost(GameHost gameHost)
        {
            Storage = gameHost.GetStorage(Path.Combine(output, "storage"));
            base.SetHost(gameHost);
        }

        protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults() => new Dictionary<FrameworkSetting, object>
        {
            [FrameworkSetting.WindowMode] = WindowMode.Windowed,
            [FrameworkSetting.WindowedSize] = new System.Drawing.Size(1280, 720),
            [FrameworkSetting.FrameSync] = FrameSync.Unlimited,
            [FrameworkSetting.ExecutionMode] = ExecutionMode.MultiThreaded,
            [FrameworkSetting.VolumeUniversal] = 0.0,
        };

        protected override void LoadComplete()
        {
            base.LoadComplete();
            Host.AllowBenchmarkUnlimitedFrames = true;
            Host.MaximumUpdateHz = 240;
            Host.MaximumInactiveHz = 240;
            Host.MaximumDrawHz = 120;
            if (Host.DrawThread != null)
                Host.DrawThread.InactiveHz = 120;

            var scene = new TestSceneBmsSessionMemory();
            LoadComponentAsync(scene, loaded =>
            {
                Add(loaded);
                loaded.SetUpSteps();
                loaded.TestAlternateTwelveMapsWithBga();
                loaded.RunAllSteps(() => finish(null), (_, error) => finish(error));
            });
        }

        protected override void Update()
        {
            base.Update();
            if (timeout.Elapsed > TimeSpan.FromMinutes(6))
                finish(new TimeoutException("The session exceeded six minutes."));
        }

        private void finish(Exception? error)
        {
            if (finished)
                return;

            finished = true;
            if (error != null)
                Console.Error.WriteLine(error);
            ExitCode = error == null ? 0 : 1;
            Exit();
        }
    }

    private partial class MemorySongSelect : BmsSoloSongSelect
    {
        public void StartSelected() => OnStart();
    }
}
