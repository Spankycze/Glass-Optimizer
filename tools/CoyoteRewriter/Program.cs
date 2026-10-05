using System.Reflection;
using Microsoft.Coyote;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CoyoteRewriter <assemblyPath> <outputDir>");
    return 2;
}

string assemblyPath = Path.GetFullPath(args[0]);
string inputDir = Path.GetDirectoryName(assemblyPath)!;
string outputDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDir);

Assembly testAsm = Assembly.Load("Microsoft.Coyote.Test");
Assembly coreAsm = Assembly.Load("Microsoft.Coyote");

Type optionsType = testAsm.GetType("Microsoft.Coyote.Rewriting.RewritingOptions", throwOnError: true)!;
Type engineType = testAsm.GetType("Microsoft.Coyote.Rewriting.RewritingEngine", throwOnError: true)!;
Type logWriterType = coreAsm.GetType("Microsoft.Coyote.Logging.LogWriter", throwOnError: true)!;
Type profilerType = coreAsm.GetType("Microsoft.Coyote.Profiler", throwOnError: true)!;

MethodInfo createOptions = optionsType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(optionsType.FullName, "Create");
object options = createOptions.Invoke(null, null)!;
Set(options, "AssembliesDirectory", inputDir);
Set(options, "OutputDirectory", outputDir);
Set(options, "IsRewritingDependencies", false);
Set(options, "IsRewritingMemoryLocations", true);
Set(options, "IsRewritingConcurrentCollections", true);
Set(options, "IsDataRaceCheckingEnabled", true);

object assemblyPaths = optionsType.GetProperty("AssemblyPaths", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(options)!;
MethodInfo add = assemblyPaths.GetType().GetMethod("Add")!;
add.Invoke(assemblyPaths, [assemblyPath]);
string corePath = Path.Combine(inputDir, "SpankyhoOptimizer.Core.dll");
if (File.Exists(corePath))
    add.Invoke(assemblyPaths, [corePath]);

Configuration configuration = Configuration.Create().WithTelemetryEnabled(false);
object logger = CreateInternal(logWriterType, configuration);
object profiler = CreateInternal(profilerType, configuration);

MethodInfo run = engineType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    .Single(m => m.Name == "Run" && m.GetParameters().Length == 4);
run.Invoke(null, [options, configuration, logger, profiler]);

Console.WriteLine("COYOTE_REWRITE_OK " + outputDir);
return 0;

static object CreateInternal(Type type, Configuration configuration)
{
    foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .OrderBy(c => c.GetParameters().Length))
    {
        try
        {
            object?[] args = ctor.GetParameters().Select(p =>
            {
                Type t = p.ParameterType;
                if (t == typeof(Configuration)) return (object?)configuration;
                if (typeof(TextWriter).IsAssignableFrom(t)) return Console.Out;
                if (t == typeof(bool)) return false;
                if (t == typeof(string)) return null;
                if (t.IsEnum) return Activator.CreateInstance(t);
                if (t.IsValueType) return Activator.CreateInstance(t);
                return null;
            }).ToArray();
            return ctor.Invoke(args);
        }
        catch
        {
        }
    }

    throw new MissingMethodException($"No usable constructor for {type.FullName}");
}

static void Set(object target, string property, object value)
{
    PropertyInfo p = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(target.GetType().FullName, property);
    p.SetValue(target, value);
}
