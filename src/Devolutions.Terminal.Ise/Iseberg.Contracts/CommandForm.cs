namespace Iseberg.Core;

public enum CommandParameterKind { Text, Switch, Boolean, Choice }

public sealed record CommandParameterDescription(
    string Name, string TypeName, CommandParameterKind Kind, bool IsMandatory, int? Position,
    bool IsArray, bool IsCommon, bool AllowsEmptyString, bool AcceptsPipelineInput,
    string HelpMessage, IReadOnlyList<string> Aliases, IReadOnlyList<string> Choices, bool ChoicesIgnoreCase = true);

public sealed record CommandParameterSetDescription(
    string Name, bool IsDefault, IReadOnlyList<CommandParameterDescription> Parameters)
{
    public override string ToString() => Name;
}

public sealed record CommandFormDescription(
    string Name, string InvocationName, IReadOnlyList<CommandParameterSetDescription> ParameterSets);

public sealed class CommandParameterValue
{
    public bool Included { get; set; }
    public string Text { get; set; } = "";
    public bool IsExpression { get; set; }
    public bool Boolean { get; set; } = true;
}

public sealed record CommandFormResult(string Script, IReadOnlyList<string> MissingParameters,
    IReadOnlyList<string> InvalidExpressions, IReadOnlyList<string> InvalidChoices)
{
    public bool IsValid => MissingParameters.Count == 0 && InvalidExpressions.Count == 0 && InvalidChoices.Count == 0;
}

public sealed class CommandForm
{
    private readonly Dictionary<string, CommandParameterValue> values = new(StringComparer.OrdinalIgnoreCase);
    public CommandFormDescription Description { get; }
    public CommandParameterSetDescription SelectedSet { get; private set; }

    public CommandForm(CommandFormDescription description)
    {
        if (description.ParameterSets.Count == 0)
            throw new ArgumentException("A command form must have at least one parameter set.", nameof(description));
        Description = description;
        SelectedSet = description.ParameterSets.FirstOrDefault(set => set.IsDefault) ?? description.ParameterSets[0];
    }

    public void SelectSet(string name) =>
        SelectedSet = Description.ParameterSets.First(set => set.Name == name);

    public CommandParameterValue Value(string name)
    {
        if (!values.TryGetValue(name, out var value)) values.Add(name, value = new());
        return value;
    }

    public CommandFormResult Build(IReadOnlyDictionary<string, bool>? expressionValidity = null)
    {
        var parts = new List<string>
        {
            "& " + Quote(Description.InvocationName)
        };
        var missing = new List<string>();
        var invalid = new List<string>();
        var invalidChoices = new List<string>();
        foreach (var parameter in SelectedSet.Parameters)
        {
            var value = Value(parameter.Name);
            if (parameter.IsMandatory && (!value.Included ||
                (parameter.IsArray || parameter.Kind is not (CommandParameterKind.Switch or CommandParameterKind.Boolean)) &&
                value.Text.Length == 0 && !parameter.AllowsEmptyString))
                missing.Add(parameter.Name);
            if (!value.Included) continue;
            if (!value.IsExpression && parameter.Choices.Count > 0)
            {
                var entries = parameter.IsArray ? value.Text.Replace("\r\n", "\n").Split('\n') : [value.Text];
                var comparer = parameter.ChoicesIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                if (entries.Any(entry => !parameter.Choices.Contains(entry, comparer))) invalidChoices.Add(parameter.Name);
            }
            if (parameter.Kind == CommandParameterKind.Switch)
            {
                parts.Add("-" + parameter.Name + (value.Boolean ? "" : ":$false"));
                continue;
            }
            string argument;
            if (parameter.Kind == CommandParameterKind.Boolean && !parameter.IsArray)
                argument = value.Boolean ? "$true" : "$false";
            else if (value.IsExpression)
            {
                argument = "(" + value.Text + "\n)";
                if (string.IsNullOrWhiteSpace(value.Text) ||
                    expressionValidity is null || !expressionValidity.TryGetValue(parameter.Name, out var valid) || !valid)
                    invalid.Add(parameter.Name);
            }
            else if (parameter.IsArray)
                argument = "@(" + string.Join(", ", value.Text.Replace("\r\n", "\n").Split('\n').Select(entry =>
                    parameter.Kind == CommandParameterKind.Boolean && bool.TryParse(entry, out var boolean)
                        ? boolean ? "$true" : "$false" : Quote(entry))) + ")";
            else
                argument = Quote(value.Text);
            parts.Add("-" + parameter.Name + " " + argument);
        }
        return new(string.Join(" ", parts), missing, invalid, invalidChoices);
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    public CommandFormBuildRequest ToRequest() => new(Description, SelectedSet.Name,
        values.ToDictionary(entry => entry.Key, entry => new CommandParameterValue
        {
            Included = entry.Value.Included, Text = entry.Value.Text,
            IsExpression = entry.Value.IsExpression, Boolean = entry.Value.Boolean
        }, StringComparer.OrdinalIgnoreCase));
}
