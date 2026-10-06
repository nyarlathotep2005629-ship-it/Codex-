using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Application.Tests;

public sealed class ArchitectureTests
{
    [Fact(DisplayName = "ARC-001 Domain has no project dependency on Application or Infrastructure")]
    public void Arc001()
    {
        var references = typeof(WorkoutSession).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Assert.DoesNotContain("WorkoutTracker.Application", references);
        Assert.DoesNotContain("WorkoutTracker.Infrastructure", references);
        Assert.True(typeof(IWorkoutSessionRepository).IsInterface);
    }
}

