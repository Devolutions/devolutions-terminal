using System.Management.Automation;
using Iseberg.Core;
namespace Iseberg.PowerShellHost;
internal static class CommandMetadata
{
    internal static CommandFormDescription Describe(CommandInfo command)
    {
        var resolved = command;
        while (resolved is AliasInfo alias)
            resolved = alias.ResolvedCommand ?? throw new InvalidOperationException($"Cannot resolve alias '{command.Name}'.");
        var sets = resolved.ParameterSets.Select(set => new CommandParameterSetDescription(set.Name, set.IsDefault,
            set.Parameters.OrderByDescending(p => p.IsMandatory).ThenBy(p => p.Position < 0 ? int.MaxValue : p.Position)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(DescribeParameter).ToArray())).ToArray();
        return new(command.Name, string.IsNullOrEmpty(command.ModuleName) ? command.Name : command.ModuleName + "\\" + command.Name,
            sets.Length == 0 ? [new("__AllParameterSets", true, [])] : sets);
    }

    private static CommandParameterDescription DescribeParameter(CommandParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var element = type.IsArray ? type.GetElementType()! : type;
        element = Nullable.GetUnderlyingType(element) ?? element;
        var validation = parameter.Attributes.OfType<ValidateSetAttribute>().FirstOrDefault();
        var choices = validation?.ValidValues.ToArray()
            ?? (element.IsEnum ? Enum.GetNames(element) : type.IsArray && element == typeof(bool) ? ["True", "False"] : []);
        var kind = !type.IsArray && element == typeof(SwitchParameter) ? CommandParameterKind.Switch
            : element == typeof(bool) ? CommandParameterKind.Boolean
            : choices.Length > 0 ? CommandParameterKind.Choice : CommandParameterKind.Text;
        return new(parameter.Name, type.Name, kind, parameter.IsMandatory,
            parameter.Position < 0 ? null : parameter.Position, type.IsArray, CommonParameters.Contains(parameter.Name),
            parameter.Attributes.OfType<AllowEmptyStringAttribute>().Any(),
            parameter.ValueFromPipeline || parameter.ValueFromPipelineByPropertyName,
            parameter.Attributes.OfType<ParameterAttribute>().FirstOrDefault(a => !string.IsNullOrEmpty(a.HelpMessage))?.HelpMessage ?? "",
            parameter.Aliases.ToArray(), choices, validation?.IgnoreCase ?? true);
    }

    private static readonly HashSet<string> CommonParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction", "ProgressAction",
        "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable", "OutBuffer", "PipelineVariable",
        "WhatIf", "Confirm"
    };
}
