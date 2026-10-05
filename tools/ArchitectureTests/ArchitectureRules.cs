using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using SpankyhoOptimizer.Services;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

public sealed class ArchitectureRules
{
    private static readonly Architecture Architecture =
        new ArchLoader().LoadAssemblies(typeof(OperationCoordinator).Assembly).Build();

    [Fact]
    public void Models_must_not_depend_on_services()
    {
        var models = Types().That().ResideInNamespace("SpankyhoOptimizer.Models");
        var services = Types().That().ResideInNamespace("SpankyhoOptimizer.Services");
        models.Should().NotDependOnAny(services).Check(Architecture);
    }

    [Fact]
    public void Core_services_must_not_depend_on_WinUI()
    {
        var services = Types().That().ResideInNamespace("SpankyhoOptimizer.Services");
        var winui = Types().That().ResideInNamespace("Microsoft.UI");
        services.Should().NotDependOnAny(winui).Check(Architecture);
    }

    [Fact]
    public void Service_types_should_stay_in_services_namespace()
    {
        Classes().That().HaveNameEndingWith("Service")
            .Should().ResideInNamespace("SpankyhoOptimizer.Services")
            .Check(Architecture);
    }
}
