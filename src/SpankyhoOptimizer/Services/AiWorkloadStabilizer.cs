using System;

namespace SpankyhoOptimizer.Services;

public sealed class AiWorkloadStabilizer
{
    private AiWorkloadClass _stable = AiWorkloadClass.Unknown;
    private AiWorkloadClass _candidate = AiWorkloadClass.Unknown;
    private int _votes;

    public AiWorkloadClass Stable => _stable;

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
