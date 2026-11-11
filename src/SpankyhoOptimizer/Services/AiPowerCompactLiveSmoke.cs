using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class AiPowerCompactLiveSmoke
{
    internal static Task RunAsync()
    {
        static void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("AI_POWER_LIVE_SMOKE_FAIL " + name);
        }

        // Preserve a mounted and unchanged Power Plan UI control tree.
        Check(AiPowerCompactLivePolicy.CanReuse(true,true,true,false,false,false,false),"collapsed no details");
        Check(AiPowerCompactLivePolicy.CanReuse(true,true,true,false,false,true,false),"collapsed expired detail");
        Check(AiPowerCompactLivePolicy.CanReuse(true,true,true,false,true,false,false),"collapsed in flight");
        Check(AiPowerCompactLivePolicy.CanReuse(true,true,true,true,true,false,false),"expanded fetch in flight");
        Check(AiPowerCompactLivePolicy.CanReuse(true,true,true,true,false,false,false),"expanded not loaded");
        Check(AiPowerCompactLivePolicy.CanReuse(true,true,true,true,false,true,true),"expanded fresh snapshot");

        // Must rebuild when one of the interactive state invariants changes.
        Check(!AiPowerCompactLivePolicy.CanReuse(false,true,true,false,false,false,true),"another host");
        Check(!AiPowerCompactLivePolicy.CanReuse(true,false,true,false,false,false,true),"empty host");
        Check(!AiPowerCompactLivePolicy.CanReuse(true,true,false,false,false,false,true),"toggle changed");
        Check(!AiPowerCompactLivePolicy.CanReuse(true,true,true,true,false,true,false),"expired visible details");
        Check(!AiPowerCompactLivePolicy.CanReuse(false,false,false,true,false,true,false),"all invalid");
        Check(!AiPowerCompactLivePolicy.CanReuse(true,true,false,true,true,false,true),"toggle during load");

        // A visible expired detail is rebuilt, but hiding details preserves UI.
        bool renderedFirst = AiPowerCompactLivePolicy.CanReuse(true,true,true,true,false,true,false);
        bool renderedAfterCollapse = AiPowerCompactLivePolicy.CanReuse(true,true,true,false,false,true,false);
        Check(!renderedFirst && renderedAfterCollapse,"collapse clears visible-expiry rebuild");

        // Avoid accidental widening of the gate with irrelevant fresh/expired data.
        for (int i=0;i<16;i++)
        {
            bool sameHost=(i&1)!=0;
            bool sameToggle=(i&2)!=0;
            bool hasChildren=(i&4)!=0;
            bool expanded=(i&8)!=0;
            bool result=AiPowerCompactLivePolicy.CanReuse(sameHost,hasChildren,sameToggle,
                expanded,false,true,true);
            Check(result==(sameHost&&sameToggle&&hasChildren),"truth table "+i);
        }
        return Task.CompletedTask;
    }
}
