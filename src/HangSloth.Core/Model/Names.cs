using System.Text.RegularExpressions;

namespace HangSloth.Model;

/// Turns runtime type and method names into the names a developer would write.
public static partial class Names
{
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["System.Boolean"] = "bool", ["System.Byte"] = "byte", ["System.Char"] = "char", ["System.Int16"] = "short",
        ["System.Int32"] = "int", ["System.Int64"] = "long", ["System.UInt32"] = "uint", ["System.UInt64"] = "ulong",
        ["System.Single"] = "float", ["System.Double"] = "double", ["System.Decimal"] = "decimal",
        ["System.String"] = "string", ["System.__Canon"] = "T", ["System.Object"] = "object", ["System.Threading.Tasks.VoidTaskResult"] = "void",
    };

    /// "HangApp.OrderService+<GetOrderAsync>d__0" -> ("HangApp.OrderService", "GetOrderAsync")
    public static bool TryParseStateMachine(string typeName, out string ownerType, out string method)
    {
        if (StateMachine(typeName) is { } sm)
        {
            ownerType = sm.Owner;
            method = sm.Method.ToString();
            return true;
        }
        ownerType = method = "";
        return false;
    }

    /// The method an async state machine runs, as its parts: "Program+<<Main>$>d__0" is the top-level statements, and
    /// "HttpClient+<<SendAsync>g__Core|83_0>d" is the async local function Core in SendAsync.
    private static (string Owner, Member Method)? StateMachine(string typeName)
    {
        var m = StateMachineRegex().Match(typeName);
        if (m.Success)
            return (m.Groups["owner"].Value, Outer(m.Groups["method"].Value));
        var nested = NestedStateMachineRegex().Match(typeName);
        if (nested.Success)
        {
            var outer = nested.Groups["outer"].Value;
            return (nested.Groups["owner"].Value, nested.Groups["kind"].Value == "b" ? Lambda(outer) : LocalFunction(outer, nested.Groups["name"].Value));
        }
        return null;
    }

    [GeneratedRegex(@"^(?<owner>.+?)\+<<(?<outer><Main>\$|[^<>]+)>(?:(?<kind>b)__[^>]*|(?<kind>g)__(?<name>[^|>]+)\|[^>]*)>d(__\d+)?(`\d+)?(<.*>)?$")]
    private static partial Regex NestedStateMachineRegex();

    /// A method name as a reader knows it, and what kind of code it is when it isn't a plain method: "Drain" and
    /// "local function in Run", or no name and "top-level statements".
    private readonly record struct Member(string? Name, string? Note = null)
    {
        public override string ToString() => Name is null ? Note ?? "" : Note is null ? Name : $"{Name} ({Note})";

        public string After(string type) =>
            type.Length == 0 ? ToString() : Name is null ? Note is null ? type : $"{type} ({Note})" : $"{type}.{this}";
    }

    /// The method the compiler makes for top-level statements: the developer wrote no method there, only the statements.
    private const string TopLevelMain = "<Main>$";

    /// The statements themselves for top-level code. The compiler's entry point that runs an async Main or top-level
    /// await ("&lt;Main&gt;") is no method the developer wrote: it is the program's entry point.
    private static Member Outer(string method) => method switch
    {
        TopLevelMain => new(null, "top-level statements"),
        "<Main>" => new(null, "entry point"),
        _ => new(method),
    };

    private static string Within(string outer) => outer == TopLevelMain ? "top-level statements" : outer;

    private static Member Lambda(string outer) => outer == TopLevelMain ? new(null, "lambda in top-level statements") : new(outer, "lambda");

    private static Member LocalFunction(string outer, string name) => new(name, $"local function in {Within(outer)}");

    /// Drops namespaces, keeps nesting and generic arguments short: "System.Threading.Tasks.Task<HangApp.Order>" -> "Task<Order>".
    public static string ShortType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return typeName;
        var sb = new System.Text.StringBuilder();
        var token = new System.Text.StringBuilder();
        void Flush()
        {
            if (token.Length == 0) return;
            var t = token.ToString();
            sb.Append(Aliases.TryGetValue(t, out var alias) ? alias : t.LastIndexOf('.') is var lastDot and >= 0 ? t[(lastDot + 1)..] : t);
            token.Clear();
        }
        foreach (var c in typeName)
        {
            if (c is '<' or '>' or ',' or '[' or ']' or ' ')
            {
                Flush();
                sb.Append(c);
            }
            else token.Append(c);
        }
        Flush();
        return sb.ToString().Replace('+', '.');
    }

    /// "HangApp.OrderService" + "GetOrderAsync" -> "OrderService.GetOrderAsync"
    public static string ShortMethod(string typeName, string method)
    {
        // Generic method instantiations ("Start[[Npgsql.Foo+<Bar>d__1, Npgsql]]") say nothing a reader needs.
        if (method.IndexOf("[[", StringComparison.Ordinal) is var generic and > 0) method = method[..generic];
        if (StateMachine(typeName) is { } sm)
            return sm.Method.After(ShortType(DisplayClassRegex().Replace(sm.Owner, "")));
        var type = ShortType(DisplayClassRegex().Replace(typeName, ""));
        return MethodName(method).After(type);
    }

    private static Member MethodName(string method)
    {
        var lambda = LambdaRegex().Match(method);
        if (lambda.Success)
            return Lambda(lambda.Groups["outer"].Value);
        var local = LocalFunctionRegex().Match(method);
        if (local.Success)
        {
            // "<Wait>g____PInvoke|24_0" is the generated P/Invoke stub for Wait: just call it Wait.
            var name = local.Groups["name"].Value;
            return name == "__PInvoke" ? Outer(local.Groups["outer"].Value) : LocalFunction(local.Groups["outer"].Value, name);
        }
        // An empty method: the type alone, never "Type.".
        return method.Length == 0 ? new(null) : Outer(CompiledLambdaRegex().Replace(method, "lambda_method"));
    }

    /// A field as the code names it: an auto-property's backing field "&lt;Total&gt;k__BackingField" is Total, a
    /// primary-constructor parameter kept in "&lt;right&gt;P" is right, and a local an async method keeps across an
    /// await, "&lt;orders&gt;5__2", is orders.
    public static string FieldName(string? name) =>
        name is not null && GeneratedFieldRegex().Match(name) is { Success: true } m ? m.Groups["name"].Value : name ?? "?";

    /// "OrderCache._sync" for a field of an object of the app, and "orders (a captured local in Program)" for a local
    /// that a lambda or local function captured: the compiler keeps those in a class of its own ("Program+&lt;&gt;c__DisplayClass0_0").
    public static string Field(string ownerType, string? field)
    {
        var name = FieldName(field);
        var closure = ClosureRegex().Match(ownerType);
        if (closure.Success) return $"{name} (a captured local in {ShortType(closure.Groups["outer"].Value)})";
        if (StateMachine(ownerType) is not null && field?.StartsWith('<') == true)
            return $"{name} (a local in {ShortMethod(ownerType, "")})";
        return $"{ShortType(ownerType)}.{name}";
    }

    [GeneratedRegex(@"^<(?<name>[^<>]+)>(k__BackingField|P|5__\d+)$")]
    private static partial Regex GeneratedFieldRegex();

    [GeneratedRegex(@"^(?<outer>.+)\+<>c__DisplayClass[\w]+$")]
    private static partial Regex ClosureRegex();

    /// The method a generated P/Invoke stub ("<Join>g____PInvoke|56_0") belongs to ("Join"), or null when the name
    /// isn't one. The number after the bar differs between builds and operating systems.
    public static string? PInvokeStubOf(string method) =>
        LocalFunctionRegex().Match(method) is { Success: true } local && local.Groups["name"].Value == "__PInvoke" ? local.Groups["outer"].Value : null;

    [GeneratedRegex(@"^(?<owner>.+)\+<(?<method><Main>\$|[^>]+)>d__\d+(`\d+)?(<.*>)?$")]
    private static partial Regex StateMachineRegex();

    [GeneratedRegex(@"^<(?<outer><Main>\$|[^>]+)>b__[\w]+$")]
    private static partial Regex LambdaRegex();

    [GeneratedRegex(@"^<(?<outer><Main>\$|[^>]+)>g__(?<name>[^|]+)\|[\d_]+$")]
    private static partial Regex LocalFunctionRegex();

    /// Expression-compiled thunks ("lambda_method12") get a fresh number per compile; the number means nothing to a reader.
    [GeneratedRegex(@"^lambda_method\d+$")]
    private static partial Regex CompiledLambdaRegex();

    [GeneratedRegex(@"\+<>c(__DisplayClass[\w]+)?$")]
    private static partial Regex DisplayClassRegex();
}
