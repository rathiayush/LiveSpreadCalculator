using Microsoft.AspNetCore.Mvc;

namespace LiveSpreadCalculator.Controllers;

/// <summary>
/// Answers "is memory actually leaking, or is this just GC behavior?" without guessing. Forces
/// a full blocking collection (GC.Collect(2, GC.MaxGeneration, blocking: true) plus
/// GC.WaitForPendingFinalizers()) so ManagedHeapBytes reflects genuinely live objects rather
/// than not-yet-collected garbage, then reports both that and the process working set. If you
/// call GET /api/diagnostics/memory a few times, minutes apart, under steady load:
///   - ManagedHeapBytes should plateau (rise a bit as the universe/trigger count grows, then
///     hold roughly flat) - if it keeps climbing indefinitely after forcing a collection, that's
///     a genuine leak (something is still holding references).
///   - WorkingSetBytes/PrivateBytes can run higher than ManagedHeapBytes and stay there - that's
///     normal (native allocations, thread stacks, JIT'd code, and memory the GC has decided not
///     to hand back to the OS yet), not evidence of a leak by itself.
/// </summary>
[ApiController]
[Route("api/diagnostics")]
public sealed class DiagnosticsController : ControllerBase
{
    [HttpGet("memory")]
    public ActionResult<object> GetMemory([FromQuery] bool forceCollect = true)
    {
        if (forceCollect)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        var managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false); // already collected above if forceCollect was true

        return Ok(new
        {
            ForcedCollectionBeforeMeasuring = forceCollect,
            ManagedHeapBytes = managedHeapBytes,
            ManagedHeapMB = Math.Round(managedHeapBytes / 1024.0 / 1024.0, 1),
            WorkingSetBytes = process.WorkingSet64,
            WorkingSetMB = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1),
            PrivateBytes = process.PrivateMemorySize64,
            PrivateMB = Math.Round(process.PrivateMemorySize64 / 1024.0 / 1024.0, 1),
            HeapCommittedMB = Math.Round(gcInfo.TotalCommittedBytes / 1024.0 / 1024.0, 1),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            IsServerGC = System.Runtime.GCSettings.IsServerGC,
            ProcessStartedAt = process.StartTime,
            ThreadCount = process.Threads.Count,
        });
    }
}
