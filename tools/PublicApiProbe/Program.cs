using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

if (args.Length < 1 || args.Length > 2 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: PublicApiProbe <assembly.dll> [--manifest]");
    return 2;
}

bool manifestMode = args.Length == 2 && args[1].Equals("--manifest", StringComparison.OrdinalIgnoreCase);

string assemblyPath = Path.GetFullPath(args[0]);
string assemblyDir = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string candidate = Path.Combine(assemblyDir, name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
var lines = new List<string>();

Type[] loadableTypes;
try
{
    loadableTypes = assembly.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    loadableTypes = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
    if (loadableTypes.Length == 0)
    {
        foreach (Exception? loaderException in ex.LoaderExceptions)
            if (loaderException != null)
                Console.Error.WriteLine(loaderException.Message);
        return 3;
    }
}

foreach (Type type in loadableTypes.Where(IsApiVisibleType).OrderBy(TypeName, StringComparer.Ordinal))
{
    lines.Add("T|" + TypeName(type) + "|base=" + TypeName(type.BaseType) + "|if=" +
        string.Join(",", type.GetInterfaces().Select(TypeName).OrderBy(x => x, StringComparer.Ordinal)));

    foreach (Type ga in type.GetGenericArguments().Where(x => x.IsGenericParameter).OrderBy(x => x.GenericParameterPosition))
    {
        lines.Add("G|" + TypeName(type) + "|" + ga.GenericParameterPosition + "|" + ga.GenericParameterAttributes + "|" +
            string.Join(",", ga.GetGenericParameterConstraints().Select(TypeName).OrderBy(x => x, StringComparer.Ordinal)));
    }

    const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    foreach (ConstructorInfo c in type.GetConstructors(declared).Where(IsApiVisibleMethod).OrderBy(MemberKey, StringComparer.Ordinal))
        lines.Add("C|" + TypeName(type) + "|" + MemberKey(c));

    foreach (MethodInfo m in type.GetMethods(declared).Where(IsApiVisibleMethod).OrderBy(MemberKey, StringComparer.Ordinal))
        lines.Add("M|" + TypeName(type) + "|" + MemberKey(m));

    foreach (FieldInfo f in type.GetFields(declared).Where(IsApiVisibleField).OrderBy(x => x.Name, StringComparer.Ordinal))
        lines.Add("F|" + TypeName(type) + "|" + f.Name + "|" + TypeName(f.FieldType) + "|" + f.Attributes);

    foreach (PropertyInfo p in type.GetProperties(declared).Where(IsApiVisibleProperty).OrderBy(x => x.Name, StringComparer.Ordinal))
        lines.Add("P|" + TypeName(type) + "|" + p.Name + "|" + TypeName(p.PropertyType) + "|" +
            string.Join(",", p.GetIndexParameters().Select(x => TypeName(x.ParameterType))));

    foreach (EventInfo e in type.GetEvents(declared).Where(IsApiVisibleEvent).OrderBy(x => x.Name, StringComparer.Ordinal))
        lines.Add("E|" + TypeName(type) + "|" + e.Name + "|" + TypeName(e.EventHandlerType));
}

string text = string.Join("\n", lines);
if (manifestMode)
{
    Console.Write(text);
    return 0;
}

byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
Console.WriteLine(Convert.ToHexString(hash));
return 0;

static bool IsApiVisibleType(Type t) =>
    t.IsPublic || t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem;

static bool IsApiVisibleMethod(MethodBase m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;
static bool IsApiVisibleField(FieldInfo f) => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly;

static bool IsApiVisibleProperty(PropertyInfo p) =>
    (p.GetMethod != null && IsApiVisibleMethod(p.GetMethod)) ||
    (p.SetMethod != null && IsApiVisibleMethod(p.SetMethod));

static bool IsApiVisibleEvent(EventInfo e) =>
    (e.AddMethod != null && IsApiVisibleMethod(e.AddMethod)) ||
    (e.RemoveMethod != null && IsApiVisibleMethod(e.RemoveMethod));

static string MemberKey(MethodBase m)
{
    string generic = m.IsGenericMethod ? "`" + m.GetGenericArguments().Length : string.Empty;
    string ret = m is MethodInfo mi ? TypeName(mi.ReturnType) : "void";
    string pars = string.Join(",", m.GetParameters().Select(p =>
        (p.IsOut ? "out:" : p.ParameterType.IsByRef ? "ref:" : string.Empty) + TypeName(p.ParameterType)));
    return m.Name + generic + "|" + ret + "|" + pars + "|" + m.Attributes;
}

static string TypeName(Type? t)
{
    if (t == null) return "";
    if (t.IsByRef) return TypeName(t.GetElementType()) + "&";
    if (t.IsPointer) return TypeName(t.GetElementType()) + "*";
    if (t.IsArray) return TypeName(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
    if (t.IsGenericParameter) return "!" + t.GenericParameterPosition + ":" + t.Name;
    if (!t.IsGenericType) return t.FullName ?? t.Name;
    string def = t.GetGenericTypeDefinition().FullName ?? t.Name;
    int tick = def.IndexOf('`');
    if (tick >= 0) def = def[..tick];
    return def + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
}
