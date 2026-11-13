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
        var m = StateMachineRegex().Match(typeName);
        if (m.Success)
        {
            ownerType = m.Groups["owner"].Value;
            method = m.Groups["method"].Value;
            return true;
        }
        ownerType = method = "";
        return false;
    }

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
        if (TryParseStateMachine(typeName, out var owner, out var asyncMethod))
            return $"{ShortType(owner)}.{asyncMethod}";
        var lambda = LambdaRegex().Match(method);
        if (lambda.Success)
            method = $"{lambda.Groups["outer"].Value} (lambda)";
        var type = ShortType(DisplayClassRegex().Replace(typeName, ""));
        return type.Length == 0 ? method : $"{type}.{method}";
    }

    [GeneratedRegex(@"^(?<owner>.+)\+<(?<method>[^>]+)>d__\d+(`\d+)?(<.*>)?$")]
    private static partial Regex StateMachineRegex();

    [GeneratedRegex(@"^<(?<outer>[^>]+)>b__[\w]+$")]
    private static partial Regex LambdaRegex();

    [GeneratedRegex(@"\+<>c(__DisplayClass[\w]+)?$")]
    private static partial Regex DisplayClassRegex();
}
