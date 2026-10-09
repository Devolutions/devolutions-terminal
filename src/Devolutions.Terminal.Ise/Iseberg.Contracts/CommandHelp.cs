using System.Collections;

using System.Text;

namespace Iseberg.Core;

public enum HelpSectionKind { Synopsis, Syntax, Description, Parameters, Inputs, Outputs, Notes, Examples, RelatedLinks, Remarks }

public sealed class HelpViewSettings
{
    public List<HelpSectionKind> Sections { get; set; } = [.. Enum.GetValues<HelpSectionKind>()];
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public double Zoom { get; set; } = 100;

    public HelpViewSettings Copy() => new() { Sections = [.. Sections], MatchCase = MatchCase, WholeWord = WholeWord, Zoom = Zoom };

    public void Normalize()
    {
        Sections ??= [.. Enum.GetValues<HelpSectionKind>()];
        if (Sections.Any(section => !Enum.IsDefined(section))) throw new InvalidDataException("An unknown help section was saved.");
        Sections = Sections.Distinct().ToList();
        Zoom = double.IsFinite(Zoom) ? Math.Clamp(Zoom, 20, 400) : 100;
    }
}

public sealed record CommandHelpSection(HelpSectionKind Kind, string Text);

public sealed record CommandHelpDocument(string Name, IReadOnlyList<CommandHelpSection> Sections)
{
    public string ToText() => string.Join("\n\n", Sections.Select(section => section.Kind + "\n" + section.Text));

}
