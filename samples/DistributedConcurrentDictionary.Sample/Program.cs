using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using Spectre.Console;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

UTF8Encoding utf8Console = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = utf8Console;
Console.InputEncoding = utf8Console;

string[] rawArgs = args;
if (rawArgs.Any(static a => string.Equals(a, "--backplane", StringComparison.OrdinalIgnoreCase)))
{
    RunBackplaneMultiProcess(rawArgs);
    return;
}

string redisConnection = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";

AnsiConsole.Write(
    new Panel(
            new Markup(
                $"[bold]Redis[/]: [cyan]{Markup.Escape(redisConnection)}[/]\n"
                + "[dim]FusionCache (bộ nhớ + Redis) + RedLock khóa theo key + Redis SET làm chỉ mục[/]"))
        .Header("[bold yellow]PLC.Shared.DistributedConcurrentDictionary[/] — demo trực quan")
        .Border(BoxBorder.Double)
        .Padding(1, 1));

using ConnectionMultiplexer mux = ConnectionMultiplexer.Connect(redisConnection);

FusionCacheOptions fusionCacheOptions = new FusionCacheOptions();
fusionCacheOptions.DefaultEntryOptions.Duration = TimeSpan.FromMinutes(10);

using FusionCache cache = new FusionCache(fusionCacheOptions);

IDistributedCache distributedCache = new RedisCache(
    Options.Create(
        new RedisCacheOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(mux),
        }));

cache.SetupDistributedCache(distributedCache, new FusionCacheSystemTextJsonSerializer());

RedisBackplane backplane = new RedisBackplane(
    Options.Create(
        new RedisBackplaneOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(mux),
        }),
    NullLogger<RedisBackplane>.Instance);

cache.SetupBackplane(backplane);

RedLockMultiplexer[] redLockEndpoints = new RedLockMultiplexer[] { new RedLockMultiplexer(mux) };
IDistributedLockFactory lockFactory = RedLockFactory.Create(redLockEndpoints);

JsonFusionRedisDistributedDictionary<string, Widget> dict = new JsonFusionRedisDistributedDictionary<string, Widget>(
    cache,
    mux,
    lockFactory,
    new FusionRedisDictionaryOptions
    {
        KeyPrefix = "sample:widget-dict",
        DefaultEntryOptions = new FusionCacheEntryOptions { Duration = TimeSpan.FromMinutes(5) },
    });

Table resultTable = new Table();
resultTable.Border(TableBorder.Rounded);
resultTable.AddColumn("[grey]Bước[/]");
resultTable.AddColumn("[grey]Mô tả[/]");
resultTable.AddColumn("[grey]Giá trị[/]");
resultTable.AddColumn("[grey]Kết luận[/]", c => c.NoWrap = true);

List<bool> passFlags = new List<bool>();

void RecordRow(string step, string description, string value, bool ok)
{
    passFlags.Add(ok);
    string verdict = ok ? "[bold green]ĐÚNG[/]" : "[bold red]SAI[/]";
    resultTable.AddRow(Markup.Escape(step), Markup.Escape(description), Markup.Escape(value), verdict);
}

dict.Clear();
RecordRow("0", "Clear (xoá index + cache entry)", "Count = " + dict.Count, dict.Count == 0);

dict.Add("k1", new Widget { Id = 1, Name = "Alpha" });
bool tryGet = dict.TryGetValue("k1", out Widget? w1);
RecordRow(
    "1",
    "Add(k1) rồi TryGet",
    $"TryGet={tryGet}, Name={w1?.Name}, Count={dict.Count}",
    tryGet && w1?.Name == "Alpha" && dict.Count == 1);

dict["k2"] = new Widget { Id = 2, Name = "Beta" };
Widget k2 = dict["k2"];
RecordRow("2", "Gán dict[k2] (upsert)", "Name=" + k2.Name + ", Count=" + dict.Count, k2.Name == "Beta" && dict.Count == 2);

List<Exception> parallelErrors = new List<Exception>();
System.Threading.Tasks.Parallel.For(
    0,
    20,
    i =>
    {
        try
        {
            string k = "p" + i;
            dict[k] = new Widget { Id = i, Name = "P-" + i };
        }
        catch (Exception ex)
        {
            lock (parallelErrors)
            {
                parallelErrors.Add(ex);
            }
        }
    });

if (parallelErrors.Count > 0)
{
    throw new AggregateException(parallelErrors);
}

const int expectedAfterParallel = 22;
RecordRow(
    "3",
    "20 luồng, mỗi luồng một key p0..p19",
    "Count = " + dict.Count,
    dict.Count == expectedAfterParallel);

RecordRow("4", "ContainsKey(p7)", dict.ContainsKey("p7").ToString(), dict.ContainsKey("p7"));

bool removed = dict.Remove("p7");
RecordRow(
    "5",
    "Remove(p7)",
    "Removed=" + removed + ", ContainsKey=" + dict.ContainsKey("p7") + ", Count=" + dict.Count,
    removed && !dict.ContainsKey("p7") && dict.Count == expectedAfterParallel - 1);

const string collisionKey = "SINGLE_SLOT";
dict.Remove(collisionKey);
int addWins = 0;
int addDuplicate = 0;
ConcurrentBag<int> winnerIds = new ConcurrentBag<int>();
System.Threading.Tasks.Parallel.For(
    0,
    30,
    i =>
    {
        try
        {
            dict.Add(collisionKey, new Widget { Id = i, Name = "Worker-" + i });
            Interlocked.Increment(ref addWins);
            winnerIds.Add(i);
        }
        catch (ArgumentException)
        {
            Interlocked.Increment(ref addDuplicate);
        }
    });

winnerIds.TryTake(out int winnerId);
string raceDetail = $"Thắng Add={addWins}, trùng khóa={addDuplicate}, Id thắng={winnerId}";
RecordRow(
    "6",
    "30 luồng cùng Add một key duy nhất (RedLock + kiểm tra trùng)",
    raceDetail,
    addWins == 1 && addDuplicate == 29 && dict.ContainsKey(collisionKey));

int countAfterRace = dict.Count;
RecordRow(
    "7",
    "Tổng Count sau bước 6",
    countAfterRace.ToString(),
    countAfterRace == expectedAfterParallel);

Table snapshot = new Table();
snapshot.Title = new TableTitle("[bold]Một phần key/value (đọc từ dictionary)[/]");
snapshot.Border(TableBorder.Simple);
snapshot.AddColumn("Key");
snapshot.AddColumn("Id");
snapshot.AddColumn("Name");
int rowCap = 0;
foreach (KeyValuePair<string, Widget> kv in dict)
{
    snapshot.AddRow(Markup.Escape(kv.Key), kv.Value.Id.ToString(), Markup.Escape(kv.Value.Name));
    if (++rowCap >= 8)
    {
        snapshot.AddRow("[dim]...[/]", "[dim]...[/]", "[dim]còn " + (dict.Count - 8) + " dòng[/]");
        break;
    }
}

AnsiConsole.Write(new Rule("[bold]Bảng kiểm tra[/]"));
AnsiConsole.Write(resultTable);

AnsiConsole.Write(new Rule("[bold]Snapshot[/]"));
AnsiConsole.Write(snapshot);

bool allOk = true;
foreach (bool p in passFlags)
{
    if (!p)
    {
        allOk = false;
        break;
    }
}

AnsiConsole.WriteLine();
if (allOk)
{
    AnsiConsole.MarkupLine("[bold green on black]  TẤT CẢ BƯỚC: ĐÚNG  [/]");
}
else
{
    AnsiConsole.MarkupLine("[bold red on black]  CÓ BƯỚC SAI — xem cột Kết luận  [/]");
}

AnsiConsole.MarkupLine(
    "\n[dim]Ý nghĩa bước 6: chỉ một luồng Add thành công, 29 luồng còn lại nhận ArgumentException (trùng khóa) — thể hiện khóa phân tán + semantics IDictionary.Add.[/]");

static void RunBackplaneMultiProcess(string[] runtimeArgs)
{
    string redisConnection = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";
    string instance = GetArgValue(runtimeArgs, "--instance", "A").ToUpperInvariant();
    string channel = GetArgValue(runtimeArgs, "--channel", "shared-demo");
    bool disableBackplane = runtimeArgs.Any(static a => string.Equals(a, "--no-backplane", StringComparison.OrdinalIgnoreCase));
    string keyPrefix = "sample:bp:" + channel;
    const string defaultKey = "shared-key";

    using ConnectionMultiplexer mux = ConnectionMultiplexer.Connect(redisConnection);

    FusionCacheOptions fusionOptions = new FusionCacheOptions();
    fusionOptions.CacheName = "fusion-bp-" + channel;
    fusionOptions.DefaultEntryOptions.Duration = TimeSpan.FromMinutes(10);

    using FusionCache cache = new FusionCache(fusionOptions);
    IDistributedCache distributedCache = new RedisCache(
        Options.Create(
            new RedisCacheOptions
            {
                ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(mux),
            }));
    cache.SetupDistributedCache(distributedCache, new FusionCacheSystemTextJsonSerializer());

    if (!disableBackplane)
    {
        RedisBackplane backplane = new RedisBackplane(
            Options.Create(
                new RedisBackplaneOptions
                {
                    ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(mux),
                }),
            NullLogger<RedisBackplane>.Instance);
        cache.SetupBackplane(backplane);
    }

    RedLockMultiplexer[] redLockEndpoints = new RedLockMultiplexer[] { new RedLockMultiplexer(mux) };
    IDistributedLockFactory lockFactory = RedLockFactory.Create(redLockEndpoints);

    JsonFusionRedisDistributedDictionary<string, Widget> dict = new JsonFusionRedisDistributedDictionary<string, Widget>(
        cache,
        mux,
        lockFactory,
        new FusionRedisDictionaryOptions
        {
            KeyPrefix = keyPrefix,
            DefaultEntryOptions = new FusionCacheEntryOptions { Duration = TimeSpan.FromMinutes(10) },
        });

    string backplaneStatus = disableBackplane ? "OFF" : "ON";
    AnsiConsole.MarkupLine($"[bold]Mode[/]: 2-process backplane demo | instance=[yellow]{Markup.Escape(instance)}[/], channel=[cyan]{Markup.Escape(channel)}[/], backplane=[green]{backplaneStatus}[/]");
    AnsiConsole.MarkupLine($"[bold]Redis[/]: {Markup.Escape(redisConnection)} | [bold]KeyPrefix[/]: {Markup.Escape(keyPrefix)}");
    AnsiConsole.MarkupLine("[dim]" + Markup.Escape("Commands: help | clear | add <key> <text> | set <key> <text> | get <key> | del <key> | list [limit] | watch [key] | exit") + "[/]");
    AnsiConsole.MarkupLine("[dim]" + Markup.Escape("Compat cũ: set <text> và get sẽ dùng key mặc định 'shared-key'.") + "[/]");
    AnsiConsole.MarkupLine("[dim]" + Markup.Escape("Demo quick flow: A:set k1 v1 -> B:get k1 -> A:set k1 v2 -> B:watch k1 (thấy đổi gần như ngay nếu backplane ON).") + "[/]");

    string scriptedCommand = GetArgValue(runtimeArgs, "--cmd", string.Empty);
    if (!string.IsNullOrWhiteSpace(scriptedCommand))
    {
        ExecuteSingleCommand(scriptedCommand.Trim(), dict, instance, defaultKey);
        return;
    }

    while (true)
    {
        string? line = AnsiConsole.Ask<string>($"[bold blue]{instance}>[/]").Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();
        string payload = parts.Length > 1 ? parts[1] : string.Empty;

        if (cmd == "exit" || cmd == "quit")
        {
            return;
        }

        if (cmd == "help")
        {
            AnsiConsole.MarkupLine("clear: xóa toàn bộ dictionary của channel hiện tại");
            AnsiConsole.MarkupLine("add <key> <text>: Add semantics (trùng key sẽ fail)");
            AnsiConsole.MarkupLine("set <key> <text>: upsert key bất kỳ");
            AnsiConsole.MarkupLine("set <text>: tương thích cũ, ghi key 'shared-key'");
            AnsiConsole.MarkupLine("get <key>: đọc key bất kỳ");
            AnsiConsole.MarkupLine("get: tương thích cũ, đọc key 'shared-key'");
            AnsiConsole.MarkupLine("del <key>: xóa key");
            AnsiConsole.MarkupLine("list [limit]: liệt kê key/value hiện có (mặc định 20)");
            AnsiConsole.MarkupLine("watch [key]: đọc key mỗi 1 giây trong 20 giây, in khi phát hiện thay đổi");
            continue;
        }

        if (cmd == "clear")
        {
            dict.Clear();
            PrintStamped(instance, "CLEAR done, Count=" + dict.Count);
            continue;
        }

        if (cmd == "set")
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                AnsiConsole.MarkupLine("[red]Thiếu payload. Dùng: set <key> <text> hoặc set <text>[/]");
                continue;
            }

            string[] setParts = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string key;
            string valueText;
            if (setParts.Length == 1)
            {
                key = defaultKey;
                valueText = setParts[0];
            }
            else
            {
                key = setParts[0];
                valueText = setParts[1];
            }

            dict[key] = new Widget
            {
                Id = Environment.TickCount,
                Name = valueText,
            };
            PrintStamped(instance, $"SET {key}={valueText}");
            continue;
        }

        if (cmd == "add")
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                AnsiConsole.MarkupLine("[red]Thiếu payload. Dùng: add <key> <text>[/]");
                continue;
            }

            string[] addParts = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (addParts.Length < 2)
            {
                AnsiConsole.MarkupLine("[red]Sai cú pháp. Dùng: add <key> <text>[/]");
                continue;
            }

            string key = addParts[0];
            string valueText = addParts[1];
            try
            {
                dict.Add(
                    key,
                    new Widget
                    {
                        Id = Environment.TickCount,
                        Name = valueText,
                    });
                PrintStamped(instance, $"ADD {key}={valueText} => OK");
            }
            catch (ArgumentException ex)
            {
                PrintStamped(instance, $"ADD {key}={valueText} => FAIL ({ex.GetType().Name})");
            }

            continue;
        }

        if (cmd == "get")
        {
            string key = string.IsNullOrWhiteSpace(payload) ? defaultKey : payload;
            if (dict.TryGetValue(key, out Widget? widget))
            {
                PrintStamped(instance, $"GET {key} => Name={widget.Name}, Id={widget.Id}");
            }
            else
            {
                PrintStamped(instance, $"GET {key} => <null>");
            }

            continue;
        }

        if (cmd == "del")
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                AnsiConsole.MarkupLine("[red]Thiếu key. Dùng: del <key>[/]");
                continue;
            }

            bool removed = dict.Remove(payload);
            PrintStamped(instance, $"DEL {payload} => Removed={removed}");
            continue;
        }

        if (cmd == "list")
        {
            int limit = 20;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                if (!int.TryParse(payload, out limit) || limit <= 0)
                {
                    AnsiConsole.MarkupLine("[red]Limit không hợp lệ. Dùng: list hoặc list <số nguyên dương>[/]");
                    continue;
                }
            }

            Table t = new Table();
            t.Border(TableBorder.Rounded);
            t.AddColumn("Key");
            t.AddColumn("Id");
            t.AddColumn("Name");

            int shown = 0;
            foreach (KeyValuePair<string, Widget> pair in dict)
            {
                if (shown >= limit)
                {
                    break;
                }

                t.AddRow(Markup.Escape(pair.Key), pair.Value.Id.ToString(), Markup.Escape(pair.Value.Name));
                shown++;
            }

            PrintStamped(instance, $"LIST shown={shown}, totalCount={dict.Count}");
            AnsiConsole.Write(t);
            continue;
        }

        if (cmd == "watch")
        {
            string key = string.IsNullOrWhiteSpace(payload) ? defaultKey : payload;
            string? last = null;
            for (int i = 0; i < 20; i++)
            {
                string now = dict.TryGetValue(key, out Widget? w)
                    ? $"Name={w.Name}, Id={w.Id}"
                    : "<null>";
                if (!string.Equals(now, last, StringComparison.Ordinal))
                {
                    PrintStamped(instance, $"WATCH {key} change => {now}");
                    last = now;
                }

                Thread.Sleep(TimeSpan.FromSeconds(1));
            }

            PrintStamped(instance, "WATCH done.");
            continue;
        }

        AnsiConsole.MarkupLine("[red]Command không hợp lệ. Gõ: help[/]");
    }
}

static void ExecuteSingleCommand(
    string line,
    JsonFusionRedisDistributedDictionary<string, Widget> dict,
    string instance,
    string defaultKey)
{
    string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
    {
        return;
    }

    string cmd = parts[0].ToLowerInvariant();
    string payload = parts.Length > 1 ? parts[1] : string.Empty;

    if (cmd == "clear")
    {
        dict.Clear();
        PrintStamped(instance, "CLEAR done, Count=" + dict.Count);
        return;
    }

    if (cmd == "set")
    {
        string[] setParts = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string key = setParts.Length <= 1 ? defaultKey : setParts[0];
        string valueText = setParts.Length <= 1 ? payload : setParts[1];
        dict[key] = new Widget { Id = Environment.TickCount, Name = valueText };
        PrintStamped(instance, $"SET {key}={valueText}");
        return;
    }

    if (cmd == "add")
    {
        string[] addParts = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (addParts.Length < 2)
        {
            PrintStamped(instance, "ADD invalid syntax");
            return;
        }

        string key = addParts[0];
        string valueText = addParts[1];
        try
        {
            dict.Add(key, new Widget { Id = Environment.TickCount, Name = valueText });
            PrintStamped(instance, $"ADD {key}={valueText} => OK");
        }
        catch (ArgumentException ex)
        {
            PrintStamped(instance, $"ADD {key}={valueText} => FAIL ({ex.GetType().Name})");
        }

        return;
    }

    if (cmd == "get")
    {
        string key = string.IsNullOrWhiteSpace(payload) ? defaultKey : payload;
        if (dict.TryGetValue(key, out Widget? widget))
        {
            PrintStamped(instance, $"GET {key} => Name={widget.Name}, Id={widget.Id}");
        }
        else
        {
            PrintStamped(instance, $"GET {key} => <null>");
        }

        return;
    }

    if (cmd == "watch")
    {
        string key = string.IsNullOrWhiteSpace(payload) ? defaultKey : payload;
        string? last = null;
        for (int i = 0; i < 20; i++)
        {
            string now = dict.TryGetValue(key, out Widget? w)
                ? $"Name={w.Name}, Id={w.Id}"
                : "<null>";
            if (!string.Equals(now, last, StringComparison.Ordinal))
            {
                PrintStamped(instance, $"WATCH {key} change => {now}");
                last = now;
            }

            Thread.Sleep(TimeSpan.FromSeconds(1));
        }

        PrintStamped(instance, "WATCH done.");
        return;
    }

    PrintStamped(instance, "Unsupported scripted command: " + line);
}

static string GetArgValue(string[] runtimeArgs, string key, string fallback)
{
    for (int i = 0; i < runtimeArgs.Length; i++)
    {
        string arg = runtimeArgs[i];
        if (arg.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
        {
            return arg.Substring(key.Length + 1);
        }

        if (string.Equals(arg, key, StringComparison.OrdinalIgnoreCase) && i + 1 < runtimeArgs.Length)
        {
            return runtimeArgs[i + 1];
        }
    }

    return fallback;
}

static void PrintStamped(string instance, string message)
{
    string ts = DateTime.Now.ToString("HH:mm:ss.fff");
    AnsiConsole.MarkupLine($"[grey]{ts}[/] [yellow]{Markup.Escape(instance)}[/] {Markup.Escape(message)}");
}

internal sealed class Widget
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}
