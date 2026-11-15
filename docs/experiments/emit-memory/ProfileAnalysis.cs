// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

public static class ProfileAnalysis
{
    private sealed record Interval(string Phase, double StartMs, double EndMs, int ThreadId);
    private sealed class Bucket
    {
        public long CpuSamples;
        public long CpuWithoutStack;
        public long AllocationTicks;
        public long AllocationWithoutStack;
        public long AllocationBytes;
        public Dictionary<string, double> CpuInclusive = new();
        public Dictionary<string, double> CpuExclusive = new();
        public Dictionary<string, double> AllocationInclusive = new();
        public Dictionary<string, double> AllocationExclusive = new();
        public Dictionary<string, double> AllocationTypes = new();
        public Dictionary<string, double> CpuStacks = new();
        public Dictionary<string, double> AllocationStacks = new();
    }

    public static int Main(string[] args)
    {
        try
        {
            var trace = Path.GetFullPath(args[0]);
            var etlx = Path.ChangeExtension(trace, ".etlx");
            TraceLog.CreateFromEventPipeDataFile(trace, etlx);
            using var log = new TraceLog(etlx);
            var intervals = new List<Interval>();
            var starts = new Dictionary<(int, string), double>();
            var eventCounts = new Dictionary<string, double>();
            foreach (var data in log.Events)
            {
                Add(eventCounts, data.ProviderName + "/" + data.EventName, 1);
                if (data.ProviderName != "Pipeline-Profile") continue;
                var phase = data.PayloadString(0);
                var key = (data.ThreadID, phase);
                if ((int)data.ID == 1) starts[key] = data.TimeStampRelativeMSec;
                else if ((int)data.ID == 2 && starts.Remove(key, out var start))
                    intervals.Add(new Interval(phase, start, data.TimeStampRelativeMSec, data.ThreadID));
            }
            var measured = intervals.Where(x => x.Phase != "warmup" && x.Phase != "generation").OrderBy(x => x.StartMs).ToArray();
            if (!measured.Any(x => x.Phase == "merkleCompile"))
                throw new InvalidDataException("No measured compiler phase markers were found.");
            var buckets = new Dictionary<string, Bucket>();
            foreach (var data in log.Events)
            {
                var cpu = data.ProviderName == "Microsoft-DotNETCore-SampleProfiler";
                var allocation = data as GCAllocationTickTraceData;
                if (!cpu && allocation is null) continue;
                var interval = measured.FirstOrDefault(x => data.TimeStampRelativeMSec >= x.StartMs && data.TimeStampRelativeMSec <= x.EndMs);
                if (interval is null) continue;
                var key = interval.Phase + (data.ThreadID == interval.ThreadId ? "" : ":other-thread");
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new Bucket();
                var frames = new List<string>();
                for (var stack = data.CallStack(); stack is not null; stack = stack.Caller)
                {
                    var name = stack.CodeAddress.FullMethodName;
                    if (string.IsNullOrEmpty(name)) name = "0x" + stack.CodeAddress.Address.ToString("x");
                    frames.Add(name);
                    if (frames.Count >= 1024) break;
                }
                var unique = frames.Distinct().ToArray();
                var folded = string.Join(" <- ", frames);
                if (cpu)
                {
                    bucket.CpuSamples++;
                    if (frames.Count == 0) bucket.CpuWithoutStack++;
                    else
                    {
                        Add(bucket.CpuExclusive, frames[0], 1);
                        foreach (var frame in unique) Add(bucket.CpuInclusive, frame, 1);
                        Add(bucket.CpuStacks, folded, 1);
                    }
                }
                if (allocation is not null)
                {
                    var amount = allocation.AllocationAmount64;
                    bucket.AllocationTicks++;
                    bucket.AllocationBytes += amount;
                    Add(bucket.AllocationTypes, allocation.TypeName ?? "<unknown>", amount);
                    if (frames.Count == 0) bucket.AllocationWithoutStack++;
                    else
                    {
                        Add(bucket.AllocationExclusive, frames[0], amount);
                        foreach (var frame in unique) Add(bucket.AllocationInclusive, frame, amount);
                        Add(bucket.AllocationStacks, folded, amount);
                    }
                }
            }
            object Top(Dictionary<string, double> values, int count = 35)
                => values.OrderByDescending(x => x.Value).Take(count).Select(x => new { name = x.Key, value = x.Value }).ToArray();
            object AppTop(Dictionary<string, double> values)
                => Top(values.Where(x => x.Key.Contains("Merkle") || x.Key.Contains("Roslyn")).ToDictionary(x => x.Key, x => x.Value), 55);
            var summary = new
            {
                trace, traceBytes = new FileInfo(trace).Length,
                eventsLost = typeof(TraceLog).GetProperty("EventsLost")?.GetValue(log),
                intervals,
                eventCounts = Top(eventCounts, 30),
                phases = buckets.Select(pair => new
                {
                    phase = pair.Key,
                    pair.Value.CpuSamples, pair.Value.CpuWithoutStack,
                    pair.Value.AllocationTicks, pair.Value.AllocationWithoutStack, pair.Value.AllocationBytes,
                    cpuInclusive = Top(pair.Value.CpuInclusive),
                    cpuExclusive = Top(pair.Value.CpuExclusive),
                    cpuApplicationInclusive = AppTop(pair.Value.CpuInclusive),
                    allocationInclusive = Top(pair.Value.AllocationInclusive),
                    allocationExclusive = Top(pair.Value.AllocationExclusive),
                    allocationApplicationInclusive = AppTop(pair.Value.AllocationInclusive),
                    allocationTypes = Top(pair.Value.AllocationTypes),
                    cpuStacks = Top(pair.Value.CpuStacks, 12),
                    allocationStacks = Top(pair.Value.AllocationStacks, 12)
                }).ToArray()
            };
            File.WriteAllText(args[1], JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            string ShortName(string name) { var p = name.IndexOf('('); return p < 0 ? name : name[..p]; }
            object SmallTop(Dictionary<string, double> values, double denominator, int count)
                => values.OrderByDescending(x => x.Value).Take(count).Select(x => new {
                    name = ShortName(x.Key), value = x.Value, percent = denominator == 0 ? 0 : 100 * x.Value / denominator
                }).ToArray();
            var interesting = new[] { "TryBuildStatementOccurrences", "TryPrepareStatementBodyForEmission",
                "StatementBodyRewriteProvenance.TryValidate", "StatementBodyRewriteProvenance.TryProjectRewrite",
                "MerkleOccurrenceSnapshotProjection.BuildCursors", "MerkleTraversalSnapshot.CollectCursors",
                "MemberSemanticModel.GetOperationWorker", "MemberSemanticModel.GetRootOperation",
                "MerkleInitializerExpressionBuilder.BuildCore" };
            var compact = new {
                trace, traceBytes = new FileInfo(trace).Length,
                eventsLost = typeof(TraceLog).GetProperty("EventsLost")?.GetValue(log),
                intervals,
                note = "Main-thread sampled stacks include GC waits; percentages are inclusive and must not be summed. Allocation bytes are weighted GC allocation ticks, not retained memory.",
                phases = buckets.Where(p => !p.Key.EndsWith(":other-thread")).Select(p => new {
                    phase = p.Key, p.Value.CpuSamples, p.Value.CpuWithoutStack,
                    p.Value.AllocationTicks, p.Value.AllocationWithoutStack, p.Value.AllocationBytes,
                    functions = p.Value.CpuInclusive.Keys.Union(p.Value.AllocationInclusive.Keys)
                        .Where(name => interesting.Any(name.Contains)).Select(name => new {
                            name = ShortName(name),
                            samples = p.Value.CpuInclusive.GetValueOrDefault(name),
                            samplePercent = 100 * p.Value.CpuInclusive.GetValueOrDefault(name) / Math.Max(1, p.Value.CpuSamples),
                            allocationBytes = p.Value.AllocationInclusive.GetValueOrDefault(name),
                            allocationPercent = 100 * p.Value.AllocationInclusive.GetValueOrDefault(name) / Math.Max(1, p.Value.AllocationBytes)
                        }).ToArray(),
                    cpuExclusive = SmallTop(p.Value.CpuExclusive, p.Value.CpuSamples, 12),
                    allocationTypes = SmallTop(p.Value.AllocationTypes, p.Value.AllocationBytes, 12)
                }).ToArray()
            };
            File.WriteAllText(Path.ChangeExtension(args[1], ".compact.json"),
                JsonSerializer.Serialize(compact, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.ChangeExtension(args[1], ".stacks.json"), JsonSerializer.Serialize(buckets,
                new JsonSerializerOptions { IncludeFields = true }));
            Console.WriteLine(JsonSerializer.Serialize(new {
                output = args[1], phases = buckets.Select(x => new {
                    phase = x.Key, x.Value.CpuSamples, x.Value.CpuWithoutStack,
                    x.Value.AllocationTicks, x.Value.AllocationWithoutStack, x.Value.AllocationBytes,
                    topCpu = Top(x.Value.CpuExclusive, 8), topAllocations = Top(x.Value.AllocationTypes, 8)
                })
            }));
            if (!buckets.TryGetValue("merkleCompile", out var compilation) || compilation.CpuSamples == 0 || compilation.AllocationTicks == 0)
                throw new InvalidDataException("Compiler phase is missing CPU or allocation samples.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Add(Dictionary<string, double> values, string key, double value)
        => values[key] = values.GetValueOrDefault(key) + value;
}
