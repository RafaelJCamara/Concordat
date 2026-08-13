using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

var asm = typeof(RabbitMQ.Client.IChannel).Assembly;
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== ASSEMBLY ===");
Console.WriteLine(asm.FullName);
Console.WriteLine("Location: " + asm.Location);
Console.WriteLine("InformationalVersion: " + asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
Console.WriteLine("FileVersion: " + System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location).FileVersion);
Console.WriteLine();

foreach (var tn in new[]
{
    "RabbitMQ.Client.IChannel",
    "RabbitMQ.Client.IChannelExtensions",
    "RabbitMQ.Client.IAsyncBasicConsumer",
    "RabbitMQ.Client.AsyncDefaultBasicConsumer",
    "RabbitMQ.Client.Events.AsyncEventingBasicConsumer",
    "RabbitMQ.Client.IReadOnlyBasicProperties",
    "RabbitMQ.Client.IBasicProperties",
    "RabbitMQ.Client.BasicProperties",
    "RabbitMQ.Client.ReadOnlyBasicProperties",
    "RabbitMQ.Client.IAmqpHeader",
    "RabbitMQ.Client.IAmqpWriteable",
    "RabbitMQ.Client.CachedString",
    "RabbitMQ.Client.Events.BasicDeliverEventArgs",
    "RabbitMQ.Client.Events.CallbackExceptionEventArgs",
    "RabbitMQ.Client.BasicGetResult",
    "RabbitMQ.Client.DeliveryModes",
})
    Probe.DumpType(asm, tn);

Console.WriteLine("=== IChannel: ALL members (flat, incl. inherited interfaces) ===");
var ich = typeof(RabbitMQ.Client.IChannel);
Console.WriteLine("  IChannel implements: " + string.Join(", ", ich.GetInterfaces().Select(Probe.N)));
foreach (var m in ich.GetMethods().Where(m => !m.IsSpecialName).OrderBy(m => m.Name)) Console.WriteLine("  " + Probe.Sig(m));
foreach (var p in ich.GetProperties().OrderBy(p => p.Name)) Console.WriteLine($"  prop {Probe.N(p.PropertyType)} {p.Name}");
foreach (var e in ich.GetEvents().OrderBy(e => e.Name)) Console.WriteLine($"  event {Probe.N(e.EventHandlerType!)} {e.Name}");
Console.WriteLine();

Console.WriteLine("=== EmptyBasicProperty ===");
var ebp = asm.GetType("RabbitMQ.Client.EmptyBasicProperty") ?? asm.GetType("RabbitMQ.Client.Impl.EmptyBasicProperty");
Console.WriteLine("  found=" + (ebp?.FullName ?? "null") + " isPublic=" + ebp?.IsPublic + " isValueType=" + ebp?.IsValueType);
if (ebp is not null) Console.WriteLine("  implements: " + string.Join(", ", ebp.GetInterfaces().Select(Probe.N)));
Console.WriteLine();

Console.WriteLine("=== Public types: Consumer / Properties / Exception ===");
foreach (var t in asm.GetExportedTypes().Where(t => t.Name.Contains("Consumer") || t.Name.Contains("Properties") || t.Name.Contains("Exception")).OrderBy(t => t.FullName))
    Console.WriteLine("  " + t.FullName);
Console.WriteLine();

Console.WriteLine("=== WireFormatting field-table entry points (internal) ===");
var wf = asm.GetType("RabbitMQ.Client.Impl.WireFormatting")!;
foreach (var m in wf.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name is "WriteTable" or "GetTableByteCount" or "ReadDictionary" or "WriteFieldValue" or "ReadFieldValue")
                    .OrderBy(m => m.Name))
    Console.WriteLine("  " + Probe.Sig(m));
Console.WriteLine();

Console.WriteLine("=== BasicProperties.Type / ContentType / Headers set+read via copy ctor ===");
var props = new RabbitMQ.Client.BasicProperties
{
    Type = "acme.orders.OrderCreated",
    ContentType = "application/json",
    Headers = new Dictionary<string, object?> { ["concordat-v"] = "1" }
};
Console.WriteLine($"  Type={props.Type}  ContentType={props.ContentType}  Headers[concordat-v]={props.Headers!["concordat-v"]} ({props.Headers["concordat-v"]!.GetType().Name})");
RabbitMQ.Client.IReadOnlyBasicProperties ro = props;
Console.WriteLine($"  as IReadOnlyBasicProperties: Type={ro.Type} ContentType={ro.ContentType}");
var copy = new RabbitMQ.Client.BasicProperties(ro);
copy.Type = "changed";
Console.WriteLine($"  copy ctor works; copy.Type={copy.Type}; original.Type={props.Type}; Headers reference-shared={ReferenceEquals(copy.Headers, props.Headers)}");
Console.WriteLine();

Console.WriteLine("=== Can a decorator construct BasicProperties from EmptyBasicProperty? ===");
try
{
    var emptyInstance = ebp!.GetField("Empty", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                        ?? ebp.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
    Console.WriteLine("  EmptyBasicProperty.Empty = " + (emptyInstance?.GetType().FullName ?? "null"));
    if (emptyInstance is RabbitMQ.Client.IReadOnlyBasicProperties rop)
    {
        var made = new RabbitMQ.Client.BasicProperties(rop);
        made.Headers = new Dictionary<string, object?> { ["concordat-v"] = "1" };
        Console.WriteLine("  -> new BasicProperties(empty) OK; Headers now settable. Type=" + (made.Type ?? "<null>"));
    }
}
catch (Exception ex) { Console.WriteLine("  ERROR: " + ex.Message); }
Console.WriteLine();

Console.WriteLine("=== IAmqpWriteable members (does a custom TProperties have to serialise itself?) ===");
var iw = asm.GetType("RabbitMQ.Client.IAmqpWriteable");
if (iw is not null)
{
    Console.WriteLine("  isPublic=" + iw.IsPublic);
    foreach (var m in iw.GetMethods()) Console.WriteLine("    " + Probe.Sig(m));
    foreach (var p in iw.GetProperties()) Console.WriteLine("    prop " + Probe.N(p.PropertyType) + " " + p.Name);
}
Console.WriteLine();

Console.WriteLine("=== ReadOnlyBasicProperties (what a consumer actually receives) ===");
var rbp = asm.GetType("RabbitMQ.Client.ReadOnlyBasicProperties");
Console.WriteLine("  " + (rbp?.FullName ?? "not found") + " isValueType=" + rbp?.IsValueType + " isPublic=" + rbp?.IsPublic);
Console.WriteLine();

await Live.RunAsync("amqp://guest:guest@localhost:15679/");
await Extra.RunAsync("amqp://guest:guest@localhost:15679/");

static class Probe
{
    public static string N(Type t)
    {
        if (t.IsGenericParameter) return t.Name;
        if (t.IsArray) return N(t.GetElementType()!) + "[]";
        if (t.IsByRef) return "ref " + N(t.GetElementType()!);
        var u = Nullable.GetUnderlyingType(t);
        if (u is not null) return N(u) + "?";
        if (t.IsGenericType)
        {
            var name = t.Name.Substring(0, t.Name.IndexOf('`'));
            return name + "<" + string.Join(", ", t.GetGenericArguments().Select(N)) + ">";
        }
        return t.Name switch
        {
            "String" => "string", "Boolean" => "bool", "Byte" => "byte", "SByte" => "sbyte",
            "Int16" => "short", "UInt16" => "ushort", "Int32" => "int", "UInt32" => "uint",
            "Int64" => "long", "UInt64" => "ulong", "Object" => "object", "Void" => "void",
            "Single" => "float", "Double" => "double", "Decimal" => "decimal", "Char" => "char",
            _ => t.Name
        };
    }

    public static string Sig(MethodInfo m)
    {
        var sb = new StringBuilder();
        sb.Append(N(m.ReturnType)).Append(' ').Append(m.Name);
        if (m.IsGenericMethodDefinition)
            sb.Append('<').Append(string.Join(", ", m.GetGenericArguments().Select(a => a.Name))).Append('>');
        sb.Append('(');
        sb.Append(string.Join(", ", m.GetParameters().Select(p =>
        {
            var s = (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + N(p.ParameterType) + " " + p.Name;
            if (p.HasDefaultValue) s += " = " + (p.RawDefaultValue?.ToString() ?? "default");
            return s;
        })));
        sb.Append(')');
        if (m.IsGenericMethodDefinition)
            foreach (var ga in m.GetGenericArguments())
            {
                var cs = ga.GetGenericParameterConstraints();
                if (cs.Length > 0) sb.Append(" where ").Append(ga.Name).Append(" : ").Append(string.Join(", ", cs.Select(N)));
            }
        return sb.ToString();
    }

    public static void DumpType(Assembly asm, string fullName)
    {
        var t = asm.GetType(fullName);
        Console.WriteLine($"=== {fullName} ===");
        if (t is null) { Console.WriteLine("  !! NOT FOUND"); Console.WriteLine(); return; }
        Console.WriteLine($"  kind={(t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class")} public={t.IsPublic} sealed={t.IsSealed} abstract={t.IsAbstract}");
        var ifs = t.GetInterfaces();
        if (ifs.Length > 0) Console.WriteLine("  implements: " + string.Join(", ", ifs.Select(N)));
        if (t.BaseType is not null && t.BaseType != typeof(object)) Console.WriteLine("  base: " + N(t.BaseType));
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(x => x.Name))
            Console.WriteLine($"  prop {N(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}{NullInfo(p)}");
        foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(x => x.Name))
            Console.WriteLine($"  event {N(e.EventHandlerType!)} {e.Name}");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                           .Where(m => !m.IsSpecialName).OrderBy(x => x.Name))
            Console.WriteLine("  " + (m.IsStatic ? "static " : "") + (!t.IsInterface && m.IsVirtual ? (m.IsAbstract ? "abstract " : "virtual ") : "") + Sig(m));
        foreach (var m in t.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                           .Where(m => m.IsFamily && !m.IsSpecialName).OrderBy(x => x.Name))
            Console.WriteLine("  protected " + (m.IsAbstract ? "abstract " : m.IsVirtual ? "virtual " : "") + Sig(m));
        Console.WriteLine();
    }

    static string NullInfo(PropertyInfo p)
    {
        var a = p.GetCustomAttributesData().FirstOrDefault(x => x.AttributeType.Name == "NullableAttribute");
        if (a is null || a.ConstructorArguments.Count == 0) return "";
        var v = a.ConstructorArguments[0].Value;
        return v is byte b ? $"   [Nullable({b})]" : "   [Nullable(...)]";
    }
}
