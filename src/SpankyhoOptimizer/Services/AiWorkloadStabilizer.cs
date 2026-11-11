using System;

namespace SpankyhoOptimizer.Services;

public sealed class AiWorkloadStabilizer
{
    private AiWorkloadClass _stable = AiWorkloadClass.Unknown;
    private AiWorkloadClass _candidate = AiWorkloadClass.Unknown;
    private int _votes;
    private DateTime _lastObservedCpuSampleUtc;

    public AiWorkloadClass Stable => _stable;

    // Lose all previous workload votes after native counter discontinuity or
    // complete sensor outage. The next DISTINCT valid scan starts fresh.
    public void ResetEvidence()
    {
        _stable = AiWorkloadClass.Unknown;
        _candidate = AiWorkloadClass.Unknown;
        _votes = 0;
        _lastObservedCpuSampleUtc = default;
    }

    // A status message can be published repeatedly from one cached native
    // Process.GetProcesses sample. Only distinct CPU samples can earn votes;
    // otherwise two FanAgent ticks could masquerade as two observations.
    public AiWorkloadClass UpdateDistinct(AiWorkloadClass observed, DateTime cpuSampleUtc)
    {
        if (cpuSampleUtc == default)
            return _stable;
        if (_lastObservedCpuSampleUtc != default &&
            _lastObservedCpuSampleUtc - cpuSampleUtc >= TimeSpan.FromMinutes(1))
        {
            // Windows wall clock may be corrected backwards. Never freeze
            // the workload stabilizer until that future timestamp catches up;
            // discard old evidence and require a new independent scan.
            _stable = AiWorkloadClass.Unknown;
            _candidate = AiWorkloadClass.Unknown;
            _votes = 0;
            _lastObservedCpuSampleUtc = cpuSampleUtc;
            return _stable;
        }
        if (cpuSampleUtc <= _lastObservedCpuSampleUtc)
            return _stable;
        _lastObservedCpuSampleUtc = cpuSampleUtc;
        return Update(observed);
    }

    public AiWorkloadClass Update(AiWorkloadClass observed)
    {
        if (observed == AiWorkloadClass.Game)
        {
            _stable = AiWorkloadClass.Game;
            _candidate = observed;
            _votes = 0;
            return _stable;
        }

        if (_stable == AiWorkloadClass.Game && observed != AiWorkloadClass.Game)
        {
            // Exit from a game quickly but not on a single transient process sample.
            return Vote(observed, required: 2);
        }

        if (_stable == AiWorkloadClass.Unknown)
        {
            _stable = observed;
            _candidate = observed;
            _votes = 0;
            return _stable;
        }

        if (observed == _stable)
        {
            _candidate = observed;
            _votes = 0;
            return _stable;
        }

        int required = observed switch
        {
            AiWorkloadClass.Idle => 3,
            AiWorkloadClass.Background => 2,
            AiWorkloadClass.Interactive => 2,
            _ => 2
        };
        return Vote(observed, required);
    }

    private AiWorkloadClass Vote(AiWorkloadClass observed, int required)
    {
        if (_candidate != observed)
        {
            _candidate = observed;
            _votes = 1;
        }
        else
        {
            _votes++;
        }

        if (_votes >= required)
        {
            _stable = observed;
            _votes = 0;
        }

        return _stable;
    }
}
