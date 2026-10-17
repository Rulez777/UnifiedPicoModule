namespace UnifiedPicoModule.Runtime;

// SDK-thread-owned counters and fixed-bin timing distributions; no sample queue or per-frame logging.
public sealed class ConsumptionDiagnostics
{
    private readonly long[] processed = new long[3], coalesced = new long[3];
    private readonly double[] pendingArrival = new double[3];
    private readonly Timing receiveToProcess = new(), process = new(), receiveToWrite = new(), write = new();
    private long newWrites, repeatedWrites;
    private bool pendingWrite;

    public void Processed(int groups, long sequence, long previous, double arrival, double start, double end)
    {
        if (groups == 0) return;
        for (int i = 0; i < 3; i++)
        {
            if ((groups & (1 << i)) == 0) continue;
            processed[i]++;
            if (previous >= 0 && sequence > previous + 1) coalesced[i] += sequence - previous - 1;
            pendingArrival[i] = arrival;
        }
        receiveToProcess.Add(start - arrival);
        process.Add(end - start);
        pendingWrite = true;
    }

    public void Written(double started, double now)
    {
        write.Add(now - started);
        if (!pendingWrite) { repeatedWrites++; return; }
        newWrites++;
        foreach (double arrival in pendingArrival) if (arrival > 0) receiveToWrite.Add(now - arrival);
        ClearPending();
    }

    public void ClearPending()
    {
        pendingWrite = false;
        Array.Clear(pendingArrival);
    }

    public string Report()
    {
        string result = $"CONSUMPTION processedLRM={string.Join("/", processed)} coalescedLRM={string.Join("/", coalesced)} " +
            $"sdkNew={newWrites} sdkRepeated={repeatedWrites} receiveToProcessMs={receiveToProcess.Report()} " +
            $"processMs={process.Report()} receiveToWriteMs={receiveToWrite.Report()} sdkWriteMs={write.Report()}";
        Array.Clear(processed); Array.Clear(coalesced);
        newWrites = repeatedWrites = 0;
        return result;
    }

    private sealed class Timing
    {
        private static readonly double[] UpperMs = [0.1, 0.25, 0.5, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024];
        private readonly long[] bins = new long[UpperMs.Length + 1];
        private long count;
        private double sum, max;
        public void Add(double seconds)
        {
            double ms = Math.Max(0, seconds * 1000);
            int bin = 0;
            while (bin < UpperMs.Length && ms > UpperMs[bin]) bin++;
            bins[bin]++; count++; sum += ms; max = Math.Max(max, ms);
        }
        private double Percentile(double quantile)
        {
            long target = (long)Math.Ceiling(count * quantile), total = 0;
            for (int i = 0; i < bins.Length; i++)
            {
                total += bins[i];
                if (total >= target) return i < UpperMs.Length ? UpperMs[i] : max;
            }
            return 0;
        }
        public string Report()
        {
            string result = count == 0 ? "n=0" : FormattableString.Invariant(
                $"n={count},mean={sum / count:F3},p50<={Percentile(.5):F3},p95<={Percentile(.95):F3},p99<={Percentile(.99):F3},max={max:F3}");
            Array.Clear(bins); count = 0; sum = max = 0;
            return result;
        }
    }
}
