using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MMOCore.GameServer;
using MMOCore.GameServer.World;
using Xunit;

namespace MMOCore.GameServer.Tests;

/// <summary>
/// Drives the authoritative 20 Hz sim headlessly and locks its behaviour.
///
/// Why reflection: SimulateTick and the Tick* helpers are private on the partial Server, and
/// widening them would change production code in a Band C file. Every reflected member is
/// resolved ONCE in the harness constructor and throws by name if missing, so a rename fails
/// loudly rather than leaving a test that silently exercises nothing.
///
/// Why this is safe without sockets: Server's constructor takes no arguments and binds no
/// listeners (_tcpListener/_udpClient stay null until RunAsync). Test sessions leave
/// UdpEndPoint null, which is the condition SendPerClientSnapshots skips on - so no UDP send
/// is ever reached.
/// </summary>
internal sealed class ServerTickHarness
{
    private readonly Server _server;
    private readonly MethodInfo _simulateTick, _rebuildGrids, _tickAreaEffects, _tickStatusEffects, _tickProjectiles;
    private readonly ConcurrentDictionary<ulong, PlayerSession> _sessions;
    private readonly ConcurrentDictionary<int, PlayerSession> _byId;

    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    private static MethodInfo M(string name) =>
        typeof(Server).GetMethod(name, Priv)
        ?? throw new InvalidOperationException(
            $"Server.{name} not found. The tick loop changed - update ServerTickHarness so this " +
            $"suite keeps testing the real tick instead of silently passing.");

    private static T F<T>(Server s, string name) =>
        (T)(typeof(Server).GetField(name, Priv)?.GetValue(s)
            ?? throw new InvalidOperationException($"Server.{name} not found - update ServerTickHarness."));

    public ServerTickHarness()
    {
        _server            = new Server();
        _simulateTick      = M("SimulateTick");
        _rebuildGrids      = M("RebuildSpatialGrids");
        _tickAreaEffects   = M("TickAreaEffects");
        _tickStatusEffects = M("TickStatusEffects");
        _tickProjectiles   = M("TickProjectiles");
        _sessions          = F<ConcurrentDictionary<ulong, PlayerSession>>(_server, "_sessions");
        _byId              = F<ConcurrentDictionary<int, PlayerSession>>(_server, "_byId");
    }

    public PlayerSession AddPlayer(int id, string name)
    {
        // Unconnected TcpClient: never dialled, so nothing is opened. SendFramed would throw
        // on GetStream() but it runs inside Task.Run with a bare catch, and with UdpEndPoint
        // null the snapshot pass is skipped entirely.
        var s = new PlayerSession(id, name, (ulong)id, new TcpClient());
        _sessions[(ulong)id] = s;
        _byId[id] = s;
        return s;
    }

    /// <summary>
    /// One tick, mirroring the body of TickLoopAsync in Server.NetTick.cs, in the same order.
    /// SendPerClientSnapshots is deliberately NOT called: it dereferences _udpClient for any
    /// viewer with a non-null UdpEndPoint, and it produces no sim-state change worth hashing.
    /// If a step is ever added to the real loop, add it here too - see Harness_ResolvesEveryTickMethod.
    /// </summary>
    public void Tick(float dt, long nowMs)
    {
        _simulateTick.Invoke(_server, new object[] { dt, nowMs });
        _rebuildGrids.Invoke(_server, null);
        _tickAreaEffects.Invoke(_server, new object[] { nowMs });
        _tickStatusEffects.Invoke(_server, new object[] { nowMs });
        _tickProjectiles.Invoke(_server, new object[] { dt, nowMs });
    }

    /// <summary>
    /// Order-independent digest of authoritative player state. Sessions are SORTED by PlayerId
    /// before hashing on purpose: _sessions is a ConcurrentDictionary whose enumeration order is
    /// not guaranteed, and hashing in traversal order would make this test flake on a detail that
    /// is not the sim's behaviour. (The ordering risk itself is real but belongs upstream.)
    /// Floats are formatted invariantly at a fixed precision so the digest is culture-stable.
    /// </summary>
    public string StateHash()
    {
        var sb = new StringBuilder();
        foreach (var s in _sessions.Values.OrderBy(s => s.PlayerId))
        {
            sb.Append(s.PlayerId).Append('|')
              .Append(N(s.X)).Append(',').Append(N(s.Y)).Append(',').Append(N(s.Z)).Append(',').Append(N(s.Yaw)).Append('|')
              .Append(s.Hp).Append('/').Append(s.MaxHp).Append('|')
              .Append(s.Alive ? 1 : 0).Append('|')
              .Append(s.Level).Append('|').Append(s.Xp).Append('|').Append(s.Gold)
              .Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static string N(float f) => f.ToString("0.0000", CultureInfo.InvariantCulture);
}

public class TickLoopTests
{
    private const float Dt = 1f / 20f;   // NetworkConfig.ServerTickRate
    private const int TickMs = 50;

    /// <summary>Scripted, seeded input: the same every run, so the sim is the only variable.</summary>
    private static void DriveScriptedInputs(List<PlayerSession> players, int tick)
    {
        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            var phase = (tick + i * 7) % 40;
            p.InputDirX = MathF.Cos(phase * 0.157f);
            p.InputDirZ = MathF.Sin(phase * 0.157f);
        }
    }

    private static ServerTickHarness RunScenario(int ticks, int playerCount = 6)
    {
        var h = new ServerTickHarness();
        var players = Enumerable.Range(1, playerCount).Select(i => h.AddPlayer(i, $"Bot{i}")).ToList();
        for (var t = 0; t < ticks; t++)
        {
            DriveScriptedInputs(players, t);
            h.Tick(Dt, t * (long)TickMs);
        }
        return h;
    }

    /// <summary>
    /// Rule-15b guard: every reflected member must resolve. Without this, a rename turns the
    /// whole suite into a test that passes while exercising nothing.
    /// </summary>
    [Fact]
    public void Harness_ResolvesEveryTickMethod()
    {
        var ex = Record.Exception(() => new ServerTickHarness());
        Assert.Null(ex);
    }

    /// <summary>Server must construct without binding TCP/UDP or reaching Postgres/Redis
    /// (it falls back to InMemoryPlayerStore). If this breaks, headless testing is gone.</summary>
    [Fact]
    public void Server_ConstructsHeadlessly_WithoutBindingListeners()
    {
        var s = new Server();
        const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var name in new[] { "_tcpListener", "_udpClient" })
        {
            // Name the field explicitly rather than null-forgiving: a rename must say WHICH
            // field vanished, not surface as a bare NullReferenceException (rule 15b).
            var f = typeof(Server).GetField(name, P);
            Assert.True(f is not null, $"Server.{name} not found - update this test; headless construction can no longer be asserted.");
            Assert.True(f!.GetValue(s) is null, $"Server.{name} was non-null straight after construction - the ctor now binds a socket and headless testing is broken.");
        }
    }

    /// <summary>
    /// THE determinism invariant. Two independent servers, identical scripted inputs, identical
    /// tick count -> byte-identical authoritative state. Catches unseeded RNG, DateTime.Now
    /// leaking into the sim, and the silent-lost-mutation class (a mutable struct in a
    /// Dictionary&lt;int,T&gt;) that CLAUDE.md names as this project's most expensive failure.
    /// </summary>
    [Fact]
    public void IdenticalInputs_ProduceIdenticalStateHash()
    {
        Assert.Equal(RunScenario(200).StateHash(), RunScenario(200).StateHash());
    }

    /// <summary>
    /// The sim must advance on TICK COUNT, not elapsed wall time. dt is a fixed timestep and
    /// nowMs is passed in, so a run with real delays between ticks must land on the same state
    /// as one with none. A regression here is a sim that behaves differently on a loaded machine.
    /// </summary>
    [Fact]
    public void Simulation_AdvancesOnTickCount_NotWallClock()
    {
        var fast = RunScenario(40);

        var slow = new ServerTickHarness();
        var players = Enumerable.Range(1, 6).Select(i => slow.AddPlayer(i, $"Bot{i}")).ToList();
        for (var t = 0; t < 40; t++)
        {
            DriveScriptedInputs(players, t);
            slow.Tick(Dt, t * (long)TickMs);
            if (t % 10 == 0) System.Threading.Thread.Sleep(15);   // real time passes; sim must not care
        }

        Assert.Equal(fast.StateHash(), slow.StateHash());
    }

    /// <summary>
    /// Anti-cheat backbone: an exaggerated input vector must not move a player further than a
    /// unit vector would. The client only sends intent; the server clamps it.
    /// </summary>
    [Fact]
    public void ExaggeratedInputVector_DoesNotOutrunAUnitVector()
    {
        var cheat = new ServerTickHarness();
        var c = cheat.AddPlayer(1, "Cheater");
        c.X = 0; c.Z = 0; c.InputDirX = 1000f; c.InputDirZ = 0f;

        var honest = new ServerTickHarness();
        var h = honest.AddPlayer(1, "Honest");
        h.X = 0; h.Z = 0; h.InputDirX = 1f; h.InputDirZ = 0f;

        for (var t = 0; t < 20; t++)
        {
            c.InputDirX = 1000f; c.InputDirZ = 0f;
            h.InputDirX = 1f; h.InputDirZ = 0f;
            cheat.Tick(Dt, t * (long)TickMs);
            honest.Tick(Dt, t * (long)TickMs);
        }

        Assert.True(MathF.Abs(c.X) <= MathF.Abs(h.X) + 0.0001f,
            $"exaggerated vector travelled {c.X} vs honest {h.X} - speed clamp is not holding");
    }

    /// <summary>
    /// Golden state lock, in the AbilityDumpTests / MMOCORE_REGEN=1 style. Regenerate with:
    ///     $env:MMOCORE_REGEN=1; dotnet test --filter GoldenTickState
    /// then COMMIT the generated file and review the diff on every later change. An unexplained
    /// diff here is a behaviour change in the authoritative sim.
    /// </summary>
    [Fact]
    public void GoldenTickState_MatchesCommittedSnapshot()
    {
        var hash = RunScenario(200).StateHash();
        var path = Path.Combine(AppContext.BaseDirectory, "golden-tick-state.txt");

        if (Environment.GetEnvironmentVariable("MMOCORE_REGEN") == "1")
        {
            File.WriteAllText(path, hash);
            return;
        }

        Assert.True(File.Exists(path),
            $"golden file missing at {path}. Generate it once with: $env:MMOCORE_REGEN=1; dotnet test --filter GoldenTickState");
        Assert.Equal(File.ReadAllText(path).Trim(), hash);
    }
}
