using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;
using PeerSharp.Tests.Core;

namespace PeerSharp.Tests.Api;

public sealed class TorrentLifecycleModelTests
{
    private const int PieceSize = ProtocolConstants.BlockSize;
    private static readonly MethodInfo RebalanceQueue = typeof(ClientEngine).GetMethod("RebalanceQueueAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo WaitForQueueStarts = typeof(ClientEngine).GetMethod("WaitForQueueStartsAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory(Timeout = 90000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task EngineAgreesWithTheLifecycleModelAfterEachCommand(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var commands = GenerateCommands(seed, scenario.Random);
        scenario.Record($"Commands: {string.Join(", ", commands)}");
        try
        {
            await RunCommandsAsync(commands, ct);
        }
        catch (ModelMismatch failure)
        {
            var shortest = await ReduceAsync(commands[..(failure.Step + 1)], failure.Rule, ct);
            scenario.Record($"Reduced commands: {string.Join(", ", shortest)}");
            throw;
        }
    }

    private static Command[] GenerateCommands(int seed, Random random)
    {
        // Cover persistence of explicit stops, waiting queue entries, and removals in the CI seeds.
        Command[][] prefixes =
        [
            [Command.StartA, Command.Pause, Command.StopA, Command.Reopen, Command.Resume],
            [Command.StartA, Command.QueueB, Command.Rebalance, Command.Pause, Command.Rebalance, Command.Reopen, Command.Resume],
            [Command.StartB, Command.Pause, Command.RemoveB, Command.Reopen, Command.Resume],
            [Command.Pause, Command.StartB, Command.Reopen, Command.Resume, Command.StopForNowB, Command.Rebalance]
        ];
        var choices = Enum.GetValues<Command>();
        var generated = Enumerable.Range(0, 22).Select(_ => choices[random.Next(choices.Length)]).ToArray();
        return [.. prefixes[seed % prefixes.Length], .. generated, Command.Reopen, Command.Resume, Command.Rebalance];
    }

    private static async Task<Command[]> ReduceAsync(Command[] commands, string rule, CancellationToken ct)
    {
        // A shorter history must reproduce the same invariant failure on a fresh session.
        for (int index = 0; index < commands.Length; index++)
        {
            var candidate = commands.Where((_, i) => i != index).ToArray();
            try
            {
                await RunCommandsAsync(candidate, ct);
            }
            catch (ModelMismatch failure)
            {
                if (failure.Rule != rule) continue;
                commands = candidate[..(failure.Step + 1)];
                index = -1;
            }
        }
        return commands;
    }

    private static async Task RunCommandsAsync(Command[] commands, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpLifecycleModel-");
        try
        {
            await RunInDirectoryAsync(root.FullName, commands, ct);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task RunInDirectoryAsync(string path, Command[] commands, CancellationToken ct)
    {
        var clock = new FakeTimeProvider();
        var model = new LifecycleModel();
        var payload = new byte[PieceSize];
        Array.Fill(payload, (byte)0x39);
        TorrentFile[] files = [MakeTorrent("a.bin", payload), MakeTorrent("b.bin", payload)];
        var engine = CreateEngine(path, clock);
        try
        {
            await engine.InitializeAsync(ct);
            for (int i = 0; i < files.Length; i++)
            {
                await engine.AddTorrentAsync(files[i], new AddTorrentOptions(path) { StartImmediately = false, QueuePriority = 2 - i }, ct);
            }

            for (int step = 0; step < commands.Length; step++)
            {
                Command command = commands[step];
                if (command == Command.Reopen)
                {
                    await engine.SaveSessionAsync(ct);
                    await engine.DisposeAsync();
                    engine = CreateEngine(path, clock);
                    await engine.InitializeAsync(ct);
                    await DrainQueueStartsAsync(engine, ct);
                }
                else
                {
                    int target = Target(command);
                    await ApplyAsync(engine, files[target].InfoHash, model.States[target], command, ct);
                }

                model.Apply(command);
                if (command is Command.StartA or Command.StartB or Command.Resume)
                {
                    // Manual starts can use a slot immediately. The queue applies its ceiling at
                    // the next pass; the model compares the settled result of that pass.
                    await RebalanceAsync(engine, ct);
                    model.Apply(Command.Rebalance);
                }
                model.Check(engine, files, step, command);
            }
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    private static async Task ApplyAsync(ClientEngine engine, InfoHash hash, TorrentIntent before, Command command, CancellationToken ct)
    {
        if (command == Command.Pause)
        {
            await engine.PauseAsync(ct);
            return;
        }
        if (command == Command.Resume)
        {
            await engine.ResumeAsync(ct);
            return;
        }
        if (command == Command.Rebalance)
        {
            await RebalanceAsync(engine, ct);
            return;
        }
        if (before == TorrentIntent.Removed) return;

        var torrent = engine.GetTorrent(hash)!;
        switch (command)
        {
            case Command.StartA or Command.StartB:
                if (before != TorrentIntent.Running) await torrent.StartAsync(ct);
                break;
            case Command.StopA or Command.StopB:
                torrent.QueueAutoStart = false;
                await torrent.StopAsync(ct);
                break;
            case Command.StopForNowA or Command.StopForNowB:
                await torrent.StopAsync(ct);
                break;
            case Command.QueueA or Command.QueueB:
                if (before == TorrentIntent.Stopped) torrent.QueueAutoStart = true;
                break;
            case Command.RemoveA or Command.RemoveB:
                await engine.RemoveTorrentAsync(torrent, cancellationToken: ct);
                await DrainQueueStartsAsync(engine, ct);
                break;
        }
    }

    internal static TorrentFile MakeTorrent(string name, byte[] payload) => new PeerSharp.Core.TorrentFileBuilder()
        .WithName(name).WithPieceLength(PieceSize).AddFile(name, payload).Build();

    internal static ClientEngine CreateEngine(string path, FakeTimeProvider clock, bool queue = true, IFileHandleCache? fileHandleCache = null)
        => ClientEngine.CreateWithServices(new TorrentClientOptions
        {
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = path, EnableReadAhead = false },
                Connection = { TcpPort = 0, UdpPort = 0, EnableTcpIn = false, EnableTcpOut = false,
                    EnableLsd = false, EnableUtpIn = false, EnableUtpOut = false, UpnpPortMapping = false, NatPmpPortMapping = false },
                Dht = { Enabled = false },
                Queue = { Enabled = queue, MaxActiveDownloads = 1 },
                Session = { Enabled = true, SessionPath = Path.Combine(path, "session"), AutoSaveIntervalSeconds = 0 }
            }
        }, clock, fileHandleCache);

    private static async Task RebalanceAsync(ClientEngine engine, CancellationToken ct)
    {
        await ((Task)RebalanceQueue.Invoke(engine, [ct])!).WaitAsync(ct);
        await DrainQueueStartsAsync(engine, ct);
    }

    private static Task DrainQueueStartsAsync(ClientEngine engine, CancellationToken ct) =>
        ((Task)WaitForQueueStarts.Invoke(engine, null)!).WaitAsync(ct);

    private static int Target(Command command) => command switch
    {
        Command.StartB or Command.StopB or Command.StopForNowB or Command.QueueB or Command.RemoveB => 1,
        _ => 0
    };

    private enum Command
    {
        StartA, StartB, StopA, StopB, StopForNowA, StopForNowB, QueueA, QueueB,
        Pause, Resume, Rebalance, Reopen, RemoveA, RemoveB
    }

    private enum TorrentIntent { Stopped, Running, Queued, Held, Removed }

    private sealed class LifecycleModel
    {
        public TorrentIntent[] States { get; } = [TorrentIntent.Stopped, TorrentIntent.Stopped];
        private bool _paused;

        public void Apply(Command command)
        {
            int target = Target(command);
            switch (command)
            {
                case Command.Pause:
                    _paused = true;
                    Replace(TorrentIntent.Running, TorrentIntent.Held);
                    return;
                case Command.Resume:
                    _paused = false;
                    Replace(TorrentIntent.Held, TorrentIntent.Running);
                    return;
                case Command.Reopen or Command.Rebalance:
                    FillQueueSlot();
                    return;
            }

            var before = States[target];
            if (before == TorrentIntent.Removed) return;
            States[target] = command switch
            {
                Command.StartA or Command.StartB => _paused ? TorrentIntent.Held : TorrentIntent.Running,
                Command.StopA or Command.StopB => TorrentIntent.Stopped,
                Command.StopForNowA or Command.StopForNowB => before switch
                {
                    TorrentIntent.Running => TorrentIntent.Queued,
                    TorrentIntent.Held => TorrentIntent.Stopped,
                    _ => before
                },
                Command.QueueA or Command.QueueB when before == TorrentIntent.Stopped => TorrentIntent.Queued,
                Command.RemoveA or Command.RemoveB => TorrentIntent.Removed,
                _ => before
            };
            if (States[target] == TorrentIntent.Removed) FillQueueSlot();
        }

        private void Replace(TorrentIntent before, TorrentIntent after)
        {
            for (int i = 0; i < States.Length; i++)
            {
                if (States[i] == before) States[i] = after;
            }
        }

        private void FillQueueSlot()
        {
            if (_paused) return;
            if (States[0] == TorrentIntent.Running && States[1] == TorrentIntent.Running)
            {
                States[1] = TorrentIntent.Queued;
            }
            if (States.Contains(TorrentIntent.Running)) return;
            int waiting = Array.IndexOf(States, TorrentIntent.Queued);
            if (waiting >= 0) States[waiting] = TorrentIntent.Running;
        }

        public void Check(ClientEngine engine, TorrentFile[] files, int step, Command command)
        {
            void Require(bool condition, string rule, string detail)
            {
                if (!condition) throw new ModelMismatch(rule, step, $"Step {step + 1} ({command}) [{rule}]: {detail}");
            }

            Require(engine.IsPaused == _paused, "session pause", $"Expected paused={_paused}.");
            Require(engine.GetTorrents().Count == States.Count(state => state != TorrentIntent.Removed),
                "registry size", $"Expected [{string.Join(", ", States)}].");
            for (int i = 0; i < files.Length; i++)
            {
                var torrent = (Torrent?)engine.GetTorrent(files[i].InfoHash);
                var expected = States[i];
                Require((torrent == null) == (expected == TorrentIntent.Removed), "registry membership",
                    $"Torrent {i} should be {expected}.");
                if (torrent == null) continue;
                Require(torrent.Started == (expected == TorrentIntent.Running), "running state",
                    $"Torrent {i} should be {expected}; actual {torrent.State}, pieces {torrent.PiecesReceived}, held={engine.IsHolding(torrent)}.");
                Require(engine.IsHolding(torrent) == (expected == TorrentIntent.Held), "pause intent",
                    $"Torrent {i} should be {expected}; actual held={engine.IsHolding(torrent)}.");
                Require(torrent.QueueAutoStart == (expected is TorrentIntent.Queued or TorrentIntent.Running), "queue intent",
                    $"Torrent {i} should be {expected}; actual QueueAutoStart={torrent.QueueAutoStart}.");
            }
        }
    }

    private sealed class ModelMismatch(string rule, int step, string message) : Exception(message)
    {
        public string Rule { get; } = rule;
        public int Step { get; } = step;
    }
}
